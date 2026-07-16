using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelLayerPicker
{
    [Parameter] public HashSet<string> SelectedLayerIds { get; set; } = [];
    [Parameter] public EventCallback<string> OnToggleLayer { get; set; }
    [Parameter] public List<Layer> AvailableLayers { get; set; } = [];
    [Parameter] public EventCallback OnClose { get; set; }

    private async Task ToggleEditPlanLayerAsync(string layerId)
    {
        await OnToggleLayer.InvokeAsync(layerId);
    }

    private async Task ToggleLayerPickerAsync()
    {
        await OnClose.InvokeAsync();
    }

    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var s = Regex.Replace(value, "[_\\-]+", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }
}