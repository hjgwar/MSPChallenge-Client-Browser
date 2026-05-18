namespace MSPChallenge_Client_Browser.Models;

/// <summary>A single plan message shown in the communication panel.</summary>
public sealed record PlanMessageEntry(
    string   MessageId,
    int      PlanId,
    int?     CountryId,
    string   CountryName,
    string   UserName,
    string   Message,
    DateTime? SentAt,
    int      Sequence);