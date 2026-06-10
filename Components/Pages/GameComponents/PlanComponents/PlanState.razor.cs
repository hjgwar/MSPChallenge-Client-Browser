using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanState : PlanComponentBase
{
    [Parameter] public IReadOnlyList<PlanRestrictionIssue> SelectedPlanIssues { get; set; } = [];
    public PlanPanelState PlanPanelStateInstance { get; set; } = null!;
    private bool _planStateOpen = false;

    private bool CanChangeState()
    {
        if (PlanController.SelectedPlan is null) return false;
        return !PlanController.SelectedPlan.State.Equals("IMPLEMENTED", StringComparison.OrdinalIgnoreCase)
            && (UserSessionService.IsAdmin || PlanController.SelectedPlan.Country == UserSessionService.User.CountryId);
    }

    private void TogglePlanStatePanel()
    {
        _planStateOpen = !_planStateOpen;
    }
    
    private async Task SetPlanStateAsync()
    {
        if (PlanPanelStateInstance.PlanStateSending || PlanPanelStateInstance.PlanStatePending is null || GameSessionState.SelectedPlanId == 0 || GameSessionState.SelectedPlanId is null)
            return;
        if (PlanPanelStateInstance.PlanStatePending.Equals(PlanController.SelectedPlan?.State, StringComparison.OrdinalIgnoreCase))
        {
            _planStateOpen = false;
            return;
        }
        PlanPanelStateInstance.PlanStateSending = true;
        StateHasChanged();
        try
        {

            // Step 1: lock the plan (direct call â€” must succeed before batch)
            await ApiClient.PostFormAsync("Plan/Lock",
                new[]
                {
                    new KeyValuePair<string, string>("id",   GameSessionState.SelectedPlanId.ToString() ?? "0"),
                    new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString() ?? "0"),
                });

            // Step 2: batch â€” unlock + set state (mirrors Unity AP_StateSelect.AcceptStatus)
            var batchRequests = System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new
                {
                    call_id       = 1,
                    endpoint      = "api/Plan/Unlock",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id           = GameSessionState.SelectedPlanId,
                        force_unlock = 0,
                        user         = UserSessionService.User.Id,
                    }),
                    group = 100,   // BATCH_GROUP_UNLOCK
                },
                new
                {
                    call_id       = 2,
                    endpoint      = "api/Plan/Message",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        plan      = GameSessionState.SelectedPlanId,
                        team_id   = UserSessionService.User.CountryId,
                        user_name = UserSessionService.User.Name,
                        text      = $"Changed the plans status to: {ConversionUtils.PlanStateLabel(PlanPanelStateInstance.PlanStatePending)}",
                    }),
                    group = 5,     // BATCH_GROUP_PLAN_CHANGE
                },
                new
                {
                    call_id       = 3,
                    endpoint      = "api/Plan/SetState",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id    = GameSessionState.SelectedPlanId,
                        state = PlanPanelStateInstance.PlanStatePending,
                        user  = UserSessionService.User.Id,
                    }),
                    group = 5,     // BATCH_GROUP_PLAN_CHANGE
                },
            });

            await ApiClient.PostFormAsync("Batch/ExecuteBatch",
                new[]
                {
                    new KeyValuePair<string, string>("country_id", UserSessionService.User.CountryId.ToString()),
                    new KeyValuePair<string, string>("user_id",    UserSessionService.User.Id.ToString() ?? "0"),
                    new KeyValuePair<string, string>("batch_guid", Guid.NewGuid().ToString()),
                    new KeyValuePair<string, string>("requests",   batchRequests),
                });

            _planStateOpen = false;
        }
        catch { /* Server state will correct on next WS update */ }
        finally
        {
            PlanPanelStateInstance.PlanStateSending = false;
            StateHasChanged();
        }
    }
}