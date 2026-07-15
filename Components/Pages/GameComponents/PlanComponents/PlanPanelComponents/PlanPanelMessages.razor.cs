using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelMessages : GameComponentBase
{
    [Parameter] public IReadOnlyList<PlanMessage>? Messages { get; set; }
    [Parameter] public EventCallback OnTogglePanel { get; set; }

    private string _planMessageDraft = string.Empty;
    private string? _planMessageSendError;
    private bool _sendingPlanMessage;

    private IReadOnlyList<PlanMessage> SelectedPlanMessages => Messages ?? [];

    private bool CanSendPlanMessage =>
        GameSessionState.SelectedPlanId != 0 &&
        !_sendingPlanMessage &&
        !string.IsNullOrWhiteSpace(_planMessageDraft);

    private async Task TogglePlanMessagesPanel()
    {
        await OnTogglePanel.InvokeAsync();
    }

    private async Task SendPlanMessageAsync()
    {
        if (!CanSendPlanMessage || GameSessionState.SelectedPlanId == 0) return;

        _sendingPlanMessage = true;
        _planMessageSendError = null;

        try
        {
            var userName = string.IsNullOrWhiteSpace(UserSessionService.User.Name) 
                ? $"Team {UserSessionService.User.Country.Id}" 
                : UserSessionService.User.Name;

            var fields = new List<KeyValuePair<string, string>>
            {
                new("plan", GameSessionState.SelectedPlanId.ToString() ?? "0"),
                new("team_id", UserSessionService.User.Country.Id.ToString()),
                new("user_name", userName),
                new("text", _planMessageDraft.Trim())
            };

            await ApiClient.PostFormAsync("Plan/Message", fields);

            // Do not append locally. The authoritative message arrives via Game/Latest WebSocket.
            _planMessageDraft = string.Empty;
        }
        catch (Exception ex)
        {
            _planMessageSendError = ex.Message;
        }
        finally
        {
            _sendingPlanMessage = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task HandlePlanMessageKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await SendPlanMessageAsync();
    }

    private static string FormatPlanMessageTime(DateTime sentAt)
    {
        var utc = sentAt.Kind switch
        {
            DateTimeKind.Utc => sentAt,
            DateTimeKind.Local => sentAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(sentAt, DateTimeKind.Utc)
        };
        return utc.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
    }

    private string PlanMessageDotColour(int? countryId)
    {
        if (!countryId.HasValue || countryId.Value <= 0)
            return "#6c757d";
        if (countryId.Value == 1 || countryId.Value == 2)
            return "#ff69b4";
        
        var country = GameSessionState.Countries.FirstOrDefault(c => c.Id == countryId.Value);
        return country?.Color ?? "#6c757d";
    }
}
