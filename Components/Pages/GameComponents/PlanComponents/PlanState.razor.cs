using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanState : GameComponentBase
{
    [Parameter] public Plan? Plan { get; set; }
    [Parameter] public IReadOnlyList<PlanRestrictionIssue> SelectedPlanIssues { get; set; } = [];
    private bool _planStateOpen = false;
    private Models.PlanState? _planStatePending;
    private bool _planStateSending = false;

    private bool CanChangeState()
    {
        if (Plan is null) return false;
        return !Plan.State.Equals(Models.PlanState.APPROVED)
            && (UserSessionService.IsAdmin || Plan.Country == UserSessionService.User.Country.Id);
    }

    private void TogglePlanStatePanel()
    {
        _planStateOpen = !_planStateOpen;
    }
    
    private async Task SetPlanStateAsync()
    {
        if (_planStateSending || _planStatePending is null || GameSessionState.SelectedPlanId == 0 || GameSessionState.SelectedPlanId is null)
            return;
        if (_planStatePending.Equals(Plan?.State))
        {
            _planStateOpen = false;
            return;
        }
        _planStateSending = true;
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
                        team_id   = UserSessionService.User.Country.Id,
                        user_name = UserSessionService.User.Name,
                        text      = $"Changed the plans status to: {Utils.PlanCalculations.PlanStates.PlanStateLabel(_planStatePending)}",
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
                        state = _planStatePending,
                        user  = UserSessionService.User.Id,
                    }),
                    group = 5,     // BATCH_GROUP_PLAN_CHANGE
                },
            });

            await ApiClient.PostFormAsync("Batch/ExecuteBatch",
                new[]
                {
                    new KeyValuePair<string, string>("country_id", UserSessionService.User.Country.Id.ToString()),
                    new KeyValuePair<string, string>("user_id",    UserSessionService.User.Id.ToString() ?? "0"),
                    new KeyValuePair<string, string>("batch_guid", Guid.NewGuid().ToString()),
                    new KeyValuePair<string, string>("requests",   batchRequests),
                });

            _planStateOpen = false;
        }
        catch { /* Server state will correct on next WS update */ }
        finally
        {
            _planStateSending = false;
            StateHasChanged();
        }
    }

    private void OnPlanStatePendingChanged(Models.PlanState newState)
    {
        _planStatePending = newState;
    }
}