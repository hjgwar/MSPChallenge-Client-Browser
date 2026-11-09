using System.Text.Json;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Partial class file containing Energy/CEL domain parsing: per-plan grid definitions (from the
/// "energy" policy's grids[]), the merged registry of currently-implemented grids, the CEL
/// simulation output time series (per grid, per country), and the derived "Energy Output" KPI
/// category fed into the generic KPI Dashboard.
/// <para>
/// Energy intentionally has its own pipeline, separate from the generic catalog-driven one in
/// GameSessionService.Kpi.cs (which explicitly skips "CEL"): unlike Ecology/Shipping/Other, there
/// is no static catalog of named KPI values for Energy in Game/PolicySimSettings — a "grid" is a
/// player-created entity (declared by plans), so the set of things we can chart only grows as
/// plans get implemented. <see cref="KpiCategoryState.GetOrAddValue"/> exists specifically to let
/// this dynamic catalog still use the same generic widget/chart code as the static ones.
/// </para>
/// <para>
/// Reserved for later: per-geometry network topology/capacity ("policy_updates" with
/// policy_type "energy" — connections[]/output[]), to support showing live capacity on the map.
/// Not yet parsed or stored.
/// </para>
/// </summary>
public sealed partial class GameSessionService
{
    private const string EnergyOutputCategoryName = "energy_output";

    public IReadOnlyList<KpiCategoryState> EnergyKpiCategories { get; private set; } = [];

    /// <summary>Currently-implemented grids, keyed by persistent grid id (later plans win for the same id).</summary>
    private readonly Dictionary<int, EnergyGridDefinition> _energyGridRegistry = new();

    /// <summary>Raw simulated output per grid (keyed by persistent grid id), as reported by the CEL stream.</summary>
    private readonly Dictionary<int, MonthlyCountrySeries> _energyGridOutput = new();

    /// <summary>
    /// Parses one plan policy entry's "grids" array (only meaningful when its policy_type is
    /// "energy"). Called from the plan-parsing loop in GameSessionService.cs alongside the existing
    /// PolicyNames/PolicyTypes parsing.
    /// </summary>
    private static List<EnergyGridDefinition> ParseEnergyGrids(JsonElement policyEl)
    {
        var grids = new List<EnergyGridDefinition>();
        if (!policyEl.TryGetProperty("grids", out var gridsEl) || gridsEl.ValueKind != JsonValueKind.Array)
            return grids;

        foreach (var g in gridsEl.EnumerateArray())
        {
            var id = ConversionUtils.GetIntProp(g, "id");
            var persistentId = ConversionUtils.GetNullableIntProp(g, "persistent") ?? id;

            var countries = new List<EnergyGridCountryShare>();
            if (g.TryGetProperty("energy", out var energyEl) && energyEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in energyEl.EnumerateArray())
                {
                    var countryId = ConversionUtils.GetIntProp(e, "country_id");
                    if (countryId <= 0) continue;
                    countries.Add(new EnergyGridCountryShare(countryId, ConversionUtils.GetDoubleProp(e, "expected")));
                }
            }

            grids.Add(new EnergyGridDefinition(
                Id: id,
                PersistentId: persistentId,
                Name: ConversionUtils.GetStringProp(g, "name") ?? $"Grid {id}",
                Active: (ConversionUtils.GetStringProp(g, "active") ?? "1") != "0",
                DistributionOnly: (ConversionUtils.GetStringProp(g, "distribution_only") ?? "0") == "1",
                Countries: countries,
                SourceGeometryIds: ParseEnergyGeometryIds(g, "sources"),
                SocketGeometryIds: ParseEnergyGeometryIds(g, "sockets")));
        }
        return grids;
    }

    private static List<string> ParseEnergyGeometryIds(JsonElement gridEl, string propertyName)
    {
        var ids = new List<string>();
        if (!gridEl.TryGetProperty(propertyName, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var item in arrEl.EnumerateArray())
        {
            var geometryId = ConversionUtils.GetStringProp(item, "geometry_id");
            if (!string.IsNullOrEmpty(geometryId)) ids.Add(geometryId);
        }
        return ids;
    }

    /// <summary>
    /// Rebuilds <see cref="_energyGridRegistry"/> from every currently-implemented plan's grids
    /// (later plans win for the same persistent grid id — same precedence rule used for geometry),
    /// and ensures a placeholder value series exists for every country now holding a grid. Call
    /// whenever plan data changes (see ClearGeometryCaches).
    /// </summary>
    private void RebuildEnergyGridRegistry()
    {
        _energyGridRegistry.Clear();
        foreach (var plan in _plans
                     .Where(p => p.State == PlanState.IMPLEMENTED)
                     .OrderBy(p => p.StartDate).ThenBy(p => p.PlanId))
        {
            foreach (var grid in plan.EnergyGrids)
            {
                if (!grid.Active)
                {
                    _energyGridRegistry.Remove(grid.PersistentId);
                    continue;
                }
                _energyGridRegistry[grid.PersistentId] = grid;
            }
        }

        if (_energyGridRegistry.Count == 0) return;

        var category = EnsureEnergyOutputCategory();
        foreach (var countryId in _energyGridRegistry.Values.SelectMany(g => g.Countries.Select(c => c.CountryId)).Distinct())
            category.GetOrAddValue(BuildEnergyCountryValueDefinition(countryId));
    }

    /// <summary>
    /// Parses "simulation_updates" rows where simulation_type is "CEL" into the raw per-grid output
    /// store, then recomputes the per-country "Energy Output" totals for every (month, country) pair
    /// touched by this batch — summing across every grid currently in <see cref="_energyGridRegistry"/>
    /// that lists that country, since a shared/interconnector grid reports one row per country and a
    /// country can own multiple grids. Independent of "plan" being present, like ApplyKpiSimulationUpdates.
    /// </summary>
    private void ApplyEnergySimulationUpdates(JsonElement payload)
    {
        if (!payload.TryGetProperty("simulation_updates", out var simUpdatesEl) ||
            simUpdatesEl.ValueKind != JsonValueKind.Array)
            return;

        var touched = new HashSet<(int Month, int Country)>();

        foreach (var simUpdate in simUpdatesEl.EnumerateArray())
        {
            var simType = ConversionUtils.GetStringProp(simUpdate, "simulation_type");
            if (!string.Equals(simType, CelSimulationType, StringComparison.OrdinalIgnoreCase)) continue;
            if (!simUpdate.TryGetProperty("kpi", out var kpiArrEl) || kpiArrEl.ValueKind != JsonValueKind.Array) continue;

            foreach (var row in kpiArrEl.EnumerateArray())
            {
                var gridId = ConversionUtils.GetIntProp(row, "grid");
                if (gridId <= 0) continue;
                var countryId = ConversionUtils.GetIntProp(row, "country");
                if (countryId <= 0) continue;
                var month = ConversionUtils.GetIntProp(row, "month");
                var actual = ConversionUtils.GetDoubleProp(row, "actual");

                if (!_energyGridOutput.TryGetValue(gridId, out var series))
                    _energyGridOutput[gridId] = series = new MonthlyCountrySeries();
                series.SetValue(month, countryId, actual);

                touched.Add((month, countryId));
            }
        }

        if (touched.Count == 0) return;

        var category = EnsureEnergyOutputCategory();
        foreach (var (month, countryId) in touched)
        {
            var total = _energyGridRegistry.Values
                .Where(g => g.Countries.Any(c => c.CountryId == countryId))
                .Sum(g => _energyGridOutput.TryGetValue(g.PersistentId, out var s) ? s.GetValue(month, countryId) ?? 0 : 0);

            // Stored under the global (-1) key rather than countryId: this category's series are
            // already one-per-country (unlike catalog categories, whose series are one-per-metric,
            // filtered down to whichever country the dashboard tab is currently viewing), so every
            // series must resolve the same way regardless of the tab's (here: absent) country filter.
            category.GetOrAddValue(BuildEnergyCountryValueDefinition(countryId)).SetValue(month, -1, total);
        }
    }

    private KpiCategoryState EnsureEnergyOutputCategory()
    {
        var existing = EnergyKpiCategories.FirstOrDefault(c => c.Definition.CategoryName == EnergyOutputCategoryName);
        if (existing is not null) return existing;

        var definition = new KpiCategoryDefinition(
            CategoryName: EnergyOutputCategoryName,
            CategoryDisplayName: "Energy Output",
            CategoryColor: "#f0ad4e",
            CategoryValueType: "Sum",
            Unit: "W",
            UseSiUnitScaling: true);
        var created = new KpiCategoryState(definition, CelSimulationType);
        EnergyKpiCategories = [created];
        return created;
    }

    private KpiValueDefinition BuildEnergyCountryValueDefinition(int countryId)
    {
        var country = Countries.FirstOrDefault(c => c.Id == countryId);
        return new KpiValueDefinition(
            ValueName: countryId.ToString(),
            ValueDisplayName: country?.Name ?? $"Country {countryId}",
            ValueColor: country?.Color ?? "#3388ff",
            Unit: "W",
            ValueDependentCountry: countryId);
    }
}
