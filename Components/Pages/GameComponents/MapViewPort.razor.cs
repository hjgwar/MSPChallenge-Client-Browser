using System.Text.Json;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils.PlanCalculations;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class MapViewPort
{
    public required IJSObjectReference MapJSModule;
    
    public async Task ToggleLayerAsync(Layer entry, bool visible)
    {
        entry.Visible = visible;

        if (visible)
        {
            // Hidden layers are not materialized on warm return until user enables them.
            await EnsureLayerRenderedAsync(entry.LayerId, visible: true);

            // If a plan is currently selected, immediately project prior-plan changes onto
            // this layer so the user sees the correct world state without re-selecting the plan.
            if (GameSessionState.SelectedPlanId != 0)
            {
                var viewedPlan = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
                if (viewedPlan is not null)
                    await ApplyPlanProjectionAsync(viewedPlan.StartDate, entry.LayerId, currentPlan: viewedPlan);
            }
        }
        else if (MapJSModule is not null)
        {
            await MapJSModule.InvokeVoidAsync("setLayerVisible", entry.LayerId, false);
        }

        if (visible && !entry.IsBaseLayer && !GameSessionState.LegendOrderLayerIds.Contains(entry.LayerId))
            GameSessionState.LegendOrderLayerIds.Add(entry.LayerId);
        else if (!visible)
            GameSessionState.LegendOrderLayerIds.Remove(entry.LayerId);

        await SyncZIndicesAsync();
    }

    private async Task EnsureLayerRenderedAsync(string layerId, bool visible)
    {
        if (MapJSModule is null) return;

        var snapshot = GameSessionState.MapLayerSnapshots.FirstOrDefault(s => s.LayerId == layerId);
        if (snapshot is null) return;

        var entry = GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == layerId);
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
    private async Task ApplyPlanProjectionAsync(int planStartDate, string? singleLayerId = null, Plan? currentPlan = null)
    {
        if (MapJSModule is null) return;

        var priorPlans = GameSessionState.Plans
            .Where(p => p.StartDate < planStartDate && PlanStates.IsFinalisedPlanState(p.State))
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

                var geoType = GameSessionState.LayerEntries
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
            await MapJSModule.InvokeVoidAsync("applyPlanProjection",
                JsonSerializer.Serialize(new {
                    hidden = hiddenFeatures.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()),
                    added  = addedFeatures
                }));
    }

    /// <summary>Assigns z-indices so that GameSessionState.LegendOrder[0] = bottom, last = top.</summary>
    public async Task SyncZIndicesAsync()
    {
        if (MapJSModule is null) return;

        int i = 0;
        foreach (string LayerId in GameSessionState.LegendOrderLayerIds)
        {
            await MapJSModule.InvokeVoidAsync("setLayerZIndex", LayerId, i + 1);
            i++;
        }
    }
}