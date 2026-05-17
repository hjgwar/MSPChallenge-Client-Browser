namespace MSPChallenge_Client_Browser.Models;

public sealed class MapLayerSnapshot
{
    public string LayerId { get; init; } = "";
    public string GeoType { get; init; } = "";

    public bool Visible { get; init; }
    public int Depth { get; init; }
    public bool IsBaseLayer { get; init; }

    // Vector payload
    public string? VectorGeometriesJson { get; init; }
    public IReadOnlyList<string> TypeColors { get; init; } = [];
    public string? LabelKey { get; init; }

    // Raster payload
    public string? RasterImageData { get; init; }
    public double[][]? RasterProjBounds { get; init; }
    public IReadOnlyList<RasterColorStop> RasterColorMap { get; init; } = [];
    public double? RasterMinCutoffNorm { get; init; }
    public bool RasterInterpolate { get; init; } = true;
}

public sealed class RasterColorStop
{
    public double Value { get; init; }
    public int[] Rgba { get; init; } = [];
}
