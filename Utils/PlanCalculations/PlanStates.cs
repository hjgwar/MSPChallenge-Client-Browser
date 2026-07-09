using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Utils.PlanCalculations;

public static class PlanStates
{
    public static IReadOnlyList<string> GetAvailablePlanStates(
        string currentState,
        bool requiresApproval,
        bool hasErrors)
    {
        var current = currentState.ToUpperInvariant();

        if (current == "IMPLEMENTED")
            return [];

        if (current == "ARCHIVED")
            return ["DESIGN"];

        if (hasErrors)
            return ["DESIGN", "ARCHIVED"];

        var states = new List<string> { "DESIGN", "CONSULTATION", "APPROVAL" };
        if (!requiresApproval)
            states.Add("APPROVED");
        states.Add("ARCHIVED");
        return states;
    }

    public static string PlanStateLabel(string? state) => state?.ToUpperInvariant() switch
    {
        "APPROVAL" => "AWAITING APPROVAL",
        _          => state?.ToUpperInvariant() ?? string.Empty,
    };

    public static int PlanStatePriority(string state) => state.ToUpperInvariant() switch
    {
        "DESIGN"        => 0,
        "CONSULTATION"  => 1,
        "APPROVAL"      => 2,
        "APPROVED"      => 3,
        "IMPLEMENTED"   => 4,
        "ARCHIVED"      => 5,
        _               => 6,
    };

    public static readonly string[] OrderedPlanStates =
    [
        "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "IMPLEMENTED", "ARCHIVED"
    ];

    public static bool IsFinalisedPlanState(PlanState state) =>
        state.Equals(PlanState.CONSULTATION) ||
        state.Equals(PlanState.APPROVAL) ||
        state.Equals(PlanState.APPROVED) ||
        state.Equals(PlanState.IMPLEMENTED);

    
}
