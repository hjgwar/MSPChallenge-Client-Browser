using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Utils;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class SideBarPanelControl
{
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private SessionState UserSessionService { get; set; } = null!;
    [Inject] private GameSessionState GameSessionState { get; set; } = null!;
    
    public void ToggleOnlineUsersPanel()
    {
        GameSessionState.OnlineUsersPanelOpen = !GameSessionState.OnlineUsersPanelOpen;
    }

    public void ToggleLayerPanel()
    {
        GameSessionState.LayerPanelOpen = !GameSessionState.LayerPanelOpen;
    }

    public void ToggleLegendPanel()
    {
        GameSessionState.LegendPanelOpen = !GameSessionState.LegendPanelOpen;
    }

    public void TogglePlansPanel()
    {
        GameSessionState.PlansPanelOpen = !GameSessionState.PlansPanelOpen;
    }

    public void ToggleCreatePlanPanel()
    {
        GameSessionState.CreatePlanOpen = !GameSessionState.CreatePlanOpen;
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
        string hex = GameSessionState.CountryColours.GetValueOrDefault(UserSessionService.User.CountryId, "");
        string rgba = ConversionUtils.HexToRGB(hex, GameSessionState.OnlineUsersPanelOpen ? 0.40 : 0.20);
        return $"background:{rgba};color:#fff;";
    }
}