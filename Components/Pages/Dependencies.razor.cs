using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Dependencies : IAsyncDisposable
{
    private IJSObjectReference? _module;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || GameState.DependencyGroups.Count == 0) return;

        _module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/dependencies.js");

        var graphData = new
        {
            wikiBaseUrl = GameState.WikiBaseUrl,
            groups = GameState.DependencyGroups.Select(g => new
            {
                name    = g.Name,
                entries = g.Entries.Select(e => new { id = e.Id, name = e.Name, link = e.Link }).ToArray()
            }).ToArray(),
            links = GameState.DependencyLinks.Select(l => new
            {
                fromId      = l.FromId,
                toId        = l.ToId,
                severity    = l.Severity,
                description = l.Description
            }).ToArray()
        };

        await _module.InvokeVoidAsync("init", "dep-graph", graphData);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("dispose"); } catch { /* ignore */ }
            await _module.DisposeAsync();
        }
    }
}
