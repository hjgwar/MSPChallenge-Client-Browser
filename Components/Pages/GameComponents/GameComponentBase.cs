using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public class GameComponentBase : ComponentBase
{
    [Inject] protected GameSessionService  GameSessionService  { get; set; } = null!;
    [Inject] protected GameUIStateService  GameUIStateService  { get; set; } = null!;
    [Inject] protected UserSessionService  UserSessionService  { get; set; } = null!;
    [Inject] protected MspApiClient        ApiClient           { get; set; } = null!;
    [Parameter] public EventCallback OnCloseThisPanel { get; set; }
}