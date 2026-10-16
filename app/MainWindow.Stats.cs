using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace FarmPhantom;

public sealed record StatRow(string Title, string Detail, int Count, uint AppId = 0)
{
    public bool HasImage => AppId != 0;
    public string HeaderUrl => AppId == 0 ? string.Empty : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
    public string HeaderFallbackUrl => AppId == 0 ? string.Empty : $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";
}

public sealed partial class MainWindow
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly DropHistoryStore _dropHistoryStore = new();
    private readonly SteamBadgeClient _badgeClient = new();
    private readonly List<DropRecord> _dropHistory = [];
    private readonly Dictionary<string, int?> _priceCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _valueCancellation;
    private bool _settingsLoaded;
    private bool _refreshingLanguageOptions;

    // ---- Kalıcı ayarlar ve sıra ----

    private void InitializePersistence()
    {
        var settings = _settingsStore.Load();

        try
        {
            _blacklistedAppIds.UnionWith(_gameBlacklistStore.Load());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Kara liste okunamadıysa boş başla; "Oyunlarımı getir" yeniden dener.
        }

        DropIntervalComboBox.SelectedIndex = IndexOrDefault(IntervalOptions, settings.DropIntervalMinutes, 1);
        DropTimeoutComboBox.SelectedIndex = IndexOrDefault(TimeoutOptions, settings.DropTimeoutMinutes, 2);
        MaxParallelComboBox.SelectedIndex = IndexOrDefault(ParallelChoices, settings.MaxParallel, 0);
        DropMonitorCheckBox.IsChecked = settings.DropMonitorEnabled;
        AutoQueueCheckBox.IsChecked = settings.AutoQueueEnabled;
        QueuePriorityComboBox.SelectedIndex = Math.Clamp(settings.QueuePriorityIndex, 0, 2);
        LocalizationService.Instance.SetLanguage(settings.LanguageCulture);
        LanguageComboBox.ItemsSource = LocalizationService.Instance.Languages;
        LanguageComboBox.SelectedItem = LocalizationService.Instance.Languages
            .FirstOrDefault(language => language.Culture.Equals(LocalizationService.Instance.SelectedCulture, StringComparison.OrdinalIgnoreCase));
        FlowDirection = LocalizationService.Instance.FlowDirection;
        GameFilterComboBox.SelectedIndex = Math.Clamp(settings.GameFilterIndex, 0, 5);
        GameSortComboBox.SelectedIndex = Math.Clamp(settings.GameSortIndex, 0, 4);
        PriceModeComboBox.SelectedIndex = Math.Clamp(settings.PriceModeIndex, 0, 1);
        CurrencyComboBox.SelectedIndex = Math.Max(Array.FindIndex(Currencies, currency => currency.Code == settings.CurrencyCode), 0);
        MinPriceTextBox.Text = settings.MinPrice;
        PresenceCheckBox.IsChecked = settings.PresenceEnabled;
        PresenceShowGameCheckBox.IsChecked = settings.PresenceShowGame;
        BotNotifyCheckBox.IsChecked = settings.DiscordBotNotify && settings.RelayNotify;
        TrayOnCloseCheckBox.IsChecked = settings.TrayOnClose;
        NotifyDropCheckBox.IsChecked = settings.NotifyOnDrop;
        SoundDropCheckBox.IsChecked = settings.SoundOnDrop;
        AutoRecoverCheckBox.IsChecked = settings.AutoRecover;
        StartWithWindowsCheckBox.IsEnabled = StartupManager.IsSupported;
        StartWithWindowsCheckBox.IsChecked = StartupManager.IsEnabled();

        foreach (var queued in settings.Queue.Where(item => item.AppId != 0).DistinctBy(item => item.AppId))
        {
            var name = string.IsNullOrWhiteSpace(queued.Name) ? $"Steam App {queued.AppId}" : queued.Name;
            _idleQueue.Add(new SteamGame(queued.AppId, name, _blacklistedAppIds.Contains(queued.AppId)));
        }

        UpdateQueueControls();

        try
        {
            _dropHistory.AddRange(_dropHistoryStore.Load());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Geçmiş dosyası okunamadı; yeni düşüşler yine de kaydedilmeye çalışılır.
        }

        SeedRecentDrops();
        _settingsLoaded = true;
        _idleQueue.CollectionChanged += (_, _) => SaveSettings();
        RefreshStats();
    }

    private static int IndexOrDefault(int[] values, int value, int fallback)
    {
        var index = Array.IndexOf(values, value);
        return index >= 0 ? index : fallback;
    }

    private void SaveSettings()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        var settings = new AppSettings
        {
            DropIntervalMinutes = IntervalOptions[Math.Clamp(DropIntervalComboBox.SelectedIndex, 0, IntervalOptions.Length - 1)],
            DropTimeoutMinutes = TimeoutOptions[Math.Clamp(DropTimeoutComboBox.SelectedIndex, 0, TimeoutOptions.Length - 1)],
            MaxParallel = MaxParallel,
            DropMonitorEnabled = DropMonitorCheckBox.IsChecked == true,
            DiscordBotNotify = BotNotifyCheckBox.IsChecked == true,
            RelayNotify = BotNotifyCheckBox.IsChecked == true,
            AutoQueueEnabled = AutoQueueCheckBox.IsChecked == true,
            QueuePriorityIndex = Math.Clamp(QueuePriorityComboBox.SelectedIndex, 0, 2),
            LanguageCulture = LocalizationService.Instance.SelectedCulture,
            GameFilterIndex = Math.Max(GameFilterComboBox.SelectedIndex, 0),
            GameSortIndex = Math.Max(GameSortComboBox.SelectedIndex, 0),
            PriceModeIndex = Math.Max(PriceModeComboBox.SelectedIndex, 0),
            CurrencyCode = SelectedCurrency.Code,
            MinPrice = MinPriceTextBox.Text?.Trim() ?? string.Empty,
            PresenceEnabled = PresenceCheckBox.IsChecked == true,
            PresenceShowGame = PresenceShowGameCheckBox.IsChecked == true,
            TrayOnClose = TrayOnCloseCheckBox.IsChecked == true,
            NotifyOnDrop = NotifyDropCheckBox.IsChecked == true,
            SoundOnDrop = SoundDropCheckBox.IsChecked == true,
            AutoRecover = AutoRecoverCheckBox.IsChecked == true,
            Queue = _idleQueue.Select(game => new QueuedGame { AppId = game.AppId, Name = game.Name }).ToList()
        };

        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ayarlar kaydedilemese de uygulama çalışmaya devam eder.
        }
    }

    private void MaxParallelComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        SaveSettings();
        if (_queueRunning)
        {
            FillSlots();
        }
    }

    private void SettingSelectionChanged(object? sender, SelectionChangedEventArgs e) => SaveSettings();

    private void QueuePriorityComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        SaveSettings();
        if (_queueRunning)
        {
            FillSlots();
        }
    }

    private void LanguageComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || _refreshingLanguageOptions || LanguageComboBox.SelectedItem is not LanguageOption language)
        {
            return;
        }

        LocalizationService.Instance.SetLanguage(language.Culture);
        FlowDirection = LocalizationService.Instance.FlowDirection;
        _refreshingLanguageOptions = true;
        LanguageComboBox.ItemsSource = LocalizationService.Instance.Languages;
        LanguageComboBox.SelectedItem = LocalizationService.Instance.Languages
            .FirstOrDefault(option => option.Culture.Equals(language.Culture, StringComparison.OrdinalIgnoreCase));
        _refreshingLanguageOptions = false;
        SaveSettings();
        ShowPage(_currentPage);
        UpdateQueueControls();
        UpdateLiveStrip();
        UpdateSteamStatus();
        RefreshNativeDllStatus();
        RefreshDiscordBotStatus();
        UpdatePresence();
    }

    private void SettingCheckChanged(object? sender, RoutedEventArgs e) => SaveSettings();

    private void SettingLostFocus(object? sender, RoutedEventArgs e) => SaveSettings();

    // ---- Oyun listesi sıralama ----

    private SteamGame[] SortGames(IEnumerable<SteamGame> games)
    {
        IOrderedEnumerable<SteamGame> ordered = GameSortComboBox.SelectedIndex switch
        {
            1 => games.OrderByDescending(game => game.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => games.OrderBy(game => game.AppId),
            3 => games.OrderByDescending(game => game.IsInstalled)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase),
            4 => games.OrderByDescending(game => game.RemainingCards ?? -1)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => games.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        return ordered.ToArray();
    }

    private void GameSortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshGamesDisplay();
        SaveSettings();
    }

    // ---- Düşüş geçmişi ve istatistik ----

    private void RecordDrop(DropRecord record)
    {
        _dropHistory.Add(record);
        try
        {
            _dropHistoryStore.Append(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Geçmiş dosyasına yazılamadı; bellekteki istatistik yine de güncellenir.
        }

        RefreshStats();
    }

    private void StatsViewComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        RefreshStats();
    }

    private void RefreshStats()
    {
        if (!_isUiInitialized)
        {
            return;
        }

        var today = DateTime.Now.Date;
        var local = _dropHistory
            .Select(record => (Record: record, Local: record.TimeUtc.ToLocalTime()))
            .ToArray();

        var todayCount = local.Count(item => item.Local.Date == today);
        var weekCount = local.Count(item => item.Local.Date > today.AddDays(-7));
        var gameCount = _dropHistory.Select(record => record.AppId).Distinct().Count();
        StatsSummaryText.Text = _dropHistory.Count == 0
            ? LocalizationService.Instance.Translate("stats.noRecordedDrops")
            : LocalizationService.Instance.Format("stats.summary", todayCount, weekCount, _dropHistory.Count, gameCount);

        List<StatRow> rows;
        if (StatsViewComboBox.SelectedIndex == 1)
        {
            rows = [];
            for (var offset = 0; offset < 14; offset++)
            {
                var day = today.AddDays(-offset);
                var items = local.Where(item => item.Local.Date == day).ToArray();
                var games = items.Select(item => item.Record.AppId).Distinct().Count();
                rows.Add(new StatRow(
                    day.ToString("dd.MM.yyyy ddd", CultureInfo.CurrentCulture),
                    items.Length == 0 ? "—" : LocalizationService.Instance.Format("stats.dayGames", games),
                    items.Length));
            }
        }
        else
        {
            rows = local
                .GroupBy(item => item.Record.AppId)
                .Select(group =>
                {
                    var last = group.OrderByDescending(item => item.Local).First();
                    return new StatRow(
                        last.Record.GameName,
                        LocalizationService.Instance.Format("stats.rowLastDrop", last.Local),
                        group.Count(),
                        last.Record.AppId);
                })
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        StatsListBox.ItemsSource = rows;
    }

    private async void CalcValueButton_Click(object? sender, RoutedEventArgs e)
    {
        var records = _dropHistory.Where(record => !string.IsNullOrWhiteSpace(record.MarketHashName)).ToArray();
        if (records.Length == 0)
        {
            StatsValueText.Text = LocalizationService.Instance.Translate("stats.valueNoDrops");
            return;
        }

        _valueCancellation?.Cancel();
        _valueCancellation?.Dispose();
        _valueCancellation = new CancellationTokenSource();
        var ct = _valueCancellation.Token;
        var currency = SelectedCurrency;
        var names = records.Select(record => record.MarketHashName).Distinct(StringComparer.Ordinal).ToArray();
        CalcValueButton.IsEnabled = false;
        string? failure = null;

        try
        {
            var madeRequest = false;
            for (var i = 0; i < names.Length; i++)
            {
                var key = $"{currency.Code}|{names[i]}";
                if (_priceCache.ContainsKey(key))
                {
                    continue;
                }

                StatsValueText.Text = LocalizationService.Instance.Format("stats.pricesLoading", i + 1, names.Length);
                if (madeRequest)
                {
                    await Task.Delay(MarketRequestDelay, ct);
                }

                madeRequest = true;
                try
                {
                    var price = await _marketClient.GetLowestPriceAsync(names[i], currency.Code, ct);
                    _priceCache[key] = price?.LowestMinor;
                }
                catch (HttpRequestException ex)
                {
                    failure = ex.Message;
                    break;
                }
                catch (JsonException)
                {
                    failure = "Steam fiyat yanıtı okunamadı.";
                    break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    failure = "Steam yanıt vermedi.";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatsValueText.Text = LocalizationService.Instance.Translate("stats.valueCancelled");
            CalcValueButton.IsEnabled = true;
            return;
        }

        var gross = 0;
        var net = 0;
        var priced = 0;
        foreach (var record in records)
        {
            if (_priceCache.TryGetValue($"{currency.Code}|{record.MarketHashName}", out var minor) && minor is int value)
            {
                gross += value;
                net += SteamPrice.SellerReceives(value);
                priced++;
            }
        }

        var failureText = failure is null ? string.Empty : LocalizationService.Instance.Format("stats.failureText", failure);
        StatsValueText.Text = LocalizationService.Instance.Format(
            "stats.valueEstimate",
            SteamPrice.FormatMinor(gross),
            currency.Label,
            SteamPrice.FormatMinor(net),
            priced,
            records.Length,
            failureText);
        CalcValueButton.IsEnabled = true;
    }
}
