using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanApproval : GameComponentBase, IDisposable
{
    [Parameter] public bool SelectedPlanApprovalRequired { get; set; } = false;
    [Parameter] public EventCallback<bool> SelectedPlanApprovalRequiredChanged { get; set; }
    private List<PlanApprovalRequirement> _approvalRequired = [];
    private readonly Dictionary<string, List<ParsedGeometry>> _parsedLayerGeometryCache = [];
    [Parameter] public EventCallback OnSubPanelOpening { get; set; }

    private bool _planApprovalOpen = false;

    /// <summary>Closes this sub-panel. Called by PlanDetails when another panel is opened.</summary>
    public void CloseSubPanel()
    {
        _planApprovalOpen = false;
        StateHasChanged();
    }

    /// <summary>Toggle the approval panel open/closed.</summary>
    public async Task ToggleApprovalPanel()
    {
        if (!_planApprovalOpen)
            await OnSubPanelOpening.InvokeAsync();
        _planApprovalOpen = !_planApprovalOpen;
        StateHasChanged();
    }

    protected override void OnInitialized()
    {
        GameSessionService.Changed += OnStateChanged;
        _ = CalculateApproval();
    }

    private void OnStateChanged()
    {
        // Recalculate whenever the server sends updated plan data — covers the
        // initial load as well as updates that arrive after a plan edit is saved.
        _ = CalculateApproval();
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        GameSessionService.Changed -= OnStateChanged;
    }

    /// <summary>
    /// Computes which country teams need to approve the plan and for what reasons,
    /// based on the approval type defined per layer_type (AllCountries / EEZ / NotDependent)
    /// and ownership of removed geometry derived from EEZ polygon intersection.
    /// </summary>
    private async Task CalculateApproval()
    {
        if (GameSessionService.SelectedPlan is null) return;

        _approvalRequired.Clear();
        var reasons = new Dictionary<int, List<string>>(); // countryId → list of reason strings
        var allCountriesReasons = new List<string>(); // reasons that apply to every country

        void AddReason(int countryId, string reason)
        {
            if (countryId <= 0 || countryId == GameSessionService.SelectedPlan.Country) return;
            if (!reasons.TryGetValue(countryId, out var list))
            {
                list = new List<string>();
                reasons[countryId] = list;
            }
            if (!list.Contains(reason)) list.Add(reason);
        }

        foreach (var planLayer in GameSessionService.SelectedPlan.Layers)
        {
            var layerEntry = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            var layerDisplayName = layerEntry?.DisplayName ?? planLayer.OriginalLayerId;

            // ── Deleted geometry ────────────────────────────────────────── 
            if (planLayer.DeletedPersistentIds.Count > 0)
            {
                var baseGeoms = GetParsedLayerGeometries(planLayer.OriginalLayerId);
                foreach (var deletedId in planLayer.DeletedPersistentIds)
                {
                    var geom = baseGeoms.FirstOrDefault(g => g.FeatureId == deletedId);
                    if (geom is null) continue;

                    var typeApproval = GetApprovalForGeom(layerEntry, geom.TypeIndex);
                    var typeLabel = (layerEntry is not null && geom.TypeIndex >= 0 && geom.TypeIndex < layerEntry.TypeDefs.Count)
                        ? layerEntry.TypeDefs[geom.TypeIndex].Label
                        : "";

                    if (string.Equals(typeApproval, "AllCountries", StringComparison.OrdinalIgnoreCase))
                    {
                        var r = string.IsNullOrEmpty(typeLabel)
                            ? $"Geometry was removed on the {layerDisplayName} layer, which requires approval from all countries."
                            : $"Geometry of type {typeLabel} was removed, which requires approval from all countries.";
                        if (!allCountriesReasons.Contains(r)) allCountriesReasons.Add(r);
                    }
                    else
                    {
                        // Derive ownership from EEZ intersection of the representative coordinate
                        if (geom.Coordinates.Count > 0)
                        {
                            var owner = GeometryUtils.GetCountryForCoordinate(geom.Coordinates[0], GameSessionService.EezPolygons);
                            if (owner > 0 && owner != GameSessionService.SelectedPlan.Country)
                            {
                                var ownerCountry = GameSessionService.Countries.FirstOrDefault(c => c.Id == owner);
                                var countryName = ownerCountry?.Name ?? $"Country {owner}";
                                AddReason(owner, $"Geometry belonging to {countryName} was removed on the {layerDisplayName} layer.");
                            }
                        }
                    }
                }
            }

            // ── New / modified geometry ─────────────────────────────────── 
            foreach (var geomItem in planLayer.Geometry)
            {
                var typeApproval = GetApprovalForGeom(layerEntry, geomItem.TypeIndex);
                var typeLabel = (layerEntry is not null && geomItem.TypeIndex >= 0 && geomItem.TypeIndex < layerEntry.TypeDefs.Count)
                    ? layerEntry.TypeDefs[geomItem.TypeIndex].Label
                    : "";

                if (string.Equals(typeApproval, "AllCountries", StringComparison.OrdinalIgnoreCase))
                {
                    var r = string.IsNullOrEmpty(typeLabel)
                        ? $"Geometry was added or moved on the {layerDisplayName} layer, which requires approval from all countries."
                        : $"Geometry of type {typeLabel} was added or moved, which requires approval from all countries.";
                    if (!allCountriesReasons.Contains(r)) allCountriesReasons.Add(r);
                }
                else if (string.Equals(typeApproval, "EEZ", StringComparison.OrdinalIgnoreCase)
                         && geomItem.Coordinates.Count > 0)
                {
                    var pt = geomItem.Coordinates[0];
                    foreach (var eez in GameSessionService.EezPolygons)
                    {
                        if (eez.CountryId != GameSessionService.SelectedPlan.Country && GeometryUtils.PointInPolygon(pt, eez.Points))
                        {
                            var eezCountry = GameSessionService.Countries.FirstOrDefault(c => c.Id == eez.CountryId);
                            var countryName = eezCountry?.Name ?? $"Country {eez.CountryId}";
                            AddReason(eez.CountryId, $"Geometry on the {layerDisplayName} layer was added or altered in {countryName}'s EEZ.");
                        }
                    }
                }
            }
        }

        // AllCountries: add those reasons to every non-owner country team
        if (allCountriesReasons.Count > 0)
        {
            foreach (var country in GameSessionService.Countries)
            {
                if (country.Id <= 2 || country.Id == GameSessionService.SelectedPlan.Country) continue; // skip admin/GM slots
                foreach (var r in allCountriesReasons)
                    AddReason(country.Id, r);
            }
        }

        // Build sorted result
        foreach (var kvp in reasons.OrderBy(k => k.Key))
        {
            var country = GameSessionService.Countries.FirstOrDefault(c => c.Id == kvp.Key);
            var name = country?.Name ?? $"Country {kvp.Key}";
            _approvalRequired.Add(new PlanApprovalRequirement(kvp.Key, name, kvp.Value));
        }
        SelectedPlanApprovalRequired = _approvalRequired.Count > 0;
        await SelectedPlanApprovalRequiredChanged.InvokeAsync(SelectedPlanApprovalRequired);
    }

    // ── Geometry parsing ──────────────────────────────────────────────────────

    private List<ParsedGeometry> GetParsedLayerGeometries(string layerId)
    {
        if (_parsedLayerGeometryCache.TryGetValue(layerId, out var cached))
            return cached;

        var parsed = new List<ParsedGeometry>();
        _parsedLayerGeometryCache[layerId] = parsed;

        var snapshot = GameSessionService.MapLayerSnapshots.FirstOrDefault(s =>
            string.Equals(s.LayerId, layerId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.VectorGeometriesJson))
            return parsed;

        try
        {
            using var document = JsonDocument.Parse(snapshot.VectorGeometriesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return parsed;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("geometry", out var geometryElement) ||
                    geometryElement.ValueKind != JsonValueKind.Array)
                    continue;

                var coordinates = new List<double[]>();
                foreach (var pointElement in geometryElement.EnumerateArray())
                {
                    if (pointElement.ValueKind != JsonValueKind.Array || pointElement.GetArrayLength() < 2)
                        continue;

                    if (pointElement[0].ValueKind != JsonValueKind.Number ||
                        pointElement[1].ValueKind != JsonValueKind.Number)
                        continue;

                    coordinates.Add([pointElement[0].GetDouble(), pointElement[1].GetDouble()]);
                }

                if (coordinates.Count == 0)
                    continue;

                var featureId = "";
                if (element.TryGetProperty("id", out var idElement))
                    featureId = idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? ""
                        : idElement.ToString();

                var typeIndex = 0;
                if (element.TryGetProperty("type", out var typeElement))
                {
                    if (typeElement.ValueKind == JsonValueKind.Number)
                        typeIndex = typeElement.GetInt32();
                    else if (typeElement.ValueKind == JsonValueKind.String)
                        int.TryParse(typeElement.GetString(), out typeIndex);
                }

                parsed.Add(new ParsedGeometry(featureId, typeIndex, coordinates));
            }
        }
        catch
        {
            // Ignore malformed cached geometry and return empty list
        }

        return parsed;
    }

    // ── Helper methods ────────────────────────────────────────────────────────

    private static string GetApprovalForGeom(Layer? layerEntry, int typeIndex)
    {
        if (layerEntry is null) return "NotDependent";
        if (typeIndex >= 0 && typeIndex < layerEntry.TypeDefs.Count)
            return layerEntry.TypeDefs[typeIndex].Approval;
        // Fallback: if there is exactly one type, use that regardless of index
        if (layerEntry.TypeDefs.Count == 1)
            return layerEntry.TypeDefs[0].Approval;
        return "NotDependent";
    }
}