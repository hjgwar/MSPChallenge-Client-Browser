using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.KpiDashboard;

/// <summary>
/// Generic KPI dashboard widget: one instance per <see cref="KpiCategoryState"/>, rendering all of its
/// values as a D3 bar chart with a time-range slider. Used identically on every dashboard tab.
/// </summary>
public partial class KpiWidget : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private GameSessionService GameSessionService { get; set; } = null!;

    [Parameter, EditorRequired] public KpiCategoryState Category { get; set; } = null!;
    [Parameter, EditorRequired] public KpiWidgetInstance Instance { get; set; } = null!;
    /// <summary>The country to resolve values for. Ignored (values are shown as-is) when the owning tab has no country selector.</summary>
    [Parameter] public int ViewingCountryId { get; set; } = -1;
    [Parameter] public EventCallback<KpiWidgetInstance> OnRemove { get; set; }
    [Parameter] public EventCallback<KpiWidgetInstance> OnFavouriteToggle { get; set; }

    private readonly string _chartContainerId  = $"kpi-chart-{Guid.NewGuid():N}";
    private readonly string _sliderContainerId = $"kpi-slider-{Guid.NewGuid():N}";

    private IJSObjectReference? _module;
    private DotNetObjectReference<KpiWidget>? _dotNetRef;
    private bool _optionsOpen;

    // Tracks what was last actually drawn, so OnParametersSetAsync (which fires on every
    // parent re-render, e.g. every WS tick) only redraws the chart when something that
    // affects its output has actually changed.
    private bool _hasRendered;
    private int  _renderedCountryId;
    private int  _renderedMostRecentMonth = int.MinValue;
    private int? _renderedStartMonth;
    private int? _renderedEndMonth;
    private bool _renderedStacked;

    private string PositionStyle =>
        $"grid-column: {Instance.Position.X + 1} / span {Instance.Position.W}; " +
        $"grid-row: {Instance.Position.Y + 1} / span {Instance.Position.H};";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/kpi-dashboard.js");
        _dotNetRef = DotNetObjectReference.Create(this);
        await RenderAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_module is null) return;

        var mostRecentMonth = Category.Values.Select(v => v.MostRecentMonth).DefaultIfEmpty(-1).Max();
        var dataChanged = !_hasRendered
            || _renderedCountryId != ViewingCountryId
            || _renderedMostRecentMonth != mostRecentMonth
            || _renderedStartMonth != Instance.SelectedStartMonth
            || _renderedEndMonth != Instance.SelectedEndMonth
            || _renderedStacked != Instance.Stacked;
        if (!dataChanged) return;

        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (_module is null) return;

        // Null start/end means "follow the full available range" — it keeps tracking
        // GameSessionService.GameCurrentMonth automatically as new months simulate.
        var maxMonth = Math.Max(0, GameSessionService.GameCurrentMonth);
        const int minMonth = 0;
        var startMonth = Instance.SelectedStartMonth ?? minMonth;
        var endMonth   = Instance.SelectedEndMonth   ?? maxMonth;
        if (endMonth < startMonth) endMonth = startMonth;

        var series = Category.Values.Select(v => new
        {
            name = v.Definition.ValueDisplayName ?? v.Definition.ValueName,
            color = v.Definition.ValueColor,
            points = Enumerable.Range(startMonth, endMonth - startMonth + 1)
                .Select(m => new { month = m, value = v.GetValue(m, ViewingCountryId) ?? 0 })
                .ToList(),
        }).ToList();

        var chartPayload = JsonSerializer.Serialize(new
        {
            unit = Category.Definition.Unit,
            stacked = Instance.Stacked,
            series,
        });

        await _module.InvokeVoidAsync("renderWidget", _chartContainerId, chartPayload);
        await _module.InvokeVoidAsync("renderRangeSlider", _sliderContainerId, minMonth, maxMonth, startMonth, endMonth, _dotNetRef);

        _hasRendered = true;
        _renderedCountryId = ViewingCountryId;
        _renderedMostRecentMonth = Category.Values.Select(v => v.MostRecentMonth).DefaultIfEmpty(-1).Max();
        _renderedStartMonth = Instance.SelectedStartMonth;
        _renderedEndMonth = Instance.SelectedEndMonth;
        _renderedStacked = Instance.Stacked;
    }

    /// <summary>Called from JS when the player drags the range-slider handles.</summary>
    [JSInvokable]
    public async Task OnRangeChanged(int startMonth, int endMonth)
    {
        Instance.SelectedStartMonth = startMonth;
        Instance.SelectedEndMonth = endMonth;
        await RenderAsync();
    }

    private void ToggleOptions() => _optionsOpen = !_optionsOpen;

    private async Task OnStackedChangedAsync(ChangeEventArgs e)
    {
        Instance.Stacked = e.Value is bool stacked && stacked;
        await RenderAsync();
    }

    private async Task ToggleFavouriteAsync()
    {
        Instance.IsFavourite = !Instance.IsFavourite;
        await OnFavouriteToggle.InvokeAsync(Instance);
    }

    private async Task RemoveAsync() => await OnRemove.InvokeAsync(Instance);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("disposeChart", _chartContainerId);
                await _module.InvokeVoidAsync("disposeRangeSlider", _sliderContainerId);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { /* circuit already gone */ }
        }
        _dotNetRef?.Dispose();
    }
}
