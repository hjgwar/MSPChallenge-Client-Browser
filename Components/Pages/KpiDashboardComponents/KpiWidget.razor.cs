using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.KpiDashboardComponents;

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
    /// <summary>Raised after the player moved or resized the widget; <see cref="KpiWidgetInstance.Position"/> already holds the new cell.</summary>
    [Parameter] public EventCallback<KpiWidgetInstance> OnLayoutChanged { get; set; }

    private readonly string _widgetId           = $"kpi-widget-{Guid.NewGuid():N}";
    private readonly string _chartContainerId  = $"kpi-chart-{Guid.NewGuid():N}";
    private readonly string _sliderContainerId = $"kpi-slider-{Guid.NewGuid():N}";
    private readonly string _filterButtonId    = $"kpi-filter-{Guid.NewGuid():N}";

    private IJSObjectReference? _module;
    private DotNetObjectReference<KpiWidget>? _dotNetRef;
    private bool _optionsOpen;
    private bool _filterOpen;
    private ElementRect _filterAnchor = new(0, 0, 0);

    /// <summary>Viewport position of the filter button, used to place the (viewport-fixed) filter list.</summary>
    private sealed record ElementRect(double Left, double Bottom, double ViewportWidth);

    // Tracks what was last actually drawn, so OnParametersSetAsync (which fires on every
    // parent re-render, e.g. every WS tick) only redraws the chart when something that
    // affects its output has actually changed.
    private bool _hasRendered;
    private int  _renderedCountryId;
    private int  _renderedMostRecentMonth = int.MinValue;
    private int? _renderedStartMonth;
    private int? _renderedEndMonth;
    private bool _renderedStacked;
    private string _renderedHiddenKey = string.Empty;

    private string PositionStyle =>
        $"grid-column: {Instance.Position.X + 1} / span {Instance.Position.W}; " +
        $"grid-row: {Instance.Position.Y + 1} / span {Instance.Position.H};";

    private static string SeriesName(KpiValueState value) =>
        value.Definition.ValueDisplayName ?? value.Definition.ValueName;

    private bool IsVisible(KpiValueState value) => !Instance.HiddenValueNames.Contains(value.Definition.ValueName);
    private IEnumerable<KpiValueState> VisibleValues => Category.Values.Where(IsVisible);
    private string HiddenKey => string.Join('|', Instance.HiddenValueNames.Order());

    private string FilterSummary
    {
        get
        {
            var shown = Category.Values.Count(IsVisible);
            return shown == Category.Values.Count ? "All bars" : shown == 0 ? "No bars" : $"{shown} of {Category.Values.Count} bars";
        }
    }

    private string FilterPopupStyle
    {
        get
        {
            const double popupWidth = 280; // slightly above the CSS width, so the list never touches the viewport edge
            var left = Math.Max(8, Math.Min(_filterAnchor.Left, _filterAnchor.ViewportWidth - popupWidth - 8));
            var top = _filterAnchor.Bottom + 4;
            return FormattableString.Invariant($"left:{left}px; top:{top}px; max-height:calc(100vh - {top + 16}px);");
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/kpi-dashboard.js");
        _dotNetRef = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("initWidgetLayout", _widgetId, _dotNetRef);
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
            || _renderedStacked != Instance.Stacked
            || _renderedHiddenKey != HiddenKey;
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

        var series = VisibleValues.Select(v => new
        {
            name = SeriesName(v),
            color = v.Definition.ValueColor,
            points = Enumerable.Range(startMonth, endMonth - startMonth + 1)
                .Select(m => new { month = m, label = GameSessionService.MonthToDate(m), value = v.GetValue(m, ViewingCountryId) ?? 0 })
                .ToList(),
        }).ToList();

        var chartPayload = JsonSerializer.Serialize(new
        {
            unit = Category.Definition.Unit,
            stacked = Instance.Stacked,
            series,
        });

        var monthLabels = Enumerable.Range(minMonth, maxMonth - minMonth + 1)
            .Select(GameSessionService.MonthToDate)
            .ToArray();

        await _module.InvokeVoidAsync("renderWidget", _chartContainerId, chartPayload);
        await _module.InvokeVoidAsync("renderRangeSlider", _sliderContainerId, minMonth, maxMonth, startMonth, endMonth, monthLabels, _dotNetRef);

        _hasRendered = true;
        _renderedCountryId = ViewingCountryId;
        _renderedMostRecentMonth = Category.Values.Select(v => v.MostRecentMonth).DefaultIfEmpty(-1).Max();
        _renderedStartMonth = Instance.SelectedStartMonth;
        _renderedEndMonth = Instance.SelectedEndMonth;
        _renderedStacked = Instance.Stacked;
        _renderedHiddenKey = HiddenKey;
    }

    /// <summary>Called from JS when the player drags the range-slider handles.</summary>
    [JSInvokable]
    public async Task OnRangeChanged(int startMonth, int endMonth)
    {
        Instance.SelectedStartMonth = startMonth;
        Instance.SelectedEndMonth = endMonth;
        await RenderAsync();
    }

    /// <summary>Called from JS when the player drops the widget on a grid cell.</summary>
    [JSInvokable]
    public async Task OnMoved(int column, int row, int columnCount)
    {
        Instance.Position.X = Math.Clamp(column, 0, Math.Max(0, columnCount - Instance.Position.W));
        Instance.Position.Y = Math.Max(0, row);
        await OnLayoutChanged.InvokeAsync(Instance);
    }

    /// <summary>Called from JS when the player finishes resizing the widget; spans are in grid cells.</summary>
    [JSInvokable]
    public async Task OnResized(int spanW, int spanH, int columnCount)
    {
        Instance.Position.W = Math.Clamp(spanW, 1, Math.Max(1, columnCount - Instance.Position.X));
        Instance.Position.H = Math.Max(1, spanH);
        await OnLayoutChanged.InvokeAsync(Instance);
    }

    private void ToggleOptions() => _optionsOpen = !_optionsOpen;

    private async Task ToggleFilterAsync()
    {
        if (_filterOpen)
        {
            _filterOpen = false;
            return;
        }
        if (_module is null) return;
        _filterAnchor = await _module.InvokeAsync<ElementRect?>("getElementRect", _filterButtonId) ?? new ElementRect(0, 0, 0);
        _filterOpen = true;
    }

    private void CloseFilter() => _filterOpen = false;

    private async Task SetSeriesVisibleAsync(KpiValueState value, bool visible)
    {
        if (visible) Instance.HiddenValueNames.Remove(value.Definition.ValueName);
        else Instance.HiddenValueNames.Add(value.Definition.ValueName);
        await RenderAsync();
    }

    private async Task ShowAllSeriesAsync()
    {
        Instance.HiddenValueNames.Clear();
        await RenderAsync();
    }

    private async Task HideAllSeriesAsync()
    {
        foreach (var value in Category.Values)
            Instance.HiddenValueNames.Add(value.Definition.ValueName);
        await RenderAsync();
    }

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
                await _module.InvokeVoidAsync("disposeWidgetLayout", _widgetId);
                await _module.InvokeVoidAsync("disposeChart", _chartContainerId);
                await _module.InvokeVoidAsync("disposeRangeSlider", _sliderContainerId);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { /* circuit already gone */ }
        }
        _dotNetRef?.Dispose();
    }
}
