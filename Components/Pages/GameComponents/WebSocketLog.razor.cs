using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class WebSocketLog
{
    [Inject] private IJSRuntime JS { get; set; } = null!;
    
    [Parameter] public IReadOnlyList<(string HeaderName, string Raw, DateTime ReceivedAt)> LogEntries { get; set; } = [];
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnClear { get; set; }

    private bool _wsCopied;

    private async Task CopyWsMessageAsync(string raw)
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", raw);
        _wsCopied = true;
        StateHasChanged();
        await Task.Delay(1500);
        _wsCopied = false;
        StateHasChanged();
    }

    private async Task CloseAsync()
    {
        await OnClose.InvokeAsync();
    }

    private async Task ClearAsync()
    {
        await OnClear.InvokeAsync();
    }
}
