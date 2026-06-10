using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class PlanDetails
{
    [CascadingParameter] public PlanControl PlanController { get; set; } = null!;
    public PlanNameDesc PlanNameDescInstance { get; set; } = null!;
    public PlanStartDate PlanStartDateInstance { get; set; } = null!;
    public PlanPolicies PlanPoliciesInstance { get; set; } = null!;
    public PlanLayers PlanLayersInstance { get; set; } = null!;
    public PlanMessages PlanMessagesInstance { get; set; } = null!;
    public PlanIssues PlanIssuesInstance { get; set; } = null!;


    public List<PlanRestrictionIssue> SelectedPlanIssues       = [];
    private PlanEntry _detailPlan = null!;
    private List<string> _detailLayers = [];
    private string _detailDotColour = "#6c757d";
    private string _detailCountryName = "Unknown Country";
    
    
    private string? _editError;

    private bool    _pendingEnterEditMode;

    private string? _editName;
    private string? _editDescription;
    private int     _editStartYear;
    private int     _editStartMonth;
    private int     _editMinConstructionMonths;
    private HashSet<string> _editPlanLayerIds = [];
    private HashSet<string> _editPolicyTypes  = [];
    private string? _enterEditError;
    private TaskCompletionSource<bool>? _editWsConfirmationTcs;

    private int     _pendingSelectPlanId;
    private string? _pendingBatchGuid;
    private int     _pendingCreatePlanCallId;

    private Dictionary<int, string>    _planIssueSeverity        = [];

    protected override void OnInitialized()
    {
        _detailPlan = GetDetailPlan();
        _detailLayers = _detailPlan.Layers
            .Select(l => GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == l.OriginalLayerId)?.DisplayName)
            .OfType<string>()
            .Distinct()
            .ToList();
        _detailDotColour = (_detailPlan.Country == 1 || _detailPlan.Country == 2)
            ? "#ff69b4"
            : GameSessionState.CountryColours.GetValueOrDefault(_detailPlan.Country, "#6c757d");
        _detailCountryName = GameSessionState.CountryNames.GetValueOrDefault(_detailPlan.Country, $"Country {_detailPlan.Country}");
    }

    private async Task ClosePlanDetailAsync()
    {
        if (GameSessionState.EditPlanMode)
            await CancelEditAsync();
        var plan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        if (plan is not null)
            await SelectPlanAsync(plan);
    }

    private bool CanEnterEditMode =>
        GameSessionState.SelectedPlanId != 0 &&
        PlanController.SelectedPlan != null &&
        PlanController.SelectedPlan.State.Equals("DESIGN", StringComparison.OrdinalIgnoreCase) &&
        (UserSessionService.User.CountryId <= 2 || PlanController.SelectedPlan.Country == UserSessionService.User.CountryId);

    private async Task EnterEditModeAsync()
    {
        // Always fetch the latest plan from _plans
        PlanController.FetchSelectedPlan();
        if (PlanController.SelectedPlan is null)
        {
            _enterEditError = "Selected plan not found. It may have been deleted or modified by another user. Please select the plan again.";
            _pendingEnterEditMode = true;
            StateHasChanged();
            return;
        }
        // Proactively check if plan is locked by another user
        if (PlanController.SelectedPlan.LockedByUserId != 0 && PlanController.SelectedPlan.LockedByUserId != UserSessionService.User.Id)
        {
            _enterEditError = "This plan is currently being edited by another user.";
            StateHasChanged();
            return;
        }
        _enterEditError = null;

        try
        {
            await ApiClient.PostFormAsync("Plan/Lock",
                new[]
                {
                    new KeyValuePair<string, string>("id",   PlanController.SelectedPlan.Id.ToString()),
                    new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                });
        }
        catch
        {
            // Lock failed (plan locked by someone else)
            _enterEditError = "This plan is currently being edited by another user.";
            StateHasChanged();
            return;
        }
        PlanController.EditMode = true;
    }

    private PlanEntry GetDetailPlan()
    {
        var plan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        if (plan != null) return plan;
        return new PlanEntry(
            0, // PlanId (new/unsaved)
            _editName ?? string.Empty,
            _editDescription ?? string.Empty,
            string.Empty, // State
            UserSessionService.CountryId,
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

    private async Task CancelEditAsync()
    {
        PlanController.EditMode = false;
        _editSaving = true;
        StateHasChanged();
        try
        {
            await PlanController.Map.MapJSModule.InvokeVoidAsync("stopGeometryEditing");
            await ApiClient.PostFormAsync("Plan/Unlock",
                new[]
                {
                    new KeyValuePair<string, string>("id", GameSessionState.SelectedPlanId.ToString()),
                    new KeyValuePair<string, string>("force_unlock", "0"),
                    new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                });
        }
        catch { }
        finally
        {
            PlanController.EditMode = false;
            _pendingBatchGuid = null;
            _pendingCreatePlanCallId = 0;
            _editSaving = false;
            _editError  = null;
            StateHasChanged();
        }
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

        _editSaving = true;
        _editError  = null;
        StateHasChanged();

        try
        {
            var isNewPlan   = _selectedPlanId == 0;
            var plan        = isNewPlan ? null : _plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
            if (!isNewPlan && plan is null)
            {
                _editError = "Plan not found.";
                return;
            }

            if (HostEnvironment.IsDevelopment())
            {
                Console.WriteLine($"[SavePlanAsync] Starting - IsNewPlan: {isNewPlan}, PlanId: {_selectedPlanId}, EditedLayer: {_geometryEditedLayerId ?? "(none)"}");
            }

            var baseAddress  = SessionState.GameServerAddress.TrimEnd('/');
            var sessionPath  = SessionState.SessionId.ToString();
            var userId       = SessionState.UserId.ToString();
            var countryInt   = SessionState.CountryId;
            var requests     = new List<object>();
            var callId       = 1;

            // ── For new plans: POST Plan/Post in group 1 ──────────────────
            // The new plan ID is referenced by other requests via "!Ref:N".
            int createPlanCallId = -1;
            if (isNewPlan)
            {
                createPlanCallId = callId++;
                requests.Add(new
                {
                    call_id       = createPlanCallId,
                    endpoint      = "api/Plan/Post",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { country = countryInt }),
                    group         = 1,
                });
            }

            // Helper: plan ID value (object: !Ref string for new, int for existing)
            object planIdVal = isNewPlan ? (object)$"!Ref:{createPlanCallId}" : _selectedPlanId;

            // ── Name, description, date (group 5 — same as BATCH_GROUP_PLAN_CHANGE) ─
            requests.Add(new
            {
                call_id       = callId++,
                endpoint      = "api/Plan/Name",
                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id   = planIdVal,
                    name = _editName ?? string.Empty,
                }),
                group = 5,
            });
            requests.Add(new
            {
                call_id       = callId++,
                endpoint      = "api/Plan/Description",
                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id          = planIdVal,
                    description = string.IsNullOrEmpty(_editDescription) ? " " : _editDescription,
                }),
                group = 5,
            });
            // StartDate is stored as month-offset from game start year
            var startMonthOffset = (_editStartYear - _gameStartYear) * 12 + _editStartMonth - 1;
            requests.Add(new
            {
                call_id       = callId++,
                endpoint      = "api/Plan/Date",
                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id   = planIdVal,
                    date = startMonthOffset,
                }),
                group = 5,
            });

            // ── Layer changes ─────────────────────────────────────────────
            var originalLayerIds = plan?.Layers
                                        .Select(l => l.OriginalLayerId)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                   ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layersToAdd    = _editPlanLayerIds.Except(originalLayerIds, StringComparer.OrdinalIgnoreCase).ToList();
            var layersToRemove = plan is not null
                ? originalLayerIds.Except(_editPlanLayerIds).ToList()
                : new List<string>();

            var newLayerCallIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var lid in layersToAdd)
            {
                if (!int.TryParse(lid, out var layerIdInt)) continue;
                var layerCallId = callId++;
                newLayerCallIds[lid] = layerCallId;
                requests.Add(new
                {
                    call_id       = layerCallId,
                    endpoint      = "api/Plan/Layer",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { id = planIdVal, layerid = layerIdInt }),
                    group         = 3,
                });
            }

            var planLayerIdByOriginal = plan?.Layers
                .ToDictionary(l => l.OriginalLayerId, l => l.LayerId, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lid in layersToRemove)
            {
                if (!planLayerIdByOriginal.TryGetValue(lid, out var plId)) continue;
                if (!int.TryParse(plId, out var plIdInt)) continue;
                requests.Add(new
                {
                    call_id       = callId++,
                    endpoint      = "api/Plan/DeleteLayer",
                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { id = plIdInt }),
                    group         = 3,
                });
            }

            // ── Fetch overlay features from JS ────────────────────────────
            OverlayFeatureDto[]? overlayItems = null;
            if (_geometryEditedLayerId is not null && _mapModule is not null)
            {
                if (HostEnvironment.IsDevelopment())
                    Console.WriteLine($"[SavePlanAsync] Fetching overlay features for layer {_geometryEditedLayerId}...");
                
                try
                {
                    var overlayJson = await _mapModule.InvokeAsync<string>(
                        "getOverlayFeaturesJson", 
                        _geometryEditedLayerId);
                    
                    if (HostEnvironment.IsDevelopment())
                        Console.WriteLine($"[SavePlanAsync] Received overlay JSON ({overlayJson?.Length ?? 0} chars)");
                    
                    overlayItems = System.Text.Json.JsonSerializer.Deserialize<OverlayFeatureDto[]>(
                        overlayJson,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (HostEnvironment.IsDevelopment())
                        Console.WriteLine($"[SavePlanAsync] Deserialized {overlayItems?.Length ?? 0} overlay items");
                }
                catch (TaskCanceledException tcex)
                {
                    if (HostEnvironment.IsDevelopment())
                    {
                        Console.WriteLine($"[SavePlanAsync] JS call was canceled!");
                        Console.WriteLine($"[SavePlanAsync] TaskCanceledException: {tcex.Message}");
                        Console.WriteLine($"[SavePlanAsync] Stack: {tcex.StackTrace}");
                    }
                    throw;
                }
                catch (Exception jsEx)
                {
                    if (HostEnvironment.IsDevelopment())
                    {
                        Console.WriteLine($"[SavePlanAsync] JS call failed: {jsEx.GetType().Name}");
                        Console.WriteLine($"[SavePlanAsync] Message: {jsEx.Message}");
                    }
                    throw;
                }
            }

            // ── Geometry changes (groups 5 / 10) ─────────────────────────
            if (_geometryEditedLayerId is not null && overlayItems is not null)
            {
                var editedLayerId = _geometryEditedLayerId;
                var planLayer     = plan?.Layers.FirstOrDefault(l =>
                    string.Equals(l.OriginalLayerId, editedLayerId, StringComparison.OrdinalIgnoreCase));

                // planLayerVal: object (!Ref string for new layers, int for existing)
                object? planLayerVal = null;
                if (planLayer is not null && int.TryParse(planLayer.LayerId, out var existingLayerIdInt))
                    planLayerVal = existingLayerIdInt;
                else if (newLayerCallIds.TryGetValue(editedLayerId, out var newLcid))
                    planLayerVal = $"!Ref:{newLcid}";



                if (planLayerVal is not null)
                {
                    var planOwnGeo = new Dictionary<string, PlanGeometryItem>(StringComparer.OrdinalIgnoreCase);
                    if (planLayer is not null)
                        foreach (var g in planLayer.Geometry) planOwnGeo[g.Id] = g;

                    // For new plans the world state is empty (plan.StartDate == 0 default), use offset
                    int planStartDate = plan?.StartDate ?? startMonthOffset;
                    var worldState    = GetProjectedLayerGeometries(editedLayerId, planStartDate);
                    var worldStateMap = new Dictionary<string, ParsedLayerGeometry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in worldState) worldStateMap[g.FeatureId] = g;

                    var overlayIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var item in overlayItems)
                    {
                        overlayIds.Add(item.FeatureId);

                        if (item.WorldStateId is not null)
                        {
                            // World-state feature — create plan geometry via POST + Data
                            // This is needed whenever coords OR type change from the base world-state
                            double[][]? origCoords = item.OriginalCoords;
                            int? origTypeIndex = null;
                            
                            // Always try to get original data from world state
                            if (worldStateMap.TryGetValue(item.WorldStateId, out var wsGeo))
                            {
                                if (origCoords is null)
                                    origCoords = wsGeo.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray();
                                origTypeIndex = wsGeo.TypeIndex;
                            }
                            
                            var coordsChanged = origCoords is null || GeometryCoordsChanged(item.Coords, origCoords);
                            var typeChanged = origTypeIndex.HasValue && item.TypeIndex != origTypeIndex.Value;
                            
                            // Skip only if NEITHER coords NOR type changed
                            if (!coordsChanged && !typeChanged)
                                continue;

                            object persistentVal = int.TryParse(item.WorldStateId, out var wsInt)
                                ? (object)wsInt : item.WorldStateId;
                            var geoCallId = callId++;
                            requests.Add(new
                            {
                                call_id       = geoCallId,
                                endpoint      = "api/Geometry/Post",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    geometry   = System.Text.Json.JsonSerializer.Serialize(item.Coords),
                                    country    = countryInt,
                                    layer      = planLayerVal,
                                    plan       = planIdVal,
                                    persistent = persistentVal,
                                }),
                                group = 5,
                            });
                            requests.Add(new
                            {
                                call_id       = callId++,
                                endpoint      = "api/Geometry/Data",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    id   = $"!Ref:{geoCallId}",
                                    data = "{}",
                                    type = item.TypeIndex.ToString(),
                                }),
                                group = 10,
                            });
                        }
                        else if (planOwnGeo.TryGetValue(item.FeatureId, out var planGeo))
                        {
                            // Existing plan geometry — update if coordinates or type changed
                            var origCoords = planGeo.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray();
                            var coordsChanged = GeometryCoordsChanged(item.Coords, origCoords);
                            var typeChanged = item.TypeIndex != planGeo.TypeIndex;
                            
                            if (!coordsChanged && !typeChanged) continue;

                            object featureIdVal = int.TryParse(item.FeatureId, out var fidInt)
                                ? (object)fidInt : item.FeatureId;
                            
                            // Update coordinates if changed
                            if (coordsChanged)
                            {
                                requests.Add(new
                                {
                                    call_id       = callId++,
                                    endpoint      = "api/Geometry/Update",
                                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        id       = featureIdVal,
                                        country  = countryInt,
                                        geometry = System.Text.Json.JsonSerializer.Serialize(item.Coords),
                                    }),
                                    group = 5,
                                });
                            }
                            
                            // Update type if changed
                            if (typeChanged)
                            {
                                requests.Add(new
                                {
                                    call_id       = callId++,
                                    endpoint      = "api/Geometry/Data",
                                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        id   = featureIdVal,
                                        data = "{}",
                                        type = item.TypeIndex.ToString(),
                                    }),
                                    group = 10,
                                });
                            }
                        }
                        else
                        {
                            // New feature (TempId) — POST + Data
                            var geoCallId = callId++;
                            requests.Add(new
                            {
                                call_id       = geoCallId,
                                endpoint      = "api/Geometry/Post",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    geometry = System.Text.Json.JsonSerializer.Serialize(item.Coords),
                                    country  = countryInt,
                                    layer    = planLayerVal,
                                    plan     = planIdVal,
                                }),
                                group = 5,
                            });
                            requests.Add(new
                            {
                                call_id       = callId++,
                                endpoint      = "api/Geometry/Data",
                                endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    id   = $"!Ref:{geoCallId}",
                                    data = "{}",
                                    type = item.TypeIndex.ToString(),
                                }),
                                group = 10,
                            });
                        }
                    }

                    // Deleted plan-own geometry
                    foreach (var (geomId, _) in planOwnGeo)
                    {
                        if (overlayIds.Contains(geomId)) continue;
                        object featureIdVal = int.TryParse(geomId, out var gidInt)
                            ? (object)gidInt : geomId;
                        requests.Add(new
                        {
                            call_id       = callId++,
                            endpoint      = "api/Geometry/Delete",
                            endpoint_data = System.Text.Json.JsonSerializer.Serialize(new { id = featureIdVal }),
                            group         = 4,
                        });
                    }

                    // Deleted world-state features
                    foreach (var wsId in _deletedWorldStateIds)
                    {
                        bool replaced = overlayItems.Any(o =>
                            string.Equals(o.WorldStateId, wsId, StringComparison.OrdinalIgnoreCase));
                        if (replaced) continue;

                        object wsIdVal = int.TryParse(wsId, out var wsInt2)
                            ? (object)wsInt2 : wsId;
                        requests.Add(new
                        {
                            call_id       = callId++,
                            endpoint      = "api/Geometry/MarkForDelete",
                            endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                id    = wsIdVal,
                                plan  = planIdVal,
                                layer = planLayerVal,
                            }),
                            group = 4,
                        });
                    }
                    
                    // Restored world-state features (previously deleted, now undeleted)
                    if (planLayer is not null)
                    {
                        var currentlyDeletedIds = planLayer.DeletedPersistentIds ?? [];
                        foreach (var deletedId in currentlyDeletedIds)
                        {
                            // If this ID was in plan's deletedIds but is NOT in our current _deletedWorldStateIds,
                            // it means the user restored it in this edit session
                            if (!_deletedWorldStateIds.Contains(deletedId))
                            {
                                object wsIdVal = int.TryParse(deletedId, out var wsInt3)
                                    ? (object)wsInt3 : deletedId;
                                requests.Add(new
                                {
                                    call_id       = callId++,
                                    endpoint      = "api/Geometry/UnmarkForDelete",
                                    endpoint_data = System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        id   = wsIdVal,
                                        plan = planIdVal,
                                    }),
                                    group = 4,
                                });
                            }
                        }
                    }
                }
            }

            // ── Send the single batch ─────────────────────────────────────
            var batchJson = System.Text.Json.JsonSerializer.Serialize(requests);
            var batchGuid = Guid.NewGuid().ToString();
            
            if (HostEnvironment.IsDevelopment())
            {
                Console.WriteLine($"[SavePlanAsync] Sending batch with {requests.Count} requests");
                Console.WriteLine($"[SavePlanAsync] Batch JSON length: {batchJson.Length} chars");
                Console.WriteLine($"[SavePlanAsync] Batch GUID: {batchGuid}");
            }

            // Store batch info for new plan tracking
            if (isNewPlan)
            {
                _pendingBatchGuid = batchGuid;
                _pendingCreatePlanCallId = createPlanCallId;
            }

            await ApiClient.PostFormAsync(
                $"{baseAddress}/{sessionPath}/api/Batch/ExecuteBatch",
                new[]
                {
                    new KeyValuePair<string, string>("country_id", SessionState.CountryId.ToString()),
                    new KeyValuePair<string, string>("user_id",    userId),
                    new KeyValuePair<string, string>("batch_guid", batchGuid),
                    new KeyValuePair<string, string>("requests",   batchJson),
                });

            if (HostEnvironment.IsDevelopment())
                Console.WriteLine("[SavePlanAsync] Batch POST completed, waiting for WS confirmation...");

            // Batch accepted — wait for WS confirmation (up to 10 s)
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _editWsConfirmationTcs = tcs;
            _editSaving = false;
            StateHasChanged();

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10_000));

            if (HostEnvironment.IsDevelopment())
            {
                if (completedTask == tcs.Task)
                    Console.WriteLine("[SavePlanAsync] WS confirmation received");
                else
                    Console.WriteLine("[SavePlanAsync] WS confirmation timeout");
            }


            // ── Unlock existing plan (separate call AFTER batch completes) ──
            if (!isNewPlan)
            {

                await ApiClient.PostFormAsync(
                    $"{baseAddress}/{sessionPath}/api/Plan/Unlock",
                    new[]
                    {
                        new KeyValuePair<string, string>("id",           _selectedPlanId.ToString()),
                        new KeyValuePair<string, string>("force_unlock", "0"),
                        new KeyValuePair<string, string>("user",         SessionState.UserId.ToString()),
                    });
            }
            
            // Stop geometry editing if active
            if (_geometryToolLayerId is not null && _mapModule is not null)
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");
                
            _editWsConfirmationTcs              = null;
            _geometryToolLayerId                = null;
            _geometryEditedLayerId              = null;
            _geometryToolSelectedFeatureId      = null;
            _geometryToolSelectedWorldStateId   = null;
            _drawingUndoStack.Clear();
            _drawingRedoStack.Clear();
            _deletedWorldStateIds.Clear();
            _editMode = false;

            // After save, keep plan selected in view mode
            // For new plans, _pendingBatchGuid and _pendingCreatePlanCallId are already set above
            // For existing plans, the plan is already selected, so no action needed
            
            // Recalculate restriction issues and approval requirements
            await RecalculatePlanIssuesAndApprovalAsync();
        }
        catch (Exception ex)
        {
            if (HostEnvironment.IsDevelopment())
            {
                Console.WriteLine($"[SavePlanAsync] EXCEPTION: {ex.GetType().Name}");
                Console.WriteLine($"[SavePlanAsync] Message: {ex.Message}");
                Console.WriteLine($"[SavePlanAsync] Stack: {ex.StackTrace}");
                if (ex.InnerException is not null)
                    Console.WriteLine($"[SavePlanAsync] Inner: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
            }

            var errorMsg = HostEnvironment.IsDevelopment() 
                ? $"Save failed: {ex.GetType().Name}: {ex.Message}" 
                : $"Save failed: {ex.Message}";
            
            _editError = errorMsg;
        }
        finally
        {
            _editSaving = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Recalculates restriction issues and approval requirements for the currently selected plan.
    /// Should be called after saving a plan to update the issues list and approval badges.
    /// </summary>
    private async Task RecalculatePlanIssuesAndApprovalAsync()
    {
        if (GameSessionState.SelectedPlanId == 0) return;
        var plan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        if (plan is null) return;

        // Clear and recalculate issues
        SelectedPlanIssues.Clear();

        foreach (var planLayer in plan.Layers)
        {
            var geoType = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId)?.GeoType
                       ?? GenericUtils.InferGeoType(planLayer.Geometry);

            foreach (var geometry in planLayer.Geometry)
            {
                var isNewGeometry = string.IsNullOrEmpty(geometry.PersistentId) || geometry.Id == geometry.PersistentId;
                var geometryIssues = EvaluateRestrictionsForGeometry(planLayer.OriginalLayerId, geoType, geometry, isNewGeometry, plan.StartDate);
                SelectedPlanIssues.AddRange(geometryIssues);
            }
        }

        SelectedPlanIssues.Sort((a, b) =>
        {
            var s = GenericUtils.SeveritySortRank(b.Severity).CompareTo(GenericUtils.SeveritySortRank(a.Severity));
            if (s != 0) return s;
            s = string.Compare(a.TargetLayer, b.TargetLayer, StringComparison.OrdinalIgnoreCase);
            return s != 0 ? s : string.Compare(a.Message, b.Message, StringComparison.OrdinalIgnoreCase);
        });

        // Update severity badge
        if (SelectedPlanIssues.Count > 0)
            _planIssueSeverity[plan.PlanId] = GenericUtils.NormaliseSeverity(SelectedPlanIssues[0].Severity);
        else
            _planIssueSeverity.Remove(plan.PlanId);

        // Recalculate approval if not in a completed state
        if (!GenericUtils.IsApprovalCompleteState(plan.State))
            CalculateApproval(plan);

        // Update the plan geometry overlay with new restriction markers
        if (PlanPanelsController.Map.MapJSModule is not null)
        {
            var layersData = new List<object>();
            foreach (var planLayer in plan.Layers)
            {
                var geoType = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == planLayer.OriginalLayerId)?.GeoType
                           ?? GenericUtils.InferGeoType(planLayer.Geometry);

                var geometries = new List<object>();
                foreach (var geometry in planLayer.Geometry)
                {
                    var isNewGeometry = string.IsNullOrEmpty(geometry.PersistentId) || geometry.Id == geometry.PersistentId;
                    var geometryIssues = SelectedPlanIssues.Where(i =>
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
                                severity = GenericUtils.NormaliseSeverity(r.Severity),
                                message = r.Message,
                                sourceLayer = r.SourceLayer,
                                targetLayer = r.TargetLayer,
                                changeKind = r.ChangeKind,
                                coord = new[] { r.MarkerX, r.MarkerY }
                            })
                            .ToArray(),
                        restrictions = geometryIssues
                            .Select(r => GenericUtils.NormaliseSeverity(r.Severity))
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
                await PlanPanelsController.Map.MapJSModule.InvokeVoidAsync("showPlanGeometry", JsonSerializer.Serialize(layersData));
        }

        StateHasChanged();
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

            // Deleted geometry 
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

            // New / modified geometry 
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

    private IReadOnlyList<PlanRestrictionIssue> EvaluateRestrictionsForGeometry(
        string sourceLayerId,
        string sourceGeoType,
        PlanGeometryItem geometry,
        bool isNewGeometry,
        int planStartDate)
    {
        if (GameSessionState.Restrictions.Count == 0 || geometry.Coordinates.Count == 0)
            return [];

        var sourceLayer = GameSessionState.LayerEntries.FirstOrDefault(l => string.Equals(l.LayerId, sourceLayerId, StringComparison.OrdinalIgnoreCase));
        if (sourceLayer is null)
            return [];

        var matches = new List<PlanRestrictionIssue>();
        // Deduplicate: same message at the same map position is considered one issue regardless
        // of whether it comes from duplicate restriction rules or coincident target geometries.
        var seenIssueKeys = new HashSet<(string severity, string message, string src, string tgt, double x, double y)>();

        foreach (var rule in GameSessionState.Restrictions)
        {
            if (!RuleLayerMatches(rule.StartLayer, sourceLayer)
                || !RestrictionTypeMatches(rule.StartType, geometry.TypeIndex, sourceLayer))
                continue;

            var targetLayer = FindLayerByRuleName(rule.EndLayer);
            var targetType = rule.EndType;

            if (targetLayer is null || targetLayer.IsRaster)
                continue;

            var targetGeometries = GetProjectedLayerGeometries(targetLayer.LayerId, planStartDate);
            var constraintSort = GenericUtils.NormaliseConstraintSort(rule.Sort);
            var sourceMarker = GenericUtils.GetGeometryCenter(geometry.Coordinates);
            var overlapFound = false;

            foreach (var targetGeometry in targetGeometries)
            {
                if (!RestrictionTypeMatches(targetType, targetGeometry.TypeIndex, targetLayer))
                    continue;

                if (!HasOverlap(sourceGeoType, geometry.Coordinates, targetLayer.GeoType, targetGeometry.Coordinates))
                    continue;

                overlapFound = true;

                // Unity inclusion constraints emit one issue per overlapping target geometry.
                if (constraintSort == "EXCLUSION")
                    continue;

                var severity = GenericUtils.NormaliseSeverity(rule.Type);
                if (string.IsNullOrEmpty(severity))
                    severity = "WARNING";
                var message = string.IsNullOrWhiteSpace(rule.Message)
                    ? $"Overlap with {targetLayer.DisplayName}"
                    : rule.Message;
                var marker = GetGeometryCenter(targetGeometry.Coordinates);
                var key = (severity, message, sourceLayer.DisplayName, targetLayer.DisplayName, marker[0], marker[1]);
                if (seenIssueKeys.Add(key))
                    matches.Add(new PlanRestrictionIssue(
                        severity,
                        message,
                        sourceLayer.DisplayName,
                        targetLayer.DisplayName,
                        isNewGeometry ? "New geometry" : "Changed geometry",
                        marker[0],
                        marker[1]));
            }

            // Unity exclusion constraints add an issue only when no overlap was found.
            if (constraintSort == "EXCLUSION" && !overlapFound)
            {
                var severity = GenericUtils.NormaliseSeverity(rule.Type);
                if (string.IsNullOrEmpty(severity))
                    severity = "WARNING";
                var message = string.IsNullOrWhiteSpace(rule.Message)
                    ? $"No valid overlap with {targetLayer.DisplayName}"
                    : rule.Message;
                var key = (severity, message, sourceLayer.DisplayName, targetLayer.DisplayName, sourceMarker[0], sourceMarker[1]);
                if (seenIssueKeys.Add(key))
                    matches.Add(new PlanRestrictionIssue(
                        severity,
                        message,
                        sourceLayer.DisplayName,
                        targetLayer.DisplayName,
                        isNewGeometry ? "New geometry" : "Changed geometry",
                        sourceMarker[0],
                        sourceMarker[1]));
            }
        }

        return matches;
    }


}