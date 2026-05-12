namespace MSPChallenge_Client_Browser.Models;

/// <summary>A single plan received via the Game/Latest WebSocket message.</summary>
public sealed record PlanEntry(
    int    PlanId,
    string Name,
    string State,
    int    Country,
    int    StartDate);
