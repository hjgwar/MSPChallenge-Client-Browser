using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;
public partial class PlanPanelsControl : IDisposable
{
    [Parameter] public MapViewPort Map { get; set; } = null!;
    private PlanDetails? PlanDetailsPanel { get; set; };

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

    // ── Edit mode ─────────────────────────────────────────────────────────────
    public bool    EditMode      = false;
    public bool    EditSaving    = false;
    public string? EditError;
    public bool   CreatePlanOpen = false;

    private string?    _geometryToolLayerId;
    
    public async Task SetPlanViewModeAsync(PlanViewMode mode)
    {
        if (MapJSModule is null || GameSessionState.SelectedPlanId == 0 || mode == GameSessionState.PlanViewMode) return;
        GameSessionState.PlanViewMode = mode;

        // Overlay is hidden only in Original mode.
        await MapJSModule.InvokeVoidAsync("setPlanOverlayVisible", mode != PlanViewMode.Original);

        // Referenced base layers are hidden only in ChangesOnly mode.
        bool showBase = mode != PlanViewMode.ChangesOnly;
        foreach (var layerId in _planReferencedLayerIds)
            await MapJSModule.InvokeVoidAsync("setLayerVisible", layerId, showBase);
        
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

    private async Task ClosePlanDetailAsync()
    {
        if (GameSessionState.EditPlanMode)
            await CancelEditAsync();
        var plan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        if (plan is not null)
            await SelectPlanAsync(plan);
    }
    
    private async Task TogglePlanStatePanel()
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