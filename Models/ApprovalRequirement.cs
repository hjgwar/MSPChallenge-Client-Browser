namespace MSPChallenge_Client_Browser.Models;

public sealed record ApprovalRequirement(
        int                   CountryId,
        string                CountryName,
        IReadOnlyList<string> Reasons);