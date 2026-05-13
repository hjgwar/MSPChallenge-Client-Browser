namespace MSPChallenge_Client_Browser.Models;

/// <summary>A single plan received via the Game/Latest WebSocket message.</summary>
public sealed record PlanEntry(
    int    PlanId,
    string Name,
    string State,
    int    Country,
    int    StartDate,
    IReadOnlyList<PlanLayerData> Layers);

/// <summary>A layer within a plan, containing geometry items.</summary>
public sealed record PlanLayerData(
    string LayerId,
    string OriginalLayerId,
    string State,
    IReadOnlyList<PlanGeometryItem> Geometry,
    IReadOnlyList<string> DeletedPersistentIds);

/// <summary>One geometry object (a set of coordinate pairs) within a plan layer.</summary>
public sealed record PlanGeometryItem(IReadOnlyList<double[]> Coordinates);
