using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Utils.PlanCalculations;

/// <summary>
/// Service that calculates which country teams need to approve a plan based on the approval type
/// defined per layer_type (AllCountries / EEZ / NotDependent) and ownership of removed geometry
/// derived from EEZ polygon intersection.
/// </summary>
public class PlanApproval
{
    /// <summary>
    /// Computes which country teams need to approve the plan and for what reasons.
    /// </summary>
    /// <param name="plan">The plan to calculate approval for</param>
    /// <param name="planCountry">The country ID that owns the plan</param>
    /// <param name="eezPolygons">EEZ polygon data for determining geometry ownership</param>
    /// <param name="layerEntries">Layer configuration data including approval modes</param>
    /// <param name="countryNames">Mapping of country IDs to names</param>
    /// <param name="baseGeometryProvider">Function to get base geometries for a layer</param>
    /// <returns>List of approval requirements grouped by country</returns>
    public List<PlanApprovalRequirement> CalculateApproval(
        PlanEntry plan,
        int planCountry,
        IReadOnlyList<EezPolygon> eezPolygons,
        IReadOnlyList<LayerEntry> layerEntries,
        IReadOnlyDictionary<int, string> countryNames,
        Func<string, IReadOnlyList<ParsedGeometry>> baseGeometryProvider)
    {
        // countryId → list of reason strings
        var reasons = new Dictionary<int, List<string>>();
        var allCountriesReasons = new List<string>(); // reasons that apply to every country

        void AddReason(int countryId, string reason)
        {
            if (countryId <= 0 || countryId == planCountry) return;
            if (!reasons.TryGetValue(countryId, out var list))
            {
                list = new List<string>();
                reasons[countryId] = list;
            }
            if (!list.Contains(reason)) list.Add(reason);
        }

        foreach (var planLayer in plan.Layers)
        {
            var layerEntry = layerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            var layerDisplayName = layerEntry?.DisplayName ?? planLayer.OriginalLayerId;

            // ── Deleted geometry ──────────────────────────────────────────
            if (planLayer.DeletedPersistentIds.Count > 0)
            {
                var baseGeoms = baseGeometryProvider(planLayer.OriginalLayerId);
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
                            {
                                var countryName = GetCountryName(owner, countryNames);
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
                            var countryName = GetCountryName(eez.CountryId, countryNames);
                            AddReason(eez.CountryId, $"Geometry on the {layerDisplayName} layer was added or altered in {countryName}'s EEZ.");
                        }
                    }
                }
            }
        }

        // AllCountries: add those reasons to every non-owner country team
        if (allCountriesReasons.Count > 0)
        {
            foreach (var kvp in countryNames)
            {
                if (kvp.Key <= 2 || kvp.Key == planCountry) continue; // skip admin/GM slots
                foreach (var r in allCountriesReasons)
                    AddReason(kvp.Key, r);
            }
        }

        // Build sorted result
        var result = new List<PlanApprovalRequirement>();
        foreach (var kvp in reasons.OrderBy(k => k.Key))
        {
            var name = GetCountryName(kvp.Key, countryNames);
            result.Add(new PlanApprovalRequirement(kvp.Key, name, kvp.Value));
        }

        return result;
    }

    private static string GetApprovalForGeom(LayerEntry? layerEntry, int typeIndex)
    {
        if (layerEntry is null) return "NotDependent";
        if (typeIndex >= 0 && typeIndex < layerEntry.TypeDefs.Count)
            return layerEntry.TypeDefs[typeIndex].Approval;
        // Fallback: if there is exactly one type, use that regardless of index
        if (layerEntry.TypeDefs.Count == 1)
            return layerEntry.TypeDefs[0].Approval;
        return "NotDependent";
    }

    public static bool IsApprovalCompleteState(PlanState state) =>
        state.Equals(PlanState.APPROVED) ||
        state.Equals(PlanState.IMPLEMENTED) ||
        state.Equals(PlanState.ARCHIVED);


}

