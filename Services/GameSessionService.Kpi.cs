using System.Text.Json;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Partial class file containing KPI catalog parsing (from Game/PolicySimSettings) and incremental
/// value updates (from Game/Latest's simulation_updates), for the KPI Dashboard feature.
/// Energy/CEL is explicitly out of scope and skipped wherever encountered.
/// </summary>
public sealed partial class GameSessionService
{
    private const string CelSimulationType = "CEL";
    private const string MelSimulationType = "MEL";
    private const string SelSimulationType = "SEL";

    public IReadOnlyList<KpiCategoryState> EcologyKpiCategories { get; private set; } = [];
    public IReadOnlyList<KpiCategoryState> ShippingKpiCategories { get; private set; } = [];
    public IReadOnlyList<KpiCategoryState> OtherKpiCategories { get; private set; } = [];

    // Fast lookup for incremental WS updates: simulation_type -> (valueName -> state).
    private readonly Dictionary<string, Dictionary<string, KpiValueState>> _kpiValuesBySimType =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses the Game/PolicySimSettings response into the KPI category catalogs. CEL is skipped entirely.</summary>
    private void ParseKpiCatalog(JsonElement payload)
    {
        EcologyKpiCategories = [];
        ShippingKpiCategories = [];
        OtherKpiCategories = [];
        _kpiValuesBySimType.Clear();

        if (!payload.TryGetProperty("simulation_settings", out var simSettingsEl) ||
            simSettingsEl.ValueKind != JsonValueKind.Array)
            return;

        var otherCategories = new List<KpiCategoryState>();

        foreach (var simSetting in simSettingsEl.EnumerateArray())
        {
            var simType = ConversionUtils.GetStringProp(simSetting, "simulation_type");
            if (string.IsNullOrEmpty(simType) || simType.Equals(CelSimulationType, StringComparison.OrdinalIgnoreCase))
                continue; // Energy/CEL is explicitly out of scope.

            IReadOnlyList<KpiCategoryDefinition> definitions;
            if (simType.Equals(MelSimulationType, StringComparison.OrdinalIgnoreCase))
            {
                definitions = simSetting.TryGetProperty("content", out var contentEl) &&
                              contentEl.TryGetProperty("ecologyCategories", out var ecologyCategoriesEl)
                    ? ParseKpiCategoryDefinitions(ecologyCategoriesEl)
                    : [];
                var categories = BuildKpiCategoryStates(definitions, simType);
                EcologyKpiCategories = categories;
                RegisterKpiValueLookup(simType, categories);
            }
            else if (simType.Equals(SelSimulationType, StringComparison.OrdinalIgnoreCase))
            {
                definitions = simSetting.TryGetProperty("kpis", out var selKpisEl)
                    ? ParseKpiCategoryDefinitions(selKpisEl)
                    : [];
                var categories = BuildKpiCategoryStates(definitions, simType);
                ShippingKpiCategories = categories;
                RegisterKpiValueLookup(simType, categories);
            }
            else
            {
                definitions = simSetting.TryGetProperty("kpis", out var otherKpisEl)
                    ? ParseKpiCategoryDefinitions(otherKpisEl)
                    : [];
                var categories = BuildKpiCategoryStates(definitions, simType);
                otherCategories.AddRange(categories);
                RegisterKpiValueLookup(simType, categories);
            }
        }

        OtherKpiCategories = otherCategories;
    }

    private static List<KpiCategoryState> BuildKpiCategoryStates(
        IReadOnlyList<KpiCategoryDefinition> definitions, string owningSimulationType) =>
        definitions.Select(d => new KpiCategoryState(d, owningSimulationType)).ToList();

    private void RegisterKpiValueLookup(string simType, IReadOnlyList<KpiCategoryState> categories)
    {
        if (!_kpiValuesBySimType.TryGetValue(simType, out var lookup))
            _kpiValuesBySimType[simType] = lookup = new Dictionary<string, KpiValueState>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in categories)
        foreach (var value in category.Values)
            lookup[value.Definition.ValueName] = value;
    }

    private static List<KpiCategoryDefinition> ParseKpiCategoryDefinitions(JsonElement arrayEl)
    {
        var result = new List<KpiCategoryDefinition>();
        if (arrayEl.ValueKind != JsonValueKind.Array) return result;

        foreach (var categoryEl in arrayEl.EnumerateArray())
        {
            var categoryName = ConversionUtils.GetStringProp(categoryEl, "categoryName");
            if (string.IsNullOrEmpty(categoryName)) continue;

            var valueDefinitions = new List<KpiValueDefinition>();
            if (categoryEl.TryGetProperty("valueDefinitions", out var valueDefsEl) &&
                valueDefsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var valueEl in valueDefsEl.EnumerateArray())
                {
                    var valueName = ConversionUtils.GetStringProp(valueEl, "valueName");
                    if (string.IsNullOrEmpty(valueName)) continue;

                    valueDefinitions.Add(new KpiValueDefinition(
                        ValueName: valueName,
                        ValueDisplayName: ConversionUtils.GetStringProp(valueEl, "valueDisplayName"),
                        ValueColor: ConversionUtils.GetStringProp(valueEl, "valueColor") ?? "#3388ff",
                        Unit: ConversionUtils.GetStringProp(valueEl, "unit"),
                        ValueDependentCountry: ConversionUtils.GetNullableIntProp(valueEl, "valueDependentCountry") ?? -1));
                }
            }

            result.Add(new KpiCategoryDefinition(
                CategoryName: categoryName,
                CategoryDisplayName: ConversionUtils.GetStringProp(categoryEl, "categoryDisplayName"),
                CategoryColor: ConversionUtils.GetStringProp(categoryEl, "categoryColor") ?? "#3388ff",
                CategoryValueType: ConversionUtils.GetStringProp(categoryEl, "categoryValueType") ?? "Sum",
                Unit: ConversionUtils.GetStringProp(categoryEl, "unit"),
                ValueDefinitions: valueDefinitions));
        }

        return result;
    }

    /// <summary>Applies incremental KPI values from a Game/Latest message's simulation_updates array. CEL is skipped.</summary>
    private void ApplyKpiSimulationUpdates(JsonElement payload)
    {
        if (!payload.TryGetProperty("simulation_updates", out var simUpdatesEl) ||
            simUpdatesEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var simUpdate in simUpdatesEl.EnumerateArray())
        {
            var simType = ConversionUtils.GetStringProp(simUpdate, "simulation_type");
            if (string.IsNullOrEmpty(simType) || simType.Equals(CelSimulationType, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!_kpiValuesBySimType.TryGetValue(simType, out var lookup)) continue;
            if (!simUpdate.TryGetProperty("kpi", out var kpiArrEl) || kpiArrEl.ValueKind != JsonValueKind.Array) continue;

            foreach (var kpiRow in kpiArrEl.EnumerateArray())
            {
                var name = ConversionUtils.GetStringProp(kpiRow, "name");
                if (string.IsNullOrEmpty(name) || !lookup.TryGetValue(name, out var valueState)) continue;

                var month = ConversionUtils.GetIntProp(kpiRow, "month");
                var value = ConversionUtils.GetDoubleProp(kpiRow, "value");
                var country = ConversionUtils.GetNullableIntProp(kpiRow, "country") ?? -1;
                valueState.SetValue(month, country, value);
            }
        }
    }
}
