using System.Text.Json;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils.PlanCalculations;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class MapViewPort
{
    public required IJSObjectReference MapJSModule;
    
    // Public method for UI components
    public Task ToggleLayerAsync(Layer entry, bool visible) 
        => ToggleLayerInternalAsync(entry, visible, skipProjection: false);
    
    // Internal method with skipProjection parameter for bulk operations
    internal async Task ToggleLayerInternalAsync(Layer entry, bool visible, bool skipProjection)
    {
        entry.Visible = visible;

        if (visible)
        {
            // Hidden layers are not materialized on warm return until user enables them.
            await EnsureLayerRenderedAsync(entry.LayerId, visible: true);

            // If a plan is currently selected, immediately project prior-plan changes onto
            // this layer so the user sees the correct world state without re-selecting the plan.
            if (!skipProjection && GameUIStateService.SelectedPlanId != 0)
            {
                var viewedPlan = GameSessionService.Plans.FirstOrDefault(p => p.PlanId == GameUIStateService.SelectedPlanId);
                if (viewedPlan is not null)
                    await ApplyPlanProjectionAsync(viewedPlan.StartDate, entry.LayerId, currentPlan: viewedPlan);
            }
        }
        else if (MapJSModule is not null)
        {
            await MapJSModule.InvokeVoidAsync("setLayerVisible", entry.LayerId, false);
        }

        if (visible && !entry.IsBaseLayer && !GameUIStateService.LegendOrderLayerIds.Contains(entry.LayerId))
            GameUIStateService.LegendOrderLayerIds.Add(entry.LayerId);
        else if (!visible)
            GameUIStateService.LegendOrderLayerIds.Remove(entry.LayerId);

        await SyncZIndicesAsync();
        GameSessionService.NotifyChanged();
    }

    private async Task EnsureLayerRenderedAsync(string layerId, bool visible)
    {
        if (MapJSModule is null) return;

        var snapshot = GameSessionService.MapLayerSnapshots.FirstOrDefault(s => s.LayerId == layerId);
        if (snapshot is null) return;

        var entry = GameSessionService.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
        if (entry?.IsRaster == true)
        {
            if (snapshot.RasterImageData is not null && snapshot.RasterProjBounds is not null)
            {
                await MapJSModule.InvokeVoidAsync(
                    "addRasterLayer",
                    snapshot.LayerId,
                    snapshot.RasterImageData,
                    snapshot.RasterProjBounds,
                    0.9,
                    visible,
                    snapshot.RasterColorMap.Count > 0 ? snapshot.RasterColorMap : null,
                    snapshot.RasterMinCutoffNorm,
                    snapshot.RasterInterpolate);
            }
            return;
        }

        if (!string.IsNullOrEmpty(snapshot.VectorGeometriesJson))
        {
            await MapJSModule.InvokeVoidAsync(
                "addVectorLayer",
                snapshot.LayerId,
                snapshot.VectorGeometriesJson,
                snapshot.GeoType,
                snapshot.TypeColors,
                visible,
                snapshot.LabelKey);
        }
    }

    /// <summary>
    /// Computes and sends the world-state projection to the map for all earlier finalised plans
    /// relative to <paramref name="planStartDate"/>.
    /// Pass <paramref name="singleLayerId"/> to restrict processing to one layer — used when
    /// the user activates a layer from the panel while a plan is already selected.
    /// Pass <paramref name="currentPlan"/> to also hide base geometry that the current plan modifies.
    /// </summary>
    public async Task ApplyPlanProjectionAsync(int planStartDate, string? singleLayerId = null, Plan? currentPlan = null)
    {
        if (MapJSModule is null) return;

        // Snapshot the Plans list: the WS background thread may call _plans.Add / _plans[i] = ...
        // via ApplyGameLatest while we iterate here.  Individual Plan / PlanLayerData /
        // PlanGeometryItem records are immutable once constructed, so no deeper copies are needed.
        var relevantPlans = GameSessionService.Plans
            .Where(p => p.StartDate < planStartDate && PlanStates.IsFinalisedPlanState(p.State))
            .OrderBy(p => p.StartDate).ThenBy(p => p.PlanId)
            .ToList();

        var hiddenFeatures = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var addedFeatures  = new List<object>();

        // Process prior finalized plans
        foreach (var plan in relevantPlans)
        {
            foreach (var planLayer in plan.Layers)
            {
                if (string.IsNullOrEmpty(planLayer.OriginalLayerId)) continue;
                if (singleLayerId is not null &&
                    !string.Equals(planLayer.OriginalLayerId, singleLayerId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!hiddenFeatures.TryGetValue(planLayer.OriginalLayerId, out var ids))
                    hiddenFeatures[planLayer.OriginalLayerId] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var deletedId in planLayer.DeletedPersistentIds)
                    ids.Add(deletedId);

                var geoType = GameSessionService.LayerEntries
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
                            coords    = geo.Coordinates.ToArray()
                        });
                }
            }
        }

        // Process current plan if provided (hide base-layer features it modifies)
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

                foreach (var deletedId in planLayer.DeletedPersistentIds)
                    ids.Add(deletedId);

                foreach (var geo in planLayer.Geometry)
                {
                    if (!string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id)
                        ids.Add(geo.PersistentId);
                }
            }
        }

        if (hiddenFeatures.Count > 0 || addedFeatures.Count > 0)
            await MapJSModule.InvokeVoidAsync("applyPlanProjection",
                JsonSerializer.Serialize(new {
                    hidden = hiddenFeatures.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()),
                    added  = addedFeatures
                }));
    }

    /// <summary>Assigns z-indices so that GameSessionService.LegendOrder[0] = bottom, last = top.</summary>
    public async Task SyncZIndicesAsync()
    {
        if (MapJSModule is null) return;

        // Snapshot the list before iterating: the await inside the loop yields control,
        // which allows concurrent plan-activation/deactivation tasks to Add or Remove
        // entries from LegendOrderLayerIds mid-iteration, causing an
        // InvalidOperationException.  A snapshot keeps the iteration stable while
        // the live list remains free to be updated by other operations.
        var snapshot = GameUIStateService.LegendOrderLayerIds.ToList();

        int i = 0;
        foreach (string layerId in snapshot)
        {
            await MapJSModule.InvokeVoidAsync("setLayerZIndex", layerId, i + 1);
            i++;
        }
    }
}