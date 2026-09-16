using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class MapViewPort
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    public IJSObjectReference? MapJSModule;

    // Layers whose full geometry has already been parsed and added to OpenLayers this
    // session; re-showing them only needs a visibility toggle, not a full rebuild.
    private readonly HashSet<string> _materializedLayerIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly TaskCompletionSource _readyTcs = new();
    /// <summary>Resolves once <see cref="MapJSModule"/> has been imported and is ready to use.</summary>
    public Task WhenReady => _readyTcs.Task;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;
        MapJSModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/map.js");
        _readyTcs.SetResult();
    }

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

    // Internal so Game.razor.cs can reuse this for the initial cold-start layer load
    // instead of duplicating the materialization logic.
    internal async Task EnsureLayerRenderedAsync(string layerId, bool visible)
    {
        if (MapJSModule is null) return;

        if (_materializedLayerIds.Contains(layerId))
        {
            // Already parsed/added once this session — just toggle visibility instead of
            // re-parsing and rebuilding every feature from scratch again.
            await MapJSModule.InvokeVoidAsync("setLayerVisible", layerId, visible);
            return;
        }

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
                _materializedLayerIds.Add(layerId);
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
            _materializedLayerIds.Add(layerId);
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

        // Prior finalised plans' hide/add contributions are cached per cutoff month in
        // GameSessionService (invalidated whenever plan data changes), so switching between
        // already-viewed plans/cutoffs during a long session no longer re-walks the full
        // plan history each time.
        var projection = GameSessionService.GetFinalizedPlansProjection(planStartDate);

        var hiddenFeatures = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var addedFeatures  = new List<object>();

        foreach (var (layerId, ids) in projection.HiddenByLayer)
        {
            if (singleLayerId is not null &&
                !string.Equals(layerId, singleLayerId, StringComparison.OrdinalIgnoreCase))
                continue;
            hiddenFeatures[layerId] = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var feature in projection.AddedFeatures)
        {
            if (singleLayerId is not null &&
                !string.Equals(feature.LayerId, singleLayerId, StringComparison.OrdinalIgnoreCase))
                continue;

            addedFeatures.Add(new {
                layerId   = feature.LayerId,
                geoType   = feature.GeoType,
                id        = feature.Id,
                typeIndex = feature.TypeIndex,
                coords    = feature.Coordinates.ToArray()
            });
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