using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Game : IAsyncDisposable
{
    [Inject] NavigationManager NavigationManager { get; set; } = null!;
    [Inject] IJSRuntime JS { get; set; } = null!;
    [Inject] IHostEnvironment HostEnvironment { get; set; } = null!;
    [Inject] SessionState SessionState { get; set; } = null!;
    [Inject] MspApiClient ApiClient { get; set; } = null!;
    [Inject] GameWebSocketService WsService { get; set; } = null!;
    [Inject] GameSessionState GameSessionState { get; set; } = null!;

    public SideBarPanelControl SideBarPanelController { get; set; } = null!;
    public PlanControl PlanController { get; set; } = null!;
    public MapViewPort Map { get; set; } = null!;
    // Returns the selected plan or a new unsaved plan for edit mode
    private IJSObjectReference? _mapModule;
    private DotNetObjectReference<Game>? _dotNetRef;

    // ── Aliases to shared session state ──────────────────────────────────────
    private IReadOnlyList<PlanEntry>    _plans          => GameState.Plans;
    private List<LayerEntry>            _layerEntries   => GameState.LayerEntries;
    private Dictionary<int, string>     _countryColours => GameState.CountryColours;
    private Dictionary<int, string>     _countryNames   => GameState.CountryNames;
    private int    _gameStartYear    => GameState.GameStartYear;
    private int    _gameCurrentMonth => GameState.GameCurrentMonth;
    private int    _gameEndMonth     => GameState.GameEndMonth;
    private int    _gameEndYear      => GameState.GameEndYear;
    private int    _gameEraTotalMonths => GameState.GameEraTotalMonths;
    private double _eraTimeLeft      => GameState.EraTimeLeft;
    private bool   IsAdmin           => SessionState.CountryId == 1 || SessionState.CountryId == 2;

    // ── Loading & UI state ────────────────────────────────────────────────────
    private bool    _isLoading     = true;
    private bool    _loadingFading = false;
    private string  _loadingStatus = "Loading\u2026";
    private string? errorMessage;
    private string? errorDetail;

    // ── Panel open/close state ────────────────────────────────────────────────
    private bool _timeManagerVisible = false;

    // ── Feature popup ─────────────────────────────────────────────────────────
    private bool                   _popupVisible;
    private double                 _popupX;
    private double                 _popupY;
    private string?                _popupLayerName;
    private List<(string, string)> _popupProps = [];

    // ── Legend order ──────────────────────────────────────────────────────────
    private List<LayerEntry> _legendOrder = [];

    // ── WebSocket log ─────────────────────────────────────────────────────────
    private const int WsLogMaxEntries = 100;
    private readonly List<(string HeaderName, string Raw, DateTime ReceivedAt)> _wsLog = [];
    private bool _wsLogVisible;
    private bool _wsCopied;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (_scrollPlanMessagesPending && _mapModule is not null)
            {
                _scrollPlanMessagesPending = false;
                await _mapModule.InvokeVoidAsync("scrollElementToBottom", ".plan-message-panel-body");
            }
            return;
        }

        // Capture cold-start status before async work so warm returns can skip map refit.
        var isColdStart = _isLoading;

        _mapModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/map.js");

        // Default view centred on North Sea – will be replaced once _PLAYAREA bounds are known
        await _mapModule.InvokeVoidAsync("initMap", "map", 54.5, 3.5, 6);

        // On warm return, restore saved camera immediately for instant visual feedback.
        if (!isColdStart && GameState.HasSavedMapView)
        {
            await _mapModule.InvokeVoidAsync(
                "setView",
                GameState.MapLat!.Value,
                GameState.MapLng!.Value,
                GameState.MapZoom!.Value,
                false);
        }

        WsService.MessageReceived += OnWsMessageReceived;

        // Ensure shared state is loaded once per circuit, then rebuild JS map layers from cached state.
        await LoadGameDataAsync();
        await BuildMapFromStateAsync(fitToPlayArea: isColdStart);

        // OnAfterRenderAsync does not implicitly re-render after async work.
        // Force a repaint so legend/panels reflect rebuilt state immediately.
        await InvokeAsync(StateHasChanged);

        if (_isLoading)
            await HideLoadingAsync();

        _dotNetRef = DotNetObjectReference.Create(this);
        await _mapModule.InvokeVoidAsync("registerClickHandler", _dotNetRef);
        await _mapModule.InvokeVoidAsync("initLegendDrag", _dotNetRef);
    }

    private async Task HideLoadingAsync()
    {
        _loadingFading = true;
        StateHasChanged();
        await Task.Delay(650);
        _isLoading = false;
        _loadingFading = false;
        StateHasChanged();
    }

    private async Task LoadGameDataAsync()
    {
        try
        {
            await GameState.EnsureInitializedAsync(
                ApiClient,
                SessionState,
                async status =>
                {
                    if (!_isLoading) return;
                    _loadingStatus = status;
                    await InvokeAsync(StateHasChanged);
                });
        }
        catch (MspApiException ex)
        {
            errorMessage = ex.Message;
            errorDetail  = $"HTTP {ex.StatusCode}\n{ex}";
        }
        catch (Exception ex)
        {
            errorMessage = $"Error loading map data: {ex.Message}";
            errorDetail  = ex.ToString();
        }
    }

    private async Task BuildMapFromStateAsync(bool fitToPlayArea)
    {
        if (_mapModule is null) return;

        // Page instance state resets on navigation; rebuild from cached session state.
        _legendOrder.Clear();

        // Always materialize the base layer first (needed for fitToPlayArea and z-index reference).
        var baseLayer = _layerEntries.FirstOrDefault(e => e.IsBaseLayer);
        if (baseLayer is not null)
        {
            var baseSnapshot = GameState.MapLayerSnapshots.FirstOrDefault(s => s.LayerId == baseLayer.LayerId);
            if (baseSnapshot is not null)
                await EnsureLayerRenderedAsync(baseLayer.LayerId, visible: true);
        }

        foreach (var snapshot in GameState.MapLayerSnapshots)
        {
            var entry = _layerEntries.FirstOrDefault(e => e.LayerId == snapshot.LayerId);
            if (entry?.IsBaseLayer == true) continue;  // Already materialized above.

            var visible = entry?.Visible ?? snapshot.Visible;

            // Warm navigation: only materialize currently visible layers for faster return.
            if (!visible) continue;

            await EnsureLayerRenderedAsync(snapshot.LayerId, visible: true);
        }

        var byId = _layerEntries
            .Where(e => e.Visible && !e.IsBaseLayer)
            .ToDictionary(e => e.LayerId, StringComparer.Ordinal);

        foreach (var id in GameState.LegendOrderLayerIds)
        {
            if (byId.TryGetValue(id, out var le))
            {
                _legendOrder.Add(le);
                byId.Remove(id);
            }
        }

        // If order is empty/stale, append remaining visible non-base layers by depth.
        foreach (var le in byId.Values.OrderBy(e => e.Depth))
            _legendOrder.Add(le);

        PersistLegendOrderToState();

        await SyncZIndicesAsync();

        if (fitToPlayArea && baseLayer is not null)
        {
            await _mapModule.InvokeVoidAsync("fitToPlayArea", baseLayer.LayerId);
        }
    }

    private async Task EnsureLayerRenderedAsync(string layerId, bool visible)
    {
        if (_mapModule is null) return;

        var snapshot = GameState.MapLayerSnapshots.FirstOrDefault(s => s.LayerId == layerId);
        if (snapshot is null) return;

        var entry = _layerEntries.FirstOrDefault(e => e.LayerId == layerId);
        if (entry?.IsRaster == true)
        {
            if (snapshot.RasterImageData is not null && snapshot.RasterProjBounds is not null)
            {
                await _mapModule.InvokeVoidAsync(
                    "addRasterLayer",
                    snapshot.LayerId,
                    snapshot.RasterImageData,
                    snapshot.RasterProjBounds,
                    0.9,
                    visible,
                    snapshot.RasterColorMap.Count > 0 ? snapshot.RasterColorMap : null,
                    snapshot.RasterMinCutoffNorm,
                    snapshot.RasterInterpolate);
            }
            return;
        }

        if (!string.IsNullOrEmpty(snapshot.VectorGeometriesJson))
        {
            await _mapModule.InvokeVoidAsync(
                "addVectorLayer",
                snapshot.LayerId,
                snapshot.VectorGeometriesJson,
                snapshot.GeoType,
                snapshot.TypeColors,
                visible,
                snapshot.LabelKey);
        }
    }

    private async Task ToggleLayerAsync(LayerEntry entry, bool visible)
    {
        entry.Visible = visible;

        if (visible)
        {
            // Hidden layers are not materialized on warm return until user enables them.
            await EnsureLayerRenderedAsync(entry.LayerId, visible: true);

            // If a plan is currently selected, immediately project prior-plan changes onto
            // this layer so the user sees the correct world state without re-selecting the plan.
            if (GameSessionState.SelectedPlanId != 0)
            {
                var viewedPlan = _plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
                if (viewedPlan is not null)
                    await ApplyPlanProjectionAsync(viewedPlan.StartDate, entry.LayerId, currentPlan: viewedPlan);
            }
        }
        else if (_mapModule is not null)
        {
            await _mapModule.InvokeVoidAsync("setLayerVisible", entry.LayerId, false);
        }

        if (visible && !entry.IsBaseLayer && !_legendOrder.Contains(entry))
            _legendOrder.Add(entry);
        else if (!visible)
            _legendOrder.Remove(entry);

        PersistLegendOrderToState();

        await SyncZIndicesAsync();
    }

    

    [JSInvokable]
    public async Task ReorderLegend(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _legendOrder.Count || to >= _legendOrder.Count) return;
        var item = _legendOrder[from];
        _legendOrder.RemoveAt(from);
        _legendOrder.Insert(to, item);
        PersistLegendOrderToState();
        await SyncZIndicesAsync();
        StateHasChanged();
    }

    private void PersistLegendOrderToState()
    {
        GameState.SetLegendOrder(_legendOrder.Select(e => e.LayerId));
    }

    /// <summary>Assigns z-indices so that _legendOrder[0] = bottom, last = top.</summary>
    private async Task SyncZIndicesAsync()
    {
        if (_mapModule is null) return;
        for (int i = 0; i < _legendOrder.Count; i++)
            await _mapModule.InvokeVoidAsync("setLayerZIndex", _legendOrder[i].LayerId, i + 1);
    }

    private static string HexToCss(string hex)
    {
        if (string.IsNullOrEmpty(hex) || !hex.StartsWith('#')) return hex;
        var h = hex.TrimStart('#');
        if (h.Length == 6) return $"rgba({Convert.ToInt32(h[..2], 16)},{Convert.ToInt32(h[2..4], 16)},{Convert.ToInt32(h[4..6], 16)},1)";
        if (h.Length == 8) return $"rgba({Convert.ToInt32(h[..2], 16)},{Convert.ToInt32(h[2..4], 16)},{Convert.ToInt32(h[4..6], 16)},{Convert.ToInt32(h[6..8], 16) / 255.0:F3})";
        return hex;
    }

    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var s = Regex.Replace(value, "[_\\-]+", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    

    // ── Map Click Handler ──────────────────────────────────────────────────────

    [JSInvokable]
    public void OnMapClick(string json)
    {
        if (_geometryToolLayerId is not null) return; // suppress feature popup while geometry tool is active
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root    = doc.RootElement;
            var layerId = root.TryGetProperty("layerId", out var lid) ? lid.GetString() ?? "" : "";

            // For plan overlay clicks, resolve the real originating layer via _mspOriginalLayerId
            string resolvedLayerId = layerId;
            if (layerId == "__plan_overlay__" &&
                root.TryGetProperty("props", out var propsForLayer) &&
                propsForLayer.TryGetProperty("_mspOriginalLayerId", out var origLid))
            {
                resolvedLayerId = origLid.GetString() ?? layerId;
            }

            var entry   = _layerEntries.FirstOrDefault(e => e.LayerId == resolvedLayerId);

            _popupX = root.TryGetProperty("clientX", out var cx) && cx.ValueKind == JsonValueKind.Number ? cx.GetDouble() : 0;
            _popupY = root.TryGetProperty("clientY", out var cy) && cy.ValueKind == JsonValueKind.Number ? cy.GetDouble() : 0;
            _popupLayerName = entry?.DisplayName ?? resolvedLayerId;
            _popupProps.Clear();

            if (root.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("_restrictionMessage", out var restrictionMessageProp))
                {
                    _popupLayerName = "Restriction";

                    var severity = props.TryGetProperty("_restrictionSeverity", out var severityProp)
                        ? severityProp.GetString() ?? ""
                        : "";
                    var sourceLayer = props.TryGetProperty("_restrictionSourceLayer", out var sourceProp)
                        ? sourceProp.GetString() ?? ""
                        : "";
                    var targetLayer = props.TryGetProperty("_restrictionTargetLayer", out var targetProp)
                        ? targetProp.GetString() ?? ""
                        : "";
                    var changeKind = props.TryGetProperty("_restrictionChangeKind", out var changeProp)
                        ? changeProp.GetString() ?? ""
                        : "";
                    var restrictionMessage = restrictionMessageProp.GetString() ?? "";

                    if (!string.IsNullOrWhiteSpace(severity))
                        _popupProps.Add(("Severity", severity));
                    if (!string.IsNullOrWhiteSpace(changeKind))
                        _popupProps.Add(("Change", changeKind));
                    if (!string.IsNullOrWhiteSpace(restrictionMessage))
                        _popupProps.Add(("Message", restrictionMessage));
                    if (!string.IsNullOrWhiteSpace(sourceLayer) || !string.IsNullOrWhiteSpace(targetLayer))
                        _popupProps.Add(("Layers", $"{sourceLayer} <-> {targetLayer}"));

                    // Activate both layers so the user can see the overlap.
                    _ = InvokeAsync(() => EnsureRestrictionLayersVisibleAsync(sourceLayer, targetLayer));

                    _popupVisible = true;
                    InvokeAsync(StateHasChanged);
                    return;
                }

                // Raster: resolve grey pixel → value category label
                if (entry?.IsRaster == true &&
                    props.TryGetProperty("_rasterGrey", out var greyProp) &&
                    greyProp.ValueKind == JsonValueKind.Number &&
                    entry.RasterThresholds.Count > 0)
                {
                    var grey = greyProp.GetDouble();
                    // Find the highest threshold that does not exceed the grey value.
                    var thresholds = entry.RasterThresholds;
                    var matchLabel = thresholds[0].Label; // default: lowest bucket
                    for (int i = 0; i < thresholds.Count; i++)
                    {
                        if (grey >= thresholds[i].NormalisedThreshold)
                            matchLabel = thresholds[i].Label;
                        else
                            break;
                    }
                    if (!string.IsNullOrWhiteSpace(matchLabel))
                        _popupProps.Add(("Category", matchLabel));
                }
                else
                {
                    // If the feature has a type index, prepend the layer_type display name
                    if (props.TryGetProperty("_mspType", out var mspTypeProp) &&
                        entry?.TypeDefs is { Count: > 0 } typeDefs)
                    {
                        var typeIdx = mspTypeProp.ValueKind == JsonValueKind.Number
                            ? mspTypeProp.GetInt32()
                            : mspTypeProp.ValueKind == JsonValueKind.String &&
                              int.TryParse(mspTypeProp.GetString(), out var parsed) ? parsed : -1;
                        if (typeIdx >= 0 && typeIdx < typeDefs.Count &&
                            !string.IsNullOrWhiteSpace(typeDefs[typeIdx].Label))
                        {
                            _popupProps.Add(("Type", typeDefs[typeIdx].Label));
                        }
                    }

                    foreach (var prop in props.EnumerateObject())
                    {
                        if (prop.Name.StartsWith('_')) continue; // skip internal props
                        var label = entry?.PropertyDisplayNames.TryGetValue(prop.Name, out var dn) == true
                            ? (string.IsNullOrWhiteSpace(dn) ? prop.Name : dn)
                            : prop.Name;
                        var value = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? ""
                            : prop.Value.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            _popupProps.Add((label, value));
                    }
                }
            }

            _popupVisible = true;
            InvokeAsync(StateHasChanged);
        }
        catch { /* ignore parse errors */ }
    }

    private void OnWsMessageReceived(WsMessage msg)
    {
        lock (_wsLog)
        {
            _wsLog.Insert(0, (msg.HeaderName, msg.RawJson, msg.ReceivedAt));
            if (_wsLog.Count > WsLogMaxEntries)
                _wsLog.RemoveAt(_wsLog.Count - 1);
        }

        // Data parsing is handled by GameSessionState which also subscribes to MessageReceived.
        if (msg.HeaderName == "Game/Latest" && _planMessagesOpen && GameSessionState.SelectedPlanId != 0)
            _scrollPlanMessagesPending = true;

        if (msg.HeaderName == "Game/Latest" && _editWsConfirmationTcs is { } confirmTcs)
            confirmTcs.TrySetResult(true);

        // Handle Batch/ExecuteBatch response to get new plan ID
        if (msg.HeaderName == "Batch/ExecuteBatch" && !string.IsNullOrEmpty(_pendingBatchGuid))
        {
            try
            {
                // Parse raw JSON to get header_data.batch_guid (not in Payload)
                using var doc = System.Text.Json.JsonDocument.Parse(msg.RawJson);
                var root = doc.RootElement;
                
                string? batchGuid = null;
                if (root.TryGetProperty("header_data", out var headerData) &&
                    headerData.TryGetProperty("batch_guid", out var guidProp))
                {
                    batchGuid = guidProp.GetString();
                }

                if (batchGuid == _pendingBatchGuid && msg.Payload.TryGetProperty("results", out var results))
                {
                    foreach (var result in results.EnumerateArray())
                    {
                        if (result.TryGetProperty("call_id", out var callIdProp) &&
                            callIdProp.GetInt32() == _pendingCreatePlanCallId &&
                            result.TryGetProperty("payload", out var payloadProp))
                        {
                            var planIdStr = payloadProp.GetString();
                            if (int.TryParse(planIdStr, out var newPlanId))
                            {
                                _pendingSelectPlanId = newPlanId;
                                _pendingBatchGuid = null;
                                _pendingCreatePlanCallId = 0;
                                if (HostEnvironment.IsDevelopment())
                                    Console.WriteLine($"[OnWsMessageReceived] New plan ID from batch: {newPlanId}");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (HostEnvironment.IsDevelopment())
                    Console.WriteLine($"[OnWsMessageReceived] Error parsing Batch/ExecuteBatch: {ex.Message}");
            }
        }

        if (_pendingSelectPlanId != 0)
        {
            var newPlan = _plans.FirstOrDefault(p => p.PlanId == _pendingSelectPlanId);
            if (newPlan is not null)
            {
                _pendingSelectPlanId  = 0;
                var enterEdit = _pendingEnterEditMode;
                _pendingEnterEditMode = false;
                _ = InvokeAsync(async () =>
                {
                    await SelectPlanAsync(newPlan);
                    if (enterEdit) await EnterEditModeAsync();
                });
                return;
            }
        }

        InvokeAsync(StateHasChanged);
    }

    public void ToggleTimeManager()
    {
        if (IsAdmin)
            _timeManagerVisible = _timeManagerVisible ? false : true;
    }

    

    private async Task CopyWsMessageAsync(string raw)
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", raw);
        _wsCopied = true;
        StateHasChanged();
        await Task.Delay(1500);
        _wsCopied = false;
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        WsService.MessageReceived -= OnWsMessageReceived;
        // GameWebSocketService lifetime is managed by DI (circuit scope); do not dispose here.
        if (_mapModule is not null)
        {
            try
            {
                var viewState = await _mapModule.InvokeAsync<JsonElement>("getViewState");
                if (viewState.ValueKind == JsonValueKind.Object)
                {
                    var lat = GetDouble(viewState, "lat");
                    var lng = GetDouble(viewState, "lng");
                    var zoom = GetDouble(viewState, "zoom");

                    if (!double.IsNaN(lat) && !double.IsNaN(lng) && !double.IsNaN(zoom))
                        GameState.SaveMapView(lat, lng, zoom);
                }

                await _mapModule.InvokeVoidAsync("unregisterClickHandler");
                await _mapModule.DisposeAsync();
            }
            catch { }
        }
        _dotNetRef?.Dispose();
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.NaN;
    }
}
