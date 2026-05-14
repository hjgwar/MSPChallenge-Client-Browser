using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
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
    private readonly List<LayerEntry> _layerEntries = new();
    /// <summary>Visible non-base layers in z-order: index 0 = bottom, last = top.</summary>
    private readonly List<LayerEntry> _legendOrder = new();
    private string _layerSearch = "";

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
    private readonly Dictionary<int, string> _countryColours = new();
    private readonly Dictionary<int, string> _countryNames   = new();

    // Wiki base URL (from Game/Config; may be absent in some game configs)
    private string _wikiBaseUrl = "";

    // Game start year (month 0 = Jan of this year)
    private int _gameStartYear = 2000;
    // Total simulation months (end_month/game_end_month from Config or WS)
    private int _gameEndMonth = 0;
    // Simulation end year ("end" field from Game/Config, e.g. 2050)
    private int _gameEndYear  = 0;

    // Game state bar — updated from Game/Latest WS messages
    private int    _gameCurrentMonth = 0;
    private string _gameState        = "";   // "pause", "play", "fastforward", "setup", "end"
    private double _eraTimeLeft      = 0;    // real-world seconds remaining in current era

    // Loading state
    private bool   _isLoading = true;
    private bool   _loadingFading = false;
    private string _loadingStatus = "Initialising…";
    private readonly TaskCompletionSource _firstWsMessageTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // WebSocket (address comes from session listing, stored in SessionState)

    // Plans panel
    private bool _plansPanelOpen;
    private List<PlanEntry> _plans = new();
    private int _selectedPlanId;
    private PlanViewMode _planViewMode = PlanViewMode.AfterChanges;
    private readonly HashSet<string> _planActivatedLayerIds  = new();
    private readonly HashSet<string> _planReferencedLayerIds = new();

    // Dev console log (capped list of recent raw WS messages)
    private const int WsLogMaxEntries = 100;
    private readonly List<(string HeaderName, string Raw, DateTime ReceivedAt)> _wsLog = new();
    private string? _wsSelectedRaw;
    private bool    _wsLogVisible = false;

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

    protected override async Task OnInitializedAsync()
    {
        if (string.IsNullOrEmpty(SessionState.ApiAccessToken) ||
            SessionState.SessionId == 0 ||
            string.IsNullOrEmpty(SessionState.GameServerAddress))
        {
            NavigationManager.NavigateTo("/session");
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        _mapModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/map.js");

        // Default view centred on North Sea – will be replaced once _PLAYAREA bounds are known
        await _mapModule.InvokeVoidAsync("initMap", "map", 54.5, 3.5, 6);

        WsService.MessageReceived += OnWsMessageReceived;

        await LoadGameDataAsync();

        if (!_firstWsMessageTcs.Task.IsCompleted)
        {
            _loadingStatus = "Waiting for game data…";
            StateHasChanged();
            await _firstWsMessageTcs.Task;
        }

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
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var sessionId   = SessionState.SessionId;

            // 1. Policy and simulation settings (fetched for server-side processing; not yet consumed)
            await ApiClient.GetAsync($"{baseAddress}/{sessionId}/api/Game/PolicySimSettings");

            // 2. Game config — contains wiki_base_url and other session-wide settings
            var configRoot    = await ApiClient.GetAsync($"{baseAddress}/{sessionId}/api/Game/Config");
            var configPayload = GetPayload(configRoot);
            if (configPayload.TryGetProperty("wiki_base_url", out var wbuProp) && wbuProp.GetString() is { Length: > 0 } wbu)
                _wikiBaseUrl = wbu.TrimEnd('/');

            if (configPayload.TryGetProperty("start", out var startProp))
                _gameStartYear = startProp.ValueKind == JsonValueKind.Number
                    ? startProp.GetInt32()
                    : int.TryParse(startProp.GetString(), out var sy) ? sy : 2000;

            // end: simulation end year (e.g. 2050) — the sole source for the end year
            if (configPayload.TryGetProperty("end", out var endYearProp))
            {
                var raw = endYearProp.ValueKind == JsonValueKind.Number
                    ? endYearProp.GetInt32()
                    : int.TryParse(endYearProp.GetString(), out var ey) ? ey : 0;
                if (raw > 0) _gameEndYear = raw;
            }

            // end_month: total simulation months (optional; used for progress bar)
            foreach (var endKey in new[] { "end_month", "game_end_month" })
            {
                if (configPayload.TryGetProperty(endKey, out var endProp))
                {
                    var raw = endProp.ValueKind == JsonValueKind.Number
                        ? endProp.GetInt32()
                        : int.TryParse(endProp.GetString(), out var em) ? em : 0;
                    if (raw > 0) { _gameEndMonth = raw; break; }
                }
            }

            // Load country id → colour mapping from the countries layer
            if (configPayload.TryGetProperty("countries", out var countriesEl) &&
                countriesEl.GetString() is { Length: > 0 } countriesLayerName)
            {
                var metaRoot2 = await ApiClient.PostFormAsync(
                    $"{baseAddress}/{sessionId}/api/Layer/MetaByName",
                    new[] { new KeyValuePair<string, string>("name", countriesLayerName) });
                var metaPayload2 = GetPayload(metaRoot2);
                if (metaPayload2.TryGetProperty("layer_type", out var layerTypes) &&
                    layerTypes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lt in layerTypes.EnumerateArray())
                    {
                        if (lt.TryGetProperty("value", out var ltVal) && ltVal.ValueKind == JsonValueKind.Number)
                        {
                            var cid2 = ltVal.GetInt32();
                            if (lt.TryGetProperty("polygonColor", out var ltCol) && ltCol.GetString() is { Length: > 0 } col)
                                _countryColours[cid2] = col;
                            if (lt.TryGetProperty("displayName", out var ltDn) && ltDn.GetString() is { Length: > 0 } dn)
                                _countryNames[cid2] = dn;
                        }
                    }
                }
            }

            // 3. Full layer metadata for this user via Game/Meta
            var metaRoot    = await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionId}/api/Game/Meta",
                new[] { new KeyValuePair<string, string>("user", SessionState.CountryId.ToString()) });
            var metaPayload = GetPayload(metaRoot);

            if (metaPayload.ValueKind != JsonValueKind.Array)
                return;

            var layerArray = metaPayload.EnumerateArray().ToList();
            var total  = layerArray.Count;
            var loaded = 0;

            foreach (var layer in layerArray)
            {
                var name = layer.TryGetProperty("layer_short", out var ls) && ls.GetString() is { Length: > 0 } s
                    ? s
                    : (layer.TryGetProperty("layer_name", out var ln) ? ln.GetString() ?? "" : "");
                _loadingStatus = $"Loading {loaded + 1} / {total}: {name}";
                StateHasChanged();

                bool visible = layer.TryGetProperty("layer_active_on_start", out var aos) &&
                               aos.ValueKind == JsonValueKind.Number && aos.GetInt32() != 0;
                await LoadLayerAsync(baseAddress, sessionId, layer, visible: visible);
                loaded++;
                StateHasChanged();
            }

            // Sort visible layers by layer_depth ascending (lower depth = rendered below)
            _legendOrder.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            await SyncZIndicesAsync();

            // Fit the initial view to the _PLAYAREA layer so its bounds fill the viewport
            var playArea = _layerEntries.FirstOrDefault(e => e.IsBaseLayer);
            if (playArea is not null && _mapModule is not null)
                await _mapModule.InvokeVoidAsync("fitToPlayArea", playArea.LayerId);

            // Connect WebSocket — last loading step
            if (!string.IsNullOrEmpty(SessionState.GameWsServerAddress))
            {
                _loadingStatus = "Connecting to game server…";
                StateHasChanged();
                await WsService.ConnectAndSubscribeAsync(
                    SessionState.GameWsServerAddress,
                    SessionState.ApiAccessToken,
                    SessionState.SessionId,
                    teamId: SessionState.CountryId,
                    userId: SessionState.UserId);
                // Loading screen held open until first message arrives (signalled via _firstWsMessageTcs)
            }
            else
            {
                _firstWsMessageTcs.TrySetResult(); // no WS — nothing to wait for
            }
        }
        catch (MspApiException ex)
        {
            errorMessage = ex.Message;
            errorDetail  = $"HTTP {ex.StatusCode}\n{ex}";
            await HideLoadingAsync();
        }
        catch (Exception ex)
        {
            errorMessage = $"Error loading map data: {ex.Message}";
            errorDetail  = ex.ToString();
            await HideLoadingAsync();
        }
    }

    private async Task LoadLayerAsync(string baseAddress, int sessionId, JsonElement layer, bool visible)
    {
        if (_mapModule is null) return;

        var layerId   = layer.TryGetProperty("layer_id",      out var id) ? id.ToString()    : null;
        var layerName = layer.TryGetProperty("layer_name",    out var n)  ? n.GetString()    : null;
        var geoType   = layer.TryGetProperty("layer_geotype", out var gt) ? gt.GetString()   : null;

        if (layerId is null || layerName is null) return;

        List<(double NormalisedThreshold, string Label)> rasterThresholds = [];

        var colorProp = geoType?.ToLowerInvariant() switch
        {
            "line" or "lines"   => "lineColor",
            "point" or "points" => "pointColor",
            _                   => "polygonColor"
        };
        var typeColors = new List<string>();
        if (layer.TryGetProperty("layer_type", out var ltEl))
        {
            if (ltEl.ValueKind == JsonValueKind.Array)
            {
                // Array form: [{polygonColor:"#...", ...}, ...]
                foreach (var lt in ltEl.EnumerateArray())
                {
                    var c = lt.TryGetProperty(colorProp, out var cp) ? cp.GetString() ?? "#3388ff" : "#3388ff";
                    typeColors.Add(c);
                }
            }
            else if (ltEl.ValueKind == JsonValueKind.Object)
            {
                // Object form: {"0": {polygonColor:"#...", ...}, "1": {...}, ...} — iterate by numeric key order
                var entries = new SortedDictionary<int, string>();
                foreach (var kv in ltEl.EnumerateObject())
                {
                    if (!int.TryParse(kv.Name, out var idx)) continue;
                    var c = kv.Value.TryGetProperty(colorProp, out var cp) ? cp.GetString() ?? "#3388ff" : "#3388ff";
                    entries[idx] = c;
                }
                foreach (var c in entries.Values) typeColors.Add(c);
            }
        }
        if (typeColors.Count == 0) typeColors.Add("#3388ff");

        try
        {
            if (string.Equals(geoType, "raster", StringComparison.OrdinalIgnoreCase))
            {
                var rasterRoot    = await ApiClient.PostFormAsync(
                    $"{baseAddress}/{sessionId}/api/Layer/GetRaster",
                    new[] { new KeyValuePair<string, string>("layer_name", layerName) });
                var rasterPayload = GetPayload(rasterRoot);

                if (rasterPayload.TryGetProperty("image_data", out var imgData) &&
                    imgData.GetString() is { } b64 &&
                    rasterPayload.TryGetProperty("displayed_bounds", out var bb) &&
                    bb.ValueKind == JsonValueKind.Array &&
                    bb.GetArrayLength() == 2)
                {
                    var projBounds = new[]
                    {
                        new[] { bb[0][0].GetDouble(), bb[0][1].GetDouble() },
                        new[] { bb[1][0].GetDouble(), bb[1][1].GetDouble() }
                    };

                    // layer_entity_value_max is the real-world maximum data value encoded as grey=255.
                    // Default to 1000 when absent (as specified by the game engine).
                    var entityValueMax = 1000.0;
                    if (layer.TryGetProperty("layer_entity_value_max", out var evmProp) &&
                        evmProp.ValueKind == JsonValueKind.Number)
                    {
                        entityValueMax = evmProp.GetDouble();
                        if (entityValueMax <= 0) entityValueMax = 1000.0;
                    }

                    // Build greyscale colour map from layer_type sorted ascending by value.
                    var rasterColorMap  = new List<object>();
                    if (layer.TryGetProperty("layer_type", out var ltRaster) && ltRaster.ValueKind == JsonValueKind.Array)
                    {
                        var entries = ltRaster.EnumerateArray()
                            .Where(lt => lt.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                            .Select(lt => (
                                threshold: lt.GetProperty("value").GetDouble(),
                                hex: lt.TryGetProperty("polygonColor", out var pc) ? pc.GetString() ?? "#000000FF" : "#000000FF",
                                label: lt.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : ""
                            ))
                            .OrderBy(e => e.threshold)
                            .ToList();

                        foreach (var (threshold, hex, label) in entries)
                        {
                            var normalised = threshold / entityValueMax * 255.0;
                            rasterColorMap.Add(new { value = normalised, rgba = HexToRgbaArray(hex) });
                            rasterThresholds.Add((normalised, label));
                        }
                    }

                    // layer_raster_color_interpolation: false = smooth gradient, true = hard discrete steps.
                    double? minCutoffNorm = null;
                    bool interpolate = true;
                    if (layer.TryGetProperty("layer_raster", out var lrObj) && lrObj.ValueKind == JsonValueKind.Object)
                    {
                        if (lrObj.TryGetProperty("layer_raster_minimum_value_cutoff", out var cutoffProp) &&
                            cutoffProp.ValueKind == JsonValueKind.Number)
                            minCutoffNorm = cutoffProp.GetDouble() * 255.0;

                        if (lrObj.TryGetProperty("layer_raster_color_interpolation", out var interpProp) &&
                            interpProp.ValueKind == JsonValueKind.True)
                            interpolate = false;
                    }

                    await _mapModule.InvokeVoidAsync("addRasterLayer", layerId, b64, projBounds, 0.9, visible,
                        rasterColorMap.Count > 0 ? rasterColorMap : null, minCutoffNorm, interpolate);
                }
            }
            else
            {
                var geoRoot    = await ApiClient.PostFormAsync(
                    $"{baseAddress}/{sessionId}/api/Layer/Get",
                    new[] { new KeyValuePair<string, string>("layer_id", layerId) });
                var geoPayload = GetPayload(geoRoot);

                if (geoPayload.ValueKind == JsonValueKind.Array && geoPayload.GetArrayLength() > 0)
                {
                    string? labelKey = null;
                    if (layer.TryGetProperty("layer_text_info", out var lti) &&
                        lti.ValueKind == JsonValueKind.Object &&
                        lti.TryGetProperty("property_per_state", out var pps) &&
                        pps.ValueKind == JsonValueKind.Object &&
                        pps.TryGetProperty("Current", out var curr) &&
                        curr.GetString() is { Length: > 0 } currentProp)
                    {
                        if (layer.TryGetProperty("layer_info_properties", out var lip) &&
                            lip.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var prop in lip.EnumerateArray())
                            {
                                if (prop.TryGetProperty("property_name", out var pn) &&
                                    pn.GetString() == currentProp)
                                { labelKey = currentProp; break; }
                            }
                        }
                    }

                    var geometriesJson = geoPayload.ToString();
                    await _mapModule.InvokeVoidAsync("addVectorLayer", layerId, geometriesJson, geoType, typeColors, visible, labelKey);
                }
            }

            var layerShort  = layer.TryGetProperty("layer_short",       out var ls2)  ? ls2.GetString()  ?? "" : "";
            var category    = layer.TryGetProperty("layer_category",    out var lc)   ? lc.GetString()   ?? "" : "";
            var subcategory = layer.TryGetProperty("layer_subcategory", out var lsc)  ? lsc.GetString()  ?? "" : "";
            var tooltip     = layer.TryGetProperty("layer_tooltip",     out var ltt)  ? ltt.GetString()  ?? "" : "";
            var depth       = layer.TryGetProperty("layer_depth",       out var ld)   && ld.ValueKind == JsonValueKind.Number ? ld.GetInt32() : 0;
            var toggleable  = layer.TryGetProperty("layer_toggleable",  out var lt2)  && lt2.ValueKind == JsonValueKind.Number ? lt2.GetInt32() != 0 : true;
            var isBase      = layerName.IndexOf("_PLAYAREA", StringComparison.OrdinalIgnoreCase) >= 0;
            var displayName = !string.IsNullOrWhiteSpace(layerShort) ? layerShort : FallbackDisplayName(layerName);
            var layerMedia  = layer.TryGetProperty("layer_media", out var lm) ? lm.GetString() ?? "" : "";
            var layerMediaUrl = ResolveWikiUrl(layerMedia);

            var typeDefs = new List<TypeDef>();
            if (layer.TryGetProperty("layer_type", out var ltDefEl))
            {
                IEnumerable<JsonElement> typeDefItems = ltDefEl.ValueKind switch
                {
                    JsonValueKind.Array  => ltDefEl.EnumerateArray(),
                    JsonValueKind.Object => ltDefEl.EnumerateObject()
                                               .OrderBy(kv => int.TryParse(kv.Name, out var n) ? n : int.MaxValue)
                                               .Select(kv => kv.Value),
                    _                    => []
                };
                foreach (var td in typeDefItems)
                {
                    var label      = td.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                    var col        = td.TryGetProperty(colorProp,     out var cp) ? cp.GetString() ?? "#3388ff" : "#3388ff";
                    var tdMedia    = td.TryGetProperty("media",       out var tm) ? tm.GetString() ?? "" : "";
                    var tdMediaUrl = ResolveWikiUrl(tdMedia);
                    if (!string.IsNullOrWhiteSpace(label))
                        typeDefs.Add(new TypeDef(label, col, tdMediaUrl));
                }
            }

            var propDisplayNames = new Dictionary<string, string>();
            if (layer.TryGetProperty("layer_info_properties", out var lipDisp) && lipDisp.ValueKind == JsonValueKind.Array)
            {
                foreach (var prop in lipDisp.EnumerateArray())
                {
                    if (prop.TryGetProperty("property_name", out var pn2) &&
                        prop.TryGetProperty("display_name",  out var dn2) &&
                        pn2.GetString() is { Length: > 0 } propName2)
                        propDisplayNames[propName2] = dn2.GetString() ?? propName2;
                }
            }

            _layerEntries.Add(new LayerEntry
            {
                LayerId              = layerId,
                LayerName            = layerName,
                DisplayName          = displayName,
                Category             = category,
                Subcategory          = subcategory,
                Tooltip              = tooltip,
                MediaUrl             = layerMediaUrl,
                IsBaseLayer          = isBase,
                IsToggleable         = toggleable,
                Visible              = visible,
                Depth                = depth,
                TypeDefs             = typeDefs,
                PropertyDisplayNames = propDisplayNames,
                IsRaster             = string.Equals(geoType, "raster", StringComparison.OrdinalIgnoreCase),
                RasterThresholds     = rasterThresholds,
                GeoType              = geoType ?? "",
                AssemblyTime         = ParseAssemblyTime(layer)
            });

            var added = _layerEntries[^1];
            if (visible && !added.IsBaseLayer)
                _legendOrder.Add(added);
        }
        catch (Exception ex)
        {
            errorDetail = (errorDetail ?? "") + $"\nLayer {layerName}: {ex.Message}";
            StateHasChanged();
        }
    }

    /// <summary>
    /// Reads the layer_states array and returns the "time" value of the ASSEMBLY state entry (0 if absent).
    /// </summary>
    private static int ParseAssemblyTime(JsonElement layer)
    {
        if (!layer.TryGetProperty("layer_states", out var statesEl) ||
            statesEl.ValueKind != JsonValueKind.Array)
            return 0;

        foreach (var s in statesEl.EnumerateArray())
        {
            var stateName = GetStringProp(s, "state");
            if (string.Equals(stateName, "ASSEMBLY", StringComparison.OrdinalIgnoreCase))
                return GetIntProp(s, "time");
        }
        return 0;
    }

    private async Task ToggleLayerAsync(LayerEntry entry, bool visible)
    {
        entry.Visible = visible;
        if (_mapModule is not null)
            await _mapModule.InvokeVoidAsync("setLayerVisible", entry.LayerId, visible);

        if (visible && !entry.IsBaseLayer && !_legendOrder.Contains(entry))
            _legendOrder.Add(entry);
        else if (!visible)
            _legendOrder.Remove(entry);

        await SyncZIndicesAsync();
    }

    [JSInvokable]
    public async Task ReorderLegend(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _legendOrder.Count || to >= _legendOrder.Count) return;
        var item = _legendOrder[from];
        _legendOrder.RemoveAt(from);
        _legendOrder.Insert(to, item);
        await SyncZIndicesAsync();
        StateHasChanged();
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

    /// <summary>Converts #RRGGBB or #RRGGBBAA hex to a CSS rgba() string.</summary>
    private static string HexToCss(string hex)
    {
        if (string.IsNullOrEmpty(hex) || !hex.StartsWith('#')) return hex;
        var h = hex.TrimStart('#');
        if (h.Length == 6) return $"rgba({Convert.ToInt32(h[..2], 16)},{Convert.ToInt32(h[2..4], 16)},{Convert.ToInt32(h[4..6], 16)},1)";
        if (h.Length == 8) return $"rgba({Convert.ToInt32(h[..2], 16)},{Convert.ToInt32(h[2..4], 16)},{Convert.ToInt32(h[4..6], 16)},{Convert.ToInt32(h[6..8], 16) / 255.0:F3})";
        return hex;
    }

    /// <summary>Converts #RRGGBB or #RRGGBBAA hex to a [r,g,b,a] int array for JS interop.</summary>
    private static int[] HexToRgbaArray(string hex)
    {
        var h = hex.TrimStart('#');
        int r = Convert.ToInt32(h[..2],  16);
        int g = Convert.ToInt32(h[2..4], 16);
        int b = Convert.ToInt32(h[4..6], 16);
        int a = h.Length >= 8 ? Convert.ToInt32(h[6..8], 16) : 255;
        return [r, g, b, a];
    }

    /// <summary>Fallback display name from snake_case when layer_short is absent.</summary>
    private static string FallbackDisplayName(string layerName)
    {
        var s = layerName.TrimStart('_');
        return Regex.Replace(s, "[_\\-]+", " ");
    }

    /// <summary>Converts a snake_case category key to a human-readable title.</summary>
    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var s = Regex.Replace(value, "[_\\-]+", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    private static JsonElement GetPayload(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("payload", out var payload))
            return payload;
        return root;
    }

    /// <summary>
    /// Converts a "wiki://PageName" media reference into a full URL using _wikiBaseUrl.
    /// Returns null if the input is empty or _wikiBaseUrl is not configured.
    /// </summary>
    private string? ResolveWikiUrl(string? media)
    {
        if (string.IsNullOrWhiteSpace(media) || string.IsNullOrWhiteSpace(_wikiBaseUrl))
            return null;
        const string prefix = "wiki://";
        var page = media.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? media[prefix.Length..]
            : media;
        return string.IsNullOrWhiteSpace(page) ? null : $"{_wikiBaseUrl}/{page}";
    }

    [JSInvokable]
    public void OnMapClick(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root    = doc.RootElement;
            var layerId = root.TryGetProperty("layerId", out var lid) ? lid.GetString() ?? "" : "";
            var entry   = _layerEntries.FirstOrDefault(e => e.LayerId == layerId);

            _popupX = root.TryGetProperty("clientX", out var cx) && cx.ValueKind == JsonValueKind.Number ? cx.GetDouble() : 0;
            _popupY = root.TryGetProperty("clientY", out var cy) && cy.ValueKind == JsonValueKind.Number ? cy.GetDouble() : 0;
            _popupLayerName = entry?.DisplayName ?? layerId;
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
        _firstWsMessageTcs.TrySetResult();

        lock (_wsLog)
        {
            _wsLog.Insert(0, (msg.HeaderName, msg.RawJson, msg.ReceivedAt));
            if (_wsLog.Count > WsLogMaxEntries)
                _wsLog.RemoveAt(_wsLog.Count - 1);
        }

        switch (msg.HeaderName)
        {
            case "Game/Latest":
                ParseGameLatest(msg.Payload);
                break;
        }
        // Batch/ExecuteBatch and ImmersiveSessions/Update are stored by the service;
        // add processing here as needed.
        InvokeAsync(StateHasChanged);
    }

    private void ParseGameLatest(JsonElement payload)
    {
        // Game/Latest payload has a "tick" sub-object that holds the live game state.
        // Fall back to top-level properties for forward-compatibility.
        var tick = payload.TryGetProperty("tick", out var t) && t.ValueKind == JsonValueKind.Object
            ? t : payload;

        // Current month: "month" inside tick, or "game_current_month" at top level
        var currentMonth = GetIntProp(tick, "month");
        if (currentMonth == 0) currentMonth = GetIntProp(payload, "game_current_month");
        if (currentMonth > 0 || tick.TryGetProperty("month", out _) || payload.TryGetProperty("game_current_month", out _))
            _gameCurrentMonth = currentMonth;

        // Game state: "state" inside tick, or "game_state" at top level
        var gameState = GetStringProp(tick, "state") ?? GetStringProp(payload, "game_state");
        if (gameState is not null)
            _gameState = gameState;

        // end month may also arrive via WS if not in config
        if (_gameEndMonth == 0)
        {
            var endMonth = GetIntProp(payload, "game_end_month");
            if (endMonth > 0) _gameEndMonth = endMonth;
        }

        // era_timeleft: real-world seconds remaining in current era — inside tick
        var etlEl = tick.TryGetProperty("era_timeleft", out var e1) ? e1
                  : payload.TryGetProperty("era_timeleft", out var e2) ? e2
                  : default;
        if (etlEl.ValueKind != JsonValueKind.Undefined)
        {
            _eraTimeLeft = etlEl.ValueKind == JsonValueKind.Number
                ? etlEl.GetDouble()
                : double.TryParse(etlEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : _eraTimeLeft;
        }

        // Plans
        if (!payload.TryGetProperty("plan", out var plansEl) ||
            plansEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var p in plansEl.EnumerateArray())
        {
            var id        = GetIntProp(p, "id");
            var name      = GetStringProp(p, "name")      ?? $"Plan {id}";
            var state     = GetStringProp(p, "state")     ?? "";
            var country   = GetIntProp(p, "country");
            var startdate = GetIntProp(p, "startdate");

            // Parse plan layers and their geometry
            var planLayers = new List<PlanLayerData>();
            if (p.TryGetProperty("layers", out var layersEl2) && layersEl2.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in layersEl2.EnumerateArray())
                {
                    var planLayerId = GetStringProp(l, "layerid") ?? "";
                    var originalId  = GetStringProp(l, "original") ?? "";
                    var layerState  = GetStringProp(l, "state") ?? "";
                    var geometries  = new List<PlanGeometryItem>();

                    if (l.TryGetProperty("geometry", out var geoArr2) && geoArr2.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in geoArr2.EnumerateArray())
                        {
                            // Skip inactive geometries
                            var activeStr = GetStringProp(g, "active") ?? "1";
                            if (activeStr == "0") continue;

                            if (g.TryGetProperty("geometry", out var coords) && coords.ValueKind == JsonValueKind.Array)
                            {
                                var pts = new List<double[]>();
                                foreach (var pt in coords.EnumerateArray())
                                {
                                    if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
                                        pts.Add([pt[0].GetDouble(), pt[1].GetDouble()]);
                                }
                                if (pts.Count > 0)
                                {
                                    var geoId     = GetStringProp(g, "id")         ?? "";
                                    var persId    = GetStringProp(g, "persistent") ?? "";
                                    geometries.Add(new PlanGeometryItem(pts, geoId, persId));
                                }
                            }
                        }
                    }

                    // Parse deleted persistent geometry IDs
                    var deletedIds = new List<string>();
                    if (l.TryGetProperty("deleted", out var deletedEl) && deletedEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in deletedEl.EnumerateArray())
                        {
                            var dId = d.ValueKind == JsonValueKind.String ? d.GetString() : d.ToString();
                            if (!string.IsNullOrEmpty(dId)) deletedIds.Add(dId);
                        }
                    }

                    // Always add every referenced layer so construction time and base-layer lookups work
                    // even for layers that carry no geometry or deletion data in this payload.
                    planLayers.Add(new PlanLayerData(planLayerId, originalId, layerState, geometries, deletedIds));
                }
            }

            // Construction time: max ASSEMBLY time across all referenced base layers.
            var constructionTime = planLayers
                .Select(l => _layerEntries.FirstOrDefault(e => e.LayerId == l.OriginalLayerId)?.AssemblyTime ?? 0)
                .DefaultIfEmpty(0)
                .Max();

            // Policies: map policy_type to a human-readable display name.
            var policyNames = new List<string>();
            if (p.TryGetProperty("policies", out var polEl) && polEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var pol in polEl.EnumerateArray())
                {
                    var ptype = GetStringProp(pol, "policy_type")?.ToLowerInvariant();
                    var display = ptype switch
                    {
                        "fishing"  => "Fishing Effort",
                        "energy"   => "Energy Distribution",
                        "shipping" => "Shipping Safety Zones",
                        _          => null
                    };
                    if (display is not null && !policyNames.Contains(display))
                        policyNames.Add(display);
                }
            }

            var entry = new PlanEntry(id, name, state, country, startdate, constructionTime, policyNames, planLayers);
            var idx   = _plans.FindIndex(e => e.PlanId == id);
            if (idx >= 0)
                _plans[idx] = entry;
            else
                _plans.Add(entry);
        }
        _plans.Sort((a, b) =>
        {
            var sp = PlanStatePriority(a.State).CompareTo(PlanStatePriority(b.State));
            if (sp != 0) return sp;
            var dp = a.StartDate.CompareTo(b.StartDate);
            return dp != 0 ? dp : a.PlanId.CompareTo(b.PlanId);
        });
    }

    private string GameStateLabel => _gameState.ToLowerInvariant() switch
    {
        "pause"       => "Paused",
        "play"        => "Running",
        "fastforward" => "Fast Forward",
        "setup"       => "Setup",
        "end"         => "Ended",
        _             => _gameState
    };

    /// Formats seconds as H:MM:SS (e.g. 2:00:00, 0:45:12).
    private static string FormatTimeLeft(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static readonly string[] OrderedPlanStates =
    [
        "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "IMPLEMENTED", "ARCHIVED"
    ];

    private static int PlanStatePriority(string state) => state.ToUpperInvariant() switch
    {
        "DESIGN"        => 0,
        "CONSULTATION"  => 1,
        "APPROVAL"      => 2,
        "APPROVED"      => 3,
        "IMPLEMENTED"   => 4,
        "ARCHIVED"      => 5,
        _               => 6,
    };

    private static string PlanStateLabel(string state) => state.ToUpperInvariant() switch
    {
        "APPROVAL" => "AWAITING APPROVAL",
        _          => state.ToUpperInvariant(),
    };

    private string MonthToDate(int month)
    {
        var d = new DateTime(_gameStartYear, 1, 1).AddMonths(month);
        return d.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Case-insensitive int read; handles Number or numeric String values.</summary>
    private static int GetIntProp(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetInt32();
            if (prop.Value.ValueKind == JsonValueKind.String &&
                int.TryParse(prop.Value.GetString(), out var v)) return v;
        }
        return 0;
    }

    /// <summary>Case-insensitive string read.</summary>
    private static string? GetStringProp(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
        }
        return null;
    }


    private enum PlanViewMode { AfterChanges, Original, ChangesOnly }

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
                    coords = g.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray(),
                    isNew  = string.IsNullOrEmpty(g.PersistentId) || g.Id == g.PersistentId
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
        // No else: plans without geometry changes are still selectable (detail panel still shows).

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

    /// <summary>
    /// Infers point/line/polygon from coordinate count when layer metadata is unavailable.
    /// </summary>
    private async Task ClosePlanDetailAsync()
    {
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan is not null)
            await SelectPlanAsync(plan);
    }

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
        await WsService.DisposeAsync();
        if (_mapModule is not null)
        {
            try
            {
                await _mapModule.InvokeVoidAsync("unregisterClickHandler");
                await _mapModule.DisposeAsync();
            }
            catch { }
        }
        _dotNetRef?.Dispose();
    }
}
