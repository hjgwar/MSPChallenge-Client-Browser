using System.Text.Json;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Circuit-scoped service that holds all loaded game configuration and live WebSocket state.
/// Survives page navigation within the same browser tab (Blazor Server circuit lifetime).
/// </summary>
public sealed class GameSessionState : IDisposable
{
    private readonly GameWebSocketService _ws;

    public GameSessionState(GameWebSocketService ws)
    {
        _ws = ws;
        _ws.MessageReceived += OnWsMessage;
    }

    // ── Config (written once during LoadGameDataAsync on the Game page) ─────────
    public List<LayerEntry>        LayerEntries   { get; } = new();
    public Dictionary<int, string> CountryColours { get; } = new();
    public Dictionary<int, string> CountryNames   { get; } = new();
    public string WikiBaseUrl   { get; set; } = "";
    public int    GameStartYear { get; set; } = 2000;
    public int    GameEndMonth  { get; set; } = 0;
    public int    GameEndYear   { get; set; } = 0;

    // ── Live WebSocket state ───────────────────────────────────────────────────
    public int    GameCurrentMonth { get; private set; } = 0;
    public string GameState        { get; private set; } = "";
    public double EraTimeLeft      { get; private set; } = 0;

    public IReadOnlyList<PlanEntry> Plans => _plans;
    private readonly List<PlanEntry> _plans = new();

    // ── Change notification ────────────────────────────────────────────────────
    /// <summary>
    /// Raised on a background thread when live WebSocket state has been updated.
    /// Subscribers must marshal to the Blazor UI thread via InvokeAsync.
    /// </summary>
    public event Action? Changed;

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

        var currentMonth = GetIntProp(tick, "month");
        if (currentMonth == 0) currentMonth = GetIntProp(payload, "game_current_month");
        if (currentMonth > 0 || tick.TryGetProperty("month", out _) || payload.TryGetProperty("game_current_month", out _))
            GameCurrentMonth = currentMonth;

        var gameState = GetStringProp(tick, "state") ?? GetStringProp(payload, "game_state");
        if (gameState is not null)
            GameState = gameState;

        if (GameEndMonth == 0)
        {
            var endMonth = GetIntProp(payload, "game_end_month");
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

        // Plan messages: count per plan_id
        var planMessageCounts = new Dictionary<int, int>();
        if (payload.TryGetProperty("planmessages", out var pmEl) && pmEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var pm in pmEl.EnumerateArray())
            {
                var pmPlanId = GetIntProp(pm, "plan_id");
                if (pmPlanId > 0)
                    planMessageCounts[pmPlanId] = planMessageCounts.GetValueOrDefault(pmPlanId) + 1;
            }
        }

        if (!payload.TryGetProperty("plan", out var plansEl) ||
            plansEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var p in plansEl.EnumerateArray())
        {
            var id          = GetIntProp(p, "id");
            var name        = GetStringProp(p, "name")        ?? $"Plan {id}";
            var description = GetStringProp(p, "description") ?? "";
            var state       = GetStringProp(p, "state")       ?? "";
            var country     = GetIntProp(p, "country");
            var startdate   = GetIntProp(p, "startdate");

            var planLayers = new List<PlanLayerData>();
            if (p.TryGetProperty("layers", out var layersEl) && layersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in layersEl.EnumerateArray())
                {
                    var planLayerId = GetStringProp(l, "layerid") ?? "";
                    var originalId  = GetStringProp(l, "original") ?? "";
                    var layerState  = GetStringProp(l, "state") ?? "";
                    var geometries  = new List<PlanGeometryItem>();

                    if (l.TryGetProperty("geometry", out var geoArr) && geoArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in geoArr.EnumerateArray())
                        {
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
                                    var geoId   = GetStringProp(g, "id")         ?? "";
                                    var persId  = GetStringProp(g, "persistent") ?? "";
                                    var typeStr = GetStringProp(g, "type")       ?? "0";
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

            var requiresApproval = false;
            if (p.TryGetProperty("approval_required", out var arEl))
                requiresApproval = arEl.ValueKind == JsonValueKind.True
                    || (arEl.ValueKind == JsonValueKind.Number && arEl.GetInt32() != 0)
                    || (arEl.ValueKind == JsonValueKind.String && arEl.GetString() is "1" or "true");

            var msgCount = planMessageCounts.GetValueOrDefault(id, 0);
            var entry = new PlanEntry(id, name, description, state, country, startdate, constructionTime, policyNames, planLayers,
                requiresApproval, msgCount);
            var idx = _plans.FindIndex(e => e.PlanId == id);
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

    // ── Display helpers ────────────────────────────────────────────────────────
    public string MonthToDate(int month)
    {
        var d = new DateTime(GameStartYear, 1, 1).AddMonths(month);
        return d.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string PlanStateLabel(string state) => state.ToUpperInvariant() switch
    {
        "APPROVAL" => "AWAITING APPROVAL",
        _          => state.ToUpperInvariant(),
    };

    public static int PlanStatePriority(string state) => state.ToUpperInvariant() switch
    {
        "DESIGN"        => 0,
        "CONSULTATION"  => 1,
        "APPROVAL"      => 2,
        "APPROVED"      => 3,
        "IMPLEMENTED"   => 4,
        "ARCHIVED"      => 5,
        _               => 6,
    };

    public static readonly string[] OrderedPlanStates =
    [
        "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "IMPLEMENTED", "ARCHIVED"
    ];

    // ── JSON helpers (private) ─────────────────────────────────────────────────
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

    private static string? GetStringProp(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
        }
        return null;
    }

    // ── Disposal ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _ws.MessageReceived -= OnWsMessage;
    }
}
