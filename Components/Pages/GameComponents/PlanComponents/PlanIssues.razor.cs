using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanIssues : GameComponentBase, IDisposable
{
    [Parameter] public MapViewPort? Map { get; set; }
    [Parameter] public List<PlanRestrictionIssue> SelectedPlanIssues { get; set; } = [];
    [Parameter] public EventCallback<List<PlanRestrictionIssue>> SelectedPlanIssuesChanged { get; set; }

    [Parameter] public EventCallback OnSubPanelOpening { get; set; }

    private bool _planIssuesOpen = false;

    public int SelectedPlanIssueCount => SelectedPlanIssues.Count;

    /// <summary>Closes this sub-panel and clears map markers. Called by PlanDetails when another panel is opened.</summary>
    public void CloseSubPanel()
    {
        _planIssuesOpen = false;
        if (Map?.MapJSModule is not null)
            _ = Map.MapJSModule.InvokeVoidAsync("clearIssueMarkers");
        StateHasChanged();
    }

    public async Task TogglePlanIssuesPanel()
    {
        if (!_planIssuesOpen)
        {
            await OnSubPanelOpening.InvokeAsync();
            _planIssuesOpen = true;
            await ShowIssueMarkersOnMapAsync();
        }
        else
        {
            _planIssuesOpen = false;
            await ClearIssueMarkersFromMapAsync();
        }
        StateHasChanged();
    }

    private async Task ShowIssueMarkersOnMapAsync()
    {
        if (Map?.MapJSModule is null || SelectedPlanIssues.Count == 0) return;
        var markersJson = System.Text.Json.JsonSerializer.Serialize(
            SelectedPlanIssues.Select(i => new { x = i.MarkerX, y = i.MarkerY, severity = i.Severity }));
        await Map.MapJSModule.InvokeVoidAsync("showIssueMarkers", markersJson);
    }

    private async Task ClearIssueMarkersFromMapAsync()
    {
        if (Map?.MapJSModule is null) return;
        await Map.MapJSModule.InvokeVoidAsync("clearIssueMarkers");
    }

    // Fingerprint of the last plan we calculated issues for.
    // Format: "{planId}:{startDate}:{totalGeometryCount}"
    // Only changes when relevant plan data changes, so we avoid recalculating on every WS heartbeat.
    private string _lastIssuePlanFingerprint = string.Empty;

    private string GetPlanFingerprint()
    {
        var plan = GameSessionState.SelectedPlan;
        if (plan is null) return string.Empty;
        var totalGeometry = plan.Layers.Sum(l => l.Geometry.Count);
        return $"{plan.PlanId}:{plan.StartDate}:{totalGeometry}";
    }

    protected override void OnInitialized()
    {
        GameSessionState.Changed += OnStateChanged;
        _ = CalculatePlanIssues();
    }

    public void Dispose() => GameSessionState.Changed -= OnStateChanged;

    private void OnStateChanged()
    {
        var fingerprint = GetPlanFingerprint();
        if (fingerprint == _lastIssuePlanFingerprint) return;
        _ = InvokeAsync(async () =>
        {
            await CalculatePlanIssues();
            // If the issues panel is open, refresh the map markers with updated locations.
            if (_planIssuesOpen)
                await ShowIssueMarkersOnMapAsync();
            StateHasChanged();
        });
    }

    private async Task CalculatePlanIssues()
    {
        _lastIssuePlanFingerprint = GetPlanFingerprint();

        if (GameSessionState.SelectedPlan is null)
        {
            SelectedPlanIssues = [];
            await SelectedPlanIssuesChanged.InvokeAsync(SelectedPlanIssues);
            return;
        }

        var issues = EvaluatePlanRestrictions(GameSessionState.SelectedPlan);
        SelectedPlanIssues = issues.ToList();
        await SelectedPlanIssuesChanged.InvokeAsync(SelectedPlanIssues);
    }

    /// <summary>
    /// Evaluates all restriction rules against the given plan and returns a sorted list of issues.
    /// </summary>
    private IReadOnlyList<PlanRestrictionIssue> EvaluatePlanRestrictions(Plan plan)
    {
        List<PlanRestrictionIssue> selectedPlanIssues = [];

        foreach (var planLayer in plan.Layers)
        {
            Layer? sourceLayer = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            foreach (var geometry in planLayer.Geometry)
            {
                var isNewGeometry = string.IsNullOrEmpty(geometry.PersistentId) || geometry.Id == geometry.PersistentId;
                var geometryIssues = EvaluateRestrictionsForGeometry(
                    GameSessionState.Restrictions,
                    GameSessionState.Plans,
                    sourceLayer,
                    geometry,
                    isNewGeometry,
                    plan.StartDate);
                selectedPlanIssues.AddRange(geometryIssues);
            }
        }

        selectedPlanIssues.Sort((a, b) =>
        {
            var s = GenericUtils.SeveritySortRank(b.Severity).CompareTo(GenericUtils.SeveritySortRank(a.Severity));
            if (s != 0) return s;
            s = string.Compare(a.TargetLayer, b.TargetLayer, StringComparison.OrdinalIgnoreCase);
            return s != 0 ? s : string.Compare(a.Message, b.Message, StringComparison.OrdinalIgnoreCase);
        });

        return selectedPlanIssues;
    }

    private IReadOnlyList<PlanRestrictionIssue> EvaluateRestrictionsForGeometry(
        IReadOnlyList<RestrictionRule> rules,
        IReadOnlyList<Plan> allPlans,
        Layer? sourceLayer,
        PlanGeometryItem geometry,
        bool isNewGeometry,
        int planStartDate)
    {
        if (rules.Count == 0 || geometry.Coordinates.Count == 0)
            return [];

        if (sourceLayer is null)
            return [];

        var matches = new List<PlanRestrictionIssue>();
        // Deduplicate: same message at the same map position is considered one issue regardless
        // of whether it comes from duplicate restriction rules or coincident target geometries.
        var seenIssueKeys = new HashSet<(string severity, string message, string src, string tgt, double x, double y)>();

        foreach (var rule in rules)
        {
            if (!RuleLayerMatches(rule.StartLayer, sourceLayer)
                || !RestrictionTypeMatches(rule.StartType, geometry.TypeIndex, sourceLayer))
                continue;

            var targetLayer = FindLayerByRuleName(rule.EndLayer);
            var targetType = rule.EndType;

            if (targetLayer is null || targetLayer.IsRaster)
                continue;

            var targetGeometries = GameSessionState.GetProjectedLayerGeometries(targetLayer.LayerId, planStartDate);
            var constraintSort = NormaliseConstraintSort(rule.Sort);
            var sourceMarker = GeometryUtils.GetGeometryCenter(geometry.Coordinates);
            var overlapFound = false;

            foreach (var targetGeometry in targetGeometries)
            {
                if (!RestrictionTypeMatches(targetType, targetGeometry.TypeIndex, targetLayer))
                    continue;

                if (!GeometryUtils.HasOverlap(sourceLayer.GeoType, geometry.Coordinates, targetLayer.GeoType, targetGeometry.Coordinates))
                    continue;

                overlapFound = true;

                // Unity inclusion constraints emit one issue per overlapping target geometry.
                if (constraintSort == "EXCLUSION")
                    continue;

                var severity = GenericUtils.NormaliseSeverity(rule.Type);
                if (string.IsNullOrEmpty(severity))
                    severity = "WARNING";
                var message = string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Overlap with {targetLayer.DisplayName}"
                    : rule.Message;
                var marker = GeometryUtils.GetGeometryCenter(targetGeometry.Coordinates);
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
                var severity = GenericUtils.NormaliseSeverity(rule.Type);
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

    // ── Layer lookup helpers ──────────────────────────────────────────────────

    private Layer? FindLayerByRuleName(string? ruleLayer)
    {
        if (string.IsNullOrWhiteSpace(ruleLayer))
            return null;

        var normalizedRule = GenericUtils.NormaliseToken(ruleLayer);
        var exact = GameSessionState.LayerEntries.FirstOrDefault(layer =>
            normalizedRule == GenericUtils.NormaliseToken(layer.LayerId)
            || normalizedRule == GenericUtils.NormaliseToken(layer.LayerName)
            || normalizedRule == GenericUtils.NormaliseToken(layer.DisplayName));
        if (exact is not null)
            return exact;

        return GameSessionState.LayerEntries.FirstOrDefault(layer => RuleLayerMatches(ruleLayer, layer));
    }

    private static bool RuleLayerMatches(string? ruleLayer, Layer layer)
    {
        if (string.IsNullOrWhiteSpace(ruleLayer))
            return false;

        if (string.Equals(ruleLayer, "*", StringComparison.Ordinal))
            return true;

        var normalizedRuleLayer = GenericUtils.NormaliseToken(ruleLayer);
        var layerId = GenericUtils.NormaliseToken(layer.LayerId);
        var layerName = GenericUtils.NormaliseToken(layer.LayerName);
        var displayName = GenericUtils.NormaliseToken(layer.DisplayName);

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

    private static bool RestrictionTypeMatches(string? ruleType, int actualTypeIndex, Layer? layer)
    {
        if (string.IsNullOrWhiteSpace(ruleType))
            return true;

        var normalizedRuleType = GenericUtils.NormaliseToken(ruleType);
        if (normalizedRuleType is "*" or "any" or "all")
            return true;

        if (int.TryParse(ruleType, out var index))
            return index == actualTypeIndex;

        if (normalizedRuleType == actualTypeIndex.ToString(CultureInfo.InvariantCulture))
            return true;

        if (layer is null || actualTypeIndex < 0 || actualTypeIndex >= layer.TypeDefs.Count)
            return false;

        var label = layer.TypeDefs[actualTypeIndex].Label;
        return normalizedRuleType == GenericUtils.NormaliseToken(label);
    }

    private static string NormaliseConstraintSort(string? rawSort)
    {
        if (string.IsNullOrWhiteSpace(rawSort)) return "INCLUSION";
        var key = GenericUtils.NormaliseToken(rawSort);
        return key switch
        {
            "0" or "inclusion" => "INCLUSION",
            "1" or "exclusion" => "EXCLUSION",
            "2" or "typeunavailable" => "TYPE_UNAVAILABLE",
            _ => key.ToUpperInvariant()
        };
    }
}