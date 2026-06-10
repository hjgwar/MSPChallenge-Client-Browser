using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanStartDate : PlanComponentBase
{
    private int _editStartMonth = 1;
    private int _editStartYear = 2020;

    protected override void OnInitialized()
    {
        var startDate    = new DateTime(GameSessionState.GameStartYear, 1, 1).AddMonths(PlanController.SelectedPlan?.StartDate ?? 0);
        _editStartMonth = startDate.Month;
        _editStartYear = startDate.Year;        
    }

    public int GetStartMonth()
    {
        return (_editStartYear - GameSessionState.GameStartYear) * 12 + _editStartMonth - 1;
    }

    private DateTime EditEarliestStart =>
        new DateTime(GameSessionState.GameStartYear, 1, 1).AddMonths(GameSessionState.GameCurrentMonth + PlanController.GetConstructionTime());

    private bool EditStartDateValid =>
        _editStartYear > EditEarliestStart.Year ||
        (_editStartYear == EditEarliestStart.Year && _editStartMonth >= EditEarliestStart.Month);

    // For the month <select>: months 1-12, but disable months before minimum when on the earliest year
    private bool EditMonthDisabled(int m) =>
        _editStartYear == EditEarliestStart.Year && m < EditEarliestStart.Month;
}