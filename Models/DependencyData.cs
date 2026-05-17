namespace MSPChallenge_Client_Browser.Models;

public sealed record DependencyGroup(string Name, IReadOnlyList<DependencyEntry> Entries);
public sealed record DependencyEntry(string Id, string Name, string? Link = null);
public sealed record DependencyLink(string FromId, string ToId, int Severity, string Description);
