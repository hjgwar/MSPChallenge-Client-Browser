namespace MSPChallenge_Client_Browser.Models;

public sealed class Layer
{
    public string LayerId     { get; init; } = "";
    public string LayerName   { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Category    { get; init; } = "";
    public string Subcategory { get; init; } = "";
    public string Tooltip     { get; init; } = "";
    public string? MediaUrl   { get; init; }
    public bool   IsBaseLayer  { get; init; }
    public bool   IsToggleable { get; init; }
    public bool   Visible     { get; set; }
    public int    Depth       { get; init; }
    public IReadOnlyList<TypeDef> TypeDefs { get; init; } = [];
    public IReadOnlyDictionary<string, string> PropertyDisplayNames { get; init; } = new Dictionary<string, string>();

    // Raster-specific: normalised thresholds (0–255) paired with display labels, sorted ascending.
    // Non-empty only for raster layers.
    public bool IsRaster { get; init; }
    public IReadOnlyList<(double NormalisedThreshold, string Label)> RasterThresholds { get; init; } = [];

    // The geometry type of this layer: "polygon", "line", "point", "raster", etc.
    public string GeoType { get; init; } = "";

    // Number of months of construction (ASSEMBLY state time); 0 means no construction phase.
    public int AssemblyTime { get; init; }

    // Whether this layer can be added to a plan for editing.
    public bool Editable { get; init; }

    // Editing type: "multitype" means multiple geometry types can be selected simultaneously (bitmask).
    public string EditingType { get; init; } = "";

    public bool IsMultiType => EditingType.Equals("multitype", StringComparison.OrdinalIgnoreCase);
}

public sealed record TypeDef(string Label, string Color, string? MediaUrl = null, string Approval = "NotDependent");

