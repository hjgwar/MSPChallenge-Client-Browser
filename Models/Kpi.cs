namespace MSPChallenge_Client_Browser.Models;

/// <summary>One KPI value definition within a category, from the session's Game/PolicySimSettings catalog.</summary>
public sealed record KpiValueDefinition(
    string ValueName,
    string? ValueDisplayName = null,
    string ValueColor = "#3388ff",
    string? Unit = null,
    int ValueDependentCountry = -1);

/// <summary>One KPI category definition (one dashboard widget's data source), from the Game/PolicySimSettings catalog.</summary>
public sealed record KpiCategoryDefinition(
    string CategoryName,
    string? CategoryDisplayName = null,
    string CategoryColor = "#3388ff",
    string CategoryValueType = "Sum",
    string? Unit = null,
    IReadOnlyList<KpiValueDefinition>? ValueDefinitions = null)
{
    public IReadOnlyList<KpiValueDefinition> ValueDefinitions { get; init; } = ValueDefinitions ?? [];
}

/// <summary>Accumulates per-(month, country) values received over the WebSocket for one KPI value definition.</summary>
public sealed class KpiValueState
{
    public KpiValueDefinition Definition { get; }
    private readonly Dictionary<(int Month, int Country), double> _values = new();

    public int MostRecentMonth { get; private set; } = -1;

    public KpiValueState(KpiValueDefinition definition)
    {
        Definition = definition;
    }

    public void SetValue(int month, int country, double value)
    {
        _values[(month, country)] = value;
        if (month > MostRecentMonth) MostRecentMonth = month;
    }

    /// <summary>Resolves the value for a month/country, falling back to the global (country -1) value if none exists.</summary>
    public double? GetValue(int month, int countryId)
    {
        if (_values.TryGetValue((month, countryId), out var value)) return value;
        if (countryId != -1 && _values.TryGetValue((month, -1), out var global)) return global;
        return null;
    }
}

/// <summary>One KPI category's live state: its definition plus the accumulated state of each child value.</summary>
public sealed class KpiCategoryState
{
    public KpiCategoryDefinition Definition { get; }

    /// <summary>The simulation_type this category came from (e.g. "MEL", "SEL", or a custom external name).</summary>
    public string OwningSimulationType { get; }
    public IReadOnlyList<KpiValueState> Values { get; }

    /// <summary>Stable identity for this category, used to reference it from KpiWidgetInstance without holding a direct object reference.</summary>
    public string CategoryKey => $"{OwningSimulationType}:{Definition.CategoryName}";

    public KpiCategoryState(KpiCategoryDefinition definition, string owningSimulationType)
    {
        Definition = definition;
        OwningSimulationType = owningSimulationType;
        Values = definition.ValueDefinitions.Select(v => new KpiValueState(v)).ToList();
    }

    public KpiValueState? FindValue(string valueName) =>
        Values.FirstOrDefault(v => string.Equals(v.Definition.ValueName, valueName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Grid position/size of a KPI widget on its dashboard tab, in cell units.</summary>
public sealed class KpiWidgetLayout
{
    // Wide enough for a legend of four entries per row; tall enough for chart + up to three legend rows.
    public const int DefaultWidth = 4;
    public const int DefaultHeight = 3;
    public const int DefaultWidgetsPerRow = 2;

    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = DefaultWidth;
    public int H { get; set; } = DefaultHeight;

    /// <summary>Default left-to-right, top-to-bottom placement of the widget at <paramref name="index"/>.</summary>
    public static KpiWidgetLayout ForIndex(int index) => new()
    {
        X = (index % DefaultWidgetsPerRow) * DefaultWidth,
        Y = (index / DefaultWidgetsPerRow) * DefaultHeight,
    };
}

/// <summary>
/// Per-circuit UI state for one widget instance shown on a KPI dashboard tab.
/// A favourited widget is represented by a second, independent instance in the Favourites list
/// (same <see cref="CategoryKey"/>, its own <see cref="Position"/>) rather than a dual-position single instance.
/// </summary>
public sealed class KpiWidgetInstance
{
    /// <summary>Identifies the <see cref="KpiCategoryState"/> this widget displays (see KpiCategoryState.CategoryKey).</summary>
    public string CategoryKey { get; }
    public KpiWidgetLayout Position { get; set; } = new();
    public bool IsFavourite { get; set; }

    /// <summary>Null on either end means "show the full available range" (session start month .. current month).</summary>
    public int? SelectedStartMonth { get; set; }
    public int? SelectedEndMonth { get; set; }
    public bool Stacked { get; set; }

    /// <summary>ValueNames of the bars the player filtered out; empty means every bar is shown.</summary>
    public HashSet<string> HiddenValueNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public KpiWidgetInstance(string categoryKey)
    {
        CategoryKey = categoryKey;
    }
}
