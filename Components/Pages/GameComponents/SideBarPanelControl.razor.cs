using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Utils;
using MSPChallenge_Client_Browser.Services;
using BlazorBootstrap;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class SideBarPanelControl : IDisposable
{
    [Inject] private NavigationManager  NavigationManager  { get; set; } = null!;
    [Inject] private UserSessionService UserSessionService { get; set; } = null!;
    [Inject] private GameSessionService GameSessionService { get; set; } = null!;
    [Inject] private GameUIStateService GameUIStateService { get; set; } = null!;

    private List<ToastMessage> messages = new List<ToastMessage>();

    protected override void OnInitialized()
        => GameSessionService.Changed += OnStateChanged;

    public void Dispose()
        => GameSessionService.Changed -= OnStateChanged;

    private void OnStateChanged()
        => InvokeAsync(StateHasChanged);

    public void OpenDependenciesPage() 
    {
        if (IsNavigationPrevented()) return;
        NavigationManager.NavigateTo("/game/dependencies");
    }

    public async Task OpenHomePage()
    {
        if (IsNavigationPrevented()) return;
        await GameSessionService.ResetAsync();
        NavigationManager.NavigateTo("/");
    }

    private bool IsInEditMode()
        => GameUIStateService.EditMode && GameUIStateService.SelectedPlanId is > 0;

    private bool IsNavigationPrevented()
    {
        if (IsInEditMode())
        {
            messages.Add(
                new ToastMessage
                {
                    Type = ToastType.Danger,
                    Title = "Plan is being edited",
                    HelpText = $"{DateTime.Now}",
                    Message = "Please cancel or finish editing the plan before navigating away.",
                    AutoHide = true,
                }
            );
            return true;
        }
        return false;
    }

    /// <summary>Inline style for the users sidebar button: tinted with the country colour.</summary>
    private string CountryBtnStyle()
    {
        string rgba = ConversionUtils.HexToRGB(UserSessionService.User.Country.Color,
            GameUIStateService.OnlineUsersPanelOpen ? 0.40 : 0.20);
        return $"background:{rgba};color:#fff;";
    }
}