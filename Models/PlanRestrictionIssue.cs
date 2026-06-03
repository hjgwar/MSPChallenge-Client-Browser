namespace MSPChallenge_Client_Browser.Models;
public sealed record PlanRestrictionIssue(
        string  Severity,
        string  Message,
        string? SourceLayer,
        string? TargetLayer,
        string  ChangeKind,
        double  MarkerX,
        double  MarkerY
);