using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class KpiDashboard : ComponentBase, IDisposable
{
    [Parameter] public string? Category { get; set; }

    private enum KpiTab { Ecology, Shipping, Other, Favourites }

    private KpiTab _activeTab = KpiTab.Ecology;
    private bool _addWidgetsOpen;

    private bool HasAnyCatalog =>
        GameState.EcologyKpiCategories.Count > 0 ||
        GameState.ShippingKpiCategories.Count > 0 ||
        GameState.OtherKpiCategories.Count > 0;

    private int OwnCountryId => UserSessionService.User.Country.Id;
    private string OwnCountryName => UserSessionService.User.Country.Name;

    protected override void OnInitialized()
    {
        _activeTab = Category?.ToLowerInvariant() switch
        {
            "shipping"   => KpiTab.Shipping,
            "other"      => KpiTab.Other,
            "favourites" => KpiTab.Favourites,
            _            => KpiTab.Ecology,
        };
        EnsureDefaultWidgets();
        // Re-render on new WS data so widgets pick up new KPI values; each KpiWidget only
        // actually redraws its chart when its own data/selection has changed (see KpiWidget).
        GameState.Changed += OnStateChanged;
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => GameState.Changed -= OnStateChanged;

    private void SetTab(KpiTab tab)
    {
        _activeTab = tab;
        _addWidgetsOpen = false;
        EnsureDefaultWidgets();
    }

    /// <summary>Auto-populates a tab's widget list with every catalog category the first time it's visited. Never called for Favourites, which always starts empty.</summary>
    private void EnsureDefaultWidgets()
    {
        switch (_activeTab)
        {
            case KpiTab.Ecology:
                GameUIStateService.EnsureKpiWidgetsPopulated(UiState.EcologyWidgets, GameState.EcologyKpiCategories);
                break;
            case KpiTab.Shipping:
                GameUIStateService.EnsureKpiWidgetsPopulated(UiState.ShippingWidgets, GameState.ShippingKpiCategories);
                break;
            case KpiTab.Other:
                GameUIStateService.EnsureKpiWidgetsPopulated(UiState.OtherWidgets, GameState.OtherKpiCategories);
                break;
        }
    }

    private List<KpiWidgetInstance> ActiveWidgets => _activeTab switch
    {
        KpiTab.Ecology    => UiState.EcologyWidgets,
        KpiTab.Shipping   => UiState.ShippingWidgets,
        KpiTab.Other      => UiState.OtherWidgets,
        KpiTab.Favourites => UiState.FavouriteWidgets,
        _                 => UiState.EcologyWidgets,
    };

    private IReadOnlyList<KpiCategoryState> ActiveCatalog => _activeTab switch
    {
        KpiTab.Ecology  => GameState.EcologyKpiCategories,
        KpiTab.Shipping => GameState.ShippingKpiCategories,
        KpiTab.Other    => GameState.OtherKpiCategories,
        _               => [], // Favourites has no catalogue of its own — widgets are pinned from other tabs.
    };

    private KpiCategoryState? FindCategory(string categoryKey) =>
        GameState.EcologyKpiCategories.FirstOrDefault(c => c.CategoryKey == categoryKey) ??
        GameState.ShippingKpiCategories.FirstOrDefault(c => c.CategoryKey == categoryKey) ??
        GameState.OtherKpiCategories.FirstOrDefault(c => c.CategoryKey == categoryKey);

    private IEnumerable<KpiCategoryState> AvailableToAdd()
    {
        var currentKeys = ActiveWidgets.Select(w => w.CategoryKey).ToHashSet();
        return ActiveCatalog.Where(c => !currentKeys.Contains(c.CategoryKey));
    }

    private void ToggleAddWidgetsPopup() => _addWidgetsOpen = !_addWidgetsOpen;

    private void AddWidget(KpiCategoryState category)
    {
        var list = ActiveWidgets;
        if (list.Any(w => w.CategoryKey == category.CategoryKey)) return;
        list.Add(new KpiWidgetInstance(category.CategoryKey) { Position = NextPosition(list) });
        _addWidgetsOpen = false;
    }

    private void RemoveWidget(KpiWidgetInstance instance) => ActiveWidgets.Remove(instance);

    /// <summary>Keeps favourite state in sync across every tab's widget list for the same category, and adds/removes the pinned copy in Favourites.</summary>
    private void ToggleFavourite(KpiWidgetInstance instance)
    {
        var categoryKey = instance.CategoryKey;
        var isFavourite = instance.IsFavourite;

        foreach (var w in UiState.EcologyWidgets.Concat(UiState.ShippingWidgets)
                     .Concat(UiState.OtherWidgets).Concat(UiState.FavouriteWidgets)
                     .Where(w => w.CategoryKey == categoryKey))
        {
            w.IsFavourite = isFavourite;
        }

        if (isFavourite)
        {
            if (!UiState.FavouriteWidgets.Any(w => w.CategoryKey == categoryKey))
            {
                UiState.FavouriteWidgets.Add(new KpiWidgetInstance(categoryKey)
                {
                    IsFavourite = true,
                    Position = NextPosition(UiState.FavouriteWidgets),
                });
            }
        }
        else
        {
            UiState.FavouriteWidgets.RemoveAll(w => w.CategoryKey == categoryKey);
        }
    }

    private static KpiWidgetLayout NextPosition(List<KpiWidgetInstance> list)
    {
        const int columns = 3;
        var index = list.Count;
        return new KpiWidgetLayout { X = (index % columns) * 2, Y = (index / columns) * 2 };
    }

    // ── Country selector (Shipping/Other/Favourites only; Ecology KPIs are global-only) ──
    private int? ActiveSelectedCountryId
    {
        get => _activeTab switch
        {
            KpiTab.Shipping   => UiState.SelectedShippingCountryId,
            KpiTab.Other      => UiState.SelectedOtherCountryId,
            KpiTab.Favourites => UiState.SelectedFavouritesCountryId,
            _                 => null,
        };
        set
        {
            switch (_activeTab)
            {
                case KpiTab.Shipping:   UiState.SelectedShippingCountryId = value; break;
                case KpiTab.Other:      UiState.SelectedOtherCountryId = value; break;
                case KpiTab.Favourites: UiState.SelectedFavouritesCountryId = value; break;
            }
        }
    }

    private int ViewingCountryId => ActiveSelectedCountryId ?? OwnCountryId;

    private string SelectedCountryIdString => ActiveSelectedCountryId?.ToString() ?? "own";

    private void OnCountryChanged(ChangeEventArgs e)
    {
        var raw = e.Value?.ToString();
        ActiveSelectedCountryId = string.IsNullOrEmpty(raw) || raw == "own" ? null : int.Parse(raw);
    }
}
