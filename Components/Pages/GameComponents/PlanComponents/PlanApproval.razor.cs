using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanApproval : PlanComponentBase
{
    [Parameter] public PlanEntry SelectedPlan { get; set; } = null!;

    private List<ApprovalRequirement> _approvalRequired = [];
    private Dictionary<int, List<string>> _reasons = new();

    protected override void OnInitialized()
    {
        CalculateApproval();
    }

    /// <summary>
    /// Computes which country teams need to approve the plan and for what reasons,
    /// based on the approval type defined per layer_type (AllCountries / EEZ / NotDependent)
    /// and ownership of removed geometry derived from EEZ polygon intersection.
    /// </summary>
    private void CalculateApproval()
    {
        _approvalRequired.Clear();
        _reasons.Clear();

        var eezPolygons = GameSessionState.EezPolygons;
        var planCountry = SelectedPlan.Country;

        // countryId â†’ list of reason strings
        var allCountriesReasons = new List<string>(); // reasons that apply to every country

        foreach (var planLayer in SelectedPlan.Layers)
        {
            var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            var layerDisplayName = layerEntry?.DisplayName ?? planLayer.OriginalLayerId;

            // Deleted geometry 
            if (planLayer.DeletedPersistentIds.Count > 0)
            {
                var baseGeoms = GetParsedLayerGeometries(planLayer.OriginalLayerId);
                foreach (var deletedId in planLayer.DeletedPersistentIds)
                {
                    var geom = baseGeoms.FirstOrDefault(g => g.FeatureId == deletedId);
                    if (geom is null) continue;

                    var typeApproval = GetApprovalForGeom(layerEntry, geom.TypeIndex);
                    var typeLabel    = GetTypeLabel(layerEntry, geom.TypeIndex);

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
                            var owner = GetCountryForCoordinate(geom.Coordinates[0], eezPolygons);
                            if (owner > 0 && owner != planCountry)
                                AddReason(owner, $"Geometry belonging to {GameSessionState.Countries.FirstOrDefault(c => c.Id == owner)?.Name ?? "Unknown"} was removed on the {layerDisplayName} layer.");
                        }
                    }
                }
            }

            // New / modified geometry 
            foreach (var geomItem in planLayer.Geometry)
            {
                var typeApproval = GetApprovalForGeom(layerEntry, geomItem.TypeIndex);
                var typeLabel    = GetTypeLabel(layerEntry, geomItem.TypeIndex);

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
                    foreach (var eez in eezPolygons)
                    {
                        if (eez.CountryId != planCountry && PointInPolygon(pt, eez.Points))
                        {
                            var countryName = GameSessionState.Countries.FirstOrDefault(c => c.Id == eez.CountryId)?.Name ?? "Unknown";
                            AddReason(eez.CountryId, $"Geometry on the {layerDisplayName} layer was added or altered in {countryName}'s EEZ.");
                        }
                    }
                }
            }
        }

        // AllCountries: add those reasons to every non-owner country team
        if (allCountriesReasons.Count > 0)
        {
            foreach (var kvp in GameSessionState.CountryNames)
            {
                if (kvp.Key <= 2 || kvp.Key == planCountry) continue; // skip admin/GM slots
                foreach (var r in allCountriesReasons)
                    AddReason(kvp.Key, r);
            }
        }

        // Build sorted result
        foreach (var kvp in _reasons.OrderBy(k => k.Key))
        {
            var name = GameSessionState.Countries.FirstOrDefault(c => c.Id == kvp.Key)?.Name ?? "Unknown";
            _approvalRequired.Add(new ApprovalRequirement(kvp.Key, name, kvp.Value));
        }
    }

    private void AddReason(int countryId, string reason)
    {
        if (countryId <= 0 || countryId == planCountry) return;
        if (!reasons.TryGetValue(countryId, out var list))
        {
            list = new List<string>();
            reasons[countryId] = list;
        }
        if (!list.Contains(reason)) list.Add(reason);
    }
}