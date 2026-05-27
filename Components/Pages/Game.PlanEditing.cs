using System.Text.Json;
using System.Globalization;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Game
{
    // ── Layer/policy pickers ──────────────────────────────────────────────────
    private bool _layerPickerOpen  = false;
    private bool _policyPickerOpen = false;

    // ── Edit mode ─────────────────────────────────────────────────────────────
    private bool    _editMode      = false;
    private bool    _editSaving    = false;
    private string? _editError;
    private string? _editName;
    private string? _editDescription;
    private int     _editStartYear;
    private int     _editStartMonth;
    private int     _editMinConstructionMonths;
    private HashSet<string> _editPlanLayerIds = [];
    private HashSet<string> _editPolicyTypes  = [];
    private string? _enterEditError;
    private TaskCompletionSource<bool>? _editWsConfirmationTcs;
    private bool    _pendingEnterEditMode;
    private int     _pendingSelectPlanId;
    private string? _pendingBatchGuid;
    private int     _pendingCreatePlanCallId;

    // ── Geometry drawing tool ─────────────────────────────────────────────────
    private string?    _geometryToolLayerId;
    private string?    _geometryEditedLayerId;
    private bool       _geometryToolCreate;
    private int        _geometryToolTypeIndex;
    private string?    _geometryToolSelectedFeatureId;
    private string?    _geometryToolSelectedWorldStateId;
    private int        _geometryToolSelectedTypeIndex;
    private double[][] _geometryToolSelectedCoords = [];
    private List<DrawingAction> _drawingUndoStack   = [];
    private List<DrawingAction> _drawingRedoStack   = [];
    private HashSet<string>     _deletedWorldStateIds = [];

    // ── Geometry caches ───────────────────────────────────────────────────────
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _parsedLayerGeometryCache = [];
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _projectedGeometryCache   = [];
    // ── Geometry Drawing Tool Methods ─────────────────────────────────────────

    private async Task OpenGeometryToolAsync(string layerId)
    {
        if (_geometryToolLayerId == layerId)
        {
            await CloseGeometryToolAsync();
            return;
        }
        if (_mapModule is null) return;
        _layerPickerOpen               = false;
        _geometryToolLayerId           = layerId;
        _geometryEditedLayerId         = layerId;
        _geometryToolTypeIndex         = 0;
        _geometryToolCreate            = false;
        _geometryToolSelectedFeatureId = null;
        _geometryToolSelectedWorldStateId = null;
        _geometryToolSelectedTypeIndex = 0;
        _geometryToolSelectedCoords    = [];
        _drawingUndoStack.Clear();
        _drawingRedoStack.Clear();
        _deletedWorldStateIds.Clear();
        await _mapModule.InvokeVoidAsync("startGeometryEdit", layerId, _dotNetRef);

        // Load world-state (projected) features for this layer so the user can edit them
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan is not null)
        {
            var worldState  = GetProjectedLayerGeometries(layerId, plan.StartDate);
            var layerEntry  = _layerEntries.FirstOrDefault(e => e.LayerId == layerId);
            var geoType     = layerEntry?.GeoType ?? "polygon";

            // IDs already in this plan's overlay (plan's own geometry for this layer)
            var planOwnIds  = plan.Layers
                .FirstOrDefault(l => string.Equals(l.OriginalLayerId, layerId, StringComparison.OrdinalIgnoreCase))
                ?.Geometry
                .Select(g => g.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>();

            var toLoad = worldState
                .Where(g => !planOwnIds.Contains(g.FeatureId) && g.Coordinates.Count > 0)
                .Select(g => new
                {
                    id        = g.FeatureId,
                    layerId   = layerId,
                    geoType,
                    typeIndex = g.TypeIndex,
                    coords    = g.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray(),
                })
                .ToList();

            if (toLoad.Count > 0)
                await _mapModule.InvokeVoidAsync("loadWorldStateFeatures",
                    JsonSerializer.Serialize(toLoad));
        }
        StateHasChanged();
    }

    private async Task CloseGeometryToolAsync()
    {
        if (_mapModule is not null)
        {
            await _mapModule.InvokeVoidAsync("stopGeometryEditing");
            // Keep modified world-state features in the overlay; remove unchanged ones
            if (_geometryToolLayerId is not null)
                await _mapModule.InvokeVoidAsync("removeUnchangedWorldStateFeatures", _geometryToolLayerId);
        }
        _geometryToolLayerId              = null;
        _geometryToolSelectedFeatureId    = null;
        _geometryToolSelectedWorldStateId = null;
        _geometryToolSelectedTypeIndex    = 0;
        _geometryToolSelectedCoords       = [];
        _drawingUndoStack.Clear();
        _drawingRedoStack.Clear();
        StateHasChanged();
    }

    private async Task SetGeometryModeCreateAsync()
    {
        if (_mapModule is null || _geometryToolLayerId is null) return;
        var layerEntry = _layerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
        _geometryToolCreate            = true;
        _geometryToolSelectedFeatureId = null;
        await _mapModule.InvokeVoidAsync("startGeometryCreate",
            _geometryToolLayerId, layerEntry?.GeoType ?? "polygon", _geometryToolTypeIndex, _dotNetRef);
        StateHasChanged();
    }

    private async Task SetGeometryModeEditAsync()
    {
        if (_mapModule is null || _geometryToolLayerId is null) return;
        _geometryToolCreate            = false;
        _geometryToolSelectedFeatureId = null;
        await _mapModule.InvokeVoidAsync("startGeometryEdit", _geometryToolLayerId, _dotNetRef);
        StateHasChanged();
    }

    private async Task SetGeometryTypeIndexAsync(int idx)
    {
        _geometryToolTypeIndex = idx;
        if (_geometryToolCreate && _mapModule is not null && _geometryToolLayerId is not null)
        {
            var layerEntry = _layerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
            await _mapModule.InvokeVoidAsync("startGeometryCreate",
                _geometryToolLayerId, layerEntry?.GeoType ?? "polygon", idx, _dotNetRef);
        }
        StateHasChanged();
    }

    private async Task ToggleGeometryTypeBitAsync(int bitIndex)
    {
        _geometryToolTypeIndex ^= 1 << bitIndex;
        if (_geometryToolCreate && _mapModule is not null && _geometryToolLayerId is not null)
        {
            var layerEntry = _layerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
            await _mapModule.InvokeVoidAsync("startGeometryCreate",
                _geometryToolLayerId, layerEntry?.GeoType ?? "polygon", _geometryToolTypeIndex, _dotNetRef);
        }
        StateHasChanged();
    }

    private async Task UndoDrawingActionAsync()
    {
        if (_drawingUndoStack.Count == 0 || _mapModule is null) return;
        var action = _drawingUndoStack[^1];
        _drawingUndoStack.RemoveAt(_drawingUndoStack.Count - 1);
        _drawingRedoStack.Add(action);
        await ApplyDrawingActionAsync(action, undo: true);
        StateHasChanged();
    }

    private async Task RedoDrawingActionAsync()
    {
        if (_drawingRedoStack.Count == 0 || _mapModule is null) return;
        var action = _drawingRedoStack[^1];
        _drawingRedoStack.RemoveAt(_drawingRedoStack.Count - 1);
        _drawingUndoStack.Add(action);
        await ApplyDrawingActionAsync(action, undo: false);
        StateHasChanged();
    }

    private async Task ApplyDrawingActionAsync(DrawingAction action, bool undo)
    {
        if (_mapModule is null) return;
        switch (action)
        {
            case MoveAction m:
                var coords = undo ? m.OldCoords : m.NewCoords;
                await _mapModule.InvokeVoidAsync("setFeatureCoords", m.FeatureId, coords);
                break;
            case AddAction a:
                if (undo)
                    await _mapModule.InvokeVoidAsync("removeFeatureFromOverlay", a.TempId);
                else
                    await _mapModule.InvokeVoidAsync("addFeatureToOverlay", JsonSerializer.Serialize(new
                    {
                        featureId = a.TempId, originalLayerId = a.OriginalLayerId,
                        typeIndex = a.TypeIndex, geoType = a.GeoType, coords = a.Coords
                    }));
                break;
            case DeleteAction d:
                if (undo)
                {
                    // Restore deleted feature (including world-state marker)
                    if (d.WorldStateId is not null)
                        _deletedWorldStateIds.Remove(d.WorldStateId);
                    await _mapModule.InvokeVoidAsync("addFeatureToOverlay", JsonSerializer.Serialize(new
                    {
                        featureId = d.FeatureId, originalLayerId = d.OriginalLayerId,
                        typeIndex = d.TypeIndex, geoType = d.GeoType, coords = d.Coords,
                        worldStateId = d.WorldStateId,
                    }));
                }
                else
                {
                    if (d.WorldStateId is not null)
                        _deletedWorldStateIds.Add(d.WorldStateId);
                    await _mapModule.InvokeVoidAsync("removeFeatureFromOverlay", d.FeatureId);
                }
                break;
        }
    }

    private async Task DeleteSelectedGeometryAsync()
    {
        if (_geometryToolSelectedFeatureId is null || _geometryToolLayerId is null || _mapModule is null) return;
        var layerEntry = _layerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
        var worldStateId = _geometryToolSelectedWorldStateId;
        _drawingUndoStack.Add(new DeleteAction(
            _geometryToolSelectedFeatureId, _geometryToolLayerId,
            _geometryToolSelectedTypeIndex, layerEntry?.GeoType ?? "polygon",
            _geometryToolSelectedCoords, worldStateId));
        _drawingRedoStack.Clear();
        if (worldStateId is not null)
            _deletedWorldStateIds.Add(worldStateId);
        await _mapModule.InvokeVoidAsync("removeFeatureFromOverlay", _geometryToolSelectedFeatureId);
        _geometryToolSelectedFeatureId    = null;
        _geometryToolSelectedWorldStateId = null;
        StateHasChanged();
    }

    private async Task RestoreLastDeletedAsync()
    {
        for (var i = _drawingUndoStack.Count - 1; i >= 0; i--)
        {
            if (_drawingUndoStack[i] is not DeleteAction d) continue;
            _drawingUndoStack.RemoveAt(i);
            if (_mapModule is not null)
                await _mapModule.InvokeVoidAsync("addFeatureToOverlay", JsonSerializer.Serialize(new
                {
                    featureId = d.FeatureId, originalLayerId = d.OriginalLayerId,
                    typeIndex = d.TypeIndex, geoType = d.GeoType, coords = d.Coords
                }));
            StateHasChanged();
            return;
        }
    }

    [JSInvokable]
    public void OnGeometryModified(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root         = doc.RootElement;
            var featureId    = root.GetProperty("featureId").GetString() ?? "";
            var worldStateId = root.TryGetProperty("worldStateId", out var wsEl) && wsEl.ValueKind != JsonValueKind.Null
                ? wsEl.GetString() : null;
            var oldCoords    = ParseCoordArray(root.GetProperty("oldCoords"));
            var newCoords    = ParseCoordArray(root.GetProperty("newCoords"));
            _drawingUndoStack.Add(new MoveAction(featureId, _geometryToolLayerId ?? "", oldCoords, newCoords, worldStateId));
            _drawingRedoStack.Clear();
            InvokeAsync(StateHasChanged);
        }
        catch { /* ignore parse errors */ }
    }

    [JSInvokable]
    public void OnGeometryCreated(string json)
    {
        try
        {
            using var doc     = JsonDocument.Parse(json);
            var root          = doc.RootElement;
            var tempId        = root.GetProperty("tempId").GetString() ?? "";
            var layerId       = root.TryGetProperty("originalLayerId", out var lidEl) ? lidEl.GetString() ?? (_geometryToolLayerId ?? "") : (_geometryToolLayerId ?? "");
            var typeIndex     = root.TryGetProperty("typeIndex", out var tiEl) ? tiEl.GetInt32() : _geometryToolTypeIndex;
            var geoType       = root.TryGetProperty("geoType", out var gtEl) ? gtEl.GetString() ?? "polygon" : "polygon";
            var coords        = ParseCoordArray(root.GetProperty("coords"));
            _drawingUndoStack.Add(new AddAction(tempId, layerId, typeIndex, geoType, coords));
            _drawingRedoStack.Clear();
            InvokeAsync(StateHasChanged);
        }
        catch { /* ignore parse errors */ }
    }

    [JSInvokable]
    public void OnGeometrySelected(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            if (root.TryGetProperty("featureId", out var fidEl)
                && fidEl.ValueKind != JsonValueKind.Null
                && fidEl.GetString() is { } fid)
            {
                _geometryToolSelectedFeatureId    = fid;
                _geometryToolSelectedTypeIndex    = root.TryGetProperty("typeIndex", out var tiEl) ? tiEl.GetInt32() : 0;
                _geometryToolSelectedCoords       = root.TryGetProperty("coords", out var ceEl) ? ParseCoordArray(ceEl) : [];
                _geometryToolSelectedWorldStateId = root.TryGetProperty("worldStateId", out var wsEl) && wsEl.ValueKind != JsonValueKind.Null
                    ? wsEl.GetString() : null;
            }
            else
            {
                _geometryToolSelectedFeatureId    = null;
                _geometryToolSelectedWorldStateId = null;
            }
            InvokeAsync(StateHasChanged);
        }
        catch { /* ignore parse errors */ }
    }

    private static double[][] ParseCoordArray(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array) return [];
        var result = new List<double[]>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Array)
                result.Add(item.EnumerateArray().Select(v => v.GetDouble()).ToArray());
        return [.. result];
    }

    private IReadOnlyList<PlanRestrictionIssue> EvaluateRestrictionsForGeometry(
        string sourceLayerId,
        string sourceGeoType,
        PlanGeometryItem geometry,
        bool isNewGeometry,
        int planStartDate)
    {
        if (GameState.Restrictions.Count == 0 || geometry.Coordinates.Count == 0)
            return [];

        var sourceLayer = _layerEntries.FirstOrDefault(l => string.Equals(l.LayerId, sourceLayerId, StringComparison.OrdinalIgnoreCase));
        if (sourceLayer is null)
            return [];

        var matches = new List<PlanRestrictionIssue>();
        // Deduplicate: same message at the same map position is considered one issue regardless
        // of whether it comes from duplicate restriction rules or coincident target geometries.
        var seenIssueKeys = new HashSet<(string severity, string message, string src, string tgt, double x, double y)>();

        foreach (var rule in GameState.Restrictions)
        {
            if (!RuleLayerMatches(rule.StartLayer, sourceLayer)
                || !RestrictionTypeMatches(rule.StartType, geometry.TypeIndex, sourceLayer))
                continue;

            var targetLayer = FindLayerByRuleName(rule.EndLayer);
            var targetType = rule.EndType;

            if (targetLayer is null || targetLayer.IsRaster)
                continue;

            var targetGeometries = GetProjectedLayerGeometries(targetLayer.LayerId, planStartDate);
            var constraintSort = NormaliseConstraintSort(rule.Sort);
            var sourceMarker = GetGeometryCenter(geometry.Coordinates);
            var overlapFound = false;

            foreach (var targetGeometry in targetGeometries)
            {
                if (!RestrictionTypeMatches(targetType, targetGeometry.TypeIndex, targetLayer))
                    continue;

                if (!HasOverlap(sourceGeoType, geometry.Coordinates, targetLayer.GeoType, targetGeometry.Coordinates))
                    continue;

                overlapFound = true;

                // Unity inclusion constraints emit one issue per overlapping target geometry.
                if (constraintSort == "EXCLUSION")
                    continue;

                var severity = NormaliseSeverity(rule.Type);
                if (string.IsNullOrEmpty(severity))
                    severity = "WARNING";
                var message = string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Overlap with {targetLayer.DisplayName}"
                    : rule.Message;
                var marker = GetGeometryCenter(targetGeometry.Coordinates);
                var key = (severity, message, sourceLayer.DisplayName, targetLayer.DisplayName, marker[0], marker[1]);
                if (seenIssueKeys.Add(key))
                    matches.Add(new PlanRestrictionIssue(
                        severity,
                        message,
                        sourceLayer.DisplayName,
                        targetLayer.DisplayName,
                        isNewGeometry ? "New geometry" : "Changed geometry",
                        marker[0],
                        marker[1]));
            }

            // Unity exclusion constraints add an issue only when no overlap was found.
            if (constraintSort == "EXCLUSION" && !overlapFound)
            {
                var severity = NormaliseSeverity(rule.Type);
                if (string.IsNullOrEmpty(severity))
                    severity = "WARNING";
                var message = string.IsNullOrWhiteSpace(rule.Message)
                    ? $"No valid overlap with {targetLayer.DisplayName}"
                    : rule.Message;
                var key = (severity, message, sourceLayer.DisplayName, targetLayer.DisplayName, sourceMarker[0], sourceMarker[1]);
                if (seenIssueKeys.Add(key))
                    matches.Add(new PlanRestrictionIssue(
                        severity,
                        message,
                        sourceLayer.DisplayName,
                        targetLayer.DisplayName,
                        isNewGeometry ? "New geometry" : "Changed geometry",
                        sourceMarker[0],
                        sourceMarker[1]));
            }
        }

        return matches;
    }

    private LayerEntry? FindLayerByRuleName(string? ruleLayer)
    {
        if (string.IsNullOrWhiteSpace(ruleLayer))
            return null;

        var normalizedRule = NormaliseToken(ruleLayer);
        var exact = _layerEntries.FirstOrDefault(layer =>
            normalizedRule == NormaliseToken(layer.LayerId)
            || normalizedRule == NormaliseToken(layer.LayerName)
            || normalizedRule == NormaliseToken(layer.DisplayName));
        if (exact is not null)
            return exact;

        return _layerEntries.FirstOrDefault(layer => RuleLayerMatches(ruleLayer, layer));
    }

    private static bool RuleLayerMatches(string? ruleLayer, LayerEntry layer)
    {
        if (string.IsNullOrWhiteSpace(ruleLayer))
            return false;

        if (string.Equals(ruleLayer, "*", StringComparison.Ordinal))
            return true;

        var normalizedRuleLayer = NormaliseToken(ruleLayer);
        var layerId = NormaliseToken(layer.LayerId);
        var layerName = NormaliseToken(layer.LayerName);
        var displayName = NormaliseToken(layer.DisplayName);

        if (normalizedRuleLayer == layerId
            || normalizedRuleLayer == layerName
            || normalizedRuleLayer == displayName)
            return true;

        // Restriction endpoints commonly use numeric layer IDs. Keep those strict to avoid
        // accidental matches like rule layer "2" matching actual layer id "12".
        if (normalizedRuleLayer.All(char.IsDigit))
            return false;

        // For textual rules, allow partial matching only for reasonably descriptive tokens.
        if (normalizedRuleLayer.Length < 4)
            return false;

        return layerName.Contains(normalizedRuleLayer, StringComparison.Ordinal)
            || displayName.Contains(normalizedRuleLayer, StringComparison.Ordinal)
            || normalizedRuleLayer.Contains(layerName, StringComparison.Ordinal)
            || normalizedRuleLayer.Contains(displayName, StringComparison.Ordinal);
    }

    private static bool RestrictionTypeMatches(string? ruleType, int actualTypeIndex, LayerEntry? layer)
    {
        if (string.IsNullOrWhiteSpace(ruleType))
            return true;

        var normalizedRuleType = NormaliseToken(ruleType);
        if (normalizedRuleType is "*" or "any" or "all")
            return true;

        if (int.TryParse(ruleType, out var index))
            return index == actualTypeIndex;

        if (normalizedRuleType == actualTypeIndex.ToString(CultureInfo.InvariantCulture))
            return true;

        if (layer is null || actualTypeIndex < 0 || actualTypeIndex >= layer.TypeDefs.Count)
            return false;

        var label = layer.TypeDefs[actualTypeIndex].Label;
        return normalizedRuleType == NormaliseToken(label);
    }

    private List<ParsedLayerGeometry> GetParsedLayerGeometries(string layerId)
    {
        if (_parsedLayerGeometryCache.TryGetValue(layerId, out var cached))
            return cached;

        var parsed = new List<ParsedLayerGeometry>();
        _parsedLayerGeometryCache[layerId] = parsed;

        var snapshot = GameState.MapLayerSnapshots.FirstOrDefault(s => string.Equals(s.LayerId, layerId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.VectorGeometriesJson))
            return parsed;

        try
        {
            using var document = JsonDocument.Parse(snapshot.VectorGeometriesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return parsed;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("geometry", out var geometryElement) || geometryElement.ValueKind != JsonValueKind.Array)
                    continue;

                var coordinates = new List<double[]>();
                foreach (var pointElement in geometryElement.EnumerateArray())
                {
                    if (pointElement.ValueKind != JsonValueKind.Array || pointElement.GetArrayLength() < 2)
                        continue;

                    if (pointElement[0].ValueKind != JsonValueKind.Number || pointElement[1].ValueKind != JsonValueKind.Number)
                        continue;

                    coordinates.Add([pointElement[0].GetDouble(), pointElement[1].GetDouble()]);
                }

                if (coordinates.Count == 0)
                    continue;

                var featureId = "";
                if (element.TryGetProperty("id", out var idElement))
                    featureId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? "" : idElement.ToString();

                var typeIndex = 0;
                if (element.TryGetProperty("type", out var typeElement))
                {
                    if (typeElement.ValueKind == JsonValueKind.Number)
                        typeIndex = typeElement.GetInt32();
                    else if (typeElement.ValueKind == JsonValueKind.String)
                        int.TryParse(typeElement.GetString(), out typeIndex);
                }

                parsed.Add(new ParsedLayerGeometry(featureId, typeIndex, coordinates));
            }
        }
        catch
        {
            // Ignore malformed cached geometry and simply skip restrictions for this layer.
        }

        return parsed;
    }

    /// <summary>
    /// Returns the effective geometries for <paramref name="layerId"/> as they would exist
    /// at the moment a plan with <paramref name="beforeStartDate"/> is implemented — i.e. after
    /// applying all APPROVED / IMPLEMENTED / ARCHIVED plans whose start date is strictly earlier.
    /// </summary>
    private List<ParsedLayerGeometry> GetProjectedLayerGeometries(string layerId, int beforeStartDate)
    {
        var cacheKey = $"{layerId}\x01{beforeStartDate}";
        if (_projectedGeometryCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var baseline = GetParsedLayerGeometries(layerId);

        // Collect finalised plans that are already in effect before this plan's start date.
        var priorPlans = _plans
            .Where(p => p.StartDate < beforeStartDate && IsFinalisedPlanState(p.State))
            .OrderBy(p => p.StartDate)
            .ThenBy(p => p.PlanId)
            .ToList();

        if (priorPlans.Count == 0)
        {
            _projectedGeometryCache[cacheKey] = baseline;
            return baseline;
        }

        // Build the set of original feature IDs that have been removed or replaced,
        // and collect all plan-introduced geometries as additions.
        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additions  = new List<ParsedLayerGeometry>();

        foreach (var priorPlan in priorPlans)
        {
            var planLayer = priorPlan.Layers.FirstOrDefault(l =>
                string.Equals(l.OriginalLayerId, layerId, StringComparison.OrdinalIgnoreCase));
            if (planLayer is null) continue;

            // Explicit deletions.
            foreach (var deletedId in planLayer.DeletedPersistentIds)
                deletedIds.Add(deletedId);

            foreach (var geo in planLayer.Geometry)
            {
                bool isModification = !string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id;
                if (isModification)
                    deletedIds.Add(geo.PersistentId); // original is superseded by the plan version

                // Whether new or modified, the plan geometry is part of the projected world.
                additions.Add(new ParsedLayerGeometry(geo.Id, geo.TypeIndex, geo.Coordinates));
            }
        }

        var projected = new List<ParsedLayerGeometry>(baseline.Count + additions.Count);
        foreach (var geo in baseline)
        {
            if (!deletedIds.Contains(geo.FeatureId))
                projected.Add(geo);
        }
        projected.AddRange(additions);

        _projectedGeometryCache[cacheKey] = projected;
        return projected;
    }

    private static bool HasOverlap(
        string sourceGeoType,
        IReadOnlyList<double[]> sourceCoordinates,
        string targetGeoType,
        IReadOnlyList<double[]> targetCoordinates)
    {
        var aType = NormaliseGeometryKind(sourceGeoType, sourceCoordinates);
        var bType = NormaliseGeometryKind(targetGeoType, targetCoordinates);

        if (aType == "point" && bType == "point")
            return PointDistanceSquared(sourceCoordinates[0], targetCoordinates[0]) <= 1.0;

        if (aType == "point" && bType == "line")
            return PointOnLine(sourceCoordinates[0], targetCoordinates);

        if (aType == "line" && bType == "point")
            return PointOnLine(targetCoordinates[0], sourceCoordinates);

        if (aType == "point" && bType == "polygon")
            return PointInPolygon(sourceCoordinates[0], targetCoordinates);

        if (aType == "polygon" && bType == "point")
            return PointInPolygon(targetCoordinates[0], sourceCoordinates);

        if (aType == "line" && bType == "line")
            return PolylineIntersectsPolyline(sourceCoordinates, targetCoordinates);

        if (aType == "line" && bType == "polygon")
            return PolylineIntersectsPolygon(sourceCoordinates, targetCoordinates);

        if (aType == "polygon" && bType == "line")
            return PolylineIntersectsPolygon(targetCoordinates, sourceCoordinates);

        return PolygonsOverlap(sourceCoordinates, targetCoordinates);
    }

    private static string NormaliseGeometryKind(string geoType, IReadOnlyList<double[]> coordinates)
    {
        var key = geoType.Trim().ToLowerInvariant();
        if (key.Contains("point")) return "point";
        if (key.Contains("line")) return "line";
        if (key.Contains("polygon")) return "polygon";
        if (coordinates.Count <= 1) return "point";
        return coordinates.Count >= 4 ? "polygon" : "line";
    }

    private static double[] GetGeometryCenter(IReadOnlyList<double[]> coordinates)
    {
        if (coordinates.Count == 0) return [0d, 0d];

        var minX = coordinates[0][0];
        var maxX = coordinates[0][0];
        var minY = coordinates[0][1];
        var maxY = coordinates[0][1];

        for (var i = 1; i < coordinates.Count; i++)
        {
            var c = coordinates[i];
            if (c[0] < minX) minX = c[0];
            if (c[0] > maxX) maxX = c[0];
            if (c[1] < minY) minY = c[1];
            if (c[1] > maxY) maxY = c[1];
        }

        return [minX + (maxX - minX) / 2d, minY + (maxY - minY) / 2d];
    }

    private static bool PointOnLine(double[] point, IReadOnlyList<double[]> line)
    {
        if (line.Count == 0) return false;
        if (line.Count == 1) return PointDistanceSquared(point, line[0]) <= 1.0;

        for (var i = 1; i < line.Count; i++)
        {
            if (DistancePointToSegmentSquared(point, line[i - 1], line[i]) <= 1.0)
                return true;
        }

        return false;
    }

    private static bool PolylineIntersectsPolyline(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (a.Count < 2 || b.Count < 2) return false;

        for (var i = 1; i < a.Count; i++)
        {
            for (var j = 1; j < b.Count; j++)
            {
                if (SegmentsIntersect(a[i - 1], a[i], b[j - 1], b[j]))
                    return true;
            }
        }

        return false;
    }

    private static bool PolylineIntersectsPolygon(IReadOnlyList<double[]> line, IReadOnlyList<double[]> polygon)
    {
        if (line.Count < 2 || polygon.Count < 3) return false;
        if (PointInPolygon(line[0], polygon)) return true;

        var polygonSegments = EnumerateSegments(polygon, closed: true);
        for (var i = 1; i < line.Count; i++)
        {
            var lineA = line[i - 1];
            var lineB = line[i];
            foreach (var segment in polygonSegments)
            {
                if (SegmentsIntersect(lineA, lineB, segment.A, segment.B))
                    return true;
            }
        }

        return false;
    }

    private static bool PolygonsOverlap(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        if (PointInPolygon(a[0], b) || PointInPolygon(b[0], a)) return true;

        foreach (var segA in EnumerateSegments(a, closed: true))
        {
            foreach (var segB in EnumerateSegments(b, closed: true))
            {
                if (SegmentsIntersect(segA.A, segA.B, segB.A, segB.B))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<(double[] A, double[] B)> EnumerateSegments(IReadOnlyList<double[]> coords, bool closed)
    {
        if (coords.Count < 2) yield break;
        for (var i = 1; i < coords.Count; i++)
            yield return (coords[i - 1], coords[i]);

        if (closed)
        {
            var first = coords[0];
            var last = coords[^1];
            if (PointDistanceSquared(first, last) > 1.0)
                yield return (last, first);
        }
    }

    private static bool PointInPolygon(double[] point, IReadOnlyList<double[]> polygon)
    {
        if (polygon.Count < 3) return false;

        var inside = false;
        var j = polygon.Count - 1;
        for (var i = 0; i < polygon.Count; i++)
        {
            var xi = polygon[i][0];
            var yi = polygon[i][1];
            var xj = polygon[j][0];
            var yj = polygon[j][1];

            var intersects = ((yi > point[1]) != (yj > point[1])) &&
                             (point[0] < (xj - xi) * (point[1] - yi) / ((yj - yi) + 1e-12) + xi);
            if (intersects)
                inside = !inside;

            j = i;
        }

        return inside;
    }

    private static bool SegmentsIntersect(double[] p1, double[] p2, double[] q1, double[] q2)
    {
        var o1 = Orientation(p1, p2, q1);
        var o2 = Orientation(p1, p2, q2);
        var o3 = Orientation(q1, q2, p1);
        var o4 = Orientation(q1, q2, p2);

        if (o1 != o2 && o3 != o4) return true;

        if (o1 == 0 && OnSegment(p1, q1, p2)) return true;
        if (o2 == 0 && OnSegment(p1, q2, p2)) return true;
        if (o3 == 0 && OnSegment(q1, p1, q2)) return true;
        if (o4 == 0 && OnSegment(q1, p2, q2)) return true;
        return false;
    }

    private static int Orientation(double[] p, double[] q, double[] r)
    {
        var value = (q[1] - p[1]) * (r[0] - q[0]) - (q[0] - p[0]) * (r[1] - q[1]);
        if (Math.Abs(value) < 1e-9) return 0;
        return value > 0 ? 1 : 2;
    }

    private static bool OnSegment(double[] p, double[] q, double[] r)
    {
        return q[0] <= Math.Max(p[0], r[0]) + 1e-9 && q[0] + 1e-9 >= Math.Min(p[0], r[0])
            && q[1] <= Math.Max(p[1], r[1]) + 1e-9 && q[1] + 1e-9 >= Math.Min(p[1], r[1]);
    }

    private static double DistancePointToSegmentSquared(double[] p, double[] a, double[] b)
    {
        var dx = b[0] - a[0];
        var dy = b[1] - a[1];
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
            return PointDistanceSquared(p, a);

        var t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / (dx * dx + dy * dy);
        t = Math.Max(0, Math.Min(1, t));
        var proj = new[] { a[0] + t * dx, a[1] + t * dy };
        return PointDistanceSquared(p, proj);
    }

    private static double PointDistanceSquared(double[] a, double[] b)
    {
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        return dx * dx + dy * dy;
    }

    private DateTime EditEarliestStart =>
        new DateTime(_gameStartYear, 1, 1).AddMonths(_gameCurrentMonth + _editMinConstructionMonths);

    private bool EditStartDateValid =>
        _editStartYear > EditEarliestStart.Year ||
        (_editStartYear == EditEarliestStart.Year && _editStartMonth >= EditEarliestStart.Month);

    // For the month <select>: months 1-12, but disable months before minimum when on the earliest year
    private bool EditMonthDisabled(int m) =>
        _editStartYear == EditEarliestStart.Year && m < EditEarliestStart.Month;

    private static string MonthName(int m) => m switch {
        1 => "January", 2 => "February", 3 => "March",    4 => "April",
        5 => "May",     6 => "June",     7 => "July",     8 => "August",
        9 => "September", 10 => "October", 11 => "November", 12 => "December",
        _ => m.ToString()
    };

    private async Task EnterEditModeAsync()
    {
        // Always fetch the latest plan from _plans
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan is null)
        {
            // Plan not found (maybe stale) — defer edit until next WS update
            _pendingEnterEditMode = true;
            _pendingSelectPlanId  = _selectedPlanId;
            StateHasChanged();
            return;
        }

        // Proactively check if plan is locked by another user
        if (plan.LockedByUserId != 0 && plan.LockedByUserId != SessionState.UserId)
        {
            _enterEditError = "This plan is currently being edited by another user.";
            StateHasChanged();
            return;
        }
        _enterEditError = null;

        var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
        var sessionPath = SessionState.SessionId.ToString();
        try
        {
            await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionPath}/api/Plan/Lock",
                new[]
                {
                    new KeyValuePair<string, string>("id",   _selectedPlanId.ToString()),
                    new KeyValuePair<string, string>("user", SessionState.UserId.ToString()),
                });
        }
        catch
        {
            // Lock failed (plan locked by someone else)
            _enterEditError = "This plan is currently being edited by another user.";
            StateHasChanged();
            return;
        }

        _editName        = plan.Name;
        _editDescription = plan.Description ?? string.Empty;
        var startDate    = new DateTime(_gameStartYear, 1, 1).AddMonths(plan.StartDate);
        _editStartYear   = startDate.Year;
        _editStartMonth  = startDate.Month;
        _editMinConstructionMonths = plan.ConstructionTime; // max AssemblyTime from layers
        _editPolicyTypes = plan.PolicyTypes.ToHashSet();
        _policyPickerOpen = false;
        _editPlanLayerIds = plan.Layers.Select(l => l.OriginalLayerId).ToHashSet();
        _layerPickerOpen  = false;
        _editError       = null;
        _editMode        = true;
    }

    private async Task CancelEditAsync()
    {
        _editSaving = true;
        StateHasChanged();
        try
        {
            if (_geometryToolLayerId is not null && _mapModule is not null)
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var sessionPath = SessionState.SessionId.ToString();
            await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionPath}/api/Plan/Unlock",
                new[]
                {
                    new KeyValuePair<string, string>("id",           _selectedPlanId.ToString()),
                    new KeyValuePair<string, string>("force_unlock", "0"),
                    new KeyValuePair<string, string>("user",         SessionState.UserId.ToString()),
                });
        }
        catch { }
        finally
        {
            _editMode         = false;
            _policyPickerOpen = false;
            _layerPickerOpen  = false;
            _geometryToolLayerId           = null;
            _geometryToolSelectedFeatureId = null;
            _drawingUndoStack.Clear();
            _drawingRedoStack.Clear();
            _pendingBatchGuid = null;
            _pendingCreatePlanCallId = 0;
            _editSaving = false;
            _editError  = null;
            StateHasChanged();
        }
    }

    private async Task ForceUnlockPlanAsync(int planId)
    {
        var confirmed = await JS.InvokeAsync<bool>("confirm",
            "Force unlock this plan? Any unsaved changes by the current editor will be lost.");
        if (!confirmed) return;
        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var sessionPath = SessionState.SessionId.ToString();
            await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionPath}/api/Plan/Unlock",
                new[]
                {
                    new KeyValuePair<string, string>("id",           planId.ToString()),
                    new KeyValuePair<string, string>("force_unlock", "1"),
                    new KeyValuePair<string, string>("user",         SessionState.UserId.ToString()),
                });
        }
        catch { }
    }

    private void TogglePolicyPicker()
    {
        _policyPickerOpen = !_policyPickerOpen;
        if (_policyPickerOpen)
        {
            _layerPickerOpen  = false;
            _planMessagesOpen = false;
            _planIssuesOpen   = false;
            _planApprovalOpen = false;
            _planStateOpen    = false;
        }
    }

    private void ToggleLayerPicker()
    {
        _layerPickerOpen = !_layerPickerOpen;
        if (_layerPickerOpen)
        {
            _geometryToolLayerId  = null;
            _policyPickerOpen = false;
            _planMessagesOpen = false;
            _planIssuesOpen   = false;
            _planApprovalOpen = false;
            _planStateOpen    = false;
        }
    }

    private async Task ToggleEditPlanLayerAsync(string layerId)
    {
        var wasAdded = !_editPlanLayerIds.Remove(layerId);
        if (wasAdded)
        {
            _editPlanLayerIds.Add(layerId);
            
            // Automatically show the layer on the map when adding it to the plan
            var layerEntry = _layerEntries.FirstOrDefault(e => e.LayerId == layerId);
            if (layerEntry is not null && !layerEntry.Visible)
            {
                await ToggleLayerAsync(layerEntry, true);
            }
        }
    }

    private void TogglePolicyType(string policyType)
    {
        if (!_editPolicyTypes.Remove(policyType))
            _editPolicyTypes.Add(policyType);
    }

    /// <summary>Closes the policy picker (Accept). API call is a stub — pending future work.</summary>
    private void AcceptPolicies() => _policyPickerOpen = false;

    private async Task SavePlanAsync()
    {
        if (_editSaving) return;
        if (!EditStartDateValid)
        {
            _editError = $"Start date must be {EditEarliestStart:MMM yyyy} or later.";
            StateHasChanged();
            return;
        }

        _editSaving = true;
        _editError  = null;
        StateHasChanged();

        try
        {
            var isNewPlan   = _selectedPlanId == 0;
            var plan        = isNewPlan ? null : _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
            if (!isNewPlan && plan is null)
            {
                _editError = "Plan not found.";
                return;
            }

            if (HostEnvironment.IsDevelopment())
            {
                Console.WriteLine($"[SavePlanAsync] Starting - IsNewPlan: {isNewPlan}, PlanId: {_selectedPlanId}, EditedLayer: {_geometryEditedLayerId ?? "(none)"}");
            }

            var baseAddress  = SessionState.GameServerAddress.TrimEnd('/');
            var sessionPath  = SessionState.SessionId.ToString();
            var userId       = SessionState.UserId.ToString();
            var countryInt   = SessionState.CountryId;
            var requests     = new List<object>();
            var callId       = 1;

            // ── For new plans: POST Plan/Post in group 1 ──────────────────
            // The new plan ID is referenced by other requests via "!Ref:N".
            int createPlanCallId = -1;
            if (isNewPlan)
            {
                createPlanCallId = callId++;
                requests.Add(new
                {
                    call_id       = createPlanCallId,
                    endpoint      = "api/Plan/Post",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { country = countryInt }),
                    group         = 1,
                });
            }

            // Helper: plan ID value (object: !Ref string for new, int for existing)
            object planIdVal = isNewPlan ? (object)$"!Ref:{createPlanCallId}" : _selectedPlanId;

            // ── Name, description, date (group 5 — same as BATCH_GROUP_PLAN_CHANGE) ─
            requests.Add(new
            {
                call_id       = callId++,
                endpoint      = "api/Plan/Name",
                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id   = planIdVal,
                    name = _editName ?? string.Empty,
                }),
                group = 5,
            });
            requests.Add(new
            {
                call_id       = callId++,
                endpoint      = "api/Plan/Description",
                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id          = planIdVal,
                    description = string.IsNullOrEmpty(_editDescription) ? " " : _editDescription,
                }),
                group = 5,
            });
            // StartDate is stored as month-offset from game start year
            var startMonthOffset = (_editStartYear - _gameStartYear) * 12 + _editStartMonth - 1;
            requests.Add(new
            {
                call_id       = callId++,
                endpoint      = "api/Plan/Date",
                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id   = planIdVal,
                    date = startMonthOffset,
                }),
                group = 5,
            });

            // ── Layer changes ─────────────────────────────────────────────
            var originalLayerIds = plan?.Layers
                                        .Select(l => l.OriginalLayerId)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                   ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layersToAdd    = _editPlanLayerIds.Except(originalLayerIds, StringComparer.OrdinalIgnoreCase).ToList();
            var layersToRemove = plan is not null
                ? originalLayerIds.Except(_editPlanLayerIds).ToList()
                : new List<string>();

            var newLayerCallIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var lid in layersToAdd)
            {
                if (!int.TryParse(lid, out var layerIdInt)) continue;
                var layerCallId = callId++;
                newLayerCallIds[lid] = layerCallId;
                requests.Add(new
                {
                    call_id       = layerCallId,
                    endpoint      = "api/Plan/Layer",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { id = planIdVal, layerid = layerIdInt }),
                    group         = 3,
                });
            }

            var planLayerIdByOriginal = plan?.Layers
                .ToDictionary(l => l.OriginalLayerId, l => l.LayerId, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lid in layersToRemove)
            {
                if (!planLayerIdByOriginal.TryGetValue(lid, out var plId)) continue;
                if (!int.TryParse(plId, out var plIdInt)) continue;
                requests.Add(new
                {
                    call_id       = callId++,
                    endpoint      = "api/Plan/DeleteLayer",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { id = plIdInt }),
                    group         = 3,
                });
            }

            // ── Fetch overlay features from JS ────────────────────────────
            OverlayFeatureDto[]? overlayItems = null;
            if (_geometryEditedLayerId is not null && _mapModule is not null)
            {
                if (HostEnvironment.IsDevelopment())
                    Console.WriteLine($"[SavePlanAsync] Fetching overlay features for layer {_geometryEditedLayerId}...");
                
                try
                {
                    var overlayJson = await _mapModule.InvokeAsync<string>(
                        "getOverlayFeaturesJson", 
                        _geometryEditedLayerId);
                    
                    if (HostEnvironment.IsDevelopment())
                        Console.WriteLine($"[SavePlanAsync] Received overlay JSON ({overlayJson?.Length ?? 0} chars)");
                    
                    overlayItems = System.Text.Json.JsonSerializer.Deserialize<OverlayFeatureDto[]>(
                        overlayJson,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (HostEnvironment.IsDevelopment())
                        Console.WriteLine($"[SavePlanAsync] Deserialized {overlayItems?.Length ?? 0} overlay items");
                }
                catch (TaskCanceledException tcex)
                {
                    if (HostEnvironment.IsDevelopment())
                    {
                        Console.WriteLine($"[SavePlanAsync] JS call was canceled!");
                        Console.WriteLine($"[SavePlanAsync] TaskCanceledException: {tcex.Message}");
                        Console.WriteLine($"[SavePlanAsync] Stack: {tcex.StackTrace}");
                    }
                    throw;
                }
                catch (Exception jsEx)
                {
                    if (HostEnvironment.IsDevelopment())
                    {
                        Console.WriteLine($"[SavePlanAsync] JS call failed: {jsEx.GetType().Name}");
                        Console.WriteLine($"[SavePlanAsync] Message: {jsEx.Message}");
                    }
                    throw;
                }
            }

            // ── Geometry changes (groups 5 / 10) ─────────────────────────
            if (_geometryEditedLayerId is not null && overlayItems is not null)
            {
                var editedLayerId = _geometryEditedLayerId;
                var planLayer     = plan?.Layers.FirstOrDefault(l =>
                    string.Equals(l.OriginalLayerId, editedLayerId, StringComparison.OrdinalIgnoreCase));

                // planLayerVal: object (!Ref string for new layers, int for existing)
                object? planLayerVal = null;
                if (planLayer is not null && int.TryParse(planLayer.LayerId, out var existingLayerIdInt))
                    planLayerVal = existingLayerIdInt;
                else if (newLayerCallIds.TryGetValue(editedLayerId, out var newLcid))
                    planLayerVal = $"!Ref:{newLcid}";



                if (planLayerVal is not null)
                {
                    var planOwnGeo = new Dictionary<string, PlanGeometryItem>(StringComparer.OrdinalIgnoreCase);
                    if (planLayer is not null)
                        foreach (var g in planLayer.Geometry) planOwnGeo[g.Id] = g;

                    // For new plans the world state is empty (plan.StartDate == 0 default), use offset
                    int planStartDate = plan?.StartDate ?? startMonthOffset;
                    var worldState    = GetProjectedLayerGeometries(editedLayerId, planStartDate);
                    var worldStateMap = new Dictionary<string, ParsedLayerGeometry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in worldState) worldStateMap[g.FeatureId] = g;

                    var overlayIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var item in overlayItems)
                    {
                        overlayIds.Add(item.FeatureId);

                        if (item.WorldStateId is not null)
                        {
                            // World-state feature — only include if coordinates changed
                            double[][]? origCoords = item.OriginalCoords;
                            if (origCoords is null && worldStateMap.TryGetValue(item.WorldStateId, out var wsGeo))
                                origCoords = wsGeo.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray();
                            if (origCoords is not null && !GeometryCoordsChanged(item.Coords, origCoords))
                                continue;

                            object persistentVal = int.TryParse(item.WorldStateId, out var wsInt)
                                ? (object)wsInt : item.WorldStateId;
                            var geoCallId = callId++;
                            requests.Add(new
                            {
                                call_id       = geoCallId,
                                endpoint      = "api/Geometry/Post",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    geometry   = System.Text.Json.JsonSerializer.Serialize(item.Coords),
                                    country    = countryInt,
                                    layer      = planLayerVal,
                                    plan       = planIdVal,
                                    persistent = persistentVal,
                                }),
                                group = 5,
                            });
                            requests.Add(new
                            {
                                call_id       = callId++,
                                endpoint      = "api/Geometry/Data",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    id   = $"!Ref:{geoCallId}",
                                    data = "",
                                    type = item.TypeIndex.ToString(),
                                }),
                                group = 10,
                            });
                        }
                        else if (planOwnGeo.TryGetValue(item.FeatureId, out var planGeo))
                        {
                            // Existing plan geometry — update only if coordinates changed
                            var origCoords = planGeo.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray();
                            if (!GeometryCoordsChanged(item.Coords, origCoords)) continue;

                            object featureIdVal = int.TryParse(item.FeatureId, out var fidInt)
                                ? (object)fidInt : item.FeatureId;
                            requests.Add(new
                            {
                                call_id       = callId++,
                                endpoint      = "api/Geometry/Update",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    id       = featureIdVal,
                                    country  = countryInt,
                                    geometry = System.Text.Json.JsonSerializer.Serialize(item.Coords),
                                }),
                                group = 5,
                            });
                        }
                        else
                        {
                            // New feature (TempId) — POST + Data
                            var geoCallId = callId++;
                            requests.Add(new
                            {
                                call_id       = geoCallId,
                                endpoint      = "api/Geometry/Post",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    geometry = System.Text.Json.JsonSerializer.Serialize(item.Coords),
                                    country  = countryInt,
                                    layer    = planLayerVal,
                                    plan     = planIdVal,
                                }),
                                group = 5,
                            });
                            requests.Add(new
                            {
                                call_id       = callId++,
                                endpoint      = "api/Geometry/Data",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    id   = $"!Ref:{geoCallId}",
                                    data = "",
                                    type = item.TypeIndex.ToString(),
                                }),
                                group = 10,
                            });
                        }
                    }

                    // Deleted plan-own geometry
                    foreach (var (geomId, _) in planOwnGeo)
                    {
                        if (overlayIds.Contains(geomId)) continue;
                        object featureIdVal = int.TryParse(geomId, out var gidInt)
                            ? (object)gidInt : geomId;
                        requests.Add(new
                        {
                            call_id       = callId++,
                            endpoint      = "api/Geometry/Delete",
                            endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { id = featureIdVal }),
                            group         = 4,
                        });
                    }

                    // Deleted world-state features
                    foreach (var wsId in _deletedWorldStateIds)
                    {
                        bool replaced = overlayItems.Any(o =>
                            string.Equals(o.WorldStateId, wsId, StringComparison.OrdinalIgnoreCase));
                        if (replaced) continue;

                        object wsIdVal = int.TryParse(wsId, out var wsInt2)
                            ? (object)wsInt2 : wsId;
                        requests.Add(new
                        {
                            call_id       = callId++,
                            endpoint      = "api/Geometry/MarkForDelete",
                            endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                id    = wsIdVal,
                                plan  = planIdVal,
                                layer = planLayerVal,
                            }),
                            group = 4,
                        });
                    }
                }
            }

            // ── Send the single batch ─────────────────────────────────────
            var batchJson = System.Text.Json.JsonSerializer.Serialize(requests);
            var batchGuid = Guid.NewGuid().ToString();
            
            if (HostEnvironment.IsDevelopment())
            {
                Console.WriteLine($"[SavePlanAsync] Sending batch with {requests.Count} requests");
                Console.WriteLine($"[SavePlanAsync] Batch JSON length: {batchJson.Length} chars");
                Console.WriteLine($"[SavePlanAsync] Batch GUID: {batchGuid}");
            }

            // Store batch info for new plan tracking
            if (isNewPlan)
            {
                _pendingBatchGuid = batchGuid;
                _pendingCreatePlanCallId = createPlanCallId;
            }

            await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionPath}/api/Batch/ExecuteBatch",
                new[]
                {
                    new KeyValuePair<string, string>("country_id", SessionState.CountryId.ToString()),
                    new KeyValuePair<string, string>("user_id",    userId),
                    new KeyValuePair<string, string>("batch_guid", batchGuid),
                    new KeyValuePair<string, string>("requests",   batchJson),
                });

            if (HostEnvironment.IsDevelopment())
                Console.WriteLine("[SavePlanAsync] Batch POST completed, waiting for WS confirmation...");

            // Batch accepted — wait for WS confirmation (up to 10 s)
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _editWsConfirmationTcs = tcs;
            _editSaving = false;
            StateHasChanged();

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10_000));

            if (HostEnvironment.IsDevelopment())
            {
                if (completedTask == tcs.Task)
                    Console.WriteLine("[SavePlanAsync] WS confirmation received");
                else
                    Console.WriteLine("[SavePlanAsync] WS confirmation timeout");
            }


            // ── Unlock existing plan (separate call AFTER batch completes) ──
            if (!isNewPlan)
            {

                await ApiClient.PostFormAsync(
                    $"{baseAddress}/{sessionPath}/api/Plan/Unlock",
                    new[]
                    {
                        new KeyValuePair<string, string>("id",           _selectedPlanId.ToString()),
                        new KeyValuePair<string, string>("force_unlock", "0"),
                        new KeyValuePair<string, string>("user",         SessionState.UserId.ToString()),
                    });
            }
            _editWsConfirmationTcs              = null;
            _geometryToolLayerId                = null;
            _geometryEditedLayerId              = null;
            _geometryToolSelectedFeatureId      = null;
            _geometryToolSelectedWorldStateId   = null;
            _drawingUndoStack.Clear();
            _drawingRedoStack.Clear();
            _deletedWorldStateIds.Clear();
            _editMode = false;

            // After save, keep plan selected in view mode
            // For new plans, _pendingBatchGuid and _pendingCreatePlanCallId are already set above
            // For existing plans, the plan is already selected, so no action needed
        }
        catch (Exception ex)
        {
            if (HostEnvironment.IsDevelopment())
            {
                Console.WriteLine($"[SavePlanAsync] EXCEPTION: {ex.GetType().Name}");
                Console.WriteLine($"[SavePlanAsync] Message: {ex.Message}");
                Console.WriteLine($"[SavePlanAsync] Stack: {ex.StackTrace}");
                if (ex.InnerException is not null)
                    Console.WriteLine($"[SavePlanAsync] Inner: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
            }

            var errorMsg = HostEnvironment.IsDevelopment() 
                ? $"Save failed: {ex.GetType().Name}: {ex.Message}" 
                : $"Save failed: {ex.Message}";
            
            _editError = errorMsg;
        }
        finally
        {
            _editSaving = false;
            StateHasChanged();
        }
    }


    private sealed record OverlayFeatureDto(
        string      FeatureId,
        int         TypeIndex,
        string?     WorldStateId,
        double[][]? OriginalCoords,
        double[][]  Coords);

    private abstract record DrawingAction;
    private sealed record MoveAction(
        string     FeatureId,
        string     LayerId,
        double[][] OldCoords,
        double[][] NewCoords,
        string?    WorldStateId) : DrawingAction;
    private sealed record AddAction(
        string     TempId,
        string     OriginalLayerId,
        int        TypeIndex,
        string     GeoType,
        double[][] Coords) : DrawingAction;
    private sealed record DeleteAction(
        string     FeatureId,
        string     OriginalLayerId,
        int        TypeIndex,
        string     GeoType,
        double[][] Coords,
        string?    WorldStateId) : DrawingAction;

    private sealed record ParsedLayerGeometry(
        string                  FeatureId,
        int                     TypeIndex,
        IReadOnlyList<double[]> Coordinates);

    private static bool GeometryCoordsChanged(double[][] a, double[][] b)
    {
        if (a.Length != b.Length) return true;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Length < 2 || b[i].Length < 2) return true;
            if (Math.Abs(a[i][0] - b[i][0]) > 1.0 || Math.Abs(a[i][1] - b[i][1]) > 1.0) return true;
        }
        return false;
    }

}
