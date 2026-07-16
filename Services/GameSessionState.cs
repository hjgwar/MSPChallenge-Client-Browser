using System.Text.Json;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Circuit-scoped service that holds all loaded game configuration and live WebSocket state.
/// Survives page navigation within the same browser tab (Blazor Server circuit lifetime).
/// </summary>
public sealed class GameSessionState : IDisposable
{
    private readonly WebSocketService _ws;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    public GameSessionState(WebSocketService ws)
    {
        ArgumentNullException.ThrowIfNull(ws);
        _ws = ws;
        _ws.MessageReceived += OnWsMessage;
    }

    // ── Config (written once during LoadGameDataAsync on the Game page) ─────────
    public List<Layer>        LayerEntries   { get; } = new();
    public List<MapLayerSnapshot>  MapLayerSnapshots { get; } = new();
    /// <summary>
    /// Visible non-base layers in legend order: index 0 = bottom, last = top.
    /// Stored in shared session state so Game page navigation preserves user ordering.
    /// </summary>
    public List<string> LegendOrderLayerIds { get; } = new();
    public IReadOnlyList<Country> Countries = []; 
    /// <summary>EEZ polygon geometries keyed by country ID. Used for approval calculation.</summary>
    public IReadOnlyList<EezPolygon> EezPolygons { get; private set; } = [];
    /// <summary>
    /// ID of the EEZ layer, seeded from Home when the player connects.
    /// Used by LoadGameConfigAsync to call Layer/Get without re-fetching Layer/MetaByName.
    /// </summary>
    public string? EezLayerId { get; set; }
    public string WikiBaseUrl   { get; set; } = "";
    public int    GameStartYear { get; set; } = 2000;
    public int    GameEndMonth  { get; set; } = 0;
    public int    GameEndYear   { get; set; } = 0;
    public int    GameEraTotalMonths { get; set; } = 120;

    // ── UI state persisted across page navigation (same circuit) ──────────────
    public bool LayerPanelOpen      { get; set; } = true;
    public bool LegendPanelOpen     { get; set; } = true;
    public bool OnlineUsersPanelOpen { get; set; } = false;
    public bool PlansPanelOpen      { get; set; } = false;
    public bool CreatePlanOpen      { get; set; } = false;
    public bool TimeManagerOpen     { get; set; } = false;
    public int? SelectedPlanId { get; set; } = null;
    public PlanViewMode PlanViewMode { get; set; } = PlanViewMode.AfterChanges;
    public bool EditMode { get; set; } = false;

    /// <summary>
    /// Temporary seed values written by Game.OpenEditModeFromCreation when the player
    /// accepts the PlanCreation form.  PlanDetails reads and clears these on its first
    /// OnInitialized / OnStateChanged after mounting in new-plan edit mode.
    /// </summary>
    public string PendingNewPlanName        { get; set; } = string.Empty;
    public string PendingNewPlanDescription { get; set; } = string.Empty;
    public int    PendingNewPlanStartYear   { get; set; } = 0;
    public int    PendingNewPlanStartMonth  { get; set; } = 1;

    /// <summary>Toggle edit mode on/off.</summary>
    public void ToggleEditMode()
    {
        EditMode = !EditMode;
        NotifyChanged();
    }

    // Persisted map camera for smooth return navigation.
    public double? MapLat { get; private set; }
    public double? MapLng { get; private set; }
    public double? MapZoom { get; private set; }
    public bool HasSavedMapView => MapLat.HasValue && MapLng.HasValue && MapZoom.HasValue;

    public Plan? SelectedPlan => SelectedPlanId.HasValue
            ? Plans.FirstOrDefault(p => p.PlanId == SelectedPlanId.Value)
            : null;
    public void SaveMapView(double lat, double lng, double zoom)
    {
        MapLat = lat;
        MapLng = lng;
        MapZoom = zoom;
    }

    // ── Live WebSocket state ───────────────────────────────────────────────────
    public const int ERA_COUNT = 4;
    public int    GameCurrentMonth { get; private set; } = 0;
    public string GameState        { get; private set; } = "";
    public double EraTimeLeft      { get; private set; } = 0;
    public int[]  EraRealTimes     { get; private set; } = new int[ERA_COUNT];

    /// <summary>Get the current era index (0-3) based on current month and era total months.</summary>
    public int GetCurrentEra()
    {
        if (GameEraTotalMonths <= 0) return 0;
        return Math.Min(GameCurrentMonth / GameEraTotalMonths, ERA_COUNT - 1);
    }

    public IReadOnlyList<Plan> Plans => _plans;
    private readonly List<Plan> _plans = new();
    private readonly Dictionary<int, List<PlanMessage>> _planMessagesByPlanId = new();
    private int _nextPlanMessageSequence = 1;

    public IReadOnlyList<PlanMessage> GetPlanMessages(int planId) =>
        _planMessagesByPlanId.TryGetValue(planId, out var messages)
            ? messages
            : Array.Empty<PlanMessage>();

    // ── Dependency graph data (from Game/Config) ───────────────────────────────
    public IReadOnlyList<DependencyGroup> DependencyGroups { get; private set; } = [];
    public IReadOnlyList<DependencyLink>  DependencyLinks  { get; private set; } = [];
    public IReadOnlyList<RestrictionRule> Restrictions => _restrictions;
    private readonly List<RestrictionRule> _restrictions = new();

    // ── Available policies (from Game/Config policy_settings) ─────────────────
    public IReadOnlyList<PolicySetting> AvailablePolicies { get; private set; } = [];

    public void SetDependencies(
        IReadOnlyList<DependencyGroup> groups,
        IReadOnlyList<DependencyLink>  links)
    {
        DependencyGroups = groups;
        DependencyLinks  = links;
    }

    // ── Change notification ────────────────────────────────────────────────────
    /// <summary>
    /// Raised on a background thread when live WebSocket state has been updated.
    /// Subscribers must marshal to the Blazor UI thread via InvokeAsync.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Manually triggers the Changed event. Use when UI state properties are modified
    /// outside of WebSocket message handling.
    /// </summary>
    public void NotifyChanged()
    {
        Changed?.Invoke();
    }

    // ── WS message routing ─────────────────────────────────────────────────────
    private void OnWsMessage(WsMessage msg)
    {
        if (msg.HeaderName == "Game/Latest")
            ApplyGameLatest(msg.Payload);
        Changed?.Invoke();
    }

    // ── Game/Latest parsing ────────────────────────────────────────────────────
    public void ApplyGameLatest(JsonElement payload)
    {
        var tick = payload.TryGetProperty("tick", out var t) && t.ValueKind == JsonValueKind.Object
            ? t : payload;

        var currentMonth = ConversionUtils.GetIntProp(tick, "month");
        if (currentMonth == 0) currentMonth = ConversionUtils.GetIntProp(payload, "game_current_month");
        if (currentMonth > 0 || tick.TryGetProperty("month", out _) || payload.TryGetProperty("game_current_month", out _))
            GameCurrentMonth = currentMonth;

        var gameState = ConversionUtils.GetStringProp(tick, "state") ?? ConversionUtils.GetStringProp(payload, "game_state");
        if (gameState is not null)
            GameState = gameState;

        if (GameEndMonth == 0)
        {
            var endMonth = ConversionUtils.GetIntProp(payload, "game_end_month");
            if (endMonth > 0) GameEndMonth = endMonth;
        }

        var etlEl = tick.TryGetProperty("era_timeleft", out var e1) ? e1
                  : payload.TryGetProperty("era_timeleft", out var e2) ? e2
                  : default;
        if (etlEl.ValueKind != JsonValueKind.Undefined)
        {
            EraTimeLeft = etlEl.ValueKind == JsonValueKind.Number
                ? etlEl.GetDouble()
                : double.TryParse(etlEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : EraTimeLeft;
        }

        // Parse planning_era_realtime (comma-separated future era times in seconds)
        var pertEl = tick.TryGetProperty("planning_era_realtime", out var p1) ? p1
                   : payload.TryGetProperty("planning_era_realtime", out var p2) ? p2
                   : default;
        if (pertEl.ValueKind != JsonValueKind.Undefined)
        {
            var pertStr = pertEl.ValueKind == JsonValueKind.String ? pertEl.GetString() : pertEl.ToString();
            if (!string.IsNullOrWhiteSpace(pertStr))
            {                var parts = pertStr.Split(',');
                for (int i = 0; i < Math.Min(parts.Length, ERA_COUNT); i++)
                {
                    if (int.TryParse(parts[i].Trim(), out var seconds))
                        EraRealTimes[i] = seconds;
                }
            }
        }

        // Plan messages: cache full message threads per plan_id.
        var parsedPlanMessages = new Dictionary<int, List<PlanMessage>>();
        if (payload.TryGetProperty("planmessages", out var pmEl) && pmEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var pm in pmEl.EnumerateArray())
            {
                var pmPlanId = ConversionUtils.GetIntProp(pm, "plan_id");
                if (pmPlanId <= 0) continue;

                var message = ConversionUtils.ParsePlanMessage(pm, pmPlanId, _nextPlanMessageSequence++);
                if (message is null) continue;

                if (!parsedPlanMessages.TryGetValue(pmPlanId, out var messages))
                {
                    messages = new List<PlanMessage>();
                    parsedPlanMessages[pmPlanId] = messages;
                }

                messages.Add(message);
            }

            foreach (var kvp in parsedPlanMessages)
            {
                MergePlanMessages(kvp.Key, kvp.Value);
            }
        }

        if (!payload.TryGetProperty("plan", out var plansEl) ||
            plansEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var p in plansEl.EnumerateArray())
        {
            var id          = ConversionUtils.GetIntProp(p, "id");
            var name        = ConversionUtils.GetStringProp(p, "name")        ?? $"Plan {id}";
            var description = ConversionUtils.GetStringProp(p, "description") ?? "";
            var state       = ConversionUtils.GetStringProp(p, "state")       ?? "";
            var country     = ConversionUtils.GetIntProp(p, "country");
            var startdate   = ConversionUtils.GetIntProp(p, "startdate");

            var planLayers = new List<PlanLayerData>();
            if (p.TryGetProperty("layers", out var layersEl) && layersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in layersEl.EnumerateArray())
                {
                    var planLayerId = ConversionUtils.GetStringProp(l, "layerid") ?? "";
                    var originalId  = ConversionUtils.GetStringProp(l, "original") ?? "";
                    var layerState  = ConversionUtils.GetStringProp(l, "state") ?? "";
                    var geometries  = new List<PlanGeometryItem>();

                    if (l.TryGetProperty("geometry", out var geoArr) && geoArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in geoArr.EnumerateArray())
                        {
                            var activeStr = ConversionUtils.GetStringProp(g, "active") ?? "1";
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
                                    var geoId   = ConversionUtils.GetStringProp(g, "id")         ?? "";
                                    var persId  = ConversionUtils.GetStringProp(g, "persistent") ?? "";
                                    var typeStr = ConversionUtils.GetStringProp(g, "type")       ?? "0";
                                    var typeIdx = int.TryParse(typeStr, out var ti) ? ti : 0;
                                    geometries.Add(new PlanGeometryItem(pts, geoId, persId, typeIdx));
                                }
                            }
                        }
                    }

                    var deletedIds = new List<string>();
                    if (l.TryGetProperty("deleted", out var deletedEl) && deletedEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in deletedEl.EnumerateArray())
                        {
                            var dId = d.ValueKind == JsonValueKind.String ? d.GetString() : d.ToString();
                            if (!string.IsNullOrEmpty(dId)) deletedIds.Add(dId);
                        }
                    }

                    planLayers.Add(new PlanLayerData(planLayerId, originalId, layerState, geometries, deletedIds));
                }
            }

            var constructionTime = planLayers
                .Select(l => LayerEntries.FirstOrDefault(e => e.LayerId == l.OriginalLayerId)?.AssemblyTime ?? 0)
                .DefaultIfEmpty(0)
                .Max();

            var policyNames = new List<string>();
            var policyTypes = new List<string>();
            if (p.TryGetProperty("policies", out var polEl) && polEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var pol in polEl.EnumerateArray())
                {
                    var ptype = ConversionUtils.GetStringProp(pol, "policy_type")?.ToLowerInvariant();
                    var display = ptype switch
                    {
                        "fishing"  => "Fishing Effort",
                        "energy"   => "Energy Distribution",
                        "shipping" => "Shipping Safety Zones",
                        _          => null
                    };
                    if (display is not null && !policyNames.Contains(display))
                    {
                        policyNames.Add(display);
                        policyTypes.Add(ptype!);
                    }
                }
            }

            var requiresApproval = false;
            if (p.TryGetProperty("approval_required", out var arEl))
                requiresApproval = arEl.ValueKind == JsonValueKind.True
                    || (arEl.ValueKind == JsonValueKind.Number && arEl.GetInt32() != 0)
                    || (arEl.ValueKind == JsonValueKind.String && arEl.GetString() is "1" or "true");

            Dictionary<int, int>? votes = null;
            if (p.TryGetProperty("votes", out var votesEl) && votesEl.ValueKind == JsonValueKind.Array)
            {
                votes = new Dictionary<int, int>();
                foreach (var v in votesEl.EnumerateArray())
                {
                    var cidEl2 = v.TryGetProperty("country", out var cidEl) ? cidEl : default;
                    var vEl2   = v.TryGetProperty("vote",    out var vEl)    ? vEl    : default;
                    var cid = cidEl2.ValueKind == JsonValueKind.Number ? cidEl2.GetInt32()
                            : cidEl2.ValueKind == JsonValueKind.String && int.TryParse(cidEl2.GetString(), out var ci) ? ci : 0;
                    var voteVal = vEl2.ValueKind == JsonValueKind.Number ? vEl2.GetInt32()
                                : vEl2.ValueKind == JsonValueKind.String && int.TryParse(vEl2.GetString(), out var vi) ? vi : -1;
                    if (cid > 0) votes[cid] = voteVal;
                }
            }

            if (p.TryGetProperty("planmessages", out var planPmEl) && planPmEl.ValueKind == JsonValueKind.Array)
            {
                var nestedMessages = new List<PlanMessage>();
                foreach (var pm in planPmEl.EnumerateArray())
                {
                    var message = ConversionUtils.ParsePlanMessage(pm, id, _nextPlanMessageSequence++);
                    if (message is not null)
                        nestedMessages.Add(message);
                }

                if (nestedMessages.Count > 0)
                {
                    MergePlanMessages(id, nestedMessages);
                }
            }

            var lockedByUserId = ConversionUtils.GetIntProp(p, "locked");
            var storedCount = _planMessagesByPlanId.TryGetValue(id, out var storedMessages)
                ? storedMessages.Count
                : 0;
            var payloadCount = ConversionUtils.GetNullableIntProp(p, "message_count", "messagecount", "messages") ?? 0;
            var msgCount = Math.Max(storedCount, payloadCount);
            // Use TryParse with ignoreCase so servers that send lowercase state strings
            // (e.g. "approved", "implemented") are handled correctly. Skip plan entries
            // with a missing or unrecognised state to prevent an exception from aborting
            // all remaining plan processing in this WS message.
            if (!Enum.TryParse<PlanState>(state, ignoreCase: true, out var planState))
                continue;
            var entry = new Plan(id, name, description, planState, country, startdate, constructionTime, policyNames, policyTypes, planLayers,
                requiresApproval, msgCount, IssueCount: 0, Votes: votes, LockedByUserId: lockedByUserId);
            var idx = _plans.FindIndex(e => e.PlanId == id);
            if (idx >= 0)
                _plans[idx] = entry;
            else
                _plans.Add(entry);
        }

        _plans.Sort((a, b) =>
        {
            var sp = a.State.CompareTo(b.State);
            if (sp != 0) return sp;
            var dp = a.StartDate.CompareTo(b.StartDate);
            return dp != 0 ? dp : a.PlanId.CompareTo(b.PlanId);
        });
    }

    private void MergePlanMessages(int planId, IEnumerable<PlanMessage> incoming)
    {
        if (!_planMessagesByPlanId.TryGetValue(planId, out var existing))
        {
            existing = new List<PlanMessage>();
            _planMessagesByPlanId[planId] = existing;
        }

        foreach (var message in incoming)
        {
            var duplicate = existing.Any(e => GenericUtils.IsSameMessage(e, message));
            if (!duplicate)
                existing.Add(message);
        }

        existing.Sort((a, b) =>
        {
            var at = a.SentAt ?? DateTime.MinValue;
            var bt = b.SentAt ?? DateTime.MinValue;
            var t = at.CompareTo(bt);
            if (t != 0) return t;
            return a.Sequence.CompareTo(b.Sequence);
        });
    }

    // ── Display helpers ────────────────────────────────────────────────────────
    public string MonthToDate(int month)
    {
        // Clamp months to prevent DateTime overflow (valid range: years 1-9999)
        // Limit to +/- 10000 years worth of months (120000 months)
        var clampedMonth = Math.Clamp(month, -120000, 120000);
        
        try
        {
            var d = new DateTime(GameStartYear, 1, 1).AddMonths(clampedMonth);
            return d.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            // If still out of range, return a fallback
            return $"Month {month}";
        }
    }
    // ── Reset (navigate home) ──────────────────────────────────────────────────

    /// <summary>
    /// Stops the WebSocket and clears all game data so the next session can
    /// start completely fresh.  Call before navigating back to the Home page.
    /// </summary>
    public async Task ResetAsync()
    {
        await _ws.StopAsync();

        LayerEntries.Clear();
        MapLayerSnapshots.Clear();
        LegendOrderLayerIds.Clear();
        EezPolygons        = [];
        WikiBaseUrl        = "";
        GameStartYear      = 2000;
        GameEndMonth       = 0;
        GameEndYear        = 0;

        LayerPanelOpen = true;
        LegendPanelOpen = true;
        OnlineUsersPanelOpen = false;
        PlansPanelOpen = false;
        MapLat = MapLng = MapZoom = null;

        GameCurrentMonth = 0;
        GameState        = "";
        EraTimeLeft      = 0;
        EraRealTimes     = new int[ERA_COUNT];

        _plans.Clear();
        _planMessagesByPlanId.Clear();
        _nextPlanMessageSequence = 1;

        DependencyGroups   = [];
        DependencyLinks    = [];
        _restrictions.Clear();
        AvailablePolicies  = [];

        IsGameDataLoaded = false;
    }

    // ── Data load status ───────────────────────────────────────────────────────
    /// <summary>True if game config has been loaded at least once in this circuit.</summary>
    public bool IsGameDataLoaded { get; set; } = false;

    /// <summary>
    /// Loads and caches all game configuration + map layer payloads once per circuit.
    /// Subsequent calls are no-ops.
    /// </summary>
    public async Task EnsureInitializedAsync(
        MspApiClient apiClient,
        UserSessionService userSessionService,
        Func<string, Task>? reportStatus = null)
    {
        if (IsGameDataLoaded) return;

        await _initGate.WaitAsync();
        try
        {
            if (IsGameDataLoaded) return;

            await LoadInitialStateAsync(apiClient, userSessionService, reportStatus);
            IsGameDataLoaded = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task LoadInitialStateAsync(
        MspApiClient apiClient,
        UserSessionService userSessionService,
        Func<string, Task>? reportStatus)
    {
        LayerEntries.Clear();
        MapLayerSnapshots.Clear();
        LegendOrderLayerIds.Clear();
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
                policies.Add(new PolicySetting("fishing",  "Fishing Effort",         true));
                policies.Add(new PolicySetting("energy",   "Energy Distribution",    true));
                policies.Add(new PolicySetting("shipping", "Shipping Safety Zones",  true));
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
                            var deId = ConversionUtils.GetStringProp(de, "id") ?? "";
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
                    var toId = ConversionUtils.GetStringProp(dl, "toId") ?? "";
                    var sev = ConversionUtils.GetIntProp(dl, "severity");
                    var desc = ConversionUtils.GetStringProp(dl, "description") ?? "";
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
        // Reconstruct the typeIndex→countryId mapping from the ordered Countries list.
        // Because Home strips Admin/RegionManager sentinels before seeding, the list index
        // matches the layer_type array position exactly.
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
        var total = layerArray.Count;
        var loaded = 0;

        foreach (var layer in layerArray)
        {
            var shortName = layer.TryGetProperty("layer_short", out var ls) ? ls.GetString() : null;
            var layerName = layer.TryGetProperty("layer_name", out var ln) ? ln.GetString() : null;
            var name = !string.IsNullOrWhiteSpace(shortName) ? shortName : (layerName ?? "");
            if (reportStatus is not null)
                await reportStatus($"Loading {loaded + 1} / {total}: {name}");

            var snapshot = await BuildLayerSnapshotAsync(apiClient, baseAddress, sessionId, layer);
            if (snapshot is not null)
                MapLayerSnapshots.Add(snapshot);

            loaded++;
        }

        // Initial legend order follows server depth (bottom -> top).
        ResetLegendOrderFromVisibleDepth();

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

    public void ResetLegendOrderFromVisibleDepth()
    {
        LegendOrderLayerIds.Clear();
        foreach (var id in LayerEntries
            .Where(e => e.Visible && !e.IsBaseLayer)
            .OrderBy(e => e.Depth)
            .Select(e => e.LayerId))
        {
            LegendOrderLayerIds.Add(id);
        }
    }

    public void SetLegendOrder(IEnumerable<string> orderedVisibleLayerIds)
    {
        LegendOrderLayerIds.Clear();

        var validVisibleNonBaseIds = LayerEntries
            .Where(e => e.Visible && !e.IsBaseLayer)
            .Select(e => e.LayerId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in orderedVisibleLayerIds)
        {
            if (validVisibleNonBaseIds.Remove(id))
                LegendOrderLayerIds.Add(id);
        }

        // Append any missing visible layers (defensive against stale order lists).
        foreach (var id in LayerEntries
            .Where(e => validVisibleNonBaseIds.Contains(e.LayerId))
            .OrderBy(e => e.Depth)
            .Select(e => e.LayerId))
        {
            LegendOrderLayerIds.Add(id);
        }
    }

    private async Task<MapLayerSnapshot?> BuildLayerSnapshotAsync(
        MspApiClient apiClient,
        string baseAddress,
        int sessionId,
        JsonElement layer)
    {
        var layerId = layer.TryGetProperty("layer_id", out var id) ? id.ToString() : null;
        var layerName = layer.TryGetProperty("layer_name", out var n) ? n.GetString() : null;
        var geoType = layer.TryGetProperty("layer_geotype", out var gt) ? gt.GetString() : null;
        if (layerId is null || layerName is null) return null;

        List<(double NormalisedThreshold, string Label)> rasterThresholds = [];

        var colorProp = geoType?.ToLowerInvariant() switch
        {
            "line" or "lines" => "lineColor",
            "point" or "points" => "pointColor",
            _ => "polygonColor"
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

        bool visible = layer.TryGetProperty("layer_active_on_start", out var aos) &&
                       aos.ValueKind == JsonValueKind.Number && aos.GetInt32() != 0;
        var depth = layer.TryGetProperty("layer_depth", out var ld) && ld.ValueKind == JsonValueKind.Number ? ld.GetInt32() : 0;
        var toggleable = layer.TryGetProperty("layer_toggleable", out var lt2) && lt2.ValueKind == JsonValueKind.Number ? lt2.GetInt32() != 0 : true;
        var editable   = layer.TryGetProperty("layer_editable",   out var leEl) && leEl.ValueKind == JsonValueKind.Number ? leEl.GetInt32() != 0 : false;
        var editingType = layer.TryGetProperty("layer_editing_type", out var letEl) ? letEl.GetString() ?? "" : "";
        var isBase = layerName.IndexOf("_PLAYAREA", StringComparison.OrdinalIgnoreCase) >= 0;

        // Prepare metadata entry used across pages.
        var layerShort = layer.TryGetProperty("layer_short", out var ls2) ? ls2.GetString() ?? "" : "";
        var category = layer.TryGetProperty("layer_category", out var lc) ? lc.GetString() ?? "" : "";
        var subcategory = layer.TryGetProperty("layer_subcategory", out var lsc) ? lsc.GetString() ?? "" : "";
        var tooltip = layer.TryGetProperty("layer_tooltip", out var ltt) ? ltt.GetString() ?? "" : "";
        var displayName = !string.IsNullOrWhiteSpace(layerShort) ? layerShort : ConversionUtils.FallbackDisplayName(layerName);
        var layerMedia = layer.TryGetProperty("layer_media", out var lm) ? lm.GetString() ?? "" : "";
        var layerMediaUrl = ResolveWikiUrl(layerMedia);

        var typeDefs = new List<TypeDef>();
        if (layer.TryGetProperty("layer_type", out var ltDefEl))
        {
            IEnumerable<JsonElement> typeDefItems = ltDefEl.ValueKind switch
            {
                JsonValueKind.Array => ltDefEl.EnumerateArray(),
                JsonValueKind.Object => ltDefEl.EnumerateObject()
                    .OrderBy(kv => int.TryParse(kv.Name, out var n) ? n : int.MaxValue)
                    .Select(kv => kv.Value),
                _ => []
            };

            foreach (var td in typeDefItems)
            {
                var label = td.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                var col = td.TryGetProperty(colorProp, out var cp) ? cp.GetString() ?? "#3388ff" : "#3388ff";
                var tdMedia = td.TryGetProperty("media", out var tm) ? tm.GetString() ?? "" : "";
                var tdMediaUrl = ResolveWikiUrl(tdMedia);
                var tdApproval = td.TryGetProperty("approval", out var ap) ? ap.GetString() ?? "NotDependent" : "NotDependent";
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
                    prop.TryGetProperty("display_name", out var dn2) &&
                    pn2.GetString() is { Length: > 0 } propName2)
                    propDisplayNames[propName2] = dn2.GetString() ?? propName2;
            }
        }

        string? vectorGeometriesJson = null;
        string? rasterImageData = null;
        double[][]? rasterProjBounds = null;
        var rasterColorMap = new List<RasterColorStop>();
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
            LayerId = layerId,
            LayerName = layerName,
            DisplayName = displayName,
            Category = category,
            Subcategory = subcategory,
            Tooltip = tooltip,
            MediaUrl = layerMediaUrl,
            IsBaseLayer   = isBase,
            IsToggleable  = toggleable,
            Visible       = visible,
            Depth         = depth,
            TypeDefs = typeDefs,
            PropertyDisplayNames = propDisplayNames,
            IsRaster = string.Equals(geoType, "raster", StringComparison.OrdinalIgnoreCase),
            RasterThresholds = rasterThresholds,
            GeoType = geoType ?? "",
            AssemblyTime = ConversionUtils.ParseAssemblyTime(layer),
            Editable = editable,
            EditingType = editingType
        });

        return new MapLayerSnapshot
        {
            LayerId = layerId,
            GeoType = geoType ?? "",
            Visible = visible,
            Depth = depth,
            IsBaseLayer = isBase,
            VectorGeometriesJson = vectorGeometriesJson,
            TypeColors = typeColors,
            LabelKey = labelKey,
            RasterImageData = rasterImageData,
            RasterProjBounds = rasterProjBounds,
            RasterColorMap = rasterColorMap,
            RasterMinCutoffNorm = minCutoffNorm,
            RasterInterpolate = interpolate
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
        {
            restrictionsEl = payload;
        }
        else if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("restrictions", out var nestedRestrictions))
        {
            restrictionsEl = nestedRestrictions;
        }
        else
        {
            return;
        }

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

            var message = ConversionUtils.GetStringPropLoose(item, "message", "text", "description", "restriction_message") ?? "";
            var type = ConversionUtils.GetStringPropLoose(item, "type", "severity", "level", "restriction_type") ?? "warning";
            var startLayer = ConversionUtils.GetStringPropLoose(item, "startlayer", "start_layer", "start", "from_layer", "from", "restriction_start_layer_id") ?? "";
            var startType = ConversionUtils.GetStringPropLoose(item, "starttype", "start_type", "start_layer_type", "from_type", "restriction_start_layer_type") ?? "";
            var endLayer = ConversionUtils.GetStringPropLoose(item, "endlayer", "end_layer", "end", "to_layer", "to", "restriction_end_layer_id") ?? "";
            var endType = ConversionUtils.GetStringPropLoose(item, "endtype", "end_type", "end_layer_type", "to_type", "restriction_end_layer_type") ?? "";
            var sort = ConversionUtils.GetStringPropLoose(item, "sort", "order", "restriction_sort") ?? "";

            if (string.IsNullOrWhiteSpace(startLayer) || string.IsNullOrWhiteSpace(endLayer))
                continue;

            _restrictions.Add(new RestrictionRule(message, type, startLayer, startType, endLayer, endType, sort));
        }
    }

    public int TotalMonths => GameEndMonth > 0 ? GameEndMonth : GameEndYear > 0  ? (GameEndYear - GameStartYear) * 12 : 0;

    // ── Geometry caches for plan calculations ─────────────────────────────────
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _parsedLayerGeometryCache = [];
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _projectedGeometryCache = [];

    /// <summary>
    /// Parses and caches layer geometry from MapLayerSnapshots.
    /// Returns cached data if available.
    /// </summary>
    public List<ParsedLayerGeometry> GetParsedLayerGeometries(string layerId)
    {
        if (_parsedLayerGeometryCache.TryGetValue(layerId, out var cached))
            return cached;

        var parsed = new List<ParsedLayerGeometry>();
        _parsedLayerGeometryCache[layerId] = parsed;

        var snapshot = MapLayerSnapshots.FirstOrDefault(s => string.Equals(s.LayerId, layerId, StringComparison.OrdinalIgnoreCase));
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
            // Ignore malformed cached geometry
        }

        return parsed;
    }

    /// <summary>
    /// Returns projected geometry for a layer at a given point in time (before a plan's start date).
    /// Projects baseline geometry with all finalized plan modifications that occurred before the specified date.
    /// Results are cached per layer-date combination.
    /// </summary>
    public List<ParsedLayerGeometry> GetProjectedLayerGeometries(string layerId, int beforeStartDate)
    {
        var cacheKey = $"{layerId}\x01{beforeStartDate}";
        if (_projectedGeometryCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var baseline = GetParsedLayerGeometries(layerId);

        // Collect finalised plans that are already in effect before this plan's start date.
        var priorPlans = Plans
            .Where(p => p.StartDate < beforeStartDate && Utils.PlanCalculations.PlanStates.IsFinalisedPlanState(p.State))
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
        var additions = new List<ParsedLayerGeometry>();

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

    /// <summary>
    /// Clears geometry caches. Call when layer data is updated to force re-parsing.
    /// </summary>
    public void ClearGeometryCaches()
    {
        _parsedLayerGeometryCache.Clear();
        _projectedGeometryCache.Clear();
    }

    // ── Disposal ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _ws.MessageReceived -= OnWsMessage;
        _initGate.Dispose();
    }
}
