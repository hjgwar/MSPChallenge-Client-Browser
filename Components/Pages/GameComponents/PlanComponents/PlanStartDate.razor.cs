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

    protected override void OnParametersSet()
    {
        var baseDate = new DateTime(GameSessionState.GameStartYear, 1, 1);
        var monthsToAdd = Plan?.StartDate ?? 0;
        
        // Clamp months to prevent DateTime overflow (valid range: years 1-9999)
        // Limit to +/- 10000 years worth of months (120000 months)
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
            _editStartYear = GameSessionState.GameStartYear;
        }
    }

    public int GetStartMonth()
    {
        return (_editStartYear - GameSessionState.GameStartYear) * 12 + _editStartMonth - 1;
    }

    private DateTime EditEarliestStart =>
        new DateTime(GameSessionState.GameStartYear, 1, 1).AddMonths(GameSessionState.GameCurrentMonth + ConstructionTime);

    private bool EditStartDateValid =>
        _editStartYear > EditEarliestStart.Year ||
        (_editStartYear == EditEarliestStart.Year && _editStartMonth >= EditEarliestStart.Month);

    // For the month <select>: months 1-12, but disable months before minimum when on the earliest year
    private bool EditMonthDisabled(int m) =>
        _editStartYear == EditEarliestStart.Year && m < EditEarliestStart.Month;
}