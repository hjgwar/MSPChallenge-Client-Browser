using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class PlansList : GameComponentBase, IDisposable
{
    private readonly Dictionary<int, string> _planIssueSeverity = [];
    
    private Dictionary<int, string> _countryColours => GameSessionState.Countries
        .Where(c => c.Id > 0)
        .ToDictionary(c => c.Id, c => c.Color);

    private Dictionary<int, string> _countryNames => GameSessionState.Countries
        .Where(c => c.Id > 0)
        .ToDictionary(c => c.Id, c => c.Name);

    protected override void OnInitialized()
    {
        base.OnInitialized();
        GameSessionState.Changed += OnStateChanged;
    }

    private void OnStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private void SetPlansPanelOpen(bool open)
    {
        GameSessionState.PlansPanelOpen = open;
        GameSessionState.NotifyChanged();
    }

    private async Task SelectPlanAsync(Plan plan)
    {
        if (GameSessionState.SelectedPlanId == plan.PlanId)
        {
            // Toggling the same plan off
            GameSessionState.SelectedPlanId = null;
            GameSessionState.EditMode = false;
        }
        else
        {
            // If an existing plan is locked for editing, release the lock before switching.
            if (GameSessionState.EditMode && GameSessionState.SelectedPlanId is > 0)
            {
                _ = ApiClient.PostFormAsync("Plan/Unlock",
                    new[]
                    {
                        new KeyValuePair<string, string>("id", GameSessionState.SelectedPlanId.ToString()!),
                        new KeyValuePair<string, string>("force_unlock", "0"),
                        new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                    });
            }
            GameSessionState.EditMode = false;

            // Selecting a plan dismisses the creation form (the two panels are mutually exclusive).
            GameSessionState.CreatePlanOpen = false;
            GameSessionState.SelectedPlanId = plan.PlanId;
        }

        GameSessionState.NotifyChanged();
        await Task.CompletedTask;
    }

    private async Task ForceUnlockPlanAsync(int planId)
    {
        if (!UserSessionService.IsAdmin) return;

        try
        {
            await ApiClient.PostFormAsync("Plan/Unlock",
                new[]
                {
                    new KeyValuePair<string, string>("id",           planId.ToString()),
                    new KeyValuePair<string, string>("force_unlock", "1"),
                    new KeyValuePair<string, string>("user",         UserSessionService.User.Id.ToString()),
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceUnlockPlanAsync] Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        GameSessionState.Changed -= OnStateChanged;
    }
}