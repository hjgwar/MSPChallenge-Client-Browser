namespace MSPChallenge_Client_Browser.Models;

/// <summary>Parsed geometry from base layer data.</summary>
public sealed record ParsedGeometry(
    string                  FeatureId,
    int                     TypeIndex,
    IReadOnlyList<double[]> Coordinates);

/// <summary>One EEZ polygon with its owning country ID. Points are [lon, lat] pairs.</summary>
public sealed record EezPolygon(int CountryId, IReadOnlyList<double[]> Points);

public sealed record ParsedLayerGeometry(
        string FeatureId,
        int TypeIndex,
        IReadOnlyList<double[]> Coordinates);

/// <summary>Overlay feature data from JavaScript map for plan geometry editing.</summary>
public sealed record OverlayFeatureDto(
    string FeatureId,
    int TypeIndex,
    string? WorldStateId,
    double[][]? OriginalCoords,
    double[][] Coords);