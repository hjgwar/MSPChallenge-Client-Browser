namespace MSPChallenge_Client_Browser.Models;

public sealed record UserEntry(
    int? Id,
    string Name,
    int CountryId
);