using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelState
{
    [Parameter] public IReadOnlyList<PlanRestrictionIssue> SelectedPlanIssues { get; set; } = [];
    [Parameter] public Func<Task> SetPlanStateAsync { get; set; } = null!;
    public string? PlanStatePending;
    public bool PlanStateSending;
    private bool _planStateDropdownOpen;

    private IReadOnlyList<string> GetAvailablePlanStates(PlanEntry? plan)
    {
        if (plan == null)
            return Array.Empty<string>();
        bool hasErrors = SelectedPlanIssues.Any(
            i => i.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase));

        return PlanStateTransitions.GetAvailablePlanStates(plan.State, plan.RequiresApproval, hasErrors);
    }
}