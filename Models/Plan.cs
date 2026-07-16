namespace MSPChallenge_Client_Browser.Models;

/// <summary>A single plan received via the Game/Latest WebSocket message.</summary>
public sealed record Plan(
    int    PlanId,
    string Name,
    string Description,
    PlanState State,
    int    Country,
    int    StartDate,
    int    ConstructionTime,
    IReadOnlyList<string>        PolicyNames,
    IReadOnlyList<string>        PolicyTypes,
    IReadOnlyList<PlanLayerData> Layers,
    bool RequiresApproval = false,
    int  MessageCount     = 0,
    int  IssueCount       = 0,
    IReadOnlyDictionary<int, int>? Votes = null,
    int  LockedByUserId = 0);

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


public sealed record PlanRestrictionIssue(
        string  Severity,
        string  Message,
        string? SourceLayer,
        string? TargetLayer,
        string  ChangeKind,
        double  MarkerX,
        double  MarkerY
);

/// <summary>A single plan message shown in the communication panel.</summary>
public sealed record PlanMessage(
    string   MessageId,
    int      PlanId,
    int?     CountryId,
    string   CountryName,
    string   UserName,
    string   Message,
    DateTime? SentAt,
    int      Sequence);

public enum PlanViewMode { AfterChanges, Original, ChangesOnly }

public enum PlanState { DESIGN, CONSULTATION, APPROVAL, APPROVED, IMPLEMENTED, DELETED }

public sealed record PlanApprovalRequirement(
    int                   CountryId,
    string                CountryName,
    IReadOnlyList<string> Reasons);
