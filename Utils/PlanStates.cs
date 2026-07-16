using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Utils.PlanCalculations;

public static class PlanStates
{
    public static IReadOnlyList<PlanState> GetAvailablePlanStates(
        PlanState currentState,
        bool requiresApproval,
        bool hasErrors)
    {
        if (currentState == PlanState.IMPLEMENTED)
            return [];

        if (currentState == PlanState.DELETED)
            return [PlanState.DESIGN];

        if (hasErrors)
            return [PlanState.DESIGN, PlanState.DELETED];

        var states = new List<PlanState> { PlanState.DESIGN, PlanState.CONSULTATION, PlanState.APPROVAL };
        if (!requiresApproval)
            states.Add(PlanState.APPROVED);
        states.Add(PlanState.DELETED);
        return states;
    }

    public static string PlanStateLabel(PlanState? state) => state?.ToString().ToUpperInvariant() switch
    {
        "APPROVAL" => "AWAITING APPROVAL",
        "DELETED" => "ARCHIVED",
        _          => state?.ToString().ToUpperInvariant() ?? string.Empty,
    };

    public static int PlanStatePriority(PlanState state) => state.ToString().ToUpperInvariant() switch
    {
        "DESIGN"        => 0,
        "CONSULTATION"  => 1,
        "APPROVAL"      => 2,
        "APPROVED"      => 3,
        "IMPLEMENTED"   => 4,
        "DELETED"      => 5,
        _               => 6,
    };

    public static readonly PlanState[] OrderedPlanStates =
    [
        PlanState.DESIGN, PlanState.CONSULTATION, PlanState.APPROVAL, PlanState.APPROVED, PlanState.IMPLEMENTED, PlanState.DELETED
    ];

    public static bool IsFinalisedPlanState(PlanState state) =>
        state.Equals(PlanState.CONSULTATION) ||
        state.Equals(PlanState.APPROVAL) ||
        state.Equals(PlanState.APPROVED) ||
        state.Equals(PlanState.IMPLEMENTED);

    public static bool IsApprovalCompleteState(PlanState state) =>
        state.Equals(PlanState.APPROVED) ||
        state.Equals(PlanState.IMPLEMENTED) ||
        state.Equals(PlanState.DELETED);
}
