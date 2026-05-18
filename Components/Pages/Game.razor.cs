using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Game : IAsyncDisposable
{
    private IJSObjectReference? _mapModule;
    private DotNetObjectReference<Game>? _dotNetRef;
    private string? errorMessage;
    private string? errorDetail;
    /// <summary>Visible non-base layers in z-order: index 0 = bottom, last = top.</summary>
    private readonly List<LayerEntry> _legendOrder = new();
    private string _layerSearch = "";

    // ── Proxy properties — forward to GameSessionState so markup needs no changes ──
    private List<LayerEntry>         _layerEntries    => GameState.LayerEntries;
    private Dictionary<int, string>  _countryColours  => GameState.CountryColours;
    private Dictionary<int, string>  _countryNames    => GameState.CountryNames;
    private IReadOnlyList<PlanEntry> _plans           => GameState.Plans;
    private int    _gameStartYear    => GameState.GameStartYear;
    private int    _gameEndMonth     => GameState.GameEndMonth;
    private int    _gameEndYear      => GameState.GameEndYear;
    private int    _gameCurrentMonth => GameState.GameCurrentMonth;
    private double _eraTimeLeft      => GameState.EraTimeLeft;

    // Properties popup state
    private bool   _popupVisible;
    private string _popupLayerName = "";
    private readonly List<(string Label, string Value)> _popupProps = new();
    private double _popupX;
    private double _popupY;

    // Sidebar panel open/close state
    private bool _layerPanelOpen  = true;
    private bool _legendPanelOpen = true;
    private bool _usersPanelOpen;

    // Online users panel
    private record UserEntry(string Name, int CountryId, string Colour);
    private List<UserEntry> _users = new();
    private bool   _usersLoading;
    private string? _usersError;

    // Loading state
    private bool   _isLoading = true;
    private bool   _loadingFading;
    private string _loadingStatus = "Initialising…";

    // Plans panel
    private bool _plansPanelOpen;
    private int _selectedPlanId;
    private bool _planMessagesOpen;
    private bool _scrollPlanMessagesPending;
    private string _planMessageDraft = string.Empty;
    private bool _sendingPlanMessage;
    private string? _planMessageSendError;
    private PlanViewMode _planViewMode = PlanViewMode.AfterChanges;
    private bool _detailDescExpanded;
    private readonly HashSet<string> _planActivatedLayerIds  = new();
    private readonly HashSet<string> _planReferencedLayerIds = new();

    // Dev console log (capped list of recent raw WS messages)
    private const int WsLogMaxEntries = 100;
    private readonly List<(string HeaderName, string Raw, DateTime ReceivedAt)> _wsLog = new();
    private string? _wsSelectedRaw;
    private bool    _wsLogVisible;

    private static string PrettyPrintJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return raw; }
    }

    protected override Task OnInitializedAsync()
    {
        if (string.IsNullOrEmpty(SessionState.ApiAccessToken) ||
            SessionState.SessionId == 0 ||
            string.IsNullOrEmpty(SessionState.GameServerAddress))
        {
            NavigationManager.NavigateTo("/");
            return Task.CompletedTask;
        }

        // Returning to /game in the same circuit should be instant.
        _isLoading = !GameState.IsGameDataLoaded;
        _loadingFading = false;

        // Restore panel layout from shared session state.
        _layerPanelOpen = GameState.LayerPanelOpen;
        _legendPanelOpen = GameState.LegendPanelOpen;
        _usersPanelOpen = GameState.UsersPanelOpen;
        _plansPanelOpen = GameState.PlansPanelOpen;

        return Task.CompletedTask;
    }

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

    private async Task LoadUsersAsync()
    {
        _usersLoading = true;
        _usersError = null;
        StateHasChanged();
        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{SessionState.SessionId}/api/User/List";
            bool isAdmin = SessionState.CountryId == 1 || SessionState.CountryId == 2;
            JsonElement root = isAdmin
                ? await ApiClient.GetAsync(url)
                : await ApiClient.PostFormAsync(url, new[] { new KeyValuePair<string, string>("country_id", SessionState.CountryId.ToString()) });
            var payload = root.TryGetProperty("payload", out var p) ? p : root;
            var list = new List<UserEntry>();
            if (payload.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in payload.EnumerateArray())
                {
                    var name = u.TryGetProperty("user_name",       out var n) ? n.GetString() ?? "" : "";
                    var cid  = u.TryGetProperty("user_country_id", out var c)
                        ? (c.ValueKind == JsonValueKind.Number ? c.GetInt32()
                           : int.TryParse(c.GetString(), out var parsed) ? parsed : 0)
                        : 0;
                    var col  = _countryColours.GetValueOrDefault(cid, "#6c757d");
                    list.Add(new UserEntry(name, cid, col));
                }
            }
            _users = isAdmin
                ? list.OrderBy(u => u.CountryId).ThenBy(u => u.Name).ToList()
                : list.OrderBy(u => u.Name).ToList();
        }
        catch (Exception ex)
        {
            _usersError = ex.Message;
        }
        finally
        {
            _usersLoading = false;
            StateHasChanged();
        }
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

    private void SetLayerPanelOpen(bool open)
    {
        _layerPanelOpen = open;
        GameState.LayerPanelOpen = open;
    }

    private void SetLegendPanelOpen(bool open)
    {
        _legendPanelOpen = open;
        GameState.LegendPanelOpen = open;
    }

    private void SetPlansPanelOpen(bool open)
    {
        _plansPanelOpen = open;
        GameState.PlansPanelOpen = open;
    }

    private async Task SetUsersPanelOpenAsync(bool open)
    {
        _usersPanelOpen = open;
        GameState.UsersPanelOpen = open;
        if (open)
            await LoadUsersAsync();
    }

    private async Task ToggleUsersPanelAsync()
    {
        await SetUsersPanelOpenAsync(!_usersPanelOpen);
    }

    private void TogglePlansPanel()
    {
        SetPlansPanelOpen(!_plansPanelOpen);
    }

    private void ToggleLayerPanel()
    {
        SetLayerPanelOpen(!_layerPanelOpen);
    }

    private void ToggleLegendPanel()
    {
        SetLegendPanelOpen(!_legendPanelOpen);
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

    /// <summary>Inline style for the users sidebar button: tinted with the country colour.</summary>
    private static string CountryBtnStyle(string hex, bool active)
    {
        if (string.IsNullOrEmpty(hex) || !hex.StartsWith('#')) return "";
        var h = hex.TrimStart('#');
        if (h.Length < 6) return "";
        int r = Convert.ToInt32(h[..2], 16);
        int g = Convert.ToInt32(h[2..4], 16);
        int b = Convert.ToInt32(h[4..6], 16);
        double a = active ? 0.40 : 0.20;
        return $"background:rgba({r},{g},{b},{a:F2});color:#fff;";
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

            var entry   = _layerEntries.FirstOrDefault(e => e.LayerId == resolvedLayerId);

            _popupX = root.TryGetProperty("clientX", out var cx) && cx.ValueKind == JsonValueKind.Number ? cx.GetDouble() : 0;
            _popupY = root.TryGetProperty("clientY", out var cy) && cy.ValueKind == JsonValueKind.Number ? cy.GetDouble() : 0;
            _popupLayerName = entry?.DisplayName ?? resolvedLayerId;
            _popupProps.Clear();

            if (root.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            {
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
        if (msg.HeaderName == "Game/Latest" && _planMessagesOpen && _selectedPlanId != 0)
            _scrollPlanMessagesPending = true;

        InvokeAsync(StateHasChanged);
    }

    private enum PlanViewMode { AfterChanges, Original, ChangesOnly }

    private string MonthToDate(int month) => GameState.MonthToDate(month);

    private static string PlanStateLabel(string state) => GameSessionState.PlanStateLabel(state);

    private string GameStateLabel => GameSessionState.GameStateLabel(GameState.GameState);

    private static string FormatTimeLeft(double totalSeconds) => GameSessionState.FormatTimeLeft(totalSeconds);

    private async Task SelectPlanAsync(PlanEntry plan)
    {
        if (_mapModule is null) return;

        // Restore any base layers hidden by a previous ChangesOnly view.
        if (_planViewMode == PlanViewMode.ChangesOnly)
        {
            foreach (var id in _planReferencedLayerIds)
                if (!_planActivatedLayerIds.Contains(id))
                    await _mapModule.InvokeVoidAsync("setLayerVisible", id, true);
        }

        // Revert layers that were activated by the previous plan selection.
        foreach (var layerId in _planActivatedLayerIds)
        {
            var le = _layerEntries.FirstOrDefault(e => e.LayerId == layerId);
            if (le is not null)
                await ToggleLayerAsync(le, false);
        }
        _planActivatedLayerIds.Clear();
        _planReferencedLayerIds.Clear();

        if (_selectedPlanId == plan.PlanId)
        {
            // Toggling the same plan off.
            _selectedPlanId = 0;
            _planViewMode = PlanViewMode.AfterChanges;
            await _mapModule.InvokeVoidAsync("clearPlanOverlay");
            StateHasChanged();
            return;
        }

        _selectedPlanId = plan.PlanId;
        _planViewMode   = PlanViewMode.AfterChanges;
        _detailDescExpanded = false;
        _planMessagesOpen = false;
        _planMessageDraft = string.Empty;
        _planMessageSendError = null;

        // Collect all referenced original layer IDs.
        foreach (var planLayer in plan.Layers)
            if (!string.IsNullOrEmpty(planLayer.OriginalLayerId))
                _planReferencedLayerIds.Add(planLayer.OriginalLayerId);

        // Activate any referenced base layers that are currently hidden.
        foreach (var planLayer in plan.Layers)
        {
            if (string.IsNullOrEmpty(planLayer.OriginalLayerId)) continue;
            var le = _layerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            if (le is null || le.Visible) continue;
            await ToggleLayerAsync(le, true);
            _planActivatedLayerIds.Add(le.LayerId);
        }

        // Build and show the plan geometry overlay.
        var layersData = new List<object>();
        foreach (var planLayer in plan.Layers)
        {
            var geoType = _layerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId)?.GeoType
                       ?? InferGeoType(planLayer.Geometry);

            var geometries = planLayer.Geometry
                .Select(g => new {
                    coords   = g.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray(),
                    isNew    = string.IsNullOrEmpty(g.PersistentId) || g.Id == g.PersistentId,
                    mspType  = g.TypeIndex
                })
                .ToArray();

            layersData.Add(new {
                geoType,
                geometries,
                originalLayerId = planLayer.OriginalLayerId,
                deletedIds      = planLayer.DeletedPersistentIds
            });
        }

        if (layersData.Count > 0)
            await _mapModule.InvokeVoidAsync("showPlanGeometry", JsonSerializer.Serialize(layersData));
        else
            await _mapModule.InvokeVoidAsync("clearPlanOverlay");

        StateHasChanged();
    }

    private async Task SetPlanViewModeAsync(PlanViewMode mode)
    {
        if (_mapModule is null || _selectedPlanId == 0 || mode == _planViewMode) return;
        _planViewMode = mode;

        // Overlay is hidden only in Original mode.
        await _mapModule.InvokeVoidAsync("setPlanOverlayVisible", mode != PlanViewMode.Original);

        // Referenced base layers are hidden only in ChangesOnly mode.
        bool showBase = mode != PlanViewMode.ChangesOnly;
        foreach (var layerId in _planReferencedLayerIds)
            await _mapModule.InvokeVoidAsync("setLayerVisible", layerId, showBase);

        StateHasChanged();
    }

    private async Task ClosePlanDetailAsync()
    {
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan is not null)
            await SelectPlanAsync(plan);
    }

    private void TogglePlanMessagesPanel()
    {
        if (_selectedPlanId == 0) return;
        _planMessagesOpen = !_planMessagesOpen;
        if (_planMessagesOpen)
            _scrollPlanMessagesPending = true;
        _planMessageSendError = null;
    }

    private IReadOnlyList<PlanMessageEntry> SelectedPlanMessages => GameState.GetPlanMessages(_selectedPlanId);

    private bool CanSendPlanMessage =>
        _selectedPlanId != 0 &&
        !_sendingPlanMessage &&
        !string.IsNullOrWhiteSpace(_planMessageDraft);

    private async Task SendPlanMessageAsync()
    {
        if (!CanSendPlanMessage) return;

        _sendingPlanMessage = true;
        _planMessageSendError = null;

        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{SessionState.SessionId}/api/Plan/Message";

            var fields = new List<KeyValuePair<string, string>>
            {
                new("plan", _selectedPlanId.ToString()),
                new("team_id", SessionState.CountryId.ToString()),
                new("user_name", string.IsNullOrWhiteSpace(SessionState.UserName) ? $"Team {SessionState.CountryId}" : SessionState.UserName),
                new("text", _planMessageDraft.Trim())
            };

            await ApiClient.PostFormAsync(url, fields);

            // Do not append locally. The authoritative message arrives via Game/Latest WebSocket.
            _planMessageDraft = string.Empty;
        }
        catch (Exception ex)
        {
            _planMessageSendError = ex.Message;
        }
        finally
        {
            _sendingPlanMessage = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task HandlePlanMessageKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await SendPlanMessageAsync();
    }

    private static string FormatPlanMessageTime(DateTime sentAt)
    {
        var utc = sentAt.Kind switch
        {
            DateTimeKind.Utc => sentAt,
            DateTimeKind.Local => sentAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(sentAt, DateTimeKind.Utc)
        };
        return utc.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
    }

    private string PlanMessageDotColour(int? countryId)
    {
        if (!countryId.HasValue || countryId.Value <= 0)
            return "#6c757d";
        if (countryId.Value == 1 || countryId.Value == 2)
            return "#ff69b4";
        return _countryColours.GetValueOrDefault(countryId.Value, "#6c757d");
    }

    /// <summary>
    /// Infers point/line/polygon from coordinate count when layer metadata is unavailable.
    /// </summary>
    private static string InferGeoType(IReadOnlyList<PlanGeometryItem> geometries)
    {
        if (geometries.Count == 0) return "point";
        var pts = geometries[0].Coordinates;
        if (pts.Count <= 1) return "point";
        if (pts.Count > 3)
        {
            var first = pts[0]; var last = pts[^1];
            if (Math.Abs(first[0] - last[0]) < 1.0 && Math.Abs(first[1] - last[1]) < 1.0)
                return "polygon";
        }
        return "line";
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
