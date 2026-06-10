namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanNameDesc : PlanComponentBase
{
    private string? _editName;
    private string? _editDescription;
    private bool _detailDescExpanded = false;

    protected override void OnInitialized()
    {
        _editName = PlanController.SelectedPlan?.Name;
        _editDescription = PlanController.SelectedPlan?.Description;
    }
}