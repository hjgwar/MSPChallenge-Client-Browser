namespace MSPChallenge_Client_Browser.Services;

public static class PlanStateTransitions
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
}
