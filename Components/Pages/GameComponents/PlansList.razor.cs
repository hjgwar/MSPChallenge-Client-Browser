using BlazorBootstrap;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class PlansList : GameComponentBase, IDisposable
{
    private ConfirmDialog forceUnlockDialog = null!;
    private readonly Dictionary<int, string> _planIssueSeverity = [];
    
    private Dictionary<int, string> _countryColours => GameSessionService.Countries
        .Where(c => c.Id > 0)
        .ToDictionary(c => c.Id, c => c.Color);

    private Dictionary<int, string> _countryNames => GameSessionService.Countries
        .Where(c => c.Id > 0)
        .ToDictionary(c => c.Id, c => c.Name);

    protected override void OnInitialized()
    {
        base.OnInitialized();
        GameSessionService.Changed += OnStateChanged;
    }

    private void OnStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private void SetPlansPanelOpen(bool open)
    {
        GameUIStateService.PlansPanelOpen = open;
        GameSessionService.NotifyChanged();
    }

    private async Task SelectPlanAsync(Plan plan)
    {
        if (GameUIStateService.SelectedPlanId == plan.PlanId)
        {
            // Toggling the same plan off
            GameUIStateService.SelectedPlanId = null;
            GameUIStateService.EditMode = false;
        }
        else
        {
            // If an existing plan is locked for editing, release the lock before switching.
            if (GameUIStateService.EditMode && GameUIStateService.SelectedPlanId is > 0)
            {
                _ = ApiClient.PostFormAsync("Plan/Unlock",
                    new[]
                    {
                        new KeyValuePair<string, string>("id", GameUIStateService.SelectedPlanId.ToString()!),
                        new KeyValuePair<string, string>("force_unlock", "0"),
                        new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                    });
            }
            GameUIStateService.EditMode = false;

            // Selecting a plan dismisses the creation form (the two panels are mutually exclusive).
            GameUIStateService.CreatePlanOpen = false;
            GameUIStateService.SelectedPlanId = plan.PlanId;
        }

        GameSessionService.NotifyChanged();
        await Task.CompletedTask;
    }

    private async Task ForceUnlockPlanAsync(int planId)
    {
        if (!UserSessionService.IsAdmin) return;
        var confirmation = await forceUnlockDialog.ShowAsync(
            title: "Are you sure you want to force unlock this plan?",
            message1: "This will unlock the plan for editing. It might be locked because someone else is currently editing it.",
            message2: "Do you want to proceed?");
        if (!confirmation) return;
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
        GameSessionService.Changed -= OnStateChanged;
    }
}