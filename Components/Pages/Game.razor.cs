using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Game : IAsyncDisposable
{
    [Inject] NavigationManager NavigationManager { get; set; } = null!;
    [Inject] IHostEnvironment HostEnvironment { get; set; } = null!;
    [Inject] UserSessionService SessionState { get; set; } = null!;
    [Inject] MspApiClient ApiClient { get; set; } = null!;
    [Inject] WebSocketService WsService { get; set; } = null!;
    [Inject] GameSessionService GameSessionService { get; set; } = null!;
    [Inject] GameUIStateService GameUIStateService { get; set; } = null!;

    public SideBarPanelControl SideBarPanelController { get; set; } = null!;
    public GameTimeView GameTimeViewer { get; set; } = null!;
    public MapViewPort Map { get; set; } = null!;
    public PlanDetails PlanDetailsPanel { get; set; } = null!;
    public PlanCreation PlanCreationPanel { get; set; } = null!;
    // Returns the selected plan or a new unsaved plan for edit mode
    private IJSObjectReference? _mapModule;
    private DotNetObjectReference<Game>? _dotNetRef;

    // ── Loading & UI state ────────────────────────────────────────────────────
    private bool    _isLoading     = true;
    private bool    _loadingFading = false;
    private string  _loadingStatus = "Loading\u2026";
    private string? errorMessage;
    private string? errorDetail;

    // ── Feature popup ─────────────────────────────────────────────────────────
    private bool                   _popupVisible;
    private double                 _popupX;
    private double                 _popupY;
    private string?                _popupLayerName;
    private List<(string, string)> _popupProps = [];

     // ── WebSocket log ─────────────────────────────────────────────────────────
    private const int WsLogMaxEntries = 100;
    private readonly List<(string HeaderName, string Raw, DateTime ReceivedAt)> _wsLog = [];
    private bool _wsLogVisible;

    protected override void OnInitialized()
    {
        GameSessionService.Changed += OnGameSessionServiceChanged;
    }

    private void ClosePopup()
    {
        _popupVisible = false;
        StateHasChanged();
    }

    private void OpenEditModeFromCreation(PlanCreation creation)
    {
        // Seed the pending values into GameSessionService so PlanDetails can read them
        // when it mounts. Setting EditMode=true and SelectedPlanId=0 here causes Blazor
        // to render PlanDetails on the next cycle; StartNewPlanEdit is then called on the
        // already-mounted instance via the NotifyChanged callback that PlanDetails subscribes to.
        GameUIStateService.CreatePlanOpen = false;
        GameUIStateService.SelectedPlanId = 0;
        GameUIStateService.EditMode = true;
        // Store the creation-form values so PlanDetails.OnStateChanged can pick them up.
        GameUIStateService.PendingNewPlanName        = creation._createPlanName ?? string.Empty;
        GameUIStateService.PendingNewPlanDescription = creation._createPlanDescription ?? string.Empty;
        GameUIStateService.PendingNewPlanStartYear   = creation._createPlanStartYear;
        GameUIStateService.PendingNewPlanStartMonth  = creation._createPlanStartMonth;
        GameSessionService.NotifyChanged();
    }

    private async Task EnsureRestrictionLayersVisibleAsync(string? sourceDisplayName, string? targetDisplayName)
    {
        if (Map is null) return;
        
        bool changed = false;
        foreach (string? name in new[] { sourceDisplayName, targetDisplayName })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var le = GameSessionService.LayerEntries.FirstOrDefault(e =>
                string.Equals(e.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.LayerName, name, StringComparison.OrdinalIgnoreCase));
            if (le is null || le.Visible) continue;
            await Map.ToggleLayerAsync(le, true);
            changed = true;
        }
        if (changed) StateHasChanged();
    }

    private void CloseWsLog()
    {
        _wsLogVisible = false;
        StateHasChanged();
    }

    private void ClearWsLog()
    {
        lock (_wsLog)
        {
            _wsLog.Clear();
        }
        StateHasChanged();
    }

    private void OnGameSessionServiceChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        // Capture cold-start status before async work so warm returns can skip map refit.
        var isColdStart = _isLoading;

        // Wait for MapViewPort to import map.js in its own OnAfterRenderAsync.
        await Map.WhenReady;
        _mapModule = Map.MapJSModule!;

        // initMap sets an EPSG:3035 placeholder view that is replaced immediately below.
        await _mapModule.InvokeVoidAsync("initMap", "map");

        // On warm return, restore saved camera immediately for instant visual feedback.
        if (!isColdStart && GameUIStateService.HasSavedMapView)
        {
            await _mapModule.InvokeVoidAsync(
                "setView",
                GameUIStateService.MapLat!.Value,
                GameUIStateService.MapLng!.Value,
                GameUIStateService.MapZoom!.Value,
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
            await GameSessionService.EnsureInitializedAsync(
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

        // Always materialize the base layer first (needed for fitToPlayArea and z-index reference).
        var baseLayer = GameSessionService.LayerEntries.FirstOrDefault(e => e.IsBaseLayer);
        if (baseLayer is not null)
        {
            var baseSnapshot = GameSessionService.MapLayerSnapshots.FirstOrDefault(s => s.LayerId == baseLayer.LayerId);
            if (baseSnapshot is not null)
                await EnsureLayerRenderedAsync(baseLayer.LayerId, visible: true);
        }

        foreach (var snapshot in GameSessionService.MapLayerSnapshots)
        {
            var entry = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == snapshot.LayerId);
            if (entry?.IsBaseLayer == true) continue;  // Already materialized above.

            var visible = entry?.Visible ?? snapshot.Visible;

            // Warm navigation: only materialize currently visible layers for faster return.
            if (!visible) continue;

            await EnsureLayerRenderedAsync(snapshot.LayerId, visible: true);
        }

        if (Map is not null)
        {
            await Map.SyncZIndicesAsync();
        }

        if (fitToPlayArea && baseLayer is not null)
        {
            await _mapModule.InvokeVoidAsync("fitToPlayArea", baseLayer.LayerId);
        }
    }

    private async Task EnsureLayerRenderedAsync(string layerId, bool visible)
    {
        if (_mapModule is null) return;

        var snapshot = GameSessionService.MapLayerSnapshots.FirstOrDefault(s => s.LayerId == layerId);
        if (snapshot is null) return;

        var entry = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
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

    // ── Legend reorder (called from JS via _dotNetRef) ────────────────────────

    [JSInvokable]
    public async Task ReorderLegend(int from, int to)
    {
        if (from == to || from < 0 || to < 0
            || from >= GameUIStateService.LegendOrderLayerIds.Count
            || to   >= GameUIStateService.LegendOrderLayerIds.Count) return;
        var item = GameUIStateService.LegendOrderLayerIds[from];
        GameUIStateService.LegendOrderLayerIds.RemoveAt(from);
        GameUIStateService.LegendOrderLayerIds.Insert(to, item);
        await Map.SyncZIndicesAsync();
        GameSessionService.NotifyChanged();
    }

    // ── Map Click Handler ──────────────────────────────────────────────────────

    [JSInvokable]
    public void OnMapClick(string json)
    {
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

            var entry   = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == resolvedLayerId);

            _popupX = root.TryGetProperty("clientX", out var cx) && cx.ValueKind == JsonValueKind.Number ? cx.GetDouble() : 0;
            _popupY = root.TryGetProperty("clientY", out var cy) && cy.ValueKind == JsonValueKind.Number ? cy.GetDouble() : 0;
            _popupLayerName = entry?.DisplayName ?? "Planned geometry";
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

        // Data parsing is handled by GameSessionService which also subscribes to MessageReceived.
        // Plan selection after batch creation is handled by PlanDetailsSave component.

        InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        GameSessionService.Changed -= OnGameSessionServiceChanged;
        WsService.MessageReceived -= OnWsMessageReceived;
        // GameWebSocketService lifetime is managed by DI (circuit scope); do not dispose here.
        if (_mapModule is not null)
        {
            try
            {
                var viewState = await _mapModule.InvokeAsync<JsonElement>("getViewState");
                if (viewState.ValueKind == JsonValueKind.Object)
                {
                    var lat = viewState.TryGetProperty("lat", out var latValue) && latValue.ValueKind == JsonValueKind.Number
                        ? latValue.GetDouble() : double.NaN;
                    var lng = viewState.TryGetProperty("lng", out var lngValue) && lngValue.ValueKind == JsonValueKind.Number
                        ? lngValue.GetDouble() : double.NaN;
                    var zoom = viewState.TryGetProperty("zoom", out var zoomValue) && zoomValue.ValueKind == JsonValueKind.Number
                        ? zoomValue.GetDouble() : double.NaN;
                    if (!double.IsNaN(lat) && !double.IsNaN(lng) && !double.IsNaN(zoom))
                        GameUIStateService.SaveMapView(lat, lng, zoom);
                }

                await _mapModule.InvokeVoidAsync("unregisterClickHandler");
                await _mapModule.DisposeAsync();
            }
            catch { }
        }
        _dotNetRef?.Dispose();
    }
}
