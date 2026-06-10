using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanIssues : PlanComponentBase
{
    // issue calculation should live here rather than the panel component
    public List<PlanRestrictionIssue> SelectedPlanIssues       = [];

    protected override void OnInitialized()
    {
        CalculatePlanIssues();
    }

    private void CalculatePlanIssues()
    {
        if (PlanController.SelectedPlan is null)
        {
            SelectedPlanIssues = [];
            return;
        }

    }
}