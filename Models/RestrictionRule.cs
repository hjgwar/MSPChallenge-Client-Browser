namespace MSPChallenge_Client_Browser.Models;

/// <summary>Restriction rule loaded from Game/Config.</summary>
public sealed record RestrictionRule(
    string Message,
    string Type,
    string StartLayer,
    string StartType,
    string EndLayer,
    string EndType,
    string Sort);

/// <summary>A single matched restriction for one planned geometry item.</summary>
public sealed record RestrictionMatch(
    string Severity,
    string Message);
