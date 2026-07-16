using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelApproval
{
    [Parameter] public IReadOnlyList<PlanApprovalRequirement> RequiredApprovals { get; set; } = [];
    [Parameter] public EventCallback OnClose { get; set; }

    private bool _sendingVote = false;
    private HashSet<int> _approvalReasonsExpanded = [];

    private async Task VoteOnPlanAsync(int? planId, int vote)
    {
        if (_sendingVote || planId == null || UserSessionService.User?.Country.Id == null) return;
        _sendingVote = true;
        StateHasChanged();
        try
        {
            await ApiClient.PostFormAsync("Plan/Vote", new[]
            {
                new KeyValuePair<string, string>("plan",    planId.ToString() ?? string.Empty),
                new KeyValuePair<string, string>("country", UserSessionService.User?.Country.Id.ToString() ?? string.Empty),
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