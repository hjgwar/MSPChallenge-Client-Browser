using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class PlanDetails : GameComponentBase, IDisposable
{
    [Parameter] public MapViewPort? Map { get; set; }
    
    public PlanNameDesc PlanNameDescInstance { get; set; } = null!;
    public PlanStartDate PlanStartDateInstance { get; set; } = null!;
    public PlanPolicies PlanPoliciesInstance { get; set; } = null!;
    public PlanLayers PlanLayersInstance { get; set; } = null!;
    public PlanMessages PlanMessagesInstance { get; set; } = null!;
    public PlanIssues PlanIssuesInstance { get; set; } = null!;
    public PlanDetailsSave PlanDetailsSaveInstance { get; set; } = null!;

    private List<PlanRestrictionIssue> _selectedPlanIssues = [];
    private Plan _detailPlan = null!;
    private List<string> _detailLayers = [];
    private string _detailDotColour = "#6c757d";
    private string _detailCountryName = "Unknown Country";
    
    private bool _editSaving = false;
    private string? _editError;

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
        GameSessionState.Changed += OnStateChanged;
        UpdateDetailPlan();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || _lastDisplayedPlanId != GameSessionState.SelectedPlanId)
        {
            await DisplaySelectedPlanAsync();
        }
    }

    private void OnStateChanged()
    {
        if (_lastDisplayedPlanId != GameSessionState.SelectedPlanId)
        {
            UpdateDetailPlan();
            InvokeAsync(async () => await DisplaySelectedPlanAsync());
        }
    }

    private void UpdateDetailPlan()
    {
        _detailPlan = GetDetailPlan();
        _detailLayers = _detailPlan.Layers
            .Select(l => GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == l.OriginalLayerId)?.DisplayName)
            .OfType<string>()
            .Distinct()
            .ToList();
        _detailDotColour = (_detailPlan.Country == 1 || _detailPlan.Country == 2)
            ? "#ff69b4"
            : GameSessionState.Countries.FirstOrDefault(c => c.Id == _detailPlan.Country)?.Color ?? "#6c757d";
        _detailCountryName = GameSessionState.Countries.FirstOrDefault(c => c.Id == _detailPlan.Country)?.Name ?? $"Country {_detailPlan.Country}";
    }

    private int GetConstructionTime()
    {
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
        if (GameSessionState.EditMode)
            await CancelEditAsync();
        GameSessionState.SelectedPlanId = null;
        GameSessionState.NotifyChanged();
    }

    private bool CanEnterEditMode =>
        GameSessionState.SelectedPlanId != 0 &&
        _detailPlan is not null &&
        _detailPlan.State == Models.PlanState.DESIGN &&
        (UserSessionService.User.Country.Id <= 2 || _detailPlan.Country == UserSessionService.User.Country.Id);

    private DateTime EditEarliestStart =>
        new DateTime(GameSessionState.GameStartYear, 1, 1).AddMonths(GameSessionState.GameCurrentMonth + GetConstructionTime());

    private bool EditStartDateValid =>
        _editStartYear > EditEarliestStart.Year ||
        (_editStartYear == EditEarliestStart.Year && _editStartMonth >= EditEarliestStart.Month);

    private async Task EnterEditModeAsync()
    {
        // Always fetch the latest plan from GameSessionState
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
        catch
        {
            _enterEditError = "Failed to lock plan.";
            StateHasChanged();
            return;
        }

        GameSessionState.ToggleEditMode();
        StateHasChanged();
    }

    private async Task CancelEditAsync()
    {
        GameSessionState.ToggleEditMode();
        _editSaving = true;
        StateHasChanged();
        try
        {
            await ApiClient.PostFormAsync("Plan/Unlock",
                new[]
                {
                    new KeyValuePair<string, string>("id", GameSessionState.SelectedPlanId.ToString()!),
                    new KeyValuePair<string, string>("force_unlock", "0"),
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
        var plan = GameSessionState.SelectedPlan;
        if (plan != null) return plan;
        
        return new Plan(
            0, // PlanId (new/unsaved)
            _editName ?? string.Empty,
            _editDescription ?? string.Empty,
            Models.PlanState.DESIGN, // State
            UserSessionService.User.Country.Id,
            (_editStartYear - GameSessionState.GameStartYear) * 12 + (_editStartMonth - 1),
            0, // ConstructionTime
            new List<string>(), // PolicyNames
            new List<string>(), // PolicyTypes
            _editPlanLayerIds.Select(id =>
                new PlanLayerData(
                    id, // LayerId
                    id, // OriginalLayerId
                    string.Empty, // State
                    new List<PlanGeometryItem>(), // Geometry
                    new List<string>() // DeletedPersistentIds
                )
            ).ToList(),
            false, // RequiresApproval
            0, // MessageCount
            0, // IssueCount
            null, // Votes
            0 // LockedByUserId
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

        // Call the PlanDetailsSave sub-component's SavePlanAsync method
        var success = await PlanDetailsSaveInstance.SavePlanAsync();
        
        if (success)
        {
            GameSessionState.ToggleEditMode();
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

        var plan = GameSessionState.SelectedPlan;
        
        // If switching plans, fully clear the previous plan first to avoid concurrent operations
        if (_lastDisplayedPlanId.HasValue && (plan is null || _lastDisplayedPlanId != plan.PlanId))
        {
            await DeactivatePlanLayersAsync();
            await Map.MapJSModule.InvokeVoidAsync("clearPlanOverlay");
            _lastDisplayedPlanId = null;
            
            // If deselecting (plan is null), we're done
            if (plan is null) return;
        }

        // At this point, plan should not be null
        if (plan is null) return;

        // Create complete snapshot of plan data upfront to avoid race conditions with WebSocket updates
        var planStartDate = plan.StartDate;
        var planId = plan.PlanId;
        var planLayersSnapshot = new List<dynamic>();
        
        foreach (var pl in plan.Layers.ToList())
        {
            var geometrySnapshot = new List<dynamic>();
            foreach (var g in pl.Geometry.ToList())
            {
                var coordsSnapshot = new List<double[]>();
                foreach (var c in g.Coordinates.ToList())
                {
                    coordsSnapshot.Add(new[] { c[0], c[1] });
                }
                
                geometrySnapshot.Add(new
                {
                    g.Id,
                    g.PersistentId,
                    g.TypeIndex,
                    Coordinates = coordsSnapshot
                });
            }
            
            planLayersSnapshot.Add(new
            {
                pl.OriginalLayerId,
                Geometry = geometrySnapshot,
                DeletedPersistentIds = pl.DeletedPersistentIds.ToList()
            });
        }

        // Now display the new plan (no concurrent operations with previous plan)
        _lastDisplayedPlanId = planId;

        // Activate layers referenced by this plan
        await ActivatePlanLayersAsync(plan);

        var layersData = new List<object>();

        foreach (var planLayer in planLayersSnapshot)
        {
            var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            if (layerEntry is null) continue;

            var geometries = new List<object>();
            foreach (var g in (IEnumerable<dynamic>)planLayer.Geometry)
            {
                if (((ICollection<double[]>)g.Coordinates).Count > 0)
                {
                    geometries.Add(new
                    {
                        id = (string)g.Id,
                        coords = ((List<double[]>)g.Coordinates).ToArray(),
                        isNew = string.IsNullOrEmpty((string?)g.PersistentId) || (string)g.Id == (string)g.PersistentId,
                        mspType = (int)g.TypeIndex
                    });
                }
            }

            if (geometries.Count > 0 || ((List<string>)planLayer.DeletedPersistentIds).Count > 0)
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
        // Don't pass currentPlan to avoid race conditions - the overlay already handles plan geometry display
        if (Map is not null)
        {
            await Map.ApplyPlanProjectionAsync(planStartDate);
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
            var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
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
            var layerEntry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
            if (layerEntry is not null)
            {
                await Map.ToggleLayerInternalAsync(layerEntry, false, skipProjection: true);
            }
        }
        
        _planActivatedLayerIds.Clear();
    }

    public void Dispose()
    {
        GameSessionState.Changed -= OnStateChanged;
    }
}


