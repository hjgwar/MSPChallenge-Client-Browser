using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanLayers : GameComponentBase, IDisposable
{
    [Parameter] public Plan? Plan { get; set; }
    [Parameter] public bool EditSaving { get; set; }
    [Parameter] public HashSet<string> EditPlanLayerIds { get; set; } = [];
    [Parameter] public EventCallback<HashSet<string>> EditPlanLayerIdsChanged { get; set; }
    [Parameter] public MapViewPort? Map { get; set; }
    [Parameter] public EventCallback OnSubPanelOpening { get; set; }

    // Expose GameSessionState publicly for child components
    public new GameSessionState GameSessionState => base.GameSessionState;

    private bool _layerPickerOpen = false;

    /// <summary>Closes all sub-panels (layer picker + geometry tool). Called by PlanDetails for mutual exclusion.</summary>
    public void CloseSubPanel()
    {
        _layerPickerOpen = false;
        if (_geometryToolLayerId is not null && Map?.MapJSModule is not null)
            _ = Map.MapJSModule.InvokeVoidAsync("stopGeometryEditing");
        _geometryToolLayerId = null;
        StateHasChanged();
    }

    private string? _geometryToolLayerId;
    private string? _geometryEditedLayerId;
    public string? GeometryEditedLayerId => _geometryEditedLayerId;
    public bool _geometryToolCreate;
    public int _geometryToolTypeIndex;
    public string? _geometryToolSelectedFeatureId;
    private string? _geometryToolSelectedWorldStateId;
    private int _geometryToolSelectedTypeIndex;
    private int _geometryToolSelectedOriginalTypeIndex;
    private double[][] _geometryToolSelectedCoords = [];
    public bool _geometryToolSelectedIsMarkedForDeletion;
    public List<DrawingAction> _drawingUndoStack = [];
    public List<DrawingAction> _drawingRedoStack = [];
    private HashSet<string> _deletedWorldStateIds = [];
    public HashSet<string> DeletedWorldStateIds => _deletedWorldStateIds;

    private DotNetObjectReference<PlanLayers>? _dotNetRef;

    private Plan _detailPlan => Plan ?? new Plan(
        0, "", "", Models.PlanState.DESIGN, 0, 0, 0, [], [], [], false, 0, 0, null, 0);

    private List<string> _detailLayers
    {
        get
        {
            if (Plan == null) return [];
            return Plan.Layers
                .Select(l => GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == l.OriginalLayerId)?.DisplayName)
                .OfType<string>()
                .Distinct()
                .ToList();
        }
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    private async Task OpenGeometryToolAsync(string layerId)
    {
        if (_geometryToolLayerId == layerId)
        {
            await CloseGeometryToolAsync();
            return;
        }
        // Opening a geometry tool: close all other sub-panels in PlanDetails first.
        await OnSubPanelOpening.InvokeAsync();
        _layerPickerOpen = false;
        if (Map?.MapJSModule is null) return;
        _geometryToolLayerId = layerId;
        _geometryEditedLayerId = layerId;
        _geometryToolTypeIndex = 0;
        _geometryToolCreate = false;
        _geometryToolSelectedFeatureId = null;
        _geometryToolSelectedWorldStateId = null;
        _geometryToolSelectedTypeIndex = 0;
        _geometryToolSelectedCoords = [];
        _drawingUndoStack.Clear();
        _drawingRedoStack.Clear();
        _deletedWorldStateIds.Clear();

        await Map.MapJSModule.InvokeVoidAsync("startGeometryEdit", layerId, _dotNetRef);

        var plan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        var planLayer = plan?.Layers.FirstOrDefault(l => 
            string.Equals(l.OriginalLayerId, layerId, StringComparison.OrdinalIgnoreCase));
        if (planLayer?.DeletedPersistentIds != null)
        {
            foreach (var deletedId in planLayer.DeletedPersistentIds)
                _deletedWorldStateIds.Add(deletedId);
        }

        var isNewPlan = GameSessionState.SelectedPlanId == 0;
        int startDate = isNewPlan 
            ? (GameSessionState.GameStartYear > 0 ? (DateTime.UtcNow.Year - GameSessionState.GameStartYear) * 12 + DateTime.UtcNow.Month - 1 : 0)
            : (plan?.StartDate ?? 0);

        var worldState = GameSessionState.GetProjectedLayerGeometries(layerId, startDate);
        var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
        var geoType = layerEntry?.GeoType ?? "polygon";

        var planOwnIds = plan?.Layers
            .FirstOrDefault(l => string.Equals(l.OriginalLayerId, layerId, StringComparison.OrdinalIgnoreCase))
            ?.Geometry
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>();

        var toLoad = worldState
            .Where(g => !planOwnIds.Contains(g.FeatureId) && g.Coordinates.Count > 0)
            .Select(g => new
            {
                id = g.FeatureId,
                layerId,
                geoType,
                typeIndex = g.TypeIndex,
                coords = g.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray(),
            })
            .ToList();

        if (toLoad.Count > 0)
            await Map.MapJSModule.InvokeVoidAsync("loadWorldStateFeatures",
                JsonSerializer.Serialize(toLoad));

        foreach (var deletedId in _deletedWorldStateIds)
        {
            await Map.MapJSModule.InvokeVoidAsync("markFeatureAsDeleted", deletedId);
        }

        if (planLayer is not null)
        {
            foreach (var geo in planLayer.Geometry)
            {
                if (!string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id)
                {
                    await Map.MapJSModule.InvokeVoidAsync("hideBaseGeometry", layerId, geo.PersistentId);
                }
            }
        }

        StateHasChanged();
    }

    public async Task CloseGeometryToolAsync()
    {
        if (Map?.MapJSModule is not null)
        {
            await Map.MapJSModule.InvokeVoidAsync("stopGeometryEditing");
            if (_geometryToolLayerId is not null)
                await Map.MapJSModule.InvokeVoidAsync("removeUnchangedWorldStateFeatures", _geometryToolLayerId);
        }
        _geometryToolLayerId = null;
        _geometryToolSelectedFeatureId = null;
        _geometryToolSelectedWorldStateId = null;
        _geometryToolSelectedTypeIndex = 0;
        _geometryToolSelectedCoords = [];
        _drawingUndoStack.Clear();
        _drawingRedoStack.Clear();
        StateHasChanged();
    }

    public async Task SetGeometryModeCreateAsync()
    {
        if (Map?.MapJSModule is null || _geometryToolLayerId is null) return;
        var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
        _geometryToolCreate = true;
        _geometryToolSelectedFeatureId = null;
        await Map.MapJSModule.InvokeVoidAsync("startGeometryCreate",
            _geometryToolLayerId, layerEntry?.GeoType ?? "polygon", _geometryToolTypeIndex, _dotNetRef);
        StateHasChanged();
    }

    public async Task SetGeometryModeEditAsync()
    {
        if (Map?.MapJSModule is null || _geometryToolLayerId is null) return;
        _geometryToolCreate = false;
        _geometryToolSelectedFeatureId = null;
        await Map.MapJSModule.InvokeVoidAsync("startGeometryEdit", _geometryToolLayerId, _dotNetRef);
        StateHasChanged();
    }

    public async Task SetGeometryTypeIndexAsync(int idx)
    {
        if (_geometryToolSelectedFeatureId is not null && !_geometryToolCreate && idx != _geometryToolSelectedTypeIndex)
        {
            var oldType = _geometryToolSelectedTypeIndex;
            _geometryToolTypeIndex = idx;
            _geometryToolSelectedTypeIndex = idx;

            if (idx != _geometryToolSelectedOriginalTypeIndex || _geometryToolSelectedWorldStateId is not null)
            {
                _drawingUndoStack.Add(new TypeChangeAction(
                    _geometryToolSelectedFeatureId, oldType, idx, _geometryToolSelectedWorldStateId,
                    _geometryToolSelectedWorldStateId is not null ? _geometryToolSelectedOriginalTypeIndex : null));
                _drawingRedoStack.Clear();

                if (Map?.MapJSModule is not null)
                    await Map.MapJSModule.InvokeVoidAsync("setFeatureType", _geometryToolSelectedFeatureId, idx);

                if (_geometryToolSelectedWorldStateId is not null && _geometryToolLayerId is not null && Map?.MapJSModule is not null)
                {
                    var isOriginal = await Map.MapJSModule.InvokeAsync<bool>("isFeatureInOriginalState", _geometryToolSelectedFeatureId);
                    if (isOriginal)
                    {
                        await UpdateGeometryBadgeAsync(_geometryToolSelectedFeatureId, "none");
                        await Map.MapJSModule.InvokeVoidAsync("unhideBaseGeometry", _geometryToolLayerId, _geometryToolSelectedWorldStateId);
                    }
                    else
                    {
                        await UpdateGeometryBadgeAsync(_geometryToolSelectedFeatureId, "edit");
                        await Map.MapJSModule.InvokeVoidAsync("hideBaseGeometry", _geometryToolLayerId, _geometryToolSelectedWorldStateId);
                    }
                }
            }
            StateHasChanged();
            return;
        }

        _geometryToolTypeIndex = idx;
        if (_geometryToolCreate && Map?.MapJSModule is not null && _geometryToolLayerId is not null)
        {
            var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
            await Map.MapJSModule.InvokeVoidAsync("startGeometryCreate",
                _geometryToolLayerId, layerEntry?.GeoType ?? "polygon", idx, _dotNetRef);
        }
        StateHasChanged();
    }

    public async Task ToggleGeometryTypeBitAsync(int bitIndex)
    {
        _geometryToolTypeIndex ^= 1 << bitIndex;
        if (_geometryToolCreate && Map?.MapJSModule is not null && _geometryToolLayerId is not null)
        {
            var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
            await Map.MapJSModule.InvokeVoidAsync("startGeometryCreate",
                _geometryToolLayerId, layerEntry?.GeoType ?? "polygon", _geometryToolTypeIndex, _dotNetRef);
        }
        StateHasChanged();
    }

    public async Task UndoDrawingActionAsync()
    {
        if (_drawingUndoStack.Count == 0 || Map?.MapJSModule is null) return;
        var action = _drawingUndoStack[^1];
        _drawingUndoStack.RemoveAt(_drawingUndoStack.Count - 1);
        _drawingRedoStack.Add(action);
        await ApplyDrawingActionAsync(action, undo: true);
        StateHasChanged();
    }

    public async Task RedoDrawingActionAsync()
    {
        if (_drawingRedoStack.Count == 0 || Map?.MapJSModule is null) return;
        var action = _drawingRedoStack[^1];
        _drawingRedoStack.RemoveAt(_drawingRedoStack.Count - 1);
        _drawingUndoStack.Add(action);
        await ApplyDrawingActionAsync(action, undo: false);
        StateHasChanged();
    }

    private async Task ApplyDrawingActionAsync(DrawingAction action, bool undo)
    {
        if (Map?.MapJSModule is null) return;
        switch (action)
        {
            case MoveAction m:
                var coords = undo ? m.OldCoords : m.NewCoords;
                await Map.MapJSModule.InvokeVoidAsync("setFeatureCoords", m.FeatureId, coords);
                if (m.WorldStateId is not null && m.LayerId is not null)
                {
                    if (undo)
                    {
                        await Map.MapJSModule.InvokeVoidAsync("checkAndUnhideIfOriginal", m.LayerId, m.FeatureId);
                        var isOriginal = await Map.MapJSModule.InvokeAsync<bool>("isFeatureInOriginalState", m.FeatureId);
                        await UpdateGeometryBadgeAsync(m.FeatureId, isOriginal ? "none" : "edit");
                    }
                    else
                    {
                        await UpdateGeometryBadgeAsync(m.FeatureId, "edit");
                        await Map.MapJSModule.InvokeVoidAsync("hideBaseGeometry", m.LayerId, m.WorldStateId);
                    }
                }
                break;
            case AddAction a:
                if (undo)
                {
                    await UpdateGeometryBadgeAsync(a.TempId, "none");
                    await Map.MapJSModule.InvokeVoidAsync("removeFeatureFromOverlay", a.TempId);
                }
                else
                {
                    await Map.MapJSModule.InvokeVoidAsync("addFeatureToOverlay", JsonSerializer.Serialize(new
                    {
                        featureId = a.TempId, originalLayerId = a.OriginalLayerId,
                        typeIndex = a.TypeIndex, geoType = a.GeoType, coords = a.Coords
                    }));
                    await UpdateGeometryBadgeAsync(a.TempId, "plus");
                }
                break;
            case DeleteAction d:
                if (undo)
                {
                    if (d.WorldStateId is not null)
                    {
                        _deletedWorldStateIds.Remove(d.WorldStateId);
                        await Map.MapJSModule.InvokeVoidAsync("unmarkFeatureAsDeleted", d.FeatureId);
                        if (!d.WasModified && d.OriginalLayerId is not null)
                        {
                            await Map.MapJSModule.InvokeVoidAsync("checkAndUnhideIfOriginal", d.OriginalLayerId, d.FeatureId);
                            var isOriginal = await Map.MapJSModule.InvokeAsync<bool>("isFeatureInOriginalState", d.FeatureId);
                            await UpdateGeometryBadgeAsync(d.FeatureId, isOriginal ? "none" : "edit");
                        }
                        else
                        {
                            await UpdateGeometryBadgeAsync(d.FeatureId, "edit");
                        }
                    }
                    else
                    {
                        await Map.MapJSModule.InvokeVoidAsync("addFeatureToOverlay", JsonSerializer.Serialize(new
                        {
                            featureId = d.FeatureId, originalLayerId = d.OriginalLayerId,
                            typeIndex = d.TypeIndex, geoType = d.GeoType, coords = d.Coords
                        }));
                        await UpdateGeometryBadgeAsync(d.FeatureId, "plus");
                    }
                }
                else
                {
                    if (d.WorldStateId is not null)
                    {
                        _deletedWorldStateIds.Add(d.WorldStateId);
                        await Map.MapJSModule.InvokeVoidAsync("markFeatureAsDeleted", d.FeatureId);
                    }
                    else
                    {
                        await UpdateGeometryBadgeAsync(d.FeatureId, "none");
                        await Map.MapJSModule.InvokeVoidAsync("removeFeatureFromOverlay", d.FeatureId);
                    }
                }
                break;
            case TypeChangeAction t:
                var typeIndex = undo ? t.OldTypeIndex : t.NewTypeIndex;
                await Map.MapJSModule.InvokeVoidAsync("setFeatureType", t.FeatureId, typeIndex);
                if (t.WorldStateId is not null && _geometryToolLayerId is not null)
                {
                    if (undo)
                    {
                        await Map.MapJSModule.InvokeVoidAsync("checkAndUnhideIfOriginal", _geometryToolLayerId, t.FeatureId);
                        var isOriginal = await Map.MapJSModule.InvokeAsync<bool>("isFeatureInOriginalState", t.FeatureId);
                        await UpdateGeometryBadgeAsync(t.FeatureId, isOriginal ? "none" : "edit");
                    }
                    else
                    {
                        await UpdateGeometryBadgeAsync(t.FeatureId, "edit");
                        await Map.MapJSModule.InvokeVoidAsync("hideBaseGeometry", _geometryToolLayerId, t.WorldStateId);
                    }
                }
                break;
        }
    }

    public async Task DeleteSelectedGeometryAsync()
    {
        if (_geometryToolSelectedFeatureId is null || _geometryToolLayerId is null || Map?.MapJSModule is null) return;
        var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == _geometryToolLayerId);
        var worldStateId = _geometryToolSelectedWorldStateId;
        var wasModified = worldStateId is not null 
            && !await Map.MapJSModule.InvokeAsync<bool>("isFeatureInOriginalState", _geometryToolSelectedFeatureId);
        _drawingUndoStack.Add(new DeleteAction(
            _geometryToolSelectedFeatureId, _geometryToolLayerId,
            _geometryToolSelectedTypeIndex, layerEntry?.GeoType ?? "polygon",
            _geometryToolSelectedCoords, worldStateId, wasModified));
        _drawingRedoStack.Clear();
        if (worldStateId is not null)
        {
            _deletedWorldStateIds.Add(worldStateId);
            await Map.MapJSModule.InvokeVoidAsync("markFeatureAsDeleted", _geometryToolSelectedFeatureId);
        }
        else
        {
            await UpdateGeometryBadgeAsync(_geometryToolSelectedFeatureId, "none");
            await Map.MapJSModule.InvokeVoidAsync("removeFeatureFromOverlay", _geometryToolSelectedFeatureId);
        }
        _geometryToolSelectedFeatureId = null;
        _geometryToolSelectedWorldStateId = null;
        StateHasChanged();
    }

    public async Task RestoreSelectedGeometryAsync()
    {
        if (_geometryToolSelectedFeatureId is null || _geometryToolSelectedWorldStateId is null 
            || _geometryToolLayerId is null || Map?.MapJSModule is null || !_geometryToolSelectedIsMarkedForDeletion) return;

        _deletedWorldStateIds.Remove(_geometryToolSelectedWorldStateId);
        await Map.MapJSModule.InvokeVoidAsync("unmarkFeatureAsDeleted", _geometryToolSelectedFeatureId);

        var isOriginal = await Map.MapJSModule.InvokeAsync<bool>("isFeatureInOriginalState", _geometryToolSelectedFeatureId);
        if (isOriginal)
        {
            await UpdateGeometryBadgeAsync(_geometryToolSelectedFeatureId, "none");
            await Map.MapJSModule.InvokeVoidAsync("unhideBaseGeometry", _geometryToolLayerId, _geometryToolSelectedWorldStateId);
        }
        else
        {
            await UpdateGeometryBadgeAsync(_geometryToolSelectedFeatureId, "edit");
            await Map.MapJSModule.InvokeVoidAsync("hideBaseGeometry", _geometryToolLayerId, _geometryToolSelectedWorldStateId);
        }

        _geometryToolSelectedIsMarkedForDeletion = false;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnGeometryModified(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var featureId = root.GetProperty("featureId").GetString() ?? "";
            var worldStateId = root.TryGetProperty("worldStateId", out var wsEl) && wsEl.ValueKind != JsonValueKind.Null
                ? wsEl.GetString() : null;
            var oldCoords = ParseCoordArray(root.GetProperty("oldCoords"));
            var newCoords = ParseCoordArray(root.GetProperty("newCoords"));
            _drawingUndoStack.Add(new MoveAction(featureId, _geometryToolLayerId ?? "", oldCoords, newCoords, worldStateId));
            _drawingRedoStack.Clear();
            if (worldStateId is not null && !string.IsNullOrEmpty(featureId))
            {
                InvokeAsync(async () =>
                {
                    await UpdateGeometryBadgeAsync(featureId, "edit");
                    if (_geometryToolLayerId is not null && Map?.MapJSModule is not null)
                        await Map.MapJSModule.InvokeVoidAsync("hideBaseGeometry", _geometryToolLayerId, worldStateId);
                    StateHasChanged();
                });
            }
            else
            {
                InvokeAsync(StateHasChanged);
            }
        }
        catch { }
    }

    [JSInvokable]
    public void OnGeometryCreated(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tempId = root.GetProperty("tempId").GetString() ?? "";
            var layerId = root.TryGetProperty("originalLayerId", out var lidEl) ? lidEl.GetString() ?? (_geometryToolLayerId ?? "") : (_geometryToolLayerId ?? "");
            var typeIndex = root.TryGetProperty("typeIndex", out var tiEl) ? tiEl.GetInt32() : _geometryToolTypeIndex;
            var geoType = root.TryGetProperty("geoType", out var gtEl) ? gtEl.GetString() ?? "polygon" : "polygon";
            var coords = ParseCoordArray(root.GetProperty("coords"));
            _drawingUndoStack.Add(new AddAction(tempId, layerId, typeIndex, geoType, coords));
            _drawingRedoStack.Clear();
            if (!string.IsNullOrEmpty(tempId))
            {
                InvokeAsync(async () =>
                {
                    await UpdateGeometryBadgeAsync(tempId, "plus");
                    StateHasChanged();
                });
            }
            else
            {
                InvokeAsync(StateHasChanged);
            }
        }
        catch { }
    }

    [JSInvokable]
    public void OnGeometrySelected(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("featureId", out var fidEl)
                && fidEl.ValueKind != JsonValueKind.Null
                && fidEl.GetString() is { } fid)
            {
                _geometryToolSelectedFeatureId = fid;
                _geometryToolSelectedTypeIndex = root.TryGetProperty("typeIndex", out var tiEl) ? tiEl.GetInt32() : 0;
                _geometryToolSelectedOriginalTypeIndex = _geometryToolSelectedTypeIndex;
                _geometryToolSelectedCoords = root.TryGetProperty("coords", out var ceEl) ? ParseCoordArray(ceEl) : [];
                _geometryToolSelectedWorldStateId = root.TryGetProperty("worldStateId", out var wsEl) && wsEl.ValueKind != JsonValueKind.Null
                    ? wsEl.GetString() : null;
                _geometryToolSelectedIsMarkedForDeletion = _geometryToolSelectedWorldStateId is not null
                    && _deletedWorldStateIds.Contains(_geometryToolSelectedWorldStateId);
                _geometryToolTypeIndex = _geometryToolSelectedTypeIndex;
            }
            else
            {
                _geometryToolSelectedFeatureId = null;
                _geometryToolSelectedWorldStateId = null;
                _geometryToolSelectedIsMarkedForDeletion = false;
            }
            InvokeAsync(StateHasChanged);
        }
        catch { }
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

    private async Task UpdateGeometryBadgeAsync(string featureId, string badgeType)
    {
        if (Map?.MapJSModule is null || string.IsNullOrEmpty(featureId)) return;
        try
        {
            await Map.MapJSModule.InvokeVoidAsync("updateFeatureBadge", featureId, badgeType);
        }
        catch { }
    }

    private async Task ToggleLayerPickerAsync()
    {
        if (!_layerPickerOpen)
            await OnSubPanelOpening.InvokeAsync();
        _layerPickerOpen = !_layerPickerOpen;
        if (_layerPickerOpen)
        {
            if (_geometryToolLayerId is not null && Map?.MapJSModule is not null)
                await Map.MapJSModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
    }

    private async Task ToggleEditLayerAsync(string layerId)
    {
        var wasAdded = !EditPlanLayerIds.Remove(layerId);
        if (wasAdded)
        {
            EditPlanLayerIds.Add(layerId);
            var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
            if (layerEntry is not null && !layerEntry.Visible && Map is not null)
            {
                await Map.ToggleLayerAsync(layerEntry, true);
            }
        }
        await EditPlanLayerIdsChanged.InvokeAsync(EditPlanLayerIds);
    }

    public void Dispose()
    {
        _dotNetRef?.Dispose();
    }

    // Drawing action records
    public abstract record DrawingAction;
    private sealed record MoveAction(
        string FeatureId, string LayerId, double[][] OldCoords, double[][] NewCoords, string? WorldStateId) : DrawingAction;
    private sealed record AddAction(
        string TempId, string OriginalLayerId, int TypeIndex, string GeoType, double[][] Coords) : DrawingAction;
    private sealed record DeleteAction(
        string FeatureId, string OriginalLayerId, int TypeIndex, string GeoType, double[][] Coords,
        string? WorldStateId, bool WasModified) : DrawingAction;
    private sealed record TypeChangeAction(
        string FeatureId, int OldTypeIndex, int NewTypeIndex, string? WorldStateId, int? OriginalWorldStateTypeIndex = null) : DrawingAction;
}