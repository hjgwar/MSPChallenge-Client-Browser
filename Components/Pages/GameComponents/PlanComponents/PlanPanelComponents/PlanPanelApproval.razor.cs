using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelApproval
{
    [CascadingParameter] public PlanControl PlanPanelsController { get; set; } = null!;
    private List<ApprovalRequirement> _approvalRequired = [];
    private bool                       _sendingVote      = false;
    private HashSet<int>              _approvalReasonsExpanded = [];

    private async Task VoteOnPlanAsync(int? planId, int vote)
    {
        if (_sendingVote || planId == null || UserSessionService.User?.CountryId == null) return;
        _sendingVote = true;
        StateHasChanged();
        try
        {
            await ApiClient.PostFormAsync("Plan/Vote", new[]
            {
                new KeyValuePair<string, string>("plan",    planId.ToString() ?? string.Empty),
                new KeyValuePair<string, string>("country", UserSessionService.User?.CountryId.ToString() ?? string.Empty),
                new KeyValuePair<string, string>("vote",    vote.ToString()),
            });
        }
        catch { /* Server will correct state on next update */ }
        finally
        {
            _sendingVote = false;
            StateHasChanged();
        }
    }

    private void ToggleApprovalReasonExpanded(int countryId)
    {
        if (!_approvalReasonsExpanded.Remove(countryId))
            _approvalReasonsExpanded.Add(countryId);
    }
}