using System.Text.Json;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Circuit-scoped service that holds all loaded game configuration and live WebSocket state.
/// Survives page navigation within the same browser tab (Blazor Server circuit lifetime).
/// UI navigation state (panel visibility, map camera, edit mode, etc.) lives in
/// <see cref="GameUIStateService"/>; its change notifications are relayed through
/// this class's <see cref="Changed"/> event so components only subscribe once.
/// Loading logic lives in the companion partial class <c>GameSessionService.Loader.cs</c>.
/// </summary>
public sealed partial class GameSessionService : IDisposable
{
    private readonly WebSocketService    _ws;
    private readonly GameUIStateService  _uiState;
    private readonly SemaphoreSlim       _initGate = new(1, 1);

    public GameSessionService(WebSocketService ws, GameUIStateService uiState)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(uiState);
        _ws      = ws;
        _uiState = uiState;
        _ws.MessageReceived += OnWsMessage;
        // Relay: UI state changes fire through this service's Changed event so all
        // components only need one subscription.
        _uiState.SetNotifyChangedCallback(() => Changed?.Invoke());
    }

    // ── Config (written once during LoadInitialStateAsync) ────────────────────
    public List<Layer>             LayerEntries      { get; } = new();
    public List<MapLayerSnapshot>  MapLayerSnapshots { get; } = new();
    public IReadOnlyList<Country>    Countries   { get; set; } = [];
    /// <summary>EEZ polygon geometries keyed by country ID. Used for approval calculation.</summary>
    public IReadOnlyList<EezPolygon> EezPolygons { get; set; } = [];
    /// <summary>
    /// ID of the EEZ layer, seeded from Home when the player connects.
    /// Used by the Loader to call Layer/Get without re-fetching Layer/MetaByName.
    /// </summary>
    public string? EezLayerId  { get; set; }
    public string  WikiBaseUrl   { get; set; } = "";
    public int     GameStartYear { get; set; } = 2000;
    public int     GameEndMonth  { get; set; } = 0;
    public int     GameEndYear   { get; set; } = 0;
    public int     GameEraTotalMonths { get; set; } = 120;

    // ── Computed delegation (convenience read-through for UI state) ────────────
    /// <summary>
    /// The plan currently selected by the player. Convenience shortcut that looks up
    /// <see cref="GameUIStateService.SelectedPlanId"/> inside <see cref="Plans"/>.
    /// </summary>
    public Plan? SelectedPlan => _uiState.SelectedPlanId.HasValue
        ? _plans.FirstOrDefault(p => p.PlanId == _uiState.SelectedPlanId.Value)
        : null;

    // ── Live WebSocket state ───────────────────────────────────────────────────
    public const int ERA_COUNT = 4;
    public int    GameCurrentMonth { get; private set; } = 0;
    public string GameState        { get; private set; } = "";
    public double EraTimeLeft      { get; private set; } = 0;
    public int[]  EraRealTimes     { get; private set; } = new int[ERA_COUNT];

    /// <summary>Get the current era index (0–3) based on current month and era total months.</summary>
    public int GetCurrentEra()
    {
        if (GameEraTotalMonths <= 0) return 0;
        return Math.Min(GameCurrentMonth / GameEraTotalMonths, ERA_COUNT - 1);
    }

    public IReadOnlyList<Plan> Plans => _plans;
    private readonly List<Plan>                       _plans                  = new();
    private readonly Dictionary<int, List<PlanMessage>> _planMessagesByPlanId = new();
    private int _nextPlanMessageSequence = 1;

    public IReadOnlyList<PlanMessage> GetPlanMessages(int planId) =>
        _planMessagesByPlanId.TryGetValue(planId, out var messages)
            ? messages
            : Array.Empty<PlanMessage>();

    // ── Dependency graph data (from Game/Config) ───────────────────────────────
    public IReadOnlyList<DependencyGroup> DependencyGroups { get; private set; } = [];
    public IReadOnlyList<DependencyLink>  DependencyLinks  { get; private set; } = [];
    public IReadOnlyList<RestrictionRule> Restrictions     => _restrictions;
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
    /// Raised on a background thread when live WebSocket state has been updated,
    /// or when UI state in <see cref="GameUIStateService"/> changes (via relay).
    /// Subscribers must marshal to the Blazor UI thread via InvokeAsync.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Manually triggers the Changed event. Use when game data properties are modified
    /// outside of WebSocket message handling.
    /// </summary>
    public void NotifyChanged() => Changed?.Invoke();

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
            {
                var parts = pertStr.Split(',');
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
                MergePlanMessages(kvp.Key, kvp.Value);
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
                    MergePlanMessages(id, nestedMessages);
            }

            var lockedByUserId = ConversionUtils.GetIntProp(p, "locked");
            var storedCount = _planMessagesByPlanId.TryGetValue(id, out var storedMessages)
                ? storedMessages.Count : 0;
            var payloadCount = ConversionUtils.GetNullableIntProp(p, "message_count", "messagecount", "messages") ?? 0;
            var msgCount = Math.Max(storedCount, payloadCount);

            // Use TryParse with ignoreCase so servers that send lowercase state strings
            // (e.g. "approved", "implemented") are handled correctly. Skip plan entries
            // with a missing or unrecognised state to prevent an exception from aborting
            // all remaining plan processing in this WS message.
            if (!Enum.TryParse<PlanState>(state, ignoreCase: true, out var planState))
                continue;

            var entry = new Plan(id, name, description, planState, country, startdate, constructionTime,
                policyNames, policyTypes, planLayers, requiresApproval, msgCount,
                IssueCount: 0, Votes: votes, LockedByUserId: lockedByUserId);

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
        var clampedMonth = Math.Clamp(month, -120000, 120000);
        try
        {
            var d = new DateTime(GameStartYear, 1, 1).AddMonths(clampedMonth);
            return d.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"Month {month}";
        }
    }

    public int TotalMonths => GameEndMonth > 0 ? GameEndMonth : GameEndYear > 0 ? (GameEndYear - GameStartYear) * 12 : 0;

    // ── Reset (navigate home) ──────────────────────────────────────────────────
    /// <summary>
    /// Stops the WebSocket and clears all game data so the next session can
    /// start completely fresh. Call before navigating back to the Home page.
    /// </summary>
    public async Task ResetAsync()
    {
        await _ws.StopAsync();

        LayerEntries.Clear();
        MapLayerSnapshots.Clear();
        EezPolygons        = [];
        WikiBaseUrl        = "";
        GameStartYear      = 2000;
        GameEndMonth       = 0;
        GameEndYear        = 0;

        GameCurrentMonth = 0;
        GameState        = "";
        EraTimeLeft      = 0;
        EraRealTimes     = new int[ERA_COUNT];

        _plans.Clear();
        _planMessagesByPlanId.Clear();
        _nextPlanMessageSequence = 1;

        DependencyGroups  = [];
        DependencyLinks   = [];
        _restrictions.Clear();
        AvailablePolicies = [];

        _uiState.Reset();

        IsGameDataLoaded = false;
    }

    // ── Data load status ───────────────────────────────────────────────────────
    /// <summary>True if game config has been loaded at least once in this circuit.</summary>
    public bool IsGameDataLoaded { get; set; } = false;

    /// <summary>
    /// Loads and caches all game configuration + map layer payloads once per circuit.
    /// Subsequent calls are no-ops. Loading logic lives in GameSessionService.Loader.cs.
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

    // ── Geometry caches for plan calculations ──────────────────────────────────
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _parsedLayerGeometryCache   = [];
    private readonly Dictionary<string, List<ParsedLayerGeometry>> _projectedGeometryCache     = [];

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
                    if (pointElement.ValueKind != JsonValueKind.Array || pointElement.GetArrayLength() < 2) continue;
                    if (pointElement[0].ValueKind != JsonValueKind.Number || pointElement[1].ValueKind != JsonValueKind.Number) continue;
                    coordinates.Add([pointElement[0].GetDouble(), pointElement[1].GetDouble()]);
                }
                if (coordinates.Count == 0) continue;

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
        catch { /* Ignore malformed cached geometry */ }

        return parsed;
    }

    /// <summary>
    /// Returns projected geometry for a layer at a given point in time (before a plan's start date).
    /// Projects baseline geometry with all finalised plan modifications that occurred before the specified date.
    /// Results are cached per layer-date combination.
    /// </summary>
    public List<ParsedLayerGeometry> GetProjectedLayerGeometries(string layerId, int beforeStartDate)
    {
        var cacheKey = $"{layerId}\x01{beforeStartDate}";
        if (_projectedGeometryCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var baseline = GetParsedLayerGeometries(layerId);

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

        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additions  = new List<ParsedLayerGeometry>();

        foreach (var priorPlan in priorPlans)
        {
            var planLayer = priorPlan.Layers.FirstOrDefault(l =>
                string.Equals(l.OriginalLayerId, layerId, StringComparison.OrdinalIgnoreCase));
            if (planLayer is null) continue;

            foreach (var deletedId in planLayer.DeletedPersistentIds)
                deletedIds.Add(deletedId);

            foreach (var geo in planLayer.Geometry)
            {
                bool isModification = !string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id;
                if (isModification)
                    deletedIds.Add(geo.PersistentId);
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

    /// <summary>Clears geometry caches. Call when layer data is updated to force re-parsing.</summary>
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
