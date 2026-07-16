using Microsoft.AspNetCore.Components;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class GameLoading
{
    [Parameter] public bool IsFading { get; set; }
    [Parameter] public string LoadingStatus { get; set; } = "Loading...";
}