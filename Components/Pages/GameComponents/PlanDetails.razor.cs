using System.Text.Json;
using BlazorBootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class PlanDetails : GameComponentBase, IDisposable
{
    [Parameter] public MapViewPort? Map { get; set; }
    public PlanNameDesc PlanNameDescInstance { get; set; } = null!;
    public PlanStartDate PlanStartDateInstance { get; set; } = null!;
    public PlanComponents.PlanState PlanStateInstance { get; set; } = null!;
    public PlanApproval PlanApprovalInstance { get; set; } = null!;
    public PlanPolicies PlanPoliciesInstance { get; set; } = null!;
    public PlanLayers PlanLayersInstance { get; set; } = null!;
    public PlanMessages PlanMessagesInstance { get; set; } = null!;
    public PlanIssues PlanIssuesInstance { get; set; } = null!;
    public PlanDetailsSave PlanDetailsSaveInstance { get; set; } = null!;
    private ConfirmDialog cancelDialog = null!;

    /// <summary>
    /// Closes all sub-panels. Wired to OnSubPanelOpening on every child component that
    /// hosts a toggleable sub-panel, enforcing mutual exclusion across the panel set.
    /// </summary>
    private void CloseAllSubPanels()
    {
        PlanApprovalInstance?.CloseSubPanel();
        PlanMessagesInstance?.CloseSubPanel();
        PlanIssuesInstance?.CloseSubPanel();
        PlanStateInstance?.CloseSubPanel();
        PlanPoliciesInstance?.CloseSubPanel();
        PlanLayersInstance?.CloseSubPanel();
    }

    private List<PlanRestrictionIssue> _selectedPlanIssues = [];
    private bool _selectedPlanApprovalRequired = false;
    private Plan _detailPlan = null!;
    private List<string> _detailLayers = [];
    private string _detailDotColour = "#6c757d";
    private string _detailCountryName = "Unknown Country";
    
    private bool _editSaving = false;
    private string? _editError;
    // Tracks whether we were in edit mode on the previous state change, so that
    // OnStateChanged can detect the edit→view transition and refresh the map geometry.
    private bool _wasPreviouslyEditing = false;

    private string? _editName;
    private string? _editDescription;
    private int _editStartYear;
    private int _editStartMonth;
    private HashSet<string> _editPlanLayerIds = [];
    private HashSet<string> _editPolicyTypes = [];
    private string? _enterEditError;
    private int? _lastDisplayedPlanId;
    private readonly HashSet<string> _planActivatedLayerIds = [];

    protected override void OnInitialized()
    {
        GameSessionService.Changed += OnStateChanged;
        // If Game transitioned us into new-plan edit mode via PlanCreation, consume the
        // pending seed values that were stored in GameSessionService.
        ConsumePendingNewPlanSeed();
        UpdateDetailPlan();
    }

    private void ConsumePendingNewPlanSeed()
    {
        if (GameUIStateService.SelectedPlanId != 0 || !GameUIStateService.EditMode) return;
        if (GameUIStateService.PendingNewPlanStartYear == 0) return; // no pending seed

        _editName        = GameUIStateService.PendingNewPlanName;
        _editDescription = GameUIStateService.PendingNewPlanDescription;
        _editStartYear   = GameUIStateService.PendingNewPlanStartYear;
        _editStartMonth  = GameUIStateService.PendingNewPlanStartMonth;

        // Clear so a later re-render doesn't re-apply stale values.
        GameUIStateService.PendingNewPlanName        = string.Empty;
        GameUIStateService.PendingNewPlanDescription = string.Empty;
        GameUIStateService.PendingNewPlanStartYear   = 0;
        GameUIStateService.PendingNewPlanStartMonth  = 1;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || _lastDisplayedPlanId != GameUIStateService.SelectedPlanId)
        {
            await DisplaySelectedPlanAsync();
        }
    }

    private void OnStateChanged()
    {
        // Always refresh the plan data so WS-delivered changes (name, state, start date,
        // layers, etc.) are reflected immediately in the PlanDetails UI.
        UpdateDetailPlan();

        // Decide whether to also refresh the map:
        //  • Plan identity changed (user switched plans) — always refresh.
        //  • Just finished editing (_wasPreviouslyEditing && !EditMode) — refresh once
        //    so new/altered geometry from the save is shown on the map.
        //  • Routine WS ticks with same plan in view-mode — UI re-render only.
        var needsMapRefresh = _lastDisplayedPlanId != GameUIStateService.SelectedPlanId
            || (_wasPreviouslyEditing && !GameUIStateService.EditMode);

        _wasPreviouslyEditing = GameUIStateService.EditMode;

        if (needsMapRefresh)
        {
            InvokeAsync(async () => await DisplaySelectedPlanAsync());
        }
        else
        {
            InvokeAsync(StateHasChanged);
        }
    }

    private void UpdateDetailPlan()
    {
        _detailPlan = GetDetailPlan();
        _detailLayers = _detailPlan.Layers
            .Select(l => GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == l.OriginalLayerId)?.DisplayName)
            .OfType<string>()
            .Distinct()
            .ToList();
        _detailDotColour = (_detailPlan.Country == 1 || _detailPlan.Country == 2)
            ? "#ff69b4"
            : GameSessionService.Countries.FirstOrDefault(c => c.Id == _detailPlan.Country)?.Color ?? "#6c757d";
        _detailCountryName = GameSessionService.Countries.FirstOrDefault(c => c.Id == _detailPlan.Country)?.Name ?? $"Country {_detailPlan.Country}";
    }

    private int GetConstructionTime()
    {
        // In edit mode, compute live from the selected layer set so that adding a layer
        // with a long AssemblyTime immediately tightens the earliest-start constraint.
        if (GameUIStateService.EditMode && _editPlanLayerIds.Count > 0)
        {
            return _editPlanLayerIds
                .Select(id => GameSessionService.LayerEntries
                    .FirstOrDefault(l => l.LayerId == id)?.AssemblyTime ?? 0)
                .DefaultIfEmpty(0)
                .Max();
        }
        return _detailPlan?.ConstructionTime ?? 0;
    }

    private void OnPlanNameChanged(string newName)
    {
        _editName = newName;
    }

    private void OnPlanDescriptionChanged(string newDescription)
    {
        _editDescription = newDescription;
    }

    private void OnStartMonthChanged(int month)
    {
        _editStartMonth = month;
    }

    private void OnStartYearChanged(int year)
    {
        _editStartYear = year;
    }

    private async Task ClosePlanDetailAsync()
    {
        if (GameUIStateService.EditMode)
            await CancelEditAsync();
        CloseAllSubPanels();
        GameUIStateService.SelectedPlanId = null;
        GameSessionService.NotifyChanged();
    }

    private bool CanEnterEditMode =>
        GameUIStateService.SelectedPlanId != 0 &&
        _detailPlan is not null &&
        _detailPlan.State == Models.PlanState.DESIGN &&
        (UserSessionService.User.Country.Id <= 2 || _detailPlan.Country == UserSessionService.User.Country.Id);

    /// <summary>
    /// Called by Game when the player accepts the PlanCreation form.
    /// Seeds the edit fields from the creation form and enters edit mode for a new plan.
    /// This method is kept for direct-call scenarios; the normal path goes via
    /// GameSessionService.Pending* fields consumed in OnInitialized.
    /// </summary>
    public void StartNewPlanEdit(string name, string description, int startYear, int startMonth)
    {
        _editName        = name;
        _editDescription = description;
        _editStartYear   = startYear;
        _editStartMonth  = startMonth;
        _editPlanLayerIds.Clear();
        _editPolicyTypes.Clear();
        _editError       = null;
        _enterEditError  = null;
        GameUIStateService.SelectedPlanId = 0;
        GameUIStateService.EditMode = true;
        GameSessionService.NotifyChanged();
    }

    private DateTime EditEarliestStart =>
        new DateTime(GameSessionService.GameStartYear, 1, 1).AddMonths(GameSessionService.GameCurrentMonth + Math.Max(GetConstructionTime(), 1));

    private bool EditStartDateValid =>
        _editStartYear > EditEarliestStart.Year ||
        (_editStartYear == EditEarliestStart.Year && _editStartMonth >= EditEarliestStart.Month);

    private async Task EnterEditModeAsync()
    {
        // Always fetch the latest plan from GameSessionService
        _detailPlan = GetDetailPlan();
        if (_detailPlan is null)
        {
            _enterEditError = "Selected plan not found. It may have been deleted or modified by another user. Please select the plan again.";
            StateHasChanged();
            return;
        }

        if (_detailPlan.LockedByUserId != 0 && _detailPlan.LockedByUserId != UserSessionService.User.Id)
        {
            _enterEditError = $"Plan is currently locked by another user.";
            StateHasChanged();
            return;
        }

        // Try to lock the plan
        try
        {
            await ApiClient.PostFormAsync("Plan/Lock",
                new[]
                {
                    new KeyValuePair<string, string>("id", _detailPlan.PlanId.ToString()),
                    new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                });
        }
        catch (MspApiException ex)
        {
            _enterEditError = $"Failed to lock plan: {ex.Message}";
            StateHasChanged();
            return;
        }
        catch (Exception ex)
        {
            _enterEditError = $"Failed to lock plan: {ex.Message}";
            StateHasChanged();
            return;
        }

        // Seed every edit field from the existing plan so that:
        //  • EditStartDateValid passes for the plan's already-saved start date.
        //  • Fields the user doesn't touch are sent back unchanged (not as empty/zero).
        _editName        = _detailPlan.Name;
        _editDescription = _detailPlan.Description;
        _editPlanLayerIds = _detailPlan.Layers
            .Select(l => l.OriginalLayerId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _editPolicyTypes = _detailPlan.PolicyTypes
            .Where(t => !string.IsNullOrEmpty(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _editError       = null;
        _enterEditError  = null;

        var baseDate = new DateTime(GameSessionService.GameStartYear, 1, 1);
        try
        {
            var d = baseDate.AddMonths(Math.Clamp(_detailPlan.StartDate, -120000, 120000));
            _editStartYear  = d.Year;
            _editStartMonth = d.Month;
        }
        catch
        {
            _editStartYear  = GameSessionService.GameStartYear;
            _editStartMonth = 1;
        }

        GameUIStateService.ToggleEditMode();
        StateHasChanged();
    }

    private async Task CancelEditAsync()
    {
        var confirmation = await cancelDialog.ShowAsync(
            title: "Are you sure you want to cancel editing this plan?",
            message1: "This will undo any edits you made to the plan, and unlock it again.",
            message2: "Do you want to proceed?");
        if (!confirmation) return;
        GameUIStateService.ToggleEditMode();
        if (GameUIStateService.SelectedPlanId == 0) // new plan
        {
            GameUIStateService.SelectedPlanId = null;
            StateHasChanged();
            return;
        }
        
        _editSaving = true;
        StateHasChanged();
        try
        {
            await ApiClient.PostFormAsync("Plan/Unlock",
                new[]
                {
                    new KeyValuePair<string, string>("id", GameUIStateService.SelectedPlanId.ToString()!),
                    new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                });
        }
        catch { }
        finally
        {
            _editSaving = false;
            _editError = null;
            StateHasChanged();
        }
    }

    private Plan GetDetailPlan()
    {
        var plan = GameSessionService.SelectedPlan;
        if (plan != null) return plan;
        
        return new Plan(
            Name: _editName ?? string.Empty,
            Description: _editDescription ?? string.Empty,
            Country: UserSessionService.User.Country.Id,
            StartDate: (_editStartYear - GameSessionService.GameStartYear) * 12 + (_editStartMonth - 1),
            Layers: _editPlanLayerIds.Select(id =>
                new PlanLayerData(
                    LayerId: id,
                    OriginalLayerId: id,
                    State: string.Empty,
                    Geometry: new List<PlanGeometryItem>(),
                    DeletedPersistentIds: new List<string>()
                )
            ).ToList()
        );
    }

    private async Task SavePlanAsync()
    {
        if (_editSaving) return;
        if (!EditStartDateValid)
        {
            _editError = $"Start date must be {EditEarliestStart:MMM yyyy} or later.";
            StateHasChanged();
            return;
        }
        CloseAllSubPanels();
        // Pass geometry state from PlanLayers → PlanDetailsSave before saving.
        // PlanDetailsSave needs the map JS module to call getOverlayFeaturesJson,
        // the edited layer ID to know which overlay to read, and the set of
        // world-state features the player deleted in this edit session.
        PlanDetailsSaveInstance.SetMapModule(Map?.MapJSModule);
        PlanDetailsSaveInstance.SetGeometryEditedLayerId(PlanLayersInstance.GeometryEditedLayerId);
        PlanDetailsSaveInstance.SetDeletedWorldStateIds(PlanLayersInstance.DeletedWorldStateIds);

        var success = await PlanDetailsSaveInstance.SavePlanAsync();

        if (success)
        {
            GameUIStateService.ToggleEditMode();
        }
    }

    private void OnSaveError(string? error)
    {
        _editError = error;
        StateHasChanged();
    }

    private void OnSavingChanged(bool saving)
    {
        _editSaving = saving;
        StateHasChanged();
    }

    private async Task DisplaySelectedPlanAsync()
    {
        if (Map?.MapJSModule is null) return;

        var plan = GameSessionService.SelectedPlan;
        
        // If switching plans, fully clear the previous plan first to avoid concurrent operations
        if (_lastDisplayedPlanId.HasValue && (plan is null || _lastDisplayedPlanId != plan.PlanId))
        {
            await Map.MapJSModule.InvokeVoidAsync("clearIssueMarkers");
            await DeactivatePlanLayersAsync();
            await Map.MapJSModule.InvokeVoidAsync("clearPlanOverlay");
            _lastDisplayedPlanId = null;
            
            // If deselecting (plan is null), we're done
            if (plan is null) return;
        }

        // At this point, plan should not be null
        if (plan is null) return;

        // Now display the new plan (no concurrent operations with previous plan)
        _lastDisplayedPlanId = plan.PlanId;

        // Activate layers referenced by this plan
        await ActivatePlanLayersAsync(plan);

        var layersData = new List<object>();

        foreach (var planLayer in plan.Layers)
        {
            var layerEntry = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            if (layerEntry is null) continue;

            var geometries = planLayer.Geometry
                .Where(g => g.Coordinates.Count > 0)
                .Select(g => new
                {
                    id = g.Id,
                    coords = g.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray(),
                    isNew = string.IsNullOrEmpty(g.PersistentId) || g.Id == g.PersistentId,
                    mspType = g.TypeIndex
                })
                .ToList();

            if (geometries.Count > 0 || planLayer.DeletedPersistentIds.Count > 0)
            {
                layersData.Add(new
                {
                    originalLayerId = planLayer.OriginalLayerId,
                    geoType = layerEntry.GeoType,
                    geometries,
                    deletedIds = planLayer.DeletedPersistentIds
                });
            }
        }

        if (layersData.Count > 0)
        {
            await Map.MapJSModule.InvokeVoidAsync("showPlanGeometry", JsonSerializer.Serialize(layersData));
        }
        else
        {
            await Map.MapJSModule.InvokeVoidAsync("clearPlanOverlay");
        }

        // Apply plan projection to show world state at plan start date
        if (Map is not null)
        {
            await Map.ApplyPlanProjectionAsync(plan.StartDate, currentPlan: plan);
        }

        StateHasChanged();
    }

    private async Task ActivatePlanLayersAsync(Plan plan)
    {
        if (Map is null) return;

        // Get all unique layer IDs referenced by the plan
        var referencedLayerIds = plan.Layers
            .Where(pl => !string.IsNullOrEmpty(pl.OriginalLayerId))
            .Select(pl => pl.OriginalLayerId)
            .Distinct()
            .ToList();

        foreach (var layerId in referencedLayerIds)
        {
            var layerEntry = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
            if (layerEntry is null || layerEntry.IsBaseLayer) continue;

            // Only activate if not already visible
            if (!layerEntry.Visible)
            {
                await Map.ToggleLayerInternalAsync(layerEntry, true, skipProjection: true);
                _planActivatedLayerIds.Add(layerId);
            }
        }
    }

    private async Task DeactivatePlanLayersAsync()
    {
        if (Map is null || _planActivatedLayerIds.Count == 0) return;

        // Deactivate layers that were activated by the plan
        foreach (var layerId in _planActivatedLayerIds.ToList())
        {
            var layerEntry = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
            if (layerEntry is not null)
            {
                await Map.ToggleLayerInternalAsync(layerEntry, false, skipProjection: true);
            }
        }
        
        _planActivatedLayerIds.Clear();
    }

    public void Dispose()
    {
        GameSessionService.Changed -= OnStateChanged;
    }
}


