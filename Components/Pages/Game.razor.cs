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
    private bool _planIssuesOpen;
    private bool _planApprovalOpen;
    private bool _scrollPlanMessagesPending;
    private string _planMessageDraft = string.Empty;
    private bool _sendingPlanMessage;
    private string? _planMessageSendError;
    private PlanViewMode _planViewMode = PlanViewMode.AfterChanges;
    private bool _detailDescExpanded;
    private readonly HashSet<string> _planActivatedLayerIds  = new();
    private readonly HashSet<string> _planReferencedLayerIds = new();
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _parsedLayerGeometryCache    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _projectedGeometryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PlanRestrictionIssue> _selectedPlanIssues = new();
    private readonly Dictionary<int, string> _planIssueSeverity = new();

    private sealed record ParsedLayerGeometry(string FeatureId, int TypeIndex, IReadOnlyList<double[]> Coordinates);
    private sealed record PlanRestrictionIssue(
        string Severity,
        string Message,
        string SourceLayer,
        string TargetLayer,
        string ChangeKind,
        double MarkerX,
        double MarkerY);
    private sealed record ApprovalRequirement(int CountryId, string CountryName, IReadOnlyList<string> Reasons);
    private readonly List<ApprovalRequirement> _approvalRequired = new();
    private readonly HashSet<int> _approvalReasonsExpanded = new();

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

            // If a plan is currently selected, immediately project prior-plan changes onto
            // this layer so the user sees the correct world state without re-selecting the plan.
            if (_selectedPlanId != 0)
            {
                var viewedPlan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
                if (viewedPlan is not null)
                    await ApplyPlanProjectionAsync(viewedPlan.StartDate, entry.LayerId);
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

    private async Task FocusPlanIssueAsync(PlanRestrictionIssue issue)
    {
        if (_mapModule is null) return;
        await EnsureRestrictionLayersVisibleAsync(issue.SourceLayer, issue.TargetLayer);
        await _mapModule.InvokeVoidAsync("focusOnCoordinate", issue.MarkerX, issue.MarkerY);
    }

    private async Task EnsureRestrictionLayersVisibleAsync(string? sourceDisplayName, string? targetDisplayName)
    {
        var changed = false;
        foreach (var name in new[] { sourceDisplayName, targetDisplayName })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var le = _layerEntries.FirstOrDefault(e =>
                string.Equals(e.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.LayerName,   name, StringComparison.OrdinalIgnoreCase));
            if (le is null || le.Visible) continue;
            await ToggleLayerAsync(le, true);
            changed = true;
        }
        if (changed) StateHasChanged();
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
        if (msg.HeaderName == "Game/Latest" && _planMessagesOpen && _selectedPlanId != 0)
            _scrollPlanMessagesPending = true;

        InvokeAsync(StateHasChanged);
    }

    private enum PlanViewMode { AfterChanges, Original, ChangesOnly }

    private string MonthToDate(int month) => GameState.MonthToDate(month);

    private static string PlanStateLabel(string state) => GameSessionState.PlanStateLabel(state);

    private string GameStateLabel => GameSessionState.GameStateLabel(GameState.GameState);

    private static string FormatTimeLeft(double totalSeconds) => GameSessionState.FormatTimeLeft(totalSeconds);

    private int SelectedPlanIssueCount => _selectedPlanIssues.Count;
    private IReadOnlyList<PlanRestrictionIssue> SelectedPlanIssues => _selectedPlanIssues;

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
        _projectedGeometryCache.Clear();
        await _mapModule.InvokeVoidAsync("clearPlanProjection");

        if (_selectedPlanId == plan.PlanId)
        {
            // Toggling the same plan off.
            _selectedPlanId = 0;
            _planViewMode = PlanViewMode.AfterChanges;
            _planIssuesOpen = false;
            _planApprovalOpen = false;
            _selectedPlanIssues.Clear();
            _approvalRequired.Clear();
            _approvalReasonsExpanded.Clear();
            await _mapModule.InvokeVoidAsync("clearPlanOverlay");
            StateHasChanged();
            return;
        }

        _selectedPlanId = plan.PlanId;
        _planViewMode   = PlanViewMode.AfterChanges;
        _detailDescExpanded = false;
        _planMessagesOpen = false;
        _planIssuesOpen = false;
        _planApprovalOpen = false;
        _planMessageDraft = string.Empty;
        _planMessageSendError = null;
        _selectedPlanIssues.Clear();
        _approvalRequired.Clear();
        _approvalReasonsExpanded.Clear();

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

            var geometries = new List<object>();
            foreach (var geometry in planLayer.Geometry)
            {
                var isNewGeometry = string.IsNullOrEmpty(geometry.PersistentId) || geometry.Id == geometry.PersistentId;
                var geometryIssues = EvaluateRestrictionsForGeometry(planLayer.OriginalLayerId, geoType, geometry, isNewGeometry, plan.StartDate);
                _selectedPlanIssues.AddRange(geometryIssues);

                geometries.Add(new {
                    coords = geometry.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray(),
                    isNew = isNewGeometry,
                    mspType = geometry.TypeIndex,
                    restrictionMarkers = geometryIssues
                        .Select(r => new
                        {
                            severity = NormaliseSeverity(r.Severity),
                            message = r.Message,
                            sourceLayer = r.SourceLayer,
                            targetLayer = r.TargetLayer,
                            changeKind = r.ChangeKind,
                            coord = new[] { r.MarkerX, r.MarkerY }
                        })
                        .ToArray(),
                    restrictions = geometryIssues
                        .Select(r => NormaliseSeverity(r.Severity))
                        .Where(r => !string.IsNullOrEmpty(r))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                });
            }

            layersData.Add(new {
                geoType,
                geometries = geometries.ToArray(),
                originalLayerId = planLayer.OriginalLayerId,
                deletedIds      = planLayer.DeletedPersistentIds
            });
        }

        _selectedPlanIssues.Sort((a, b) =>
        {
            var s = SeveritySortRank(b.Severity).CompareTo(SeveritySortRank(a.Severity));
            if (s != 0) return s;
            s = string.Compare(a.TargetLayer, b.TargetLayer, StringComparison.OrdinalIgnoreCase);
            return s != 0 ? s : string.Compare(a.Message, b.Message, StringComparison.OrdinalIgnoreCase);
        });

        // Record the worst severity for this plan so the plans-panel can show an issue badge.
        if (_selectedPlanIssues.Count > 0)
            _planIssueSeverity[plan.PlanId] = NormaliseSeverity(_selectedPlanIssues[0].Severity);
        else
            _planIssueSeverity.Remove(plan.PlanId);

        if (layersData.Count > 0)
            await _mapModule.InvokeVoidAsync("showPlanGeometry", JsonSerializer.Serialize(layersData));
        else
            await _mapModule.InvokeVoidAsync("clearPlanOverlay");

        await ApplyPlanProjectionAsync(plan.StartDate);

        if (!IsApprovalCompleteState(plan.State))
            CalculateApproval(plan);

        StateHasChanged();
    }

    /// <summary>
    /// Computes and sends the world-state projection to the map for all earlier finalised plans
    /// relative to <paramref name="planStartDate"/>.
    /// Pass <paramref name="singleLayerId"/> to restrict processing to one layer — used when
    /// the user activates a layer from the panel while a plan is already selected.
    /// </summary>
    private async Task ApplyPlanProjectionAsync(int planStartDate, string? singleLayerId = null)
    {
        if (_mapModule is null) return;

        var priorPlans = _plans
            .Where(p => p.StartDate < planStartDate && IsFinalisedPlanState(p.State))
            .OrderBy(p => p.StartDate).ThenBy(p => p.PlanId)
            .ToList();
        if (priorPlans.Count == 0) return;

        var hiddenFeatures = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var addedFeatures  = new List<object>();

        foreach (var priorPlan in priorPlans)
        {
            foreach (var planLayer in priorPlan.Layers)
            {
                if (string.IsNullOrEmpty(planLayer.OriginalLayerId)) continue;
                if (singleLayerId is not null &&
                    !string.Equals(planLayer.OriginalLayerId, singleLayerId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!hiddenFeatures.TryGetValue(planLayer.OriginalLayerId, out var ids))
                    hiddenFeatures[planLayer.OriginalLayerId] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var deletedId in planLayer.DeletedPersistentIds)
                    ids.Add(deletedId);

                var geoType = _layerEntries
                    .FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId)?.GeoType ?? "";

                foreach (var geo in planLayer.Geometry)
                {
                    if (!string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id)
                        ids.Add(geo.PersistentId);

                    if (geo.Coordinates.Count > 0)
                        addedFeatures.Add(new {
                            layerId   = planLayer.OriginalLayerId,
                            geoType,
                            id        = geo.Id,
                            typeIndex = geo.TypeIndex,
                            coords    = geo.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray()
                        });
                }
            }
        }

        if (hiddenFeatures.Count > 0 || addedFeatures.Count > 0)
            await _mapModule.InvokeVoidAsync("applyPlanProjection",
                JsonSerializer.Serialize(new {
                    hidden = hiddenFeatures.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()),
                    added  = addedFeatures
                }));
    }

    private IReadOnlyList<PlanRestrictionIssue> EvaluateRestrictionsForGeometry(
        string sourceLayerId,
        string sourceGeoType,
        PlanGeometryItem geometry,
        bool isNewGeometry,
        int planStartDate)
    {
        if (GameState.Restrictions.Count == 0 || geometry.Coordinates.Count == 0)
            return [];

        var sourceLayer = _layerEntries.FirstOrDefault(l => string.Equals(l.LayerId, sourceLayerId, StringComparison.OrdinalIgnoreCase));
        if (sourceLayer is null)
            return [];

        var matches = new List<PlanRestrictionIssue>();
        // Deduplicate: same message at the same map position is considered one issue regardless
        // of whether it comes from duplicate restriction rules or coincident target geometries.
        var seenIssueKeys = new HashSet<(string severity, string message, string src, string tgt, double x, double y)>();

        foreach (var rule in GameState.Restrictions)
        {
            if (!RuleLayerMatches(rule.StartLayer, sourceLayer)
                || !RestrictionTypeMatches(rule.StartType, geometry.TypeIndex, sourceLayer))
                continue;

            var targetLayer = FindLayerByRuleName(rule.EndLayer);
            var targetType = rule.EndType;

            if (targetLayer is null || targetLayer.IsRaster)
                continue;

            var targetGeometries = GetProjectedLayerGeometries(targetLayer.LayerId, planStartDate);
            var constraintSort = NormaliseConstraintSort(rule.Sort);
            var sourceMarker = GetGeometryCenter(geometry.Coordinates);
            var overlapFound = false;

            foreach (var targetGeometry in targetGeometries)
            {
                if (!RestrictionTypeMatches(targetType, targetGeometry.TypeIndex, targetLayer))
                    continue;

                if (!HasOverlap(sourceGeoType, geometry.Coordinates, targetLayer.GeoType, targetGeometry.Coordinates))
                    continue;

                overlapFound = true;

                // Unity inclusion constraints emit one issue per overlapping target geometry.
                if (constraintSort == "EXCLUSION")
                    continue;

                var severity = NormaliseSeverity(rule.Type);
                if (string.IsNullOrEmpty(severity))
                    severity = "WARNING";
                var message = string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Overlap with {targetLayer.DisplayName}"
                    : rule.Message;
                var marker = GetGeometryCenter(targetGeometry.Coordinates);
                var key = (severity, message, sourceLayer.DisplayName, targetLayer.DisplayName, marker[0], marker[1]);
                if (seenIssueKeys.Add(key))
                    matches.Add(new PlanRestrictionIssue(
                        severity,
                        message,
                        sourceLayer.DisplayName,
                        targetLayer.DisplayName,
                        isNewGeometry ? "New geometry" : "Changed geometry",
                        marker[0],
                        marker[1]));
            }

            // Unity exclusion constraints add an issue only when no overlap was found.
            if (constraintSort == "EXCLUSION" && !overlapFound)
            {
                var severity = NormaliseSeverity(rule.Type);
                if (string.IsNullOrEmpty(severity))
                    severity = "WARNING";
                var message = string.IsNullOrWhiteSpace(rule.Message)
                    ? $"No valid overlap with {targetLayer.DisplayName}"
                    : rule.Message;
                var key = (severity, message, sourceLayer.DisplayName, targetLayer.DisplayName, sourceMarker[0], sourceMarker[1]);
                if (seenIssueKeys.Add(key))
                    matches.Add(new PlanRestrictionIssue(
                        severity,
                        message,
                        sourceLayer.DisplayName,
                        targetLayer.DisplayName,
                        isNewGeometry ? "New geometry" : "Changed geometry",
                        sourceMarker[0],
                        sourceMarker[1]));
            }
        }

        return matches;
    }

    private LayerEntry? FindLayerByRuleName(string? ruleLayer)
    {
        if (string.IsNullOrWhiteSpace(ruleLayer))
            return null;

        var normalizedRule = NormaliseToken(ruleLayer);
        var exact = _layerEntries.FirstOrDefault(layer =>
            normalizedRule == NormaliseToken(layer.LayerId)
            || normalizedRule == NormaliseToken(layer.LayerName)
            || normalizedRule == NormaliseToken(layer.DisplayName));
        if (exact is not null)
            return exact;

        return _layerEntries.FirstOrDefault(layer => RuleLayerMatches(ruleLayer, layer));
    }

    private static bool RuleLayerMatches(string? ruleLayer, LayerEntry layer)
    {
        if (string.IsNullOrWhiteSpace(ruleLayer))
            return false;

        if (string.Equals(ruleLayer, "*", StringComparison.Ordinal))
            return true;

        var normalizedRuleLayer = NormaliseToken(ruleLayer);
        var layerId = NormaliseToken(layer.LayerId);
        var layerName = NormaliseToken(layer.LayerName);
        var displayName = NormaliseToken(layer.DisplayName);

        if (normalizedRuleLayer == layerId
            || normalizedRuleLayer == layerName
            || normalizedRuleLayer == displayName)
            return true;

        // Restriction endpoints commonly use numeric layer IDs. Keep those strict to avoid
        // accidental matches like rule layer "2" matching actual layer id "12".
        if (normalizedRuleLayer.All(char.IsDigit))
            return false;

        // For textual rules, allow partial matching only for reasonably descriptive tokens.
        if (normalizedRuleLayer.Length < 4)
            return false;

        return layerName.Contains(normalizedRuleLayer, StringComparison.Ordinal)
            || displayName.Contains(normalizedRuleLayer, StringComparison.Ordinal)
            || normalizedRuleLayer.Contains(layerName, StringComparison.Ordinal)
            || normalizedRuleLayer.Contains(displayName, StringComparison.Ordinal);
    }

    private static bool RestrictionTypeMatches(string? ruleType, int actualTypeIndex, LayerEntry? layer)
    {
        if (string.IsNullOrWhiteSpace(ruleType))
            return true;

        var normalizedRuleType = NormaliseToken(ruleType);
        if (normalizedRuleType is "*" or "any" or "all")
            return true;

        if (int.TryParse(ruleType, out var index))
            return index == actualTypeIndex;

        if (normalizedRuleType == actualTypeIndex.ToString(CultureInfo.InvariantCulture))
            return true;

        if (layer is null || actualTypeIndex < 0 || actualTypeIndex >= layer.TypeDefs.Count)
            return false;

        var label = layer.TypeDefs[actualTypeIndex].Label;
        return normalizedRuleType == NormaliseToken(label);
    }

    private List<ParsedLayerGeometry> GetParsedLayerGeometries(string layerId)
    {
        if (_parsedLayerGeometryCache.TryGetValue(layerId, out var cached))
            return cached;

        var parsed = new List<ParsedLayerGeometry>();
        _parsedLayerGeometryCache[layerId] = parsed;

        var snapshot = GameState.MapLayerSnapshots.FirstOrDefault(s => string.Equals(s.LayerId, layerId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.VectorGeometriesJson))
            return parsed;

        try
        {
            using var document = JsonDocument.Parse(snapshot.VectorGeometriesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return parsed;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("geometry", out var geometryElement) || geometryElement.ValueKind != JsonValueKind.Array)
                    continue;

                var coordinates = new List<double[]>();
                foreach (var pointElement in geometryElement.EnumerateArray())
                {
                    if (pointElement.ValueKind != JsonValueKind.Array || pointElement.GetArrayLength() < 2)
                        continue;

                    if (pointElement[0].ValueKind != JsonValueKind.Number || pointElement[1].ValueKind != JsonValueKind.Number)
                        continue;

                    coordinates.Add([pointElement[0].GetDouble(), pointElement[1].GetDouble()]);
                }

                if (coordinates.Count == 0)
                    continue;

                var featureId = "";
                if (element.TryGetProperty("id", out var idElement))
                    featureId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? "" : idElement.ToString();

                var typeIndex = 0;
                if (element.TryGetProperty("type", out var typeElement))
                {
                    if (typeElement.ValueKind == JsonValueKind.Number)
                        typeIndex = typeElement.GetInt32();
                    else if (typeElement.ValueKind == JsonValueKind.String)
                        int.TryParse(typeElement.GetString(), out typeIndex);
                }

                parsed.Add(new ParsedLayerGeometry(featureId, typeIndex, coordinates));
            }
        }
        catch
        {
            // Ignore malformed cached geometry and simply skip restrictions for this layer.
        }

        return parsed;
    }

    /// <summary>
    /// Returns the effective geometries for <paramref name="layerId"/> as they would exist
    /// at the moment a plan with <paramref name="beforeStartDate"/> is implemented — i.e. after
    /// applying all APPROVED / IMPLEMENTED / ARCHIVED plans whose start date is strictly earlier.
    /// </summary>
    private List<ParsedLayerGeometry> GetProjectedLayerGeometries(string layerId, int beforeStartDate)
    {
        var cacheKey = $"{layerId}\x01{beforeStartDate}";
        if (_projectedGeometryCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var baseline = GetParsedLayerGeometries(layerId);

        // Collect finalised plans that are already in effect before this plan's start date.
        var priorPlans = _plans
            .Where(p => p.StartDate < beforeStartDate && IsFinalisedPlanState(p.State))
            .OrderBy(p => p.StartDate)
            .ThenBy(p => p.PlanId)
            .ToList();

        if (priorPlans.Count == 0)
        {
            _projectedGeometryCache[cacheKey] = baseline;
            return baseline;
        }

        // Build the set of original feature IDs that have been removed or replaced,
        // and collect all plan-introduced geometries as additions.
        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additions  = new List<ParsedLayerGeometry>();

        foreach (var priorPlan in priorPlans)
        {
            var planLayer = priorPlan.Layers.FirstOrDefault(l =>
                string.Equals(l.OriginalLayerId, layerId, StringComparison.OrdinalIgnoreCase));
            if (planLayer is null) continue;

            // Explicit deletions.
            foreach (var deletedId in planLayer.DeletedPersistentIds)
                deletedIds.Add(deletedId);

            foreach (var geo in planLayer.Geometry)
            {
                bool isModification = !string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id;
                if (isModification)
                    deletedIds.Add(geo.PersistentId); // original is superseded by the plan version

                // Whether new or modified, the plan geometry is part of the projected world.
                additions.Add(new ParsedLayerGeometry(geo.Id, geo.TypeIndex, geo.Coordinates));
            }
        }

        var projected = new List<ParsedLayerGeometry>(baseline.Count + additions.Count);
        foreach (var geo in baseline)
        {
            if (!deletedIds.Contains(geo.FeatureId))
                projected.Add(geo);
        }
        projected.AddRange(additions);

        _projectedGeometryCache[cacheKey] = projected;
        return projected;
    }

    private static bool IsFinalisedPlanState(string state) =>
        state.Equals("CONSULTATION", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("APPROVAL",     StringComparison.OrdinalIgnoreCase) ||
        state.Equals("APPROVED",     StringComparison.OrdinalIgnoreCase) ||
        state.Equals("IMPLEMENTED",  StringComparison.OrdinalIgnoreCase);

    private static bool IsApprovalCompleteState(string? state) =>
        state is not null &&
        (state.Equals("APPROVED",    StringComparison.OrdinalIgnoreCase) ||
         state.Equals("IMPLEMENTED", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("ARCHIVED",    StringComparison.OrdinalIgnoreCase));

    private string? SelectedPlanState => _selectedPlanId == 0
        ? null
        : _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId)?.State;

    private static string NormaliseSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var key = NormaliseToken(raw);
        return key switch
        {
            "error" or "err" or "danger" or "2" => "ERROR",
            "warning" or "warn" or "1" => "WARNING",
            "info" or "information" or "0" => "INFO",
            _ => key.ToUpperInvariant()
        };
    }

    private static int SeveritySortRank(string? severity)
    {
        return NormaliseSeverity(severity) switch
        {
            "ERROR" => 3,
            "WARNING" => 2,
            "INFO" => 1,
            _ => 0
        };
    }

    private static string NormaliseConstraintSort(string? rawSort)
    {
        if (string.IsNullOrWhiteSpace(rawSort)) return "INCLUSION";
        var key = NormaliseToken(rawSort);
        return key switch
        {
            "0" or "inclusion" => "INCLUSION",
            "1" or "exclusion" => "EXCLUSION",
            "2" or "typeunavailable" => "TYPE_UNAVAILABLE",
            _ => key.ToUpperInvariant()
        };
    }

    private static string NormaliseToken(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static bool HasOverlap(
        string sourceGeoType,
        IReadOnlyList<double[]> sourceCoordinates,
        string targetGeoType,
        IReadOnlyList<double[]> targetCoordinates)
    {
        var aType = NormaliseGeometryKind(sourceGeoType, sourceCoordinates);
        var bType = NormaliseGeometryKind(targetGeoType, targetCoordinates);

        if (aType == "point" && bType == "point")
            return PointDistanceSquared(sourceCoordinates[0], targetCoordinates[0]) <= 1.0;

        if (aType == "point" && bType == "line")
            return PointOnLine(sourceCoordinates[0], targetCoordinates);

        if (aType == "line" && bType == "point")
            return PointOnLine(targetCoordinates[0], sourceCoordinates);

        if (aType == "point" && bType == "polygon")
            return PointInPolygon(sourceCoordinates[0], targetCoordinates);

        if (aType == "polygon" && bType == "point")
            return PointInPolygon(targetCoordinates[0], sourceCoordinates);

        if (aType == "line" && bType == "line")
            return PolylineIntersectsPolyline(sourceCoordinates, targetCoordinates);

        if (aType == "line" && bType == "polygon")
            return PolylineIntersectsPolygon(sourceCoordinates, targetCoordinates);

        if (aType == "polygon" && bType == "line")
            return PolylineIntersectsPolygon(targetCoordinates, sourceCoordinates);

        return PolygonsOverlap(sourceCoordinates, targetCoordinates);
    }

    private static string NormaliseGeometryKind(string geoType, IReadOnlyList<double[]> coordinates)
    {
        var key = geoType.Trim().ToLowerInvariant();
        if (key.Contains("point")) return "point";
        if (key.Contains("line")) return "line";
        if (key.Contains("polygon")) return "polygon";
        if (coordinates.Count <= 1) return "point";
        return coordinates.Count >= 4 ? "polygon" : "line";
    }

    private static double[] GetGeometryCenter(IReadOnlyList<double[]> coordinates)
    {
        if (coordinates.Count == 0) return [0d, 0d];

        var minX = coordinates[0][0];
        var maxX = coordinates[0][0];
        var minY = coordinates[0][1];
        var maxY = coordinates[0][1];

        for (var i = 1; i < coordinates.Count; i++)
        {
            var c = coordinates[i];
            if (c[0] < minX) minX = c[0];
            if (c[0] > maxX) maxX = c[0];
            if (c[1] < minY) minY = c[1];
            if (c[1] > maxY) maxY = c[1];
        }

        return [minX + (maxX - minX) / 2d, minY + (maxY - minY) / 2d];
    }

    private static bool PointOnLine(double[] point, IReadOnlyList<double[]> line)
    {
        if (line.Count == 0) return false;
        if (line.Count == 1) return PointDistanceSquared(point, line[0]) <= 1.0;

        for (var i = 1; i < line.Count; i++)
        {
            if (DistancePointToSegmentSquared(point, line[i - 1], line[i]) <= 1.0)
                return true;
        }

        return false;
    }

    private static bool PolylineIntersectsPolyline(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (a.Count < 2 || b.Count < 2) return false;

        for (var i = 1; i < a.Count; i++)
        {
            for (var j = 1; j < b.Count; j++)
            {
                if (SegmentsIntersect(a[i - 1], a[i], b[j - 1], b[j]))
                    return true;
            }
        }

        return false;
    }

    private static bool PolylineIntersectsPolygon(IReadOnlyList<double[]> line, IReadOnlyList<double[]> polygon)
    {
        if (line.Count < 2 || polygon.Count < 3) return false;
        if (PointInPolygon(line[0], polygon)) return true;

        var polygonSegments = EnumerateSegments(polygon, closed: true);
        for (var i = 1; i < line.Count; i++)
        {
            var lineA = line[i - 1];
            var lineB = line[i];
            foreach (var segment in polygonSegments)
            {
                if (SegmentsIntersect(lineA, lineB, segment.A, segment.B))
                    return true;
            }
        }

        return false;
    }

    private static bool PolygonsOverlap(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        if (PointInPolygon(a[0], b) || PointInPolygon(b[0], a)) return true;

        foreach (var segA in EnumerateSegments(a, closed: true))
        {
            foreach (var segB in EnumerateSegments(b, closed: true))
            {
                if (SegmentsIntersect(segA.A, segA.B, segB.A, segB.B))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<(double[] A, double[] B)> EnumerateSegments(IReadOnlyList<double[]> coords, bool closed)
    {
        if (coords.Count < 2) yield break;
        for (var i = 1; i < coords.Count; i++)
            yield return (coords[i - 1], coords[i]);

        if (closed)
        {
            var first = coords[0];
            var last = coords[^1];
            if (PointDistanceSquared(first, last) > 1.0)
                yield return (last, first);
        }
    }

    private static bool PointInPolygon(double[] point, IReadOnlyList<double[]> polygon)
    {
        if (polygon.Count < 3) return false;

        var inside = false;
        var j = polygon.Count - 1;
        for (var i = 0; i < polygon.Count; i++)
        {
            var xi = polygon[i][0];
            var yi = polygon[i][1];
            var xj = polygon[j][0];
            var yj = polygon[j][1];

            var intersects = ((yi > point[1]) != (yj > point[1])) &&
                             (point[0] < (xj - xi) * (point[1] - yi) / ((yj - yi) + 1e-12) + xi);
            if (intersects)
                inside = !inside;

            j = i;
        }

        return inside;
    }

    private static bool SegmentsIntersect(double[] p1, double[] p2, double[] q1, double[] q2)
    {
        var o1 = Orientation(p1, p2, q1);
        var o2 = Orientation(p1, p2, q2);
        var o3 = Orientation(q1, q2, p1);
        var o4 = Orientation(q1, q2, p2);

        if (o1 != o2 && o3 != o4) return true;

        if (o1 == 0 && OnSegment(p1, q1, p2)) return true;
        if (o2 == 0 && OnSegment(p1, q2, p2)) return true;
        if (o3 == 0 && OnSegment(q1, p1, q2)) return true;
        if (o4 == 0 && OnSegment(q1, p2, q2)) return true;
        return false;
    }

    private static int Orientation(double[] p, double[] q, double[] r)
    {
        var value = (q[1] - p[1]) * (r[0] - q[0]) - (q[0] - p[0]) * (r[1] - q[1]);
        if (Math.Abs(value) < 1e-9) return 0;
        return value > 0 ? 1 : 2;
    }

    private static bool OnSegment(double[] p, double[] q, double[] r)
    {
        return q[0] <= Math.Max(p[0], r[0]) + 1e-9 && q[0] + 1e-9 >= Math.Min(p[0], r[0])
            && q[1] <= Math.Max(p[1], r[1]) + 1e-9 && q[1] + 1e-9 >= Math.Min(p[1], r[1]);
    }

    private static double DistancePointToSegmentSquared(double[] p, double[] a, double[] b)
    {
        var dx = b[0] - a[0];
        var dy = b[1] - a[1];
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
            return PointDistanceSquared(p, a);

        var t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / (dx * dx + dy * dy);
        t = Math.Max(0, Math.Min(1, t));
        var proj = new[] { a[0] + t * dx, a[1] + t * dy };
        return PointDistanceSquared(p, proj);
    }

    private static double PointDistanceSquared(double[] a, double[] b)
    {
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        return dx * dx + dy * dy;
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
        {
            _planIssuesOpen = false;
            _planApprovalOpen = false;
            _approvalReasonsExpanded.Clear();
            _scrollPlanMessagesPending = true;
        }
        _planMessageSendError = null;
    }

    private void TogglePlanIssuesPanel()
    {
        if (_selectedPlanId == 0) return;
        _planIssuesOpen = !_planIssuesOpen;
        if (_planIssuesOpen)
        {
            _planMessagesOpen = false;
            _planApprovalOpen = false;
            _approvalReasonsExpanded.Clear();
        }
    }

    private void ToggleApprovalPanel()
    {
        if (_selectedPlanId == 0) return;
        _planApprovalOpen = !_planApprovalOpen;
        if (_planApprovalOpen)
        {
            _planMessagesOpen = false;
            _planIssuesOpen = false;
        }
        else
        {
            _approvalReasonsExpanded.Clear();
        }
    }

    private void ToggleApprovalReasonExpanded(int countryId)
    {
        if (!_approvalReasonsExpanded.Remove(countryId))
            _approvalReasonsExpanded.Add(countryId);
    }

    private bool _sendingVote;

    private async Task VoteOnPlanAsync(int planId, int vote)
    {
        if (_sendingVote) return;
        _sendingVote = true;
        StateHasChanged();
        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{SessionState.SessionId}/api/Plan/Vote";
            await ApiClient.PostFormAsync(url, new[]
            {
                new KeyValuePair<string, string>("plan",    planId.ToString()),
                new KeyValuePair<string, string>("country", SessionState.CountryId.ToString()),
                new KeyValuePair<string, string>("vote",    vote.ToString()),
            });
        }
        catch { /* Server will correct state on next update */ }
        finally
        {
            _sendingVote = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Computes which country teams need to approve the plan and for what reasons,
    /// based on the approval type defined per layer_type (AllCountries / EEZ / NotDependent)
    /// and ownership of removed geometry derived from EEZ polygon intersection.
    /// </summary>
    private void CalculateApproval(PlanEntry plan)
    {
        _approvalRequired.Clear();
        var eezPolygons = GameState.EezPolygons;
        var planCountry = plan.Country;

        // countryId → list of reason strings
        var reasons = new Dictionary<int, List<string>>();
        var allCountriesReasons = new List<string>(); // reasons that apply to every country

        void AddReason(int countryId, string reason)
        {
            if (countryId <= 0 || countryId == planCountry) return;
            if (!reasons.TryGetValue(countryId, out var list))
            {
                list = new List<string>();
                reasons[countryId] = list;
            }
            if (!list.Contains(reason)) list.Add(reason);
        }

        foreach (var planLayer in plan.Layers)
        {
            var layerEntry = _layerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            var layerDisplayName = layerEntry?.DisplayName ?? planLayer.OriginalLayerId;

            // ── Deleted geometry ──────────────────────────────────────────────
            if (planLayer.DeletedPersistentIds.Count > 0)
            {
                var baseGeoms = GetParsedLayerGeometries(planLayer.OriginalLayerId);
                foreach (var deletedId in planLayer.DeletedPersistentIds)
                {
                    var geom = baseGeoms.FirstOrDefault(g => g.FeatureId == deletedId);
                    if (geom is null) continue;

                    var typeApproval = GetApprovalForGeom(layerEntry, geom.TypeIndex);
                    var typeLabel    = GetTypeLabel(layerEntry, geom.TypeIndex);

                    if (string.Equals(typeApproval, "AllCountries", StringComparison.OrdinalIgnoreCase))
                    {
                        var r = string.IsNullOrEmpty(typeLabel)
                            ? $"Geometry was removed on the {layerDisplayName} layer, which requires approval from all countries."
                            : $"Geometry of type {typeLabel} was removed, which requires approval from all countries.";
                        if (!allCountriesReasons.Contains(r)) allCountriesReasons.Add(r);
                    }
                    else
                    {
                        // Derive ownership from EEZ intersection of the representative coordinate
                        if (geom.Coordinates.Count > 0)
                        {
                            var owner = GetCountryForCoordinate(geom.Coordinates[0], eezPolygons);
                            if (owner > 0 && owner != planCountry)
                                AddReason(owner, $"Geometry belonging to {GetCountryName(owner)} was removed on the {layerDisplayName} layer.");
                        }
                    }
                }
            }

            // ── New / modified geometry ───────────────────────────────────────
            foreach (var geomItem in planLayer.Geometry)
            {
                var typeApproval = GetApprovalForGeom(layerEntry, geomItem.TypeIndex);
                var typeLabel    = GetTypeLabel(layerEntry, geomItem.TypeIndex);

                if (string.Equals(typeApproval, "AllCountries", StringComparison.OrdinalIgnoreCase))
                {
                    var r = string.IsNullOrEmpty(typeLabel)
                        ? $"Geometry was added or moved on the {layerDisplayName} layer, which requires approval from all countries."
                        : $"Geometry of type {typeLabel} was added or moved, which requires approval from all countries.";
                    if (!allCountriesReasons.Contains(r)) allCountriesReasons.Add(r);
                }
                else if (string.Equals(typeApproval, "EEZ", StringComparison.OrdinalIgnoreCase)
                         && geomItem.Coordinates.Count > 0)
                {
                    var pt = geomItem.Coordinates[0];
                    foreach (var eez in eezPolygons)
                    {
                        if (eez.CountryId != planCountry && PointInPolygon(pt, eez.Points))
                        {
                            var countryName = GetCountryName(eez.CountryId);
                            AddReason(eez.CountryId, $"Geometry on the {layerDisplayName} layer was added or altered in {countryName}'s EEZ.");
                        }
                    }
                }
            }
        }

        // AllCountries: add those reasons to every non-owner country team
        if (allCountriesReasons.Count > 0)
        {
            foreach (var kvp in _countryNames)
            {
                if (kvp.Key <= 2 || kvp.Key == planCountry) continue; // skip admin/GM slots
                foreach (var r in allCountriesReasons)
                    AddReason(kvp.Key, r);
            }
        }

        // Build sorted result
        foreach (var kvp in reasons.OrderBy(k => k.Key))
        {
            var name = GetCountryName(kvp.Key);
            var dot  = _countryColours.GetValueOrDefault(kvp.Key, "#6c757d");
            _approvalRequired.Add(new ApprovalRequirement(kvp.Key, name, kvp.Value));
        }
    }

    private static string GetApprovalForGeom(LayerEntry? layerEntry, int typeIndex)
    {
        if (layerEntry is null) return "NotDependent";
        if (typeIndex >= 0 && typeIndex < layerEntry.TypeDefs.Count)
            return layerEntry.TypeDefs[typeIndex].Approval;
        // Fallback: if there is exactly one type, use that regardless of index
        if (layerEntry.TypeDefs.Count == 1)
            return layerEntry.TypeDefs[0].Approval;
        return "NotDependent";
    }

    private static string GetTypeLabel(LayerEntry? layerEntry, int typeIndex)
    {
        if (layerEntry is null) return "";
        if (typeIndex >= 0 && typeIndex < layerEntry.TypeDefs.Count)
            return layerEntry.TypeDefs[typeIndex].Label;
        return "";
    }

    private string GetCountryName(int countryId) =>
        _countryNames.TryGetValue(countryId, out var n) ? n : $"Country {countryId}";

    private static int GetCountryForCoordinate(double[] pt, IReadOnlyList<EezPolygon> eezPolygons)
    {
        foreach (var eez in eezPolygons)
        {
            if (PointInPolygon(pt, eez.Points))
                return eez.CountryId;
        }
        return 0;
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
