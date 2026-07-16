using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanStartDate : GameComponentBase
{
    [Parameter] public Plan? Plan { get; set; }
    [Parameter] public bool EditSaving { get; set; }
    [Parameter] public int ConstructionTime { get; set; }
    [Parameter] public EventCallback<int> StartMonthChanged { get; set; }
    [Parameter] public EventCallback<int> StartYearChanged { get; set; }

    private int _editStartMonth = 1;
    private int _editStartYear = 2020;
    // Track which plan we last initialised from so that normal Blazor re-renders
    // during editing don't overwrite the user's in-progress selection.
    private int? _lastInitialisedPlanId;

    protected override void OnParametersSet()
    {
        var currentPlanId = Plan?.PlanId;

        // Only reinitialise when the plan identity changes (e.g. switching plans,
        // entering edit mode for the first time, or mounting for a brand-new plan).
        if (currentPlanId == _lastInitialisedPlanId) return;
        _lastInitialisedPlanId = currentPlanId;

        var baseDate = new DateTime(GameSessionService.GameStartYear, 1, 1);
        var monthsToAdd = Plan?.StartDate ?? 0;

        // Clamp months to prevent DateTime overflow (valid range: years 1-9999)
        monthsToAdd = Math.Clamp(monthsToAdd, -120000, 120000);

        try
        {
            var startDate = baseDate.AddMonths(monthsToAdd);
            _editStartMonth = startDate.Month;
            _editStartYear = startDate.Year;
        }
        catch (ArgumentOutOfRangeException)
        {
            // If still out of range, fall back to game start date
            _editStartMonth = 1;
            _editStartYear = GameSessionService.GameStartYear;
        }
    }

    public int GetStartMonth()
    {
        return (_editStartYear - GameSessionService.GameStartYear) * 12 + _editStartMonth - 1;
    }

    // Called via @bind:after on the month <select> to propagate the new value to PlanDetails.
    private async Task OnMonthChangedAsync() => await StartMonthChanged.InvokeAsync(_editStartMonth);

    // Called via @bind:after on the year <select> to propagate the new value to PlanDetails.
    private async Task OnYearChangedAsync() => await StartYearChanged.InvokeAsync(_editStartYear);

    private DateTime EditEarliestStart =>
        new DateTime(GameSessionService.GameStartYear, 1, 1).AddMonths(GameSessionService.GameCurrentMonth + Math.Max(ConstructionTime, 1));

    private bool EditStartDateValid =>
        _editStartYear > EditEarliestStart.Year ||
        (_editStartYear == EditEarliestStart.Year && _editStartMonth >= EditEarliestStart.Month);

    // For the month <select>: months 1-12, but disable months before minimum when on the earliest year
    private bool EditMonthDisabled(int m) =>
        _editStartYear == EditEarliestStart.Year && m < EditEarliestStart.Month;
}