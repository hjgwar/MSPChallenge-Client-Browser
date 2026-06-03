using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Services;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public class GameComponentBase : ComponentBase
{
    [Inject] protected GameSessionState GameSessionState { get; set; } = null!;
    [Inject] protected SessionState SessionState { get; set; } = null!;
    [Inject] protected MspApiClient ApiClient { get; set; } = null!;

    [Parameter] public EventCallback OnClose { get; set; }
}