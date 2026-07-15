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
            var url = $"api/Plan/ForceUnlock";
            var fields = new List<KeyValuePair<string, string>>
            {
                new("plan_id", planId.ToString())
            };

            await ApiClient.PostFormAsync(url, fields);
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