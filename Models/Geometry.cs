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

/// <summary>A geometry item contributed to the map by a finalised plan, spanning one base layer.</summary>
public sealed record PlanProjectionFeature(
    string                  LayerId,
    string                  GeoType,
    string                  Id,
    int                     TypeIndex,
    IReadOnlyList<double[]> Coordinates);

/// <summary>
/// Aggregated world-state projection contributed by every finalised plan before a cutoff month:
/// base-layer feature ids to hide (superseded/deleted) and plan-added features to draw.
/// </summary>
public sealed record PlanProjection(
    IReadOnlyDictionary<string, HashSet<string>> HiddenByLayer,
    IReadOnlyList<PlanProjectionFeature>          AddedFeatures);