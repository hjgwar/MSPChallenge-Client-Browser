using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;
public partial class PlanControl : IDisposable
{
    [Parameter] public MapViewPort Map { get; set; } = null!;
    public GameTimeView GameTimeViewer { get; set; } = null!;
    public PlanDetails? PlanDetailsPanel { get; set; }

    public PlanEntry? SelectedPlan = null;

    public bool    EditMode      = false;
    public bool    EditSaving    = false;
    
    private int?     _pendingSelectPlanId;


    private HashSet<string>            _planActivatedLayerIds    = [];
    private HashSet<string>            _planReferencedLayerIds   = [];
    
    
    private bool                       _detailDescExpanded       = false;
    private bool                       _scrollPlanMessagesPending = false;
    
    private bool _layerPickerOpen  = false;
    private bool _policyPickerOpen = false;
    private bool _planMessagesOpen = false;
    private bool _planIssuesOpen   = false;
    private bool _planApprovalOpen = false;
    private bool _planStateOpen    = false;
    private bool _planStateSending;
    private bool _planStateDropdownOpen;
    private string? _planStatePending;
    private HashSet<int> _approvalReasonsExpanded = [];

        public bool   CreatePlanOpen = false;

    private string?    _geometryToolLayerId;
    
    protected override async Task OnInitializedAsync()
    {
        if (GameSessionState.SelectedPlanId != 0 && GameSessionState.SelectedPlanId is not null)
        {
            FetchSelectedPlan();
            if (SelectedPlan is not null)
                await SelectPlanAsync();
        }
    }

    public bool FetchSelectedPlan()
    {
        SelectedPlan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        if (SelectedPlan is null)
        {
            // Plan not found (maybe stale) — defer edit until next WS update
            _pendingSelectPlanId  = GameSessionState.SelectedPlanId;
            StateHasChanged();
            return false;
        }
        return true;
    }

    public int GetConstructionTime()
    {
        // to do: check through PlanDetails > PlanLayers for any construction time overrides? For now just use the original Plan construction time value
        return SelectedPlan?.ConstructionTime ?? 0;
    }

    // should just call FetchSelectedPlan & Map related functions to show planned layers and geometry and any restriction issues
    private async Task SelectPlanAsync()
    {
        var plan = SelectedPlan;
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

        _createPlanOpen = false;

        if (_selectedPlanId == plan.PlanId)
        {
            // Toggling the same plan off.
            if (_geometryToolLayerId is not null)
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");
            _selectedPlanId = 0;
            _planViewMode = PlanViewMode.AfterChanges;
            _planIssuesOpen = false;
            _planApprovalOpen = false;
            _planStateOpen = false;
            _editMode = false;
            _policyPickerOpen = false;
            _editPolicyTypes.Clear();
            _layerPickerOpen  = false;
            _editPlanLayerIds.Clear();
            _selectedPlanIssues.Clear();
            _approvalRequired.Clear();
            _approvalReasonsExpanded.Clear();
            _enterEditError = null;
            _geometryToolLayerId           = null;
            _geometryToolSelectedFeatureId = null;
            _drawingUndoStack.Clear();
            _drawingRedoStack.Clear();
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
        _planStateOpen = false;
        _editMode = false;
        _policyPickerOpen = false;
        _editPolicyTypes.Clear();
        _layerPickerOpen  = false;
        _editPlanLayerIds.Clear();
        _planMessageDraft = string.Empty;
        _planMessageSendError = null;
        _enterEditError = null;
        _selectedPlanIssues.Clear();
        _approvalRequired.Clear();
        _approvalReasonsExpanded.Clear();
        _geometryToolLayerId           = null;
        _geometryToolSelectedFeatureId = null;
        _drawingUndoStack.Clear();
        _drawingRedoStack.Clear();

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
                    id     = geometry.Id,
                    coords = geometry.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray(),
                    isNew  = isNewGeometry,
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

        await ApplyPlanProjectionAsync(plan.StartDate, currentPlan: plan);

        if (!IsApprovalCompleteState(plan.State))
            CalculateApproval(plan);

        StateHasChanged();
    }

    public void OpenEditMode(PlanCreation planCreation)
    {
        // Save to memory and open plan-detail-panel in edit mode
        _editName = planCreation._createPlanName;
        _editDescription = planCreation._createPlanDescription;
        _editStartYear = planCreation._createPlanStartYear;
        _editStartMonth = planCreation._createPlanStartMonth;
        _editPlanLayerIds.Clear();
        _editPolicyTypes.Clear();
        _drawingUndoStack.Clear();
        _drawingRedoStack.Clear();
        _deletedWorldStateIds.Clear();
        _geometryEditedLayerId = null;
        _geometryToolLayerId = null;
        _editMode = true;
        GameSessionState.CreatePlanOpen = false;
        GameSessionState.SelectedPlanId = 0; // New plan, not yet on server
        StateHasChanged();
    }

    

    
    
    public async Task TogglePlanStatePanel()
    {
        if (GameSessionState.SelectedPlanId == 0) return;
        var plan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        if (plan is null
            || plan.State.Equals("IMPLEMENTED", StringComparison.OrdinalIgnoreCase)
            || (!UserSessionService.IsAdmin && plan.Country != UserSessionService.User?.CountryId)) return;
        _planStateOpen = !_planStateOpen;
        if (_planStateOpen)
        {
            _planMessagesOpen = false;
            _planIssuesOpen   = false;
            _planApprovalOpen = false;
            _policyPickerOpen = false;
            _layerPickerOpen  = false;
            _approvalReasonsExpanded.Clear();
            _planStatePending = plan.State.ToUpperInvariant();
            
            // Close geometry tool
            if (_geometryToolLayerId is not null)
                await Map.MapJSModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
        else
        {
            _planStateDropdownOpen = false;
        }
    }

    public async Task ToggleApprovalPanel()
    {
        if (GameSessionState.SelectedPlanId == 0) return;
        _planApprovalOpen = !_planApprovalOpen;
        if (_planApprovalOpen)
        {
            _planMessagesOpen = false;
            _planIssuesOpen   = false;
            _planStateOpen    = false;
            _policyPickerOpen = false;
            _layerPickerOpen  = false;
            
            // Close geometry tool
            if (_geometryToolLayerId is not null && _mapModule is not null)
                await Map.MapJSModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
        else
        {
            _approvalReasonsExpanded.Clear();
        }
    }

    public async Task TogglePlanIssuesPanel()
    {
        if (GameSessionState.SelectedPlanId == 0) return;
        _planIssuesOpen = !_planIssuesOpen;
        if (_planIssuesOpen)
        {
            _planMessagesOpen = false;
            _planApprovalOpen = false;
            _planStateOpen    = false;
            _policyPickerOpen = false;
            _layerPickerOpen  = false;
            _approvalReasonsExpanded.Clear();
            
            // Close geometry tool
            if (_geometryToolLayerId is not null)
                await Map.MapJSModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
    }

    public void CloseCreatePlanPanel()
    {
        GameSessionState.CreatePlanOpen  = false;
    }

    public void Dispose()
    {
        
    }
}