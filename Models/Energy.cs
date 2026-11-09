namespace MSPChallenge_Client_Browser.Models;

/// <summary>
/// One country's declared stake in an <see cref="EnergyGridDefinition"/>: the expected output
/// (Watts) the plan predicted for it. Actual, simulated output arrives separately via the "CEL"
/// simulation_updates stream (see GameSessionService.Energy.cs).
/// </summary>
public sealed record EnergyGridCountryShare(int CountryId, double Expected);

/// <summary>
/// One energy grid as declared by a plan's "energy" policy (policies[].grids[]). A grid is a
/// self-contained network: one or more sources, connected via cables (optionally through
/// transformers) to one or more sockets — which layers can act as which role is defined per-session
/// by each layer's "layer_editing_type" ("sourcepolygon"/"sourcepoint", "cable", "socket", "transformer").
/// Cable geometry/topology itself is not captured here; it lives in the "policy_updates" energy
/// connections data (reserved for later map-visualization use, not yet consumed client-side).
/// </summary>
public sealed record EnergyGridDefinition(
    int Id,
    /// <summary>Stable identity across plan edits/revisions — use this (not <see cref="Id"/>) to track a grid over time.</summary>
    int PersistentId,
    string Name,
    bool Active,
    bool DistributionOnly,
    IReadOnlyList<EnergyGridCountryShare> Countries,
    IReadOnlyList<string> SourceGeometryIds,
    IReadOnlyList<string> SocketGeometryIds);
