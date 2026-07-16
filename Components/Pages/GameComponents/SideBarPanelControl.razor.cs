using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Utils;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class SideBarPanelControl : IDisposable
{
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private UserSessionService UserSessionService { get; set; } = null!;
    [Inject] private GameSessionState GameSessionState { get; set; } = null!;
    [Inject] private MspApiClient ApiClient { get; set; } = null!;

    protected override void OnInitialized()
        => GameSessionState.Changed += OnStateChanged;

    public void Dispose()
        => GameSessionState.Changed -= OnStateChanged;

    private void OnStateChanged()
        => InvokeAsync(StateHasChanged);

    public void ToggleOnlineUsersPanel()
    {
        GameSessionState.OnlineUsersPanelOpen = !GameSessionState.OnlineUsersPanelOpen;
        GameSessionState.NotifyChanged();
    }

    public void ToggleLayerPanel()
    {
        GameSessionState.LayerPanelOpen = !GameSessionState.LayerPanelOpen;
        GameSessionState.NotifyChanged();
    }

    public void ToggleLegendPanel()
    {
        GameSessionState.LegendPanelOpen = !GameSessionState.LegendPanelOpen;
        GameSessionState.NotifyChanged();
    }

    public void TogglePlansPanel()
    {
        GameSessionState.PlansPanelOpen = !GameSessionState.PlansPanelOpen;
        GameSessionState.NotifyChanged();
    }

    public void ToggleCreatePlanPanel()
    {
        var opening = !GameSessionState.CreatePlanOpen;
        if (opening)
        {
            // If an existing plan is locked for editing, release the lock (fire-and-forget).
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
            // Cancel any active edit/view before showing the creation form.
            GameSessionState.EditMode = false;
            GameSessionState.SelectedPlanId = null;
        }
        GameSessionState.CreatePlanOpen = opening;
        GameSessionState.NotifyChanged();
    }

    public void OpenDependenciesPage()
    {
        NavigationManager.NavigateTo("/game/dependencies");
    }

    public async Task OpenHomePage()
    {
        await GameSessionState.ResetAsync();
        NavigationManager.NavigateTo("/");
    }

    /// <summary>Inline style for the users sidebar button: tinted with the country colour.</summary>
    private string CountryBtnStyle()
    {
        string rgba = ConversionUtils.HexToRGB(UserSessionService.User.Country.Color, GameSessionState.OnlineUsersPanelOpen ? 0.40 : 0.20);
        return $"background:{rgba};color:#fff;";
    }
}