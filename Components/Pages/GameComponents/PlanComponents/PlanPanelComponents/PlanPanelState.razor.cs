using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
using MSPChallenge_Client_Browser.Utils.PlanCalculations;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelState
{
    [Parameter] public IReadOnlyList<PlanRestrictionIssue> SelectedPlanIssues { get; set; } = [];
    [Parameter] public bool SelectedPlanApprovalRequired { get; set; } = false;
    [Parameter] public Models.PlanState? PlanStatePending { get; set; }
    [Parameter] public bool PlanStateSending { get; set; }
    [Parameter] public EventCallback<Models.PlanState> PlanStatePendingChanged { get; set; }
    [Parameter] public Func<Task> SetPlanStateAsync { get; set; } = null!;
    private bool _planStateDropdownOpen;

    private IReadOnlyList<Models.PlanState> GetAvailablePlanStates(Plan? plan)
    {
        if (plan == null)
            return Array.Empty<Models.PlanState>();
        bool hasErrors = SelectedPlanIssues.Any(
            i => i.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase));

        return PlanStates.GetAvailablePlanStates(plan.State, SelectedPlanApprovalRequired, hasErrors);
    }
}