namespace MSPChallenge_Client_Browser.Models;

/// <summary>A plan policy type available in this game session.</summary>
public sealed record PolicySetting(string PolicyType, string DisplayName, bool Enabled);