namespace MSPChallenge_Client_Browser.Models;

internal sealed class LayerEntry
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
}
