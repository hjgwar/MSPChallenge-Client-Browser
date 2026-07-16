using Microsoft.AspNetCore.Components;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelGeometryTool
{
    [Parameter] public string? LayerId { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [CascadingParameter] public PlanLayers? Parent { get; set; }

    private async Task CloseGeometryToolAsync()
    {
        if (Parent is not null)
            await Parent.CloseGeometryToolAsync();
        await OnClose.InvokeAsync();
    }

    private async Task ToggleGeometryTypeBitAsync(int bitIndex)
    {
        if (Parent is not null)
            await Parent.ToggleGeometryTypeBitAsync(bitIndex);
    }

    private async Task SetGeometryTypeIndexAsync(int idx)
    {
        if (Parent is not null)
            await Parent.SetGeometryTypeIndexAsync(idx);
    }

    private async Task SetGeometryModeEditAsync()
    {
        if (Parent is not null)
            await Parent.SetGeometryModeEditAsync();
    }

    private async Task SetGeometryModeCreateAsync()
    {
        if (Parent is not null)
            await Parent.SetGeometryModeCreateAsync();
    }

    private async Task UndoDrawingActionAsync()
    {
        if (Parent is not null)
            await Parent.UndoDrawingActionAsync();
    }

    private async Task RedoDrawingActionAsync()
    {
        if (Parent is not null)
            await Parent.RedoDrawingActionAsync();
    }

    private async Task RestoreSelectedGeometryAsync()
    {
        if (Parent is not null)
            await Parent.RestoreSelectedGeometryAsync();
    }

    private async Task DeleteSelectedGeometryAsync()
    {
        if (Parent is not null)
            await Parent.DeleteSelectedGeometryAsync();
    }
}