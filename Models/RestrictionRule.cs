namespace MSPChallenge_Client_Browser.Models;

/// <summary>Restriction rule loaded from Game/Config.</summary>
public sealed record RestrictionRule(
    string Message,
    string Type,
    string StartLayer,
    string StartType,
    string EndLayer,
    string EndType,
    string Sort
);