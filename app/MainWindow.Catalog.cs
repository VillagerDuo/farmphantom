using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace FarmPhantom;

internal sealed record SteamStoreRegion(string CountryCode, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class MainWindow
{
    private static readonly TimeSpan CardCatalogCacheLifetime = TimeSpan.FromHours(6);
    private static readonly (string CountryCode, string NameKey)[] CardCatalogRegionDefinitions =
    [
        ("tr", "catalog.region.turkey"),
        ("us", "catalog.region.unitedStates"),
        ("sa", "catalog.region.saudiArabia"),
        ("ae", "catalog.region.unitedArabEmirates"),
        ("kw", "catalog.region.kuwait"),
        ("qa", "catalog.region.qatar"),
        ("bh", "catalog.region.bahrain"),
        ("om", "catalog.region.oman"),
        ("il", "catalog.region.israel"),
        ("eg", "catalog.region.egypt"),
        ("jo", "catalog.region.jordan"),
        ("lb", "catalog.region.lebanon"),
        ("iq", "catalog.region.iraq"),
        ("ma", "catalog.region.morocco"),
        ("tn", "catalog.region.tunisia"),
        ("dz", "catalog.region.algeria"),
        ("ly", "catalog.region.libya"),
        ("ps", "catalog.region.palestine"),
        ("sy", "catalog.region.syria"),
        ("ye", "catalog.region.yemen"),
        ("ir", "catalog.region.iran"),
        ("sd", "catalog.region.sudan")
    ];
    private readonly SteamCardCatalogClient _steamCardCatalogClient = new();
    private IReadOnlyList<SteamCardCatalogGame> _cardCatalogGames = [];
    private DateTimeOffset? _cardCatalogUpdatedAt;
    private CancellationTokenSource? _cardCatalogCancellation;
    private bool _refreshingCardCatalogRegionOptions;
    private int? _cardCatalogBudgetMinor;

    private SteamStoreRegion SelectedCardCatalogRegion =>
        CardCatalogRegionComboBox.SelectedItem as SteamStoreRegion
        ?? new SteamStoreRegion("tr", LocalizationService.Instance.Translate("catalog.region.turkey"));

    private void InitializeCardCatalogRegionOptions(string countryCode)
    {
        _refreshingCardCatalogRegionOptions = true;
        var regions = CardCatalogRegionDefinitions
            .Select(region => new SteamStoreRegion(
                region.CountryCode,
                LocalizationService.Instance.Translate(region.NameKey)))
            .ToArray();
        CardCatalogRegionComboBox.ItemsSource = regions;
        CardCatalogRegionComboBox.SelectedItem = regions.FirstOrDefault(region =>
            region.CountryCode.Equals(countryCode, StringComparison.OrdinalIgnoreCase)) ?? regions[0];
        _refreshingCardCatalogRegionOptions = false;
    }

    private async void RefreshCardCatalogButton_Click(object? sender, RoutedEventArgs e) =>
        await LoadCardCatalogAsync(forceRefresh: true);

    private void CardCatalogRegionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized || !_settingsLoaded || _refreshingCardCatalogRegionOptions)
        {
            return;
        }

        SaveSettings();
        _cardCatalogGames = [];
        _cardCatalogUpdatedAt = null;
        UpdateCardCatalogBudgetFilter();
        RefreshCardCatalogList();
        _ = LoadCardCatalogAsync(forceRefresh: false);
    }

    private async Task LoadCardCatalogAsync(bool forceRefresh)
    {
        if (_cardCatalogCancellation is not null)
        {
            return;
        }

        var region = SelectedCardCatalogRegion;
        var cached = _steamCardCatalogClient.ReadCache(region.CountryCode);
        if (_cardCatalogGames.Count == 0 && cached is not null)
        {
            SetCardCatalog(cached);
        }

        if (!forceRefresh
            && cached is not null
            && cached.Games.Any(game => game.CardSetValueMinor.HasValue)
            && DateTimeOffset.UtcNow - cached.UpdatedAt < CardCatalogCacheLifetime)
        {
            CardCatalogStatusText.Text = LocalizationService.Instance.Format(
                "catalog.cached",
                cached.Games.Count,
                region.Name,
                FormatCardCatalogDate(cached.UpdatedAt));
            return;
        }

        _cardCatalogCancellation = new CancellationTokenSource();
        var cancellationToken = _cardCatalogCancellation.Token;
        RefreshCardCatalogButton.IsEnabled = false;
        CardCatalogRegionComboBox.IsEnabled = false;
        CancelCardCatalogButton.IsVisible = true;
        CardCatalogStatusText.Text = LocalizationService.Instance.Format("catalog.loading", region.Name);

        try
        {
            var result = await _steamCardCatalogClient.RefreshAsync(
                region.CountryCode,
                (loaded, total) => CardCatalogStatusText.Text = LocalizationService.Instance.Format(
                    "catalog.progress",
                    region.Name,
                    loaded,
                    total),
                cancellationToken);

            SetCardCatalog(result.Snapshot);
            CardCatalogStatusText.Text = result.CacheSaved
                ? LocalizationService.Instance.Format(
                    "catalog.updated",
                    result.Snapshot.Games.Count,
                    region.Name,
                    FormatCardCatalogDate(result.Snapshot.UpdatedAt))
                : LocalizationService.Instance.Format(
                    "catalog.updatedNoCache",
                    result.Snapshot.Games.Count,
                    region.Name,
                    FormatCardCatalogDate(result.Snapshot.UpdatedAt));
        }
        catch (HttpRequestException ex)
        {
            ShowCardCatalogRefreshError(cached, region.Name, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            ShowCardCatalogRefreshError(cached, region.Name, ex.Message);
        }
        catch (JsonException ex)
        {
            ShowCardCatalogRefreshError(cached, region.Name, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CardCatalogStatusText.Text = LocalizationService.Instance.Translate("catalog.cancelled");
        }
        catch (OperationCanceledException)
        {
            ShowCardCatalogRefreshError(
                cached,
                region.Name,
                LocalizationService.Instance.Translate("catalog.timeout"));
        }
        finally
        {
            RefreshCardCatalogButton.IsEnabled = true;
            CardCatalogRegionComboBox.IsEnabled = true;
            CancelCardCatalogButton.IsVisible = false;
            _cardCatalogCancellation.Dispose();
            _cardCatalogCancellation = null;
        }
    }

    private void CancelCardCatalogButton_Click(object? sender, RoutedEventArgs e) =>
        _cardCatalogCancellation?.Cancel();

    private void SetCardCatalog(SteamCardCatalogSnapshot snapshot)
    {
        _cardCatalogGames = snapshot.Games;
        _cardCatalogUpdatedAt = snapshot.UpdatedAt;
        RefreshCardCatalogList();
    }

    private void ShowCardCatalogRefreshError(SteamCardCatalogSnapshot? cached, string regionName, string error)
    {
        CardCatalogStatusText.Text = _cardCatalogGames.Count > 0
            ? LocalizationService.Instance.Format(
                "catalog.refreshFailedWithCache",
                regionName,
                FormatCardCatalogDate(_cardCatalogUpdatedAt ?? cached?.UpdatedAt ?? DateTimeOffset.MinValue),
                error)
            : LocalizationService.Instance.Format("catalog.refreshFailed", regionName, error);
    }

    private static string FormatCardCatalogDate(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private void CardCatalogSearchTextBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshCardCatalogList();

    private void CardCatalogSortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        RefreshCardCatalogList();

    private void CardCatalogBudgetTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCardCatalogBudgetFilter();
        RefreshCardCatalogList();
    }

    private void UpdateCardCatalogBudgetFilter()
    {
        var text = CardCatalogBudgetTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _cardCatalogBudgetMinor = null;
            CardCatalogBudgetHintText.Text = LocalizationService.Instance.Translate("catalog.budgetHint");
        }
        else if (SteamPrice.ParseMinor(text) is int budgetMinor && budgetMinor >= 0)
        {
            _cardCatalogBudgetMinor = budgetMinor;
            CardCatalogBudgetHintText.Text = LocalizationService.Instance.Format(
                "catalog.budgetFilterActive",
                (budgetMinor / 100m).ToString("0.##", CultureInfo.CurrentCulture),
                SelectedCardCatalogRegion.Name);
        }
        else
        {
            _cardCatalogBudgetMinor = null;
            CardCatalogBudgetHintText.Text = LocalizationService.Instance.Translate("catalog.budgetInvalid");
        }
    }

    private void RefreshCardCatalogList()
    {
        if (!_isUiInitialized)
        {
            return;
        }

        var search = CardCatalogSearchTextBox.Text?.Trim();
        var games = _cardCatalogGames.AsEnumerable();
        if (!string.IsNullOrEmpty(search))
        {
            games = games.Where(game =>
                game.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || game.AppId.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.Ordinal));
        }

        if (_cardCatalogBudgetMinor is int budgetMinor)
        {
            games = games.Where(game => game.FinalPriceMinor is int price && price <= budgetMinor);
        }

        var sorted = CardCatalogSortComboBox.SelectedIndex == 1
            ? games.OrderBy(game => game.HasNoPrice)
                .ThenByDescending(game => game.FinalPriceMinor)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            : games.OrderBy(game => game.HasNoPrice)
                .ThenBy(game => game.FinalPriceMinor)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase);
        var result = sorted.ToArray();
        CardCatalogListBox.ItemsSource = result;
        CardCatalogEmptyStateText.IsVisible = result.Length == 0;
    }

    private void OpenCardCatalogStoreButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SteamCardCatalogGame game })
        {
            return;
        }

        if (!OpenUrl($"https://store.steampowered.com/app/{game.AppId}/?cc={SelectedCardCatalogRegion.CountryCode}"))
        {
            CardCatalogStatusText.Text = LocalizationService.Instance.Translate("catalog.browserUnavailable");
        }
    }

    private void OpenCardCatalogSource_Click(object? sender, RoutedEventArgs e)
    {
        if (!OpenUrl("https://www.steamcardexchange.net/index.php?badgeprices"))
        {
            CardCatalogStatusText.Text = LocalizationService.Instance.Translate("catalog.browserUnavailable");
        }
    }
}
