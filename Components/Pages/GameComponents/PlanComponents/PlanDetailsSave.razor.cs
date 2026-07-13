using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;
using MSPChallenge_Client_Browser.Utils.PlanCalculations;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanDetailsSave : GameComponentBase
{
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private IWebHostEnvironment HostEnvironment { get; set; } = null!;

    [Parameter] public string? EditName { get; set; }
    [Parameter] public string? EditDescription { get; set; }
    [Parameter] public int EditStartYear { get; set; }
    [Parameter] public int EditStartMonth { get; set; }
    [Parameter] public HashSet<string> EditPlanLayerIds { get; set; } = [];
    [Parameter] public HashSet<string> EditPolicyTypes { get; set; } = [];
    [Parameter] public EventCallback<string?> OnSaveError { get; set; }
    [Parameter] public EventCallback<bool> OnSavingChanged { get; set; }

    // ── Geometry editing state ────────────────────────────────────────────────
    private IJSObjectReference? _mapModule;
    private string?    _geometryEditedLayerId;
    private HashSet<string> _deletedWorldStateIds = [];

    // ── Save state ────────────────────────────────────────────────────────────
    private TaskCompletionSource<bool>? _editWsConfirmationTcs;
    private string? _pendingBatchGuid;
    private int     _pendingCreatePlanCallId;

    public void SetMapModule(IJSObjectReference? mapModule)
    {
        _mapModule = mapModule;
    }

    public void SetGeometryEditedLayerId(string? layerId)
    {
        _geometryEditedLayerId = layerId;
    }

    public void SetDeletedWorldStateIds(HashSet<string> deletedIds)
    {
        _deletedWorldStateIds = deletedIds;
    }

    public void ClearCaches()
    {
        GameSessionState.ClearGeometryCaches();
    }

    public async Task<bool> SavePlanAsync()
    {
        var isNewPlan = GameSessionState.SelectedPlanId == 0;
        var plan = isNewPlan ? null : GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
        
        if (!isNewPlan && plan is null)
        {
            await OnSaveError.InvokeAsync("Plan not found.");
            return false;
        }

        await OnSavingChanged.InvokeAsync(true);

        try
        {
            if (HostEnvironment.IsDevelopment())
            {
                Console.WriteLine($"[SavePlanAsync] Starting - IsNewPlan: {isNewPlan}, PlanId: {GameSessionState.SelectedPlanId}, EditedLayer: {_geometryEditedLayerId ?? "(none)"}");
            }

            var requests = new List<object>();
            var callId = 1;

            // ── For new plans: POST Plan/Post in group 1 ──────────────────
            int createPlanCallId = -1;
            if (isNewPlan)
            {
                createPlanCallId = callId++;
                requests.Add(new
                {
                    call_id = createPlanCallId,
                    endpoint = "api/Plan/Post",
                    endpoint_data = JsonSerializer.Serialize(new { country = UserSessionService.User.Country.Id }),
                    group = 1,
                });
            }

            // Helper: plan ID value (object: !Ref string for new, int for existing)
            object planIdVal = isNewPlan ? (object)$"!Ref:{createPlanCallId}" : (object)GameSessionState.SelectedPlanId!.Value;

            // ── Name, description, date (group 5) ─────────────────────────
            requests.Add(new
            {
                call_id = callId++,
                endpoint = "api/Plan/Name",
                endpoint_data = JsonSerializer.Serialize(new
                {
                    id = planIdVal,
                    name = EditName ?? string.Empty,
                }),
                group = 5,
            });
            requests.Add(new
            {
                call_id = callId++,
                endpoint = "api/Plan/Description",
                endpoint_data = JsonSerializer.Serialize(new
                {
                    id = planIdVal,
                    description = string.IsNullOrEmpty(EditDescription) ? " " : EditDescription,
                }),
                group = 5,
            });

            // StartDate is stored as month-offset from game start year
            var startMonthOffset = (EditStartYear - GameSessionState.GameStartYear) * 12 + EditStartMonth - 1;
            requests.Add(new
            {
                call_id = callId++,
                endpoint = "api/Plan/Date",
                endpoint_data = JsonSerializer.Serialize(new
                {
                    id = planIdVal,
                    date = startMonthOffset,
                }),
                group = 5,
            });

            // ── Layer changes ─────────────────────────────────────────────
            var originalLayerIds = plan?.Layers
                                        .Select(l => l.OriginalLayerId)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                   ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layersToAdd = EditPlanLayerIds.Except(originalLayerIds, StringComparer.OrdinalIgnoreCase).ToList();
            var layersToRemove = plan is not null
                ? originalLayerIds.Except(EditPlanLayerIds).ToList()
                : new List<string>();

            var newLayerCallIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var lid in layersToAdd)
            {
                if (!int.TryParse(lid, out var layerIdInt)) continue;
                var layerCallId = callId++;
                newLayerCallIds[lid] = layerCallId;
                requests.Add(new
                {
                    call_id = layerCallId,
                    endpoint = "api/Plan/Layer",
                    endpoint_data = JsonSerializer.Serialize(new { id = planIdVal, layerid = layerIdInt }),
                    group = 3,
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
                    call_id = callId++,
                    endpoint = "api/Plan/DeleteLayer",
                    endpoint_data = JsonSerializer.Serialize(new { id = plIdInt }),
                    group = 3,
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

                    if (!string.IsNullOrEmpty(overlayJson))
                    {
                        overlayItems = JsonSerializer.Deserialize<OverlayFeatureDto[]>(
                            overlayJson,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (HostEnvironment.IsDevelopment())
                            Console.WriteLine($"[SavePlanAsync] Deserialized {overlayItems?.Length ?? 0} overlay items");
                    }
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
                var planLayer = plan?.Layers.FirstOrDefault(l =>
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
                    var worldState = GameSessionState.GetProjectedLayerGeometries(editedLayerId, planStartDate);
                    var worldStateMap = new Dictionary<string, ParsedLayerGeometry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in worldState) worldStateMap[g.FeatureId] = g;

                    var overlayIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var item in overlayItems)
                    {
                        overlayIds.Add(item.FeatureId);

                        if (item.WorldStateId is not null)
                        {
                            // World-state feature — create plan geometry via POST + Data
                            double[][]? origCoords = item.OriginalCoords;
                            int? origTypeIndex = null;

                            // Always try to get original data from world state
                            if (worldStateMap.TryGetValue(item.WorldStateId, out var wsGeo))
                            {
                                if (origCoords is null)
                                    origCoords = wsGeo.Coordinates.Select(c => new[] { c[0], c[1] }).ToArray();
                                origTypeIndex = wsGeo.TypeIndex;
                            }

                            var coordsChanged = origCoords is null || GeometryUtils.CoordsChanged(item.Coords, origCoords);
                            var typeChanged = origTypeIndex.HasValue && item.TypeIndex != origTypeIndex.Value;

                            // Skip only if NEITHER coords NOR type changed
                            if (!coordsChanged && !typeChanged)
                                continue;

                            object persistentVal = int.TryParse(item.WorldStateId, out var wsInt)
                                ? (object)wsInt : item.WorldStateId;
                            var geoCallId = callId++;
                            requests.Add(new
                            {
                                call_id = geoCallId,
                                endpoint = "api/Geometry/Post",
                                endpoint_data = JsonSerializer.Serialize(new
                                {
                                    geometry = JsonSerializer.Serialize(item.Coords),
                                    country = UserSessionService.User.Country.Id,
                                    layer = planLayerVal,
                                    plan = planIdVal,
                                    persistent = persistentVal,
                                }),
                                group = 5,
                            });
                            requests.Add(new
                            {
                                call_id = callId++,
                                endpoint = "api/Geometry/Data",
                                endpoint_data = JsonSerializer.Serialize(new
                                {
                                    id = $"!Ref:{geoCallId}",
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
                            var coordsChanged = GeometryUtils.CoordsChanged(item.Coords, origCoords);
                            var typeChanged = item.TypeIndex != planGeo.TypeIndex;

                            if (!coordsChanged && !typeChanged) continue;

                            object featureIdVal = int.TryParse(item.FeatureId, out var fidInt)
                                ? (object)fidInt : item.FeatureId;

                            // Update coordinates if changed
                            if (coordsChanged)
                            {
                                requests.Add(new
                                {
                                    call_id = callId++,
                                    endpoint = "api/Geometry/Update",
                                    endpoint_data = JsonSerializer.Serialize(new
                                    {
                                        id = featureIdVal,
                                        country = UserSessionService.User.Country.Id,
                                        geometry = JsonSerializer.Serialize(item.Coords),
                                    }),
                                    group = 5,
                                });
                            }

                            // Update type if changed
                            if (typeChanged)
                            {
                                requests.Add(new
                                {
                                    call_id = callId++,
                                    endpoint = "api/Geometry/Data",
                                    endpoint_data = JsonSerializer.Serialize(new
                                    {
                                        id = featureIdVal,
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
                                call_id = geoCallId,
                                endpoint = "api/Geometry/Post",
                                endpoint_data = JsonSerializer.Serialize(new
                                {
                                    geometry = JsonSerializer.Serialize(item.Coords),
                                    country = UserSessionService.User.Country.Id,
                                    layer = planLayerVal,
                                    plan = planIdVal,
                                }),
                                group = 5,
                            });
                            requests.Add(new
                            {
                                call_id = callId++,
                                endpoint = "api/Geometry/Data",
                                endpoint_data = JsonSerializer.Serialize(new
                                {
                                    id = $"!Ref:{geoCallId}",
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
                            call_id = callId++,
                            endpoint = "api/Geometry/Delete",
                            endpoint_data = JsonSerializer.Serialize(new { id = featureIdVal }),
                            group = 4,
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
                            call_id = callId++,
                            endpoint = "api/Geometry/MarkForDelete",
                            endpoint_data = JsonSerializer.Serialize(new
                            {
                                id = wsIdVal,
                                plan = planIdVal,
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
                                    call_id = callId++,
                                    endpoint = "api/Geometry/UnmarkForDelete",
                                    endpoint_data = JsonSerializer.Serialize(new
                                    {
                                        id = wsIdVal,
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
            var batchJson = JsonSerializer.Serialize(requests);
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
                "api/Batch/ExecuteBatch",
                new[]
                {
                    new KeyValuePair<string, string>("country_id", UserSessionService.User.Country.Id.ToString()),
                    new KeyValuePair<string, string>("user_id", UserSessionService.User.Id.ToString()),
                    new KeyValuePair<string, string>("batch_guid", batchGuid),
                    new KeyValuePair<string, string>("requests", batchJson),
                });

            if (HostEnvironment.IsDevelopment())
                Console.WriteLine("[SavePlanAsync] Batch POST completed, waiting for WS confirmation...");

            // Batch accepted — wait for WS confirmation (up to 10 s)
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _editWsConfirmationTcs = tcs;

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
                    "api/Plan/Unlock",
                    new[]
                    {
                        new KeyValuePair<string, string>("id", GameSessionState.SelectedPlanId.ToString()!),
                        new KeyValuePair<string, string>("force_unlock", "0"),
                        new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                    });
            }

            // Stop geometry editing if active
            if (_geometryEditedLayerId is not null && _mapModule is not null)
                await _mapModule.InvokeVoidAsync("stopGeometryEditing");

            _editWsConfirmationTcs = null;
            _geometryEditedLayerId = null;
            _deletedWorldStateIds.Clear();

            await OnSavingChanged.InvokeAsync(false);
            return true;
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

            await OnSaveError.InvokeAsync(errorMsg);
            await OnSavingChanged.InvokeAsync(false);
            return false;
        }
    }
}
