using System.Text.Json;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Partial class file containing all game-data loading logic for <see cref="GameSessionService"/>.
/// Separating this into its own file keeps the core class readable while the heavy async
/// loading and JSON-parsing code remains in one place.
/// </summary>
public sealed partial class GameSessionService
{
    private async Task LoadInitialStateAsync(
        MspApiClient apiClient,
        UserSessionService userSessionService,
        Func<string, Task>? reportStatus)
    {
        LayerEntries.Clear();
        MapLayerSnapshots.Clear();
        _plans.Clear();
        _planMessagesByPlanId.Clear();
        DependencyGroups = [];
        DependencyLinks = [];
        _restrictions.Clear();

        var baseAddress = userSessionService.GameServerAddress.TrimEnd('/');
        var sessionId = userSessionService.SessionId;

        if (reportStatus is not null)
            await reportStatus("Loading game settings…");

        // 1) Policy and simulation settings (currently fetched for completeness)
        await apiClient.GetAsync($"{baseAddress}/{sessionId}/api/Game/PolicySimSettings");

        // 2) Global game config
        var configRoot = await apiClient.GetAsync($"{baseAddress}/{sessionId}/api/Game/Config");
        var configPayload = ConversionUtils.GetPayload(configRoot);

        if (configPayload.TryGetProperty("wiki_base_url", out var wbuProp))
        {
            var wbu = wbuProp.GetString();
            if (!string.IsNullOrWhiteSpace(wbu))
                WikiBaseUrl = wbu.TrimEnd('/');
        }

        if (configPayload.TryGetProperty("start", out var startProp))
            GameStartYear = startProp.ValueKind == JsonValueKind.Number
                ? startProp.GetInt32()
                : int.TryParse(startProp.GetString(), out var sy) ? sy : 2000;

        if (configPayload.TryGetProperty("end", out var endYearProp))
        {
            int parsedEnd = endYearProp.ValueKind == JsonValueKind.Number
                ? endYearProp.GetInt32()
                : int.TryParse(endYearProp.GetString(), out var ey) ? ey : 0;
            if (parsedEnd > 0)
                GameEndYear = parsedEnd;
        }

        if (configPayload.TryGetProperty("era_total_months", out var eraTotalMonthsProp))
        {
            int parsedMonths = eraTotalMonthsProp.ValueKind == JsonValueKind.Number
                ? eraTotalMonthsProp.GetInt32()
                : int.TryParse(eraTotalMonthsProp.GetString(), out var em) ? em : 0;
            if (parsedMonths > 0)
                GameEraTotalMonths = parsedMonths;
        }

        // Parse policy_settings to know which policy types are enabled for this session
        if (configPayload.TryGetProperty("policy_settings", out var policySettingsEl))
        {
            static string? PolicyDisplayName(string type) => type.ToLowerInvariant() switch
            {
                "fishing"  => "Fishing Effort",
                "energy"   => "Energy Distribution",
                "shipping" => "Shipping Safety Zones",
                _          => null,
            };

            var policies = new List<PolicySetting>();
            if (policySettingsEl.ValueKind == JsonValueKind.Object)
            {
                // Object format: { "fishing": { "enabled": true }, ... }
                foreach (var kv in policySettingsEl.EnumerateObject())
                {
                    var enabled = true;
                    if (kv.Value.TryGetProperty("enabled", out var enEl))
                        enabled = enEl.ValueKind != JsonValueKind.False
                                  && !(enEl.ValueKind == JsonValueKind.Number && enEl.GetInt32() == 0)
                                  && !(enEl.ValueKind == JsonValueKind.String && enEl.GetString() is "0" or "false");
                    var display = PolicyDisplayName(kv.Name) ?? kv.Name;
                    policies.Add(new PolicySetting(kv.Name.ToLowerInvariant(), display, enabled));
                }
            }
            else if (policySettingsEl.ValueKind == JsonValueKind.Array)
            {
                // Array format: [ { "type": "fishing", "enabled": true }, ... ]
                foreach (var el in policySettingsEl.EnumerateArray())
                {
                    var ptype = ConversionUtils.GetStringProp(el, "type") ?? ConversionUtils.GetStringProp(el, "policy_type") ?? "";
                    if (string.IsNullOrWhiteSpace(ptype)) continue;
                    var enabled = true;
                    if (el.TryGetProperty("enabled", out var enEl))
                        enabled = enEl.ValueKind != JsonValueKind.False
                                  && !(enEl.ValueKind == JsonValueKind.Number && enEl.GetInt32() == 0)
                                  && !(enEl.ValueKind == JsonValueKind.String && enEl.GetString() is "0" or "false");
                    var display = PolicyDisplayName(ptype) ?? ptype;
                    policies.Add(new PolicySetting(ptype.ToLowerInvariant(), display, enabled));
                }
            }

            // Fall back: if server doesn't expose policy_settings at all, seed from known types
            if (policies.Count == 0)
            {
                policies.Add(new PolicySetting("fishing",  "Fishing Effort",        true));
                policies.Add(new PolicySetting("energy",   "Energy Distribution",   true));
                policies.Add(new PolicySetting("shipping", "Shipping Safety Zones", true));
            }

            AvailablePolicies = policies;
        }
        else
        {
            // No policy_settings key — default to all three known types enabled
            AvailablePolicies = new List<PolicySetting>
            {
                new("fishing",  "Fishing Effort",        true),
                new("energy",   "Energy Distribution",   true),
                new("shipping", "Shipping Safety Zones", true),
            };
        }

        if (configPayload.TryGetProperty("dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.Object)
        {
            var depGroups = new List<DependencyGroup>();
            if (depsEl.TryGetProperty("groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var dg in groupsEl.EnumerateArray())
                {
                    var dgName = ConversionUtils.GetStringProp(dg, "name") ?? "";
                    var dgEntries = new List<DependencyEntry>();
                    if (dg.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var de in entriesEl.EnumerateArray())
                        {
                            var deId   = ConversionUtils.GetStringProp(de, "id")   ?? "";
                            var deName = ConversionUtils.GetStringProp(de, "name") ?? "";
                            var deLink = ConversionUtils.GetStringProp(de, "link");
                            if (!string.IsNullOrEmpty(deId))
                                dgEntries.Add(new DependencyEntry(deId, deName, deLink));
                        }
                    }
                    if (!string.IsNullOrEmpty(dgName))
                        depGroups.Add(new DependencyGroup(dgName, dgEntries));
                }
            }

            var depLinks = new List<DependencyLink>();
            if (depsEl.TryGetProperty("links", out var linksEl) && linksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var dl in linksEl.EnumerateArray())
                {
                    var fromId = ConversionUtils.GetStringProp(dl, "fromId") ?? "";
                    var toId   = ConversionUtils.GetStringProp(dl, "toId")   ?? "";
                    var sev    = ConversionUtils.GetIntProp(dl, "severity");
                    var desc   = ConversionUtils.GetStringProp(dl, "description") ?? "";
                    if (!string.IsNullOrEmpty(fromId) && !string.IsNullOrEmpty(toId))
                        depLinks.Add(new DependencyLink(fromId, toId, sev, desc));
                }
            }
            SetDependencies(depGroups, depLinks);
        }

        // Unity-compatible source of restrictions.
        await LoadPlanRestrictionsAsync(apiClient, baseAddress, sessionId);

        // EEZ polygon geometry — Countries and EezLayerId are both seeded from Home before
        // the Game page opens, so no Layer/MetaByName call is needed here.
        if (!string.IsNullOrWhiteSpace(EezLayerId) && Countries.Count > 0)
        {
            var typeIndexToCountry = Countries
                .Select((c, i) => (i, c.Id))
                .ToDictionary(x => x.i, x => x.Id);

            try
            {
                var geoRoot = await apiClient.PostFormAsync(
                    $"{baseAddress}/{sessionId}/api/Layer/Get",
                    new[] { new KeyValuePair<string, string>("layer_id", EezLayerId) });
                var geoPayload = ConversionUtils.GetPayload(geoRoot);
                if (geoPayload.ValueKind == JsonValueKind.Array)
                {
                    var eezList = new List<EezPolygon>();
                    foreach (var feat in geoPayload.EnumerateArray())
                    {
                        var typeIdx = 0;
                        if (feat.TryGetProperty("type", out var tEl))
                        {
                            if (tEl.ValueKind == JsonValueKind.Number) typeIdx = tEl.GetInt32();
                            else if (tEl.ValueKind == JsonValueKind.String) int.TryParse(tEl.GetString(), out typeIdx);
                        }
                        if (!typeIndexToCountry.TryGetValue(typeIdx, out var countryId)) continue;
                        if (!feat.TryGetProperty("geometry", out var geomEl) || geomEl.ValueKind != JsonValueKind.Array) continue;

                        var pts = new List<double[]>();
                        foreach (var pt in geomEl.EnumerateArray())
                        {
                            if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2
                                && pt[0].ValueKind == JsonValueKind.Number && pt[1].ValueKind == JsonValueKind.Number)
                                pts.Add([pt[0].GetDouble(), pt[1].GetDouble()]);
                        }
                        if (pts.Count >= 3)
                            eezList.Add(new EezPolygon(countryId, pts));
                    }
                    EezPolygons = eezList;
                }
            }
            catch { /* EEZ geometry is optional; skip on any error */ }
        }

        if (reportStatus is not null)
            await reportStatus("Loading map layers…");

        var metaRoot = await apiClient.PostFormAsync(
            $"{baseAddress}/{sessionId}/api/Game/Meta",
            new[] { new KeyValuePair<string, string>("user", userSessionService.User.Country.Id.ToString()) });
        var metaPayload = ConversionUtils.GetPayload(metaRoot);
        if (metaPayload.ValueKind != JsonValueKind.Array) return;

        var layerArray = metaPayload.EnumerateArray().ToList();
        var total  = layerArray.Count;
        var loaded = 0;

        foreach (var layer in layerArray)
        {
            var shortName = layer.TryGetProperty("layer_short", out var ls) ? ls.GetString() : null;
            var layerName = layer.TryGetProperty("layer_name",  out var ln) ? ln.GetString() : null;
            var name = !string.IsNullOrWhiteSpace(shortName) ? shortName : (layerName ?? "");
            if (reportStatus is not null)
                await reportStatus($"Loading {loaded + 1} / {total}: {name}");

            var snapshot = await BuildLayerSnapshotAsync(apiClient, baseAddress, sessionId, layer);
            if (snapshot is not null)
                MapLayerSnapshots.Add(snapshot);

            loaded++;
        }

        // Initial legend order follows server depth (bottom → top).
        _uiState.ResetLegendOrderFromVisibleDepth(LayerEntries);

        if (!string.IsNullOrEmpty(userSessionService.GameWsServerAddress) &&
            _ws.State != System.Net.WebSockets.WebSocketState.Open &&
            _ws.State != System.Net.WebSockets.WebSocketState.Connecting)
        {
            if (reportStatus is not null)
                await reportStatus("Connecting to game server…");

            await _ws.ConnectAndSubscribeAsync(
                userSessionService.GameWsServerAddress,
                userSessionService.ApiAccessToken,
                userSessionService.SessionId,
                teamId: userSessionService.User.Country.Id,
                userId: userSessionService.User.Id);
        }
    }


    private async Task<MapLayerSnapshot?> BuildLayerSnapshotAsync(
        MspApiClient apiClient,
        string baseAddress,
        int sessionId,
        JsonElement layer)
    {
        var layerId   = layer.TryGetProperty("layer_id",   out var id) ? id.ToString()     : null;
        var layerName = layer.TryGetProperty("layer_name", out var n)  ? n.GetString()     : null;
        var geoType   = layer.TryGetProperty("layer_geotype", out var gt) ? gt.GetString() : null;
        if (layerId is null || layerName is null) return null;

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
                foreach (var lt in ltEl.EnumerateArray())
                {
                    var c = lt.TryGetProperty(colorProp, out var cp) ? cp.GetString() ?? "#3388ff" : "#3388ff";
                    typeColors.Add(c);
                }
            }
            else if (ltEl.ValueKind == JsonValueKind.Object)
            {
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

        string? labelKey = null;
        if (layer.TryGetProperty("layer_text_info", out var lti) &&
            lti.ValueKind == JsonValueKind.Object &&
            lti.TryGetProperty("property_per_state", out var pps) &&
            pps.ValueKind == JsonValueKind.Object &&
            pps.TryGetProperty("Current", out var curr) &&
            curr.GetString() is { Length: > 0 } currentProp)
        {
            if (layer.TryGetProperty("layer_info_properties", out var lip) && lip.ValueKind == JsonValueKind.Array)
            {
                foreach (var prop in lip.EnumerateArray())
                {
                    if (prop.TryGetProperty("property_name", out var pn) && pn.GetString() == currentProp)
                    {
                        labelKey = currentProp;
                        break;
                    }
                }
            }
        }

        bool visible    = layer.TryGetProperty("layer_active_on_start", out var aos) &&
                          aos.ValueKind == JsonValueKind.Number && aos.GetInt32() != 0;
        var depth       = layer.TryGetProperty("layer_depth",        out var ld)   && ld.ValueKind   == JsonValueKind.Number ? ld.GetInt32()   : 0;
        var toggleable  = layer.TryGetProperty("layer_toggleable",   out var lt2)  && lt2.ValueKind  == JsonValueKind.Number ? lt2.GetInt32()  != 0 : true;
        var editable    = layer.TryGetProperty("layer_editable",     out var leEl) && leEl.ValueKind == JsonValueKind.Number ? leEl.GetInt32() != 0 : false;
        var editingType = layer.TryGetProperty("layer_editing_type", out var letEl) ? letEl.GetString() ?? "" : "";
        var isBase      = layerName.IndexOf("_PLAYAREA", StringComparison.OrdinalIgnoreCase) >= 0;

        var layerShort   = layer.TryGetProperty("layer_short",       out var ls2)  ? ls2.GetString()  ?? "" : "";
        var category     = layer.TryGetProperty("layer_category",    out var lc)   ? lc.GetString()   ?? "" : "";
        var subcategory  = layer.TryGetProperty("layer_subcategory", out var lsc)  ? lsc.GetString()  ?? "" : "";
        var tooltip      = layer.TryGetProperty("layer_tooltip",     out var ltt)  ? ltt.GetString()  ?? "" : "";
        var displayName  = !string.IsNullOrWhiteSpace(layerShort) ? layerShort : ConversionUtils.FallbackDisplayName(layerName);
        var layerMedia   = layer.TryGetProperty("layer_media",       out var lm)   ? lm.GetString()   ?? "" : "";
        var layerMediaUrl = ResolveWikiUrl(layerMedia);

        var typeDefs = new List<TypeDef>();
        if (layer.TryGetProperty("layer_type", out var ltDefEl))
        {
            IEnumerable<JsonElement> typeDefItems = ltDefEl.ValueKind switch
            {
                JsonValueKind.Array  => ltDefEl.EnumerateArray(),
                JsonValueKind.Object => ltDefEl.EnumerateObject()
                    .OrderBy(kv => int.TryParse(kv.Name, out var n2) ? n2 : int.MaxValue)
                    .Select(kv => kv.Value),
                _ => []
            };

            foreach (var td in typeDefItems)
            {
                var label      = td.TryGetProperty("displayName", out var dn)  ? dn.GetString()  ?? "" : "";
                var col        = td.TryGetProperty(colorProp,     out var cp)  ? cp.GetString()  ?? "#3388ff" : "#3388ff";
                var tdMedia    = td.TryGetProperty("media",       out var tm)  ? tm.GetString()  ?? "" : "";
                var tdMediaUrl = ResolveWikiUrl(tdMedia);
                var tdApproval = td.TryGetProperty("approval",    out var ap)  ? ap.GetString()  ?? "NotDependent" : "NotDependent";
                if (!string.IsNullOrWhiteSpace(label))
                    typeDefs.Add(new TypeDef(label, col, tdMediaUrl, tdApproval));
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

        string?   vectorGeometriesJson = null;
        string?   rasterImageData      = null;
        double[][]? rasterProjBounds   = null;
        var rasterColorMap  = new List<RasterColorStop>();
        double? minCutoffNorm = null;
        var interpolate = true;

        if (string.Equals(geoType, "raster", StringComparison.OrdinalIgnoreCase))
        {
            var rasterRoot = await apiClient.PostFormAsync(
                $"{baseAddress}/{sessionId}/api/Layer/GetRaster",
                new[] { new KeyValuePair<string, string>("layer_name", layerName) });
            var rasterPayload = ConversionUtils.GetPayload(rasterRoot);

            if (rasterPayload.TryGetProperty("image_data", out var imgData) &&
                imgData.GetString() is string b64 &&
                rasterPayload.TryGetProperty("displayed_bounds", out var bb) &&
                bb.ValueKind == JsonValueKind.Array &&
                bb.GetArrayLength() == 2)
            {
                rasterImageData = b64;
                rasterProjBounds =
                [
                    [ bb[0][0].GetDouble(), bb[0][1].GetDouble() ],
                    [ bb[1][0].GetDouble(), bb[1][1].GetDouble() ]
                ];

                var entityValueMax = 1000.0;
                if (layer.TryGetProperty("layer_entity_value_max", out var evmProp) &&
                    evmProp.ValueKind == JsonValueKind.Number)
                {
                    entityValueMax = evmProp.GetDouble();
                    if (entityValueMax <= 0) entityValueMax = 1000.0;
                }

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
                        rasterColorMap.Add(new RasterColorStop { Value = normalised, Rgba = ConversionUtils.HexToRgbaArray(hex) });
                        rasterThresholds.Add((normalised, label));
                    }
                }

                if (layer.TryGetProperty("layer_raster", out var lrObj) && lrObj.ValueKind == JsonValueKind.Object)
                {
                    if (lrObj.TryGetProperty("layer_raster_minimum_value_cutoff", out var cutoffProp) &&
                        cutoffProp.ValueKind == JsonValueKind.Number)
                        minCutoffNorm = cutoffProp.GetDouble() * 255.0;

                    if (lrObj.TryGetProperty("layer_raster_color_interpolation", out var interpProp) &&
                        interpProp.ValueKind == JsonValueKind.True)
                        interpolate = false;
                }
            }
        }
        else
        {
            var geoRoot = await apiClient.PostFormAsync(
                $"{baseAddress}/{sessionId}/api/Layer/Get",
                new[] { new KeyValuePair<string, string>("layer_id", layerId) });
            var geoPayload = ConversionUtils.GetPayload(geoRoot);

            if (geoPayload.ValueKind == JsonValueKind.Array && geoPayload.GetArrayLength() > 0)
                vectorGeometriesJson = geoPayload.ToString();
        }

        LayerEntries.Add(new Layer
        {
            LayerId      = layerId,
            LayerName    = layerName,
            DisplayName  = displayName,
            Category     = category,
            Subcategory  = subcategory,
            Tooltip      = tooltip,
            MediaUrl     = layerMediaUrl,
            IsBaseLayer  = isBase,
            IsToggleable = toggleable,
            Visible      = visible,
            Depth        = depth,
            TypeDefs     = typeDefs,
            PropertyDisplayNames = propDisplayNames,
            IsRaster     = string.Equals(geoType, "raster", StringComparison.OrdinalIgnoreCase),
            RasterThresholds = rasterThresholds,
            GeoType      = geoType ?? "",
            AssemblyTime = ConversionUtils.ParseAssemblyTime(layer),
            Editable     = editable,
            EditingType  = editingType
        });

        return new MapLayerSnapshot
        {
            LayerId              = layerId,
            GeoType              = geoType ?? "",
            Visible              = visible,
            Depth                = depth,
            IsBaseLayer          = isBase,
            VectorGeometriesJson = vectorGeometriesJson,
            TypeColors           = typeColors,
            LabelKey             = labelKey,
            RasterImageData      = rasterImageData,
            RasterProjBounds     = rasterProjBounds,
            RasterColorMap       = rasterColorMap,
            RasterMinCutoffNorm  = minCutoffNorm,
            RasterInterpolate    = interpolate
        };
    }

    private string? ResolveWikiUrl(string? media)
    {
        if (string.IsNullOrWhiteSpace(media) || string.IsNullOrWhiteSpace(WikiBaseUrl))
            return null;
        const string prefix = "wiki://";
        var page = media.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? media[prefix.Length..]
            : media;
        return string.IsNullOrWhiteSpace(page) ? null : $"{WikiBaseUrl}/{page}";
    }

    private async Task LoadPlanRestrictionsAsync(MspApiClient apiClient, string baseAddress, int sessionId)
    {
        _restrictions.Clear();
        var url = $"{baseAddress}/{sessionId}/api/Plan/Restrictions";

        try
        {
            var root = await apiClient.GetAsync(url);
            ParseRestrictions(root, clearExisting: true);
        }
        catch (MspApiException)
        {
            try
            {
                var root = await apiClient.PostFormAsync(url, []);
                ParseRestrictions(root, clearExisting: true);
            }
            catch { }
        }
    }

    private void ParseRestrictions(JsonElement root, bool clearExisting)
    {
        if (clearExisting)
            _restrictions.Clear();

        var payload = ConversionUtils.GetPayload(root);

        JsonElement restrictionsEl;
        if (payload.ValueKind == JsonValueKind.Array)
            restrictionsEl = payload;
        else if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("restrictions", out var nestedRestrictions))
            restrictionsEl = nestedRestrictions;
        else
            return;

        IEnumerable<JsonElement> items = restrictionsEl.ValueKind switch
        {
            JsonValueKind.Array => restrictionsEl.EnumerateArray(),
            JsonValueKind.Object when restrictionsEl.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array
                => itemsEl.EnumerateArray(),
            JsonValueKind.Object => restrictionsEl.EnumerateObject().Select(p => p.Value),
            _ => []
        };

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var message    = ConversionUtils.GetStringPropLoose(item, "message", "text", "description", "restriction_message") ?? "";
            var type       = ConversionUtils.GetStringPropLoose(item, "type", "severity", "level", "restriction_type") ?? "warning";
            var startLayer = ConversionUtils.GetStringPropLoose(item, "startlayer", "start_layer", "start", "from_layer", "from", "restriction_start_layer_id") ?? "";
            var startType  = ConversionUtils.GetStringPropLoose(item, "starttype", "start_type", "start_layer_type", "from_type", "restriction_start_layer_type") ?? "";
            var endLayer   = ConversionUtils.GetStringPropLoose(item, "endlayer", "end_layer", "end", "to_layer", "to", "restriction_end_layer_id") ?? "";
            var endType    = ConversionUtils.GetStringPropLoose(item, "endtype", "end_type", "end_layer_type", "to_type", "restriction_end_layer_type") ?? "";
            var sort       = ConversionUtils.GetStringPropLoose(item, "sort", "order", "restriction_sort") ?? "";

            if (string.IsNullOrWhiteSpace(startLayer) || string.IsNullOrWhiteSpace(endLayer))
                continue;

            _restrictions.Add(new RestrictionRule(message, type, startLayer, startType, endLayer, endType, sort));
        }
    }
}
