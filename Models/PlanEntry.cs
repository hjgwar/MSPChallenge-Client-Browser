namespace MSPChallenge_Client_Browser.Models;

/// <summary>A single plan received via the Game/Latest WebSocket message.</summary>
public sealed record PlanEntry(
    int    PlanId,
    string Name,
    string Description,
    string State,
    int    Country,
    int    StartDate,
    int    ConstructionTime,
    IReadOnlyList<string>        PolicyNames,
    IReadOnlyList<PlanLayerData> Layers,
    bool RequiresApproval = false,
    int  MessageCount     = 0,
    int  IssueCount       = 0);

/// <summary>A layer within a plan, containing geometry items.</summary>
public sealed record PlanLayerData(
    string LayerId,
    string OriginalLayerId,
    string State,
    IReadOnlyList<PlanGeometryItem> Geometry,
    IReadOnlyList<string> DeletedPersistentIds);

/// <summary>One geometry object (a set of coordinate pairs) within a plan layer.</summary>
public sealed record PlanGeometryItem(
    IReadOnlyList<double[]> Coordinates,
    string Id           = "",
    string PersistentId = "",
    int    TypeIndex    = 0);
