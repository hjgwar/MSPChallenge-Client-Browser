using MSPChallenge_Client_Browser.Services;
using Microsoft.AspNetCore.Components;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class PlanCreation : IDisposable
{
    [Parameter] public Action<PlanCreation> OpenEditMode { get; set; } = null!;
    public string? _createPlanName { get; set; }
    public string? _createPlanDescription { get; set; }
    public int     _createPlanStartYear { get; set; }
    public int     _createPlanStartMonth { get; set; }
    private bool    _createPlanSaving;
    private string? _createPlanError;

    protected override void OnInitialized()
    {
        var earliest = CreatePlanEarliestStart;
        _createPlanName        = string.Empty;
        _createPlanDescription = string.Empty;
        _createPlanStartYear   = earliest.Year;
        _createPlanStartMonth  = earliest.Month;
        _createPlanSaving      = false;
        _createPlanError       = null;
    }
        
    private DateTime CreatePlanEarliestStart =>
        GameSessionService.GameStartYear > 0
            ? new DateTime(GameSessionService.GameStartYear, 1, 1).AddMonths(GameSessionService.GameCurrentMonth + 1)
            : DateTime.Now.AddMonths(1);

    private bool CreatePlanStartDateValid =>
        _createPlanStartYear > CreatePlanEarliestStart.Year ||
        (_createPlanStartYear == CreatePlanEarliestStart.Year && _createPlanStartMonth >= CreatePlanEarliestStart.Month);

    private bool CreatePlanMonthDisabled(int m) =>
        _createPlanStartYear == CreatePlanEarliestStart.Year && m < CreatePlanEarliestStart.Month;

    private async Task CreatePlanAsync()
    {
        if (_createPlanSaving) return;

        if (string.IsNullOrWhiteSpace(_createPlanName))
        {
            _createPlanError = "Plan name is required.";
            StateHasChanged();
            return;
        }
        if (!CreatePlanStartDateValid)
        {
            _createPlanError = $"Start date must be {CreatePlanEarliestStart:MMM yyyy} or later.";
            StateHasChanged();
            return;
        }
        OpenEditMode(this);
    }

    private void ClosePanel()
    {
        GameUIStateService.CreatePlanOpen = false;
        GameSessionService.NotifyChanged();
    }

    public void Dispose()
    {
        
    }
}