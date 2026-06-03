using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Game
{
    // â”€â”€ Plan selection & view â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private int                        _selectedPlanId           = 0;
    private PlanViewMode               _planViewMode             = PlanViewMode.AfterChanges;
    private HashSet<string>            _planActivatedLayerIds    = [];
    private HashSet<string>            _planReferencedLayerIds   = [];
    private List<PlanRestrictionIssue> _selectedPlanIssues       = [];
    private Dictionary<int, string>    _planIssueSeverity        = [];
    private bool                       _detailDescExpanded       = false;
    private bool                       _scrollPlanMessagesPending = false;

    // â”€â”€ Plans panel â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private bool   _plansPanelOpen = false;
    private bool   _createPlanOpen = false;
    private string _layerSearch    = string.Empty;

    // â”€â”€ Plan sub-panels â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private bool _planMessagesOpen = false;
    private bool _planIssuesOpen   = false;
    private bool _planApprovalOpen = false;
    private bool _planStateOpen    = false;

    // â”€â”€ Plan state change â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private bool    _planStateSending;
    private bool    _planStateDropdownOpen;
    private string? _planStatePending;

    // â”€â”€ Approval â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private List<ApprovalRequirement> _approvalRequired        = [];
    private HashSet<int>              _approvalReasonsExpanded = [];

    // â”€â”€ Plan messages â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private string  _planMessageDraft     = string.Empty;
    private string? _planMessageSendError;
    private bool    _sendingPlanMessage;
    private bool    _sendingVote;
    private PlanEntry GetDetailPlan()
    {
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan != null) return plan;
        return new PlanEntry(
            0, // PlanId (new/unsaved)
            _editName ?? string.Empty,
            _editDescription ?? string.Empty,
            string.Empty, // State
            SessionState.CountryId,
            (_editStartYear - _gameStartYear) * 12 + (_editStartMonth - 1),
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

    private void SetPlansPanelOpen(bool open)
    {
        _plansPanelOpen = open;
        GameState.PlansPanelOpen = open;
    }

    private void TogglePlansPanel()
    {
        SetPlansPanelOpen(!_plansPanelOpen);
    }

    private string MonthToDate(int month) => GameState.MonthToDate(month);

    private static string PlanStateLabel(string state) => GameSessionState.PlanStateLabel(state);

    private string GameStateLabel => GameSessionState.GameStateLabel(GameState.GameState);

    private static string FormatTimeLeft(double totalSeconds) => GameSessionState.FormatTimeLeft(totalSeconds);

    private int SelectedPlanIssueCount => _selectedPlanIssues.Count;
    private IReadOnlyList<PlanRestrictionIssue> SelectedPlanIssues => _selectedPlanIssues;

    private async Task SelectPlanAsync(PlanEntry plan)
    {
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

    /// <summary>
    /// Computes and sends the world-state projection to the map for all earlier finalised plans
    /// relative to <paramref name="planStartDate"/>.
    /// Pass <paramref name="singleLayerId"/> to restrict processing to one layer — used when
    /// the user activates a layer from the panel while a plan is already selected.
    /// Pass <paramref name="currentPlan"/> to also hide base geometry that the current plan modifies.
    /// </summary>
    private async Task ApplyPlanProjectionAsync(int planStartDate, string? singleLayerId = null, PlanEntry? currentPlan = null)
    {
        if (_mapModule is null) return;

        var priorPlans = _plans
            .Where(p => p.StartDate < planStartDate && IsFinalisedPlanState(p.State))
            .OrderBy(p => p.StartDate).ThenBy(p => p.PlanId)
            .ToList();

        var hiddenFeatures = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var addedFeatures  = new List<object>();

        // Process prior finalized plans
        foreach (var priorPlan in priorPlans)
        {
            foreach (var planLayer in priorPlan.Layers)
            {
                if (string.IsNullOrEmpty(planLayer.OriginalLayerId)) continue;
                if (singleLayerId is not null &&
                    !string.Equals(planLayer.OriginalLayerId, singleLayerId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!hiddenFeatures.TryGetValue(planLayer.OriginalLayerId, out var ids))
                    hiddenFeatures[planLayer.OriginalLayerId] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var deletedId in planLayer.DeletedPersistentIds)
                    ids.Add(deletedId);

                var geoType = _layerEntries
                    .FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId)?.GeoType ?? "";

                foreach (var geo in planLayer.Geometry)
                {
                    if (!string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id)
                        ids.Add(geo.PersistentId);

                    if (geo.Coordinates.Count > 0)
                        addedFeatures.Add(new {
                            layerId   = planLayer.OriginalLayerId,
                            geoType,
                            id        = geo.Id,
                            typeIndex = geo.TypeIndex,
                            coords    = geo.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray()
                        });
                }
            }
        }

        // Process current plan if provided - hide base geometry that it modifies
        // Note: Don't add current plan's geometry to base layers (it's in the overlay)
        if (currentPlan is not null)
        {
            foreach (var planLayer in currentPlan.Layers)
            {
                if (string.IsNullOrEmpty(planLayer.OriginalLayerId)) continue;
                if (singleLayerId is not null &&
                    !string.Equals(planLayer.OriginalLayerId, singleLayerId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!hiddenFeatures.TryGetValue(planLayer.OriginalLayerId, out var ids))
                    hiddenFeatures[planLayer.OriginalLayerId] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Hide deleted base geometry
                foreach (var deletedId in planLayer.DeletedPersistentIds)
                    ids.Add(deletedId);

                // Hide modified base geometry (where PersistentId != Id)
                foreach (var geo in planLayer.Geometry)
                {
                    if (!string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id)
                        ids.Add(geo.PersistentId);
                }
            }
        }

        if (hiddenFeatures.Count > 0 || addedFeatures.Count > 0)
            await _mapModule.InvokeVoidAsync("applyPlanProjection",
                JsonSerializer.Serialize(new {
                    hidden = hiddenFeatures.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()),
                    added  = addedFeatures
                }));
    }

    private static bool IsFinalisedPlanState(string state) =>
        state.Equals("CONSULTATION", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("APPROVAL",     StringComparison.OrdinalIgnoreCase) ||
        state.Equals("APPROVED",     StringComparison.OrdinalIgnoreCase) ||
        state.Equals("IMPLEMENTED",  StringComparison.OrdinalIgnoreCase);

    private static bool IsApprovalCompleteState(string? state) =>
        state is not null &&
        (state.Equals("APPROVED",    StringComparison.OrdinalIgnoreCase) ||
         state.Equals("IMPLEMENTED", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("ARCHIVED",    StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recalculates restriction issues and approval requirements for the currently selected plan.
    /// Should be called after saving a plan to update the issues list and approval badges.
    /// </summary>
    private async Task RecalculatePlanIssuesAndApprovalAsync()
    {
        if (_selectedPlanId == 0) return;
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan is null) return;

        // Clear and recalculate issues
        _selectedPlanIssues.Clear();

        foreach (var planLayer in plan.Layers)
        {
            var geoType = _layerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId)?.GeoType
                       ?? InferGeoType(planLayer.Geometry);

            foreach (var geometry in planLayer.Geometry)
            {
                var isNewGeometry = string.IsNullOrEmpty(geometry.PersistentId) || geometry.Id == geometry.PersistentId;
                var geometryIssues = EvaluateRestrictionsForGeometry(planLayer.OriginalLayerId, geoType, geometry, isNewGeometry, plan.StartDate);
                _selectedPlanIssues.AddRange(geometryIssues);
            }
        }

        _selectedPlanIssues.Sort((a, b) =>
        {
            var s = SeveritySortRank(b.Severity).CompareTo(SeveritySortRank(a.Severity));
            if (s != 0) return s;
            s = string.Compare(a.TargetLayer, b.TargetLayer, StringComparison.OrdinalIgnoreCase);
            return s != 0 ? s : string.Compare(a.Message, b.Message, StringComparison.OrdinalIgnoreCase);
        });

        // Update severity badge
        if (_selectedPlanIssues.Count > 0)
            _planIssueSeverity[plan.PlanId] = NormaliseSeverity(_selectedPlanIssues[0].Severity);
        else
            _planIssueSeverity.Remove(plan.PlanId);

        // Recalculate approval if not in a completed state
        if (!IsApprovalCompleteState(plan.State))
            CalculateApproval(plan);

        // Update the plan geometry overlay with new restriction markers
        if (_mapModule is not null)
        {
            var layersData = new List<object>();
            foreach (var planLayer in plan.Layers)
            {
                var geoType = _layerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId)?.GeoType
                           ?? InferGeoType(planLayer.Geometry);

                var geometries = new List<object>();
                foreach (var geometry in planLayer.Geometry)
                {
                    var isNewGeometry = string.IsNullOrEmpty(geometry.PersistentId) || geometry.Id == geometry.PersistentId;
                    var geometryIssues = _selectedPlanIssues.Where(i =>
                        string.Equals(i.TargetLayer, planLayer.OriginalLayerId, StringComparison.OrdinalIgnoreCase) &&
                        geometry.Coordinates.Any(c => Math.Abs(c[0] - i.MarkerX) < 0.01 && Math.Abs(c[1] - i.MarkerY) < 0.01)
                    ).ToList();

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

            if (layersData.Count > 0)
                await _mapModule.InvokeVoidAsync("showPlanGeometry", JsonSerializer.Serialize(layersData));
        }

        StateHasChanged();
    }

    private string? SelectedPlanState => _selectedPlanId == 0
        ? null
        : _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId)?.State;

    private static string NormaliseSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var key = NormaliseToken(raw);
        return key switch
        {
            "error" or "err" or "danger" or "2" => "ERROR",
            "warning" or "warn" or "1" => "WARNING",
            "info" or "information" or "0" => "INFO",
            _ => key.ToUpperInvariant()
        };
    }

    private static int SeveritySortRank(string? severity)
    {
        return NormaliseSeverity(severity) switch
        {
            "ERROR" => 3,
            "WARNING" => 2,
            "INFO" => 1,
            _ => 0
        };
    }

    private static string NormaliseConstraintSort(string? rawSort)
    {
        if (string.IsNullOrWhiteSpace(rawSort)) return "INCLUSION";
        var key = NormaliseToken(rawSort);
        return key switch
        {
            "0" or "inclusion" => "INCLUSION",
            "1" or "exclusion" => "EXCLUSION",
            "2" or "typeunavailable" => "TYPE_UNAVAILABLE",
            _ => key.ToUpperInvariant()
        };
    }

    private static string NormaliseToken(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private async Task SetPlanViewModeAsync(PlanViewMode mode)
    {
        if (_mapModule is null || _selectedPlanId == 0 || mode == _planViewMode) return;
        _planViewMode = mode;

        // Overlay is hidden only in Original mode.
        await _mapModule.InvokeVoidAsync("setPlanOverlayVisible", mode != PlanViewMode.Original);

        // Referenced base layers are hidden only in ChangesOnly mode.
        bool showBase = mode != PlanViewMode.ChangesOnly;
        foreach (var layerId in _planReferencedLayerIds)
            await _mapModule.InvokeVoidAsync("setLayerVisible", layerId, showBase);

        StateHasChanged();
    }

    private async Task ClosePlanDetailAsync()
    {
        if (_editMode)
            await CancelEditAsync();
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan is not null)
            await SelectPlanAsync(plan);
    }

    private async Task TogglePlanMessagesPanel()
    {
        if (_selectedPlanId == 0) return;
        _planMessagesOpen = !_planMessagesOpen;
        if (_planMessagesOpen)
        {
            _planIssuesOpen   = false;
            _planApprovalOpen = false;
            _planStateOpen    = false;
            _policyPickerOpen = false;
            _layerPickerOpen  = false;
            _approvalReasonsExpanded.Clear();
            _scrollPlanMessagesPending = true;
            
            // Close geometry tool
            if (_geometryToolLayerId is not null && _mapModule is not null)
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
        _planMessageSendError = null;
    }

    private async Task TogglePlanIssuesPanel()
    {
        if (_selectedPlanId == 0) return;
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
            if (_geometryToolLayerId is not null && _mapModule is not null)
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
    }

    private async Task ToggleApprovalPanel()
    {
        if (_selectedPlanId == 0) return;
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
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
        else
        {
            _approvalReasonsExpanded.Clear();
        }
    }

    private void ToggleApprovalReasonExpanded(int countryId)
    {
        if (!_approvalReasonsExpanded.Remove(countryId))
            _approvalReasonsExpanded.Add(countryId);
    }

    private async Task TogglePlanStatePanel()
    {
        if (_selectedPlanId == 0) return;
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        bool isManager = SessionState.CountryId <= 2;
        if (plan is null
            || plan.State.Equals("IMPLEMENTED", StringComparison.OrdinalIgnoreCase)
            || (!isManager && plan.Country != SessionState.CountryId)) return;
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
            if (_geometryToolLayerId is not null && _mapModule is not null)
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");
            _geometryToolLayerId = null;
        }
        else
        {
            _planStateDropdownOpen = false;
        }
    }

    // â”€â”€ Edit mode â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private bool CanEnterEditMode =>
        _selectedPlanId != 0 &&
        _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId) is { } ep &&
        ep.State.Equals("DESIGN", StringComparison.OrdinalIgnoreCase) &&
        (SessionState.CountryId <= 2 || ep.Country == SessionState.CountryId);

   
    private void OpenCreatePlanPanel()
    {
        // Close any open plan detail
        _selectedPlanId   = 0;
        _editMode         = false;
        _policyPickerOpen = false;
        _createPlanOpen   = true;
    }

    private void CloseCreatePlanPanel()
    {
        _createPlanOpen  = false;
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
        _createPlanOpen = false;
        _selectedPlanId = 0; // New plan, not yet on server
        StateHasChanged();
    }
    // â”€â”€ Edit mode â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // Earliest calendar date the plan may start (current sim month + min construction)
    private IReadOnlyList<string> GetAvailablePlanStates(PlanEntry plan)
    {
        bool hasErrors = _selectedPlanIssues.Any(
            i => i.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase));

        return PlanStateTransitions.GetAvailablePlanStates(plan.State, plan.RequiresApproval, hasErrors);
    }

    private async Task SetPlanStateAsync()
    {
        if (_planStateSending || _planStatePending is null || _selectedPlanId == 0) return;
        var plan = _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
        if (plan is null || _planStatePending.Equals(plan.State, StringComparison.OrdinalIgnoreCase))
        {
            _planStateOpen = false;
            return;
        }
        _planStateSending = true;
        StateHasChanged();
        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var sessionPath = SessionState.SessionId.ToString();
            var userId      = SessionState.UserId.ToString();

            // Step 1: lock the plan (direct call â€” must succeed before batch)
            await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionPath}/api/Plan/Lock",
                new[]
                {
                    new KeyValuePair<string, string>("id",   _selectedPlanId.ToString()),
                    new KeyValuePair<string, string>("user", userId),
                });

            // Step 2: batch â€” unlock + set state (mirrors Unity AP_StateSelect.AcceptStatus)
            var batchRequests = System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new
                {
                    call_id       = 1,
                    endpoint      = "api/Plan/Unlock",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id           = _selectedPlanId,
                        force_unlock = 0,
                        user         = userId,
                    }),
                    group = 100,   // BATCH_GROUP_UNLOCK
                },
                new
                {
                    call_id       = 2,
                    endpoint      = "api/Plan/Message",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        plan      = _selectedPlanId,
                        team_id   = SessionState.CountryId,
                        user_name = SessionState.UserName,
                        text      = $"Changed the plans status to: {PlanStateLabel(_planStatePending)}",
                    }),
                    group = 5,     // BATCH_GROUP_PLAN_CHANGE
                },
                new
                {
                    call_id       = 3,
                    endpoint      = "api/Plan/SetState",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id    = _selectedPlanId,
                        state = _planStatePending,
                        user  = userId,
                    }),
                    group = 5,     // BATCH_GROUP_PLAN_CHANGE
                },
            });

            await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionPath}/api/Batch/ExecuteBatch",
                new[]
                {
                    new KeyValuePair<string, string>("country_id", SessionState.CountryId.ToString()),
                    new KeyValuePair<string, string>("user_id",    userId),
                    new KeyValuePair<string, string>("batch_guid", Guid.NewGuid().ToString()),
                    new KeyValuePair<string, string>("requests",   batchRequests),
                });

            _planStateOpen = false;
        }
        catch { /* Server state will correct on next WS update */ }
        finally
        {
            _planStateSending = false;
            StateHasChanged();
        }
    }

    private async Task VoteOnPlanAsync(int planId, int vote)
    {
        if (_sendingVote) return;
        _sendingVote = true;
        StateHasChanged();
        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{SessionState.SessionId}/api/Plan/Vote";
            await ApiClient.PostFormAsync(url, new[]
            {
                new KeyValuePair<string, string>("plan",    planId.ToString()),
                new KeyValuePair<string, string>("country", SessionState.CountryId.ToString()),
                new KeyValuePair<string, string>("vote",    vote.ToString()),
            });
        }
        catch { /* Server will correct state on next update */ }
        finally
        {
            _sendingVote = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Computes which country teams need to approve the plan and for what reasons,
    /// based on the approval type defined per layer_type (AllCountries / EEZ / NotDependent)
    /// and ownership of removed geometry derived from EEZ polygon intersection.
    /// </summary>
    private void CalculateApproval(PlanEntry plan)
    {
        _approvalRequired.Clear();
        var eezPolygons = GameState.EezPolygons;
        var planCountry = plan.Country;

        // countryId â†’ list of reason strings
        var reasons = new Dictionary<int, List<string>>();
        var allCountriesReasons = new List<string>(); // reasons that apply to every country

        void AddReason(int countryId, string reason)
        {
            if (countryId <= 0 || countryId == planCountry) return;
            if (!reasons.TryGetValue(countryId, out var list))
            {
                list = new List<string>();
                reasons[countryId] = list;
            }
            if (!list.Contains(reason)) list.Add(reason);
        }

        foreach (var planLayer in plan.Layers)
        {
            var layerEntry = _layerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId);
            var layerDisplayName = layerEntry?.DisplayName ?? planLayer.OriginalLayerId;

            // â”€â”€ Deleted geometry â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            if (planLayer.DeletedPersistentIds.Count > 0)
            {
                var baseGeoms = GetParsedLayerGeometries(planLayer.OriginalLayerId);
                foreach (var deletedId in planLayer.DeletedPersistentIds)
                {
                    var geom = baseGeoms.FirstOrDefault(g => g.FeatureId == deletedId);
                    if (geom is null) continue;

                    var typeApproval = GetApprovalForGeom(layerEntry, geom.TypeIndex);
                    var typeLabel    = GetTypeLabel(layerEntry, geom.TypeIndex);

                    if (string.Equals(typeApproval, "AllCountries", StringComparison.OrdinalIgnoreCase))
                    {
                        var r = string.IsNullOrEmpty(typeLabel)
                            ? $"Geometry was removed on the {layerDisplayName} layer, which requires approval from all countries."
                            : $"Geometry of type {typeLabel} was removed, which requires approval from all countries.";
                        if (!allCountriesReasons.Contains(r)) allCountriesReasons.Add(r);
                    }
                    else
                    {
                        // Derive ownership from EEZ intersection of the representative coordinate
                        if (geom.Coordinates.Count > 0)
                        {
                            var owner = GetCountryForCoordinate(geom.Coordinates[0], eezPolygons);
                            if (owner > 0 && owner != planCountry)
                                AddReason(owner, $"Geometry belonging to {GetCountryName(owner)} was removed on the {layerDisplayName} layer.");
                        }
                    }
                }
            }

            // â”€â”€ New / modified geometry â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            foreach (var geomItem in planLayer.Geometry)
            {
                var typeApproval = GetApprovalForGeom(layerEntry, geomItem.TypeIndex);
                var typeLabel    = GetTypeLabel(layerEntry, geomItem.TypeIndex);

                if (string.Equals(typeApproval, "AllCountries", StringComparison.OrdinalIgnoreCase))
                {
                    var r = string.IsNullOrEmpty(typeLabel)
                        ? $"Geometry was added or moved on the {layerDisplayName} layer, which requires approval from all countries."
                        : $"Geometry of type {typeLabel} was added or moved, which requires approval from all countries.";
                    if (!allCountriesReasons.Contains(r)) allCountriesReasons.Add(r);
                }
                else if (string.Equals(typeApproval, "EEZ", StringComparison.OrdinalIgnoreCase)
                         && geomItem.Coordinates.Count > 0)
                {
                    var pt = geomItem.Coordinates[0];
                    foreach (var eez in eezPolygons)
                    {
                        if (eez.CountryId != planCountry && PointInPolygon(pt, eez.Points))
                        {
                            var countryName = GetCountryName(eez.CountryId);
                            AddReason(eez.CountryId, $"Geometry on the {layerDisplayName} layer was added or altered in {countryName}'s EEZ.");
                        }
                    }
                }
            }
        }

        // AllCountries: add those reasons to every non-owner country team
        if (allCountriesReasons.Count > 0)
        {
            foreach (var kvp in _countryNames)
            {
                if (kvp.Key <= 2 || kvp.Key == planCountry) continue; // skip admin/GM slots
                foreach (var r in allCountriesReasons)
                    AddReason(kvp.Key, r);
            }
        }

        // Build sorted result
        foreach (var kvp in reasons.OrderBy(k => k.Key))
        {
            var name = GetCountryName(kvp.Key);
            var dot  = _countryColours.GetValueOrDefault(kvp.Key, "#6c757d");
            _approvalRequired.Add(new ApprovalRequirement(kvp.Key, name, kvp.Value));
        }
    }

    private static string GetApprovalForGeom(LayerEntry? layerEntry, int typeIndex)
    {
        if (layerEntry is null) return "NotDependent";
        if (typeIndex >= 0 && typeIndex < layerEntry.TypeDefs.Count)
            return layerEntry.TypeDefs[typeIndex].Approval;
        // Fallback: if there is exactly one type, use that regardless of index
        if (layerEntry.TypeDefs.Count == 1)
            return layerEntry.TypeDefs[0].Approval;
        return "NotDependent";
    }

    private static string GetTypeLabel(LayerEntry? layerEntry, int typeIndex)
    {
        if (layerEntry is null) return "";
        if (typeIndex >= 0 && typeIndex < layerEntry.TypeDefs.Count)
            return layerEntry.TypeDefs[typeIndex].Label;
        return "";
    }

    private string GetCountryName(int countryId) =>
        _countryNames.TryGetValue(countryId, out var n) ? n : $"Country {countryId}";

    private static int GetCountryForCoordinate(double[] pt, IReadOnlyList<EezPolygon> eezPolygons)
    {
        foreach (var eez in eezPolygons)
        {
            if (PointInPolygon(pt, eez.Points))
                return eez.CountryId;
        }
        return 0;
    }

    private IReadOnlyList<PlanMessageEntry> SelectedPlanMessages => GameState.GetPlanMessages(_selectedPlanId);

    private bool CanSendPlanMessage =>
        _selectedPlanId != 0 &&
        !_sendingPlanMessage &&
        !string.IsNullOrWhiteSpace(_planMessageDraft);

    private async Task SendPlanMessageAsync()
    {
        if (!CanSendPlanMessage) return;

        _sendingPlanMessage = true;
        _planMessageSendError = null;

        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{SessionState.SessionId}/api/Plan/Message";

            var fields = new List<KeyValuePair<string, string>>
            {
                new("plan", _selectedPlanId.ToString()),
                new("team_id", SessionState.CountryId.ToString()),
                new("user_name", string.IsNullOrWhiteSpace(SessionState.UserName) ? $"Team {SessionState.CountryId}" : SessionState.UserName),
                new("text", _planMessageDraft.Trim())
            };

            await ApiClient.PostFormAsync(url, fields);

            // Do not append locally. The authoritative message arrives via Game/Latest WebSocket.
            _planMessageDraft = string.Empty;
        }
        catch (Exception ex)
        {
            _planMessageSendError = ex.Message;
        }
        finally
        {
            _sendingPlanMessage = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task HandlePlanMessageKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await SendPlanMessageAsync();
    }

    private static string FormatPlanMessageTime(DateTime sentAt)
    {
        var utc = sentAt.Kind switch
        {
            DateTimeKind.Utc => sentAt,
            DateTimeKind.Local => sentAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(sentAt, DateTimeKind.Utc)
        };
        return utc.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
    }

    private string PlanMessageDotColour(int? countryId)
    {
        if (!countryId.HasValue || countryId.Value <= 0)
            return "#6c757d";
        if (countryId.Value == 1 || countryId.Value == 2)
            return "#ff69b4";
        return _countryColours.GetValueOrDefault(countryId.Value, "#6c757d");
    }

    /// <summary>
    /// Infers point/line/polygon from coordinate count when layer metadata is unavailable.
    /// </summary>
    private static string InferGeoType(IReadOnlyList<PlanGeometryItem> geometries)
    {
        if (geometries.Count == 0) return "point";
        var pts = geometries[0].Coordinates;
        if (pts.Count <= 1) return "point";
        if (pts.Count > 3)
        {
            var first = pts[0]; var last = pts[^1];
            if (Math.Abs(first[0] - last[0]) < 1.0 && Math.Abs(first[1] - last[1]) < 1.0)
                return "polygon";
        }
        return "line";
    }
}
