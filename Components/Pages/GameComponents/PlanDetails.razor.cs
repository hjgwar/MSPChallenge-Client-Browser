using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Utils;
using MSPChallenge_Client_Browser.Utils.PlanCalculations;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class PlanDetails : GameComponentBase
{
    [Parameter] public MapViewPort? Map { get; set; }
    
    public PlanNameDesc PlanNameDescInstance { get; set; } = null!;
    public PlanStartDate PlanStartDateInstance { get; set; } = null!;
    public PlanPolicies PlanPoliciesInstance { get; set; } = null!;
    public PlanLayers PlanLayersInstance { get; set; } = null!;
    public PlanMessages PlanMessagesInstance { get; set; } = null!;
    public PlanIssues PlanIssuesInstance { get; set; } = null!;
    public PlanDetailsSave PlanDetailsSaveInstance { get; set; } = null!;

    private List<PlanRestrictionIssue> _selectedPlanIssues = [];
    private Plan _detailPlan = null!;
    private List<string> _detailLayers = [];
    private string _detailDotColour = "#6c757d";
    private string _detailCountryName = "Unknown Country";
    
    private bool _editSaving = false;
    private string? _editError;

    private bool _pendingEnterEditMode;

    private string? _editName;
    private string? _editDescription;
    private int _editStartYear;
    private int _editStartMonth;
    private int _editMinConstructionMonths;
    private HashSet<string> _editPlanLayerIds = [];
    private HashSet<string> _editPolicyTypes = [];
    private string? _enterEditError;

    protected override void OnInitialized()
    {
        _detailPlan = GetDetailPlan();
        _detailLayers = _detailPlan.Layers
            .Select(l => GameSessionState.LayerEntries.FirstOrDefault(e => e.LayerId == l.OriginalLayerId)?.DisplayName)
            .OfType<string>()
            .Distinct()
            .ToList();
        _detailDotColour = (_detailPlan.Country == 1 || _detailPlan.Country == 2)
            ? "#ff69b4"
            : GameSessionState.Countries.FirstOrDefault(c => c.Id == _detailPlan.Country)?.Color ?? "#6c757d";
        _detailCountryName = GameSessionState.Countries.FirstOrDefault(c => c.Id == _detailPlan.Country)?.Name ?? $"Country {_detailPlan.Country}";
    }

    private int GetConstructionTime()
    {
        return _detailPlan?.ConstructionTime ?? 0;
    }

    private void OnPlanNameChanged(string newName)
    {
        _editName = newName;
    }

    private void OnPlanDescriptionChanged(string newDescription)
    {
        _editDescription = newDescription;
    }

    private void OnStartMonthChanged(int month)
    {
        _editStartMonth = month;
    }

    private void OnStartYearChanged(int year)
    {
        _editStartYear = year;
    }

    private async Task ClosePlanDetailAsync()
    {
        if (GameSessionState.EditMode)
            await CancelEditAsync();
        GameSessionState.SelectedPlanId = 0;
    }

    private bool CanEnterEditMode =>
        GameSessionState.SelectedPlanId != 0 &&
        _detailPlan is not null &&
        _detailPlan.State == Models.PlanState.DESIGN &&
        (UserSessionService.User.Country.Id <= 2 || _detailPlan.Country == UserSessionService.User.Country.Id);

    private DateTime EditEarliestStart =>
        new DateTime(GameSessionState.GameStartYear, 1, 1).AddMonths(GameSessionState.GameCurrentMonth + GetConstructionTime());

    private bool EditStartDateValid =>
        _editStartYear > EditEarliestStart.Year ||
        (_editStartYear == EditEarliestStart.Year && _editStartMonth >= EditEarliestStart.Month);

    private async Task EnterEditModeAsync()
    {
        // Always fetch the latest plan from GameSessionState
        _detailPlan = GetDetailPlan();
        if (_detailPlan is null)
        {
            _enterEditError = "Selected plan not found. It may have been deleted or modified by another user. Please select the plan again.";
            _pendingEnterEditMode = true;
            StateHasChanged();
            return;
        }

        if (_detailPlan.LockedByUserId != 0 && _detailPlan.LockedByUserId != UserSessionService.User.Id)
        {
            _enterEditError = $"Plan is currently locked by another user.";
            _pendingEnterEditMode = true;
            StateHasChanged();
            return;
        }

        // Try to lock the plan
        try
        {
            await ApiClient.PostFormAsync("Plan/Lock",
                new[]
                {
                    new KeyValuePair<string, string>("id", _detailPlan.PlanId.ToString()),
                    new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                });
        }
        catch
        {
            _enterEditError = "Failed to lock plan.";
            _pendingEnterEditMode = true;
            StateHasChanged();
            return;
        }

        GameSessionState.ToggleEditMode();
        StateHasChanged();
    }

    private async Task CancelEditAsync()
    {
        GameSessionState.ToggleEditMode();
        _editSaving = true;
        StateHasChanged();
        try
        {
            await ApiClient.PostFormAsync("Plan/Unlock",
                new[]
                {
                    new KeyValuePair<string, string>("id", GameSessionState.SelectedPlanId.ToString()!),
                    new KeyValuePair<string, string>("force_unlock", "0"),
                    new KeyValuePair<string, string>("user", UserSessionService.User.Id.ToString()),
                });
        }
        catch { }
        finally
        {
            _editSaving = false;
            _editError = null;
            StateHasChanged();
        }
    }

    private Plan GetDetailPlan()
    {
        var plan = GameSessionState.SelectedPlan;
        if (plan != null) return plan;
        
        return new Plan(
            0, // PlanId (new/unsaved)
            _editName ?? string.Empty,
            _editDescription ?? string.Empty,
            Models.PlanState.DESIGN, // State
            UserSessionService.User.Country.Id,
            (_editStartYear - GameSessionState.GameStartYear) * 12 + (_editStartMonth - 1),
            0, // ConstructionTime
            new List<string>(), // PolicyNames
            new List<string>(), // PolicyTypes
            _editPlanLayerIds.Select(id =>
                new PlanLayerData(
                    id, // LayerId
                    id, // OriginalLayerId
                    string.Empty, // State
                    new List<PlanGeometryItem>(), // Geometry
                    new List<string>() // DeletedPersistentIds
                )
            ).ToList(),
            false, // RequiresApproval
            0, // MessageCount
            0, // IssueCount
            null, // Votes
            0 // LockedByUserId
        );
    }

    private async Task SavePlanAsync()
    {
        if (_editSaving) return;
        if (!EditStartDateValid)
        {
            _editError = $"Start date must be {EditEarliestStart:MMM yyyy} or later.";
            StateHasChanged();
            return;
        }

        // Call the PlanDetailsSave sub-component's SavePlanAsync method
        var success = await PlanDetailsSaveInstance.SavePlanAsync();
        
        if (success)
        {
            GameSessionState.ToggleEditMode();
        }
    }

    private void OnSaveError(string? error)
    {
        _editError = error;
        StateHasChanged();
    }

    private void OnSavingChanged(bool saving)
    {
        _editSaving = saving;
        StateHasChanged();
    }
}

