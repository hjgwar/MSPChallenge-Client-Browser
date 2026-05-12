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

    // Loading state
    private bool   _isLoading = true;
    private bool   _loadingFading = false;
    private string _loadingStatus = "Initialising…";
    private readonly TaskCompletionSource _firstWsMessageTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // WebSocket (address comes from session listing, stored in SessionState)

    // Plans panel
    private bool _plansPanelOpen;
    private List<PlanEntry> _plans = new();

    // Dev console log (capped list of recent raw WS messages)
    private const int WsLogMaxEntries = 100;
    private readonly List<(string HeaderName, string Raw, DateTime ReceivedAt)> _wsLog = new();
    private string? _wsSelectedRaw;
    private bool    _wsLogVisible = true;

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

        var colorProp = geoType?.ToLowerInvariant() switch
        {
            "line" or "lines"   => "lineColor",
            "point" or "points" => "pointColor",
            _                   => "polygonColor"
        };
        var typeColors = new List<string>();
        if (layer.TryGetProperty("layer_type", out var ltArr) && ltArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var lt in ltArr.EnumerateArray())
            {
                var c = lt.TryGetProperty(colorProp, out var cp) ? cp.GetString() ?? "#3388ff" : "#3388ff";
                typeColors.Add(c);
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
                    var rasterColorMap = new List<object>();
                    if (layer.TryGetProperty("layer_type", out var ltRaster) && ltRaster.ValueKind == JsonValueKind.Array)
                    {
                        var entries = ltRaster.EnumerateArray()
                            .Where(lt => lt.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                            .Select(lt => (
                                threshold: lt.GetProperty("value").GetDouble(),
                                hex: lt.TryGetProperty("polygonColor", out var pc) ? pc.GetString() ?? "#000000FF" : "#000000FF"
                            ))
                            .OrderBy(e => e.threshold)
                            .ToList();

                        foreach (var (threshold, hex) in entries)
                        {
                            var normalised = threshold / entityValueMax * 255.0;
                            rasterColorMap.Add(new { value = normalised, rgba = HexToRgbaArray(hex) });
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
            if (layer.TryGetProperty("layer_type", out var ltDefArr) && ltDefArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var td in ltDefArr.EnumerateArray())
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
                PropertyDisplayNames = propDisplayNames
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

            _popupLayerName = entry?.DisplayName ?? layerId;
            _popupProps.Clear();

            if (root.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
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

            var entry = new PlanEntry(id, name, state, country, startdate);
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
