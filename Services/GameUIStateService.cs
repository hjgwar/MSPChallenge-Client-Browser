using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Circuit-scoped service that holds all UI navigation state: panel visibility,
/// map camera, selected plan, edit mode, pending new-plan seed values, layer legend
/// ordering, etc.
/// Injected with <see cref="MspApiClient"/> and <see cref="UserSessionService"/> to
/// support <see cref="ToggleCreatePlanPanel"/> (which may unlock a plan via the API).
/// Change notifications are relayed through <see cref="GameSessionService.Changed"/>
/// via a callback registered by <see cref="GameSessionService"/> at construction time,
/// so all components can subscribe to a single event.
/// </summary>
public sealed class GameUIStateService
{
    private readonly MspApiClient      _apiClient;
    private readonly UserSessionService _userSessionService;

    public GameUIStateService(MspApiClient apiClient, UserSessionService userSessionService)
    {
        _apiClient           = apiClient;
        _userSessionService  = userSessionService;
    }

    // Callback wired up by GameSessionService once during its own construction to hook
    // the relay. Must not be called from application code.
    private Action? _relayNotify;
    internal void SetNotifyChangedCallback(Action callback) => _relayNotify = callback;

    /// <summary>Fires the relay (→ GameSessionService.Changed → all component subscribers).</summary>
    public void NotifyChanged() => _relayNotify?.Invoke();

    // ── Panel open / close ─────────────────────────────────────────────────────
    public bool LayerPanelOpen       { get; set; } = true;
    public bool LegendPanelOpen      { get; set; } = true;
    public bool OnlineUsersPanelOpen { get; set; } = false;
    public bool PlansPanelOpen       { get; set; } = false;
    public bool CreatePlanOpen       { get; set; } = false;
    public bool TimeManagerOpen      { get; set; } = false;

    // ── Plan selection and editing ─────────────────────────────────────────────
    public int?         SelectedPlanId { get; set; } = null;
    public PlanViewMode PlanViewMode   { get; set; } = PlanViewMode.AfterChanges;
    public bool         EditMode       { get; set; } = false;

    /// <summary>Toggle edit mode on/off and notify.</summary>
    public void ToggleEditMode()
    {
        EditMode = !EditMode;
        NotifyChanged();
    }

    // ── Pending new-plan seed values ───────────────────────────────────────────
    /// <summary>
    /// Written by <c>Game.OpenEditModeFromCreation</c> when the player accepts the
    /// PlanCreation form. PlanDetails reads and clears these on first render in
    /// new-plan edit mode.
    /// </summary>
    public string PendingNewPlanName        { get; set; } = string.Empty;
    public string PendingNewPlanDescription { get; set; } = string.Empty;
    public int    PendingNewPlanStartYear   { get; set; } = 0;
    public int    PendingNewPlanStartMonth  { get; set; } = 1;

    // ── Persisted map camera (smooth return navigation) ────────────────────────
    public double? MapLat  { get; private set; }
    public double? MapLng  { get; private set; }
    public double? MapZoom { get; private set; }
    public bool HasSavedMapView => MapLat.HasValue && MapLng.HasValue && MapZoom.HasValue;

    // ── Legend layer order (UI concern: which layer appears on top in MapLegend) ─
    /// <summary>
    /// Visible non-base layers in legend order: index 0 = bottom, last = top.
    /// Stored here (UI state) so the player's custom ordering survives page navigation.
    /// </summary>
    public List<string> LegendOrderLayerIds { get; } = new();

    public void SaveMapView(double lat, double lng, double zoom)
    {
        MapLat  = lat;
        MapLng  = lng;
        MapZoom = zoom;
    }

    /// <summary>Resets legend order to follow server depth (bottom → top).</summary>
    public void ResetLegendOrderFromVisibleDepth(IEnumerable<Layer> layerEntries)
    {
        LegendOrderLayerIds.Clear();
        foreach (var id in layerEntries
            .Where(e => e.Visible && !e.IsBaseLayer)
            .OrderBy(e => e.Depth)
            .Select(e => e.LayerId))
        {
            LegendOrderLayerIds.Add(id);
        }
    }

    /// <summary>
    /// Applies a new legend ordering. Ids not present in the visible non-base layer set
    /// are silently dropped; any visible non-base layers missing from the list are appended
    /// in depth order (defensive against stale order lists).
    /// </summary>
    public void SetLegendOrder(IEnumerable<string> orderedVisibleLayerIds, IEnumerable<Layer> layerEntries)
    {
        LegendOrderLayerIds.Clear();

        var validVisibleNonBaseIds = layerEntries
            .Where(e => e.Visible && !e.IsBaseLayer)
            .Select(e => e.LayerId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in orderedVisibleLayerIds)
        {
            if (validVisibleNonBaseIds.Remove(id))
                LegendOrderLayerIds.Add(id);
        }

        // Append any missing visible layers.
        foreach (var id in layerEntries
            .Where(e => validVisibleNonBaseIds.Contains(e.LayerId))
            .OrderBy(e => e.Depth)
            .Select(e => e.LayerId))
        {
            LegendOrderLayerIds.Add(id);
        }
    }

    // ── Panel toggle helpers ───────────────────────────────────────────────────
    public void ToggleLayerPanel()
    {
        LayerPanelOpen = !LayerPanelOpen;
        NotifyChanged();
    }

    public void ToggleLegendPanel()
    {
        LegendPanelOpen = !LegendPanelOpen;
        NotifyChanged();
    }

    public void TogglePlansPanel()
    {
        PlansPanelOpen = !PlansPanelOpen;
        NotifyChanged();
    }

    public void ToggleOnlineUsersPanel()
    {
        OnlineUsersPanelOpen = !OnlineUsersPanelOpen;
        NotifyChanged();
    }

    public void ToggleTimeManager()
    {
        TimeManagerOpen = !TimeManagerOpen;
        NotifyChanged();
    }

    /// <summary>
    /// Toggles the Create Plan panel. When opening while a plan is locked for editing,
    /// releases the edit lock via the API (fire-and-forget) and resets edit state.
    /// </summary>
    public void ToggleCreatePlanPanel()
    {
        var opening = !CreatePlanOpen;
        if (opening)
        {
            // If an existing plan is locked for editing, release the lock (fire-and-forget).
            if (EditMode && SelectedPlanId is > 0)
            {
                _ = _apiClient.PostFormAsync("Plan/Unlock",
                    new[]
                    {
                        new KeyValuePair<string, string>("id", SelectedPlanId.ToString()!),
                        new KeyValuePair<string, string>("force_unlock", "0"),
                        new KeyValuePair<string, string>("user", _userSessionService.User.Id.ToString()),
                    });
            }
            // Cancel any active edit/view before showing the creation form.
            EditMode       = false;
            SelectedPlanId = null;
        }
        CreatePlanOpen = opening;
        NotifyChanged();
    }

    // ── Reset (called by GameSessionService.ResetAsync on navigate-home) ───────
    public void Reset()
    {
        LayerPanelOpen       = true;
        LegendPanelOpen      = true;
        OnlineUsersPanelOpen = false;
        PlansPanelOpen       = false;
        CreatePlanOpen       = false;
        TimeManagerOpen      = false;
        SelectedPlanId       = null;
        PlanViewMode         = PlanViewMode.AfterChanges;
        EditMode             = false;
        PendingNewPlanName        = string.Empty;
        PendingNewPlanDescription = string.Empty;
        PendingNewPlanStartYear   = 0;
        PendingNewPlanStartMonth  = 1;
        MapLat = MapLng = MapZoom = null;
        LegendOrderLayerIds.Clear();
    }
}
