using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace FarmPhantom;

public sealed partial class MainWindow : Window
{
    private static readonly Regex AppIdPattern = new("^[1-9][0-9]{0,9}$", RegexOptions.Compiled);
    private static readonly int[] ParallelChoices = [1, 2, 4, 8, 16, 32];

    private readonly SteamInventoryClient _steamInventoryClient = new();
    private readonly GameBlacklistStore _gameBlacklistStore = new();
    private readonly HashSet<uint> _blacklistedAppIds = [];
    private readonly HashSet<uint> _installedAppIds = [];
    private readonly HashSet<uint> _finishedThisRun = [];
    private readonly ObservableCollection<SteamGame> _idleQueue = [];
    private readonly IdleManager _idleManager;
    private CancellationTokenSource? _inventoryCancellation;
    private IReadOnlyList<SteamInventoryCard> _tradingCards = [];
    private IReadOnlyList<SteamGame> _ownedGames = [];
    private bool _queueRunning;
    private bool _isUiInitialized;

    public MainWindow()
    {
        _idleManager = new IdleManager((session, exitCode) =>
            Dispatcher.UIThread.Post(() => HandleSessionExited(session, exitCode)));

        InitializeComponent();
        Opened += MainWindow_Opened;
        _isUiInitialized = true;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FarmPhantom/FarmPhantom.ico")));
        QueueListBox.ItemsSource = _idleQueue;
        UpdateQueueControls();
        InitializeOverview();
        InitializeNavigation();
        InitializeDrops();
        LogPathText.Text = LocalizationService.Instance.Format("settings.logFolderLabel", AppLog.LogDirectory);
        InitializeSales();
        InitializePersistence();
        InitializeDiscord();
        RefreshNativeDllStatus();
    }

    private bool _cleanedUp;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // X düğmesi: tepsi seçeneği açıksa pencere kapanmaz, tepsiye saklanır ve idle çalışmaya devam eder.
        if (!_cleanedUp
            && TrayOnCloseCheckBox.IsChecked == true
            && e.CloseReason == WindowCloseReason.WindowClosing
            && (Application.Current as App)?.TrayAvailable == true)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private bool _trayHintShown;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == Window.WindowStateProperty
            && change.NewValue is WindowState.Minimized
            && _isUiInitialized
            && !_cleanedUp
            && TrayOnCloseCheckBox.IsChecked == true
            && (Application.Current as App)?.TrayAvailable == true)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        AppLog.Info("Pencere sistem tepsisine küçültüldü.");
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        DesktopNotifier.Show(
            LocalizationService.Instance.Translate("tray.title"),
            LocalizationService.Instance.Translate("tray.body"));
    }

    protected override void OnClosed(EventArgs e)
    {
        CleanupForExit();
        base.OnClosed(e);
    }

    /// <summary>Uygulama gerçekten kapanırken bir kez çalışır: idle süreçlerini durdurur, bağlantıları kapatır.</summary>
    internal void CleanupForExit()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        _recoveryGeneration++;
        _recovering.Clear();
        _inventoryCancellation?.Cancel();
        _inventoryCancellation?.Dispose();
        _salesCancellation?.Cancel();
        _salesCancellation?.Dispose();
        _valueCancellation?.Cancel();
        _valueCancellation?.Dispose();
        _badgeGamesCancellation?.Cancel();
        _badgeGamesCancellation?.Dispose();
        _uiTimer?.Stop();
        ShutdownDiscord();
        StopDropMonitor();
        _queueRunning = false;
        _idleManager.StopAll();
        AppLog.Info("Temizlik tamamlandı; idle süreçleri durduruldu.");
    }

    /// <summary>Tepsi menüsündeki "Idle'ı durdur".</summary>
    internal void StopAllFromTray() => StopAllIdle("tepsi menüsünden");

    private int MaxParallel =>
        ParallelChoices[Math.Clamp(MaxParallelComboBox.SelectedIndex, 0, ParallelChoices.Length - 1)];

    // ---- Oturum (yalnızca çerez) ----

    /// <summary>Çerez kutularından oturumu kurar; SteamID64 steamLoginSecure değerinden okunur.</summary>
    private bool TryGetSession([NotNullWhen(true)] out MarketSession? session, out string error)
    {
        if (!TryExtractSteamId(MarketLoginSecureTextBox.Text, out var steamId))
        {
            session = null;
            error = "Önce Oyunlar sayfasına sessionid ve steamLoginSecure çerezlerini gir.";
            return false;
        }

        return MarketSession.TryCreate(
            steamId,
            MarketSessionIdTextBox.Text,
            MarketLoginSecureTextBox.Text,
            out session,
            out error);
    }

    private async void LoadInventoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSession(out var session, out var sessionError))
        {
            InventoryStatusText.Text = sessionError;
            return;
        }

        _inventoryCancellation?.Cancel();
        _inventoryCancellation?.Dispose();
        _inventoryCancellation = new CancellationTokenSource();
        var cancellationToken = _inventoryCancellation.Token;
        LoadInventoryButton.IsEnabled = false;
        InventoryStatusText.Text = LocalizationService.Instance.Translate("inventory.loading");

        try
        {
            _tradingCards = await _steamInventoryClient.GetTradableCardsAsync(session, cancellationToken);
            TradingCardsListBox.ItemsSource = _tradingCards;
            InventorySummaryText.Text = LocalizationService.Instance.Format("inventory.summary", _tradingCards.Count);
            InventoryStatusText.Text = _tradingCards.Count == 0
                ? LocalizationService.Instance.Translate("inventory.empty")
                : LocalizationService.Instance.Translate("inventory.ready");
            OpenSelectedMarketButton.IsEnabled = false;
        }
        catch (HttpRequestException ex)
        {
            InventoryStatusText.Text = LocalizationService.Instance.Format("inventory.fetchFailed", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            InventoryStatusText.Text = ex.Message;
        }
        catch (System.Text.Json.JsonException)
        {
            InventoryStatusText.Text = LocalizationService.Instance.Translate("inventory.invalidResponse");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            InventoryStatusText.Text = LocalizationService.Instance.Translate("inventory.cancelled");
        }
        catch (OperationCanceledException)
        {
            InventoryStatusText.Text = LocalizationService.Instance.Translate("inventory.timeout");
        }
        finally
        {
            LoadInventoryButton.IsEnabled = true;
        }
    }

    private void TradingCardsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        OpenSelectedMarketButton.IsEnabled = TradingCardsListBox.SelectedItems?.Count == 1;
    }

    private void OpenSelectedMarketButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TradingCardsListBox.SelectedItem is not SteamInventoryCard card)
        {
            InventoryStatusText.Text = LocalizationService.Instance.Translate("inventory.selectOne");
            return;
        }

        var marketHashName = Uri.EscapeDataString(card.MarketHashName);
        InventoryStatusText.Text = OpenUrl($"https://steamcommunity.com/market/listings/753/{marketHashName}")
            ? LocalizationService.Instance.Format("inventory.cardOpened", card.Name)
            : LocalizationService.Instance.Translate("browser.unavailable");
    }

    private bool _suppressStartupToggle;

    private void StartWithWindowsCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _suppressStartupToggle)
        {
            return;
        }

        var wanted = StartWithWindowsCheckBox.IsChecked == true;
        if (!StartupManager.TrySet(wanted))
        {
            _suppressStartupToggle = true;
            StartWithWindowsCheckBox.IsChecked = StartupManager.IsEnabled();
            _suppressStartupToggle = false;
            StartupStatusText.Text = LocalizationService.Instance.Translate("settings.startupWriteFailed");
        }
    }

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
    {
        AppLog.Info("Günlük kullanıcı tarafından açıldı.");
        var file = AppLog.CurrentFile;
        if (!File.Exists(file))
        {
            LogPathText.Text = LocalizationService.Instance.Format("settings.logNotYet", file);
            return;
        }

        LogPathText.Text = OpenUrl(file) ? file : $"Dosya açılamadı. Elle aç: {file}";
    }

    private void OpenLogFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppLog.LogDirectory);
        LogPathText.Text = OpenUrl(AppLog.LogDirectory) ? AppLog.LogDirectory : $"Klasör açılamadı. Elle aç: {AppLog.LogDirectory}";
    }

    private static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ---- Oyun listesi ----

    private void RefreshGamesDisplay()
    {
        if (!_isUiInitialized)
        {
            return;
        }

        var selectedAppId = (SteamGamesListBox.SelectedItem as SteamGame)?.AppId;
        var searchText = GameSearchTextBox.Text?.Trim();
        var filteredGames = _ownedGames
            .Select(game => game with { IsBlacklisted = _blacklistedAppIds.Contains(game.AppId) })
            .Where(game => string.IsNullOrEmpty(searchText)
                || game.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || game.AppId.ToString().Contains(searchText, StringComparison.Ordinal))
            .Where(game => GameFilterComboBox.SelectedIndex switch
            {
                1 => !game.IsBlacklisted,
                2 => game.IsBlacklisted,
                3 => game.IsInstalled,
                4 => !game.IsInstalled,
                5 => game.RemainingCards is > 0,
                _ => true
            });
        var displayedGames = SortGames(filteredGames);
        SteamGamesListBox.ItemsSource = displayedGames;

        if (selectedAppId is uint appId)
        {
            SteamGamesListBox.SelectedItem = displayedGames.FirstOrDefault(game => game.AppId == appId);
        }

        var blacklistedCount = _ownedGames.Count(game => _blacklistedAppIds.Contains(game.AppId));
        var installedCount = _ownedGames.Count(game => _installedAppIds.Contains(game.AppId));
        GamesSummaryText.Text = LocalizationService.Instance.Format(
            "games.summary",
            _ownedGames.Count,
            installedCount,
            blacklistedCount,
            displayedGames.Length);

        GamesEmptyStateText.IsVisible = displayedGames.Length == 0;
        GamesEmptyStateText.Text = _ownedGames.Count == 0
            ? LocalizationService.Instance.Translate("games.notLoaded")
            : LocalizationService.Instance.Translate("games.noMatches");
        UpdateSelectedGameActions();
    }

    private void GameSearchTextBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshGamesDisplay();

    private void GameFilterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshGamesDisplay();
        SaveSettings();
    }

    private void UpdateSelectedGameActions()
    {
        if (SteamGamesListBox.SelectedItem is not SteamGame game)
        {
            ToggleBlacklistButton.IsEnabled = false;
            StartSelectedIdleButton.IsEnabled = false;
            return;
        }

        var isBlacklisted = _blacklistedAppIds.Contains(game.AppId);
        ToggleBlacklistButton.Content = LocalizationService.Instance.Translate(
            isBlacklisted ? "games.removeBlacklist" : "games.blacklistAdd");
        ToggleBlacklistButton.IsEnabled = true;
        StartSelectedIdleButton.IsEnabled = !isBlacklisted;
    }

    private void SteamGamesListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        if (SteamGamesListBox.SelectedItem is SteamGame game)
        {
            AppIdTextBox.Text = game.AppId.ToString();
        }

        UpdateSelectedGameActions();
    }

    private void ToggleBlacklistButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SteamGamesListBox.SelectedItem is not SteamGame game)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("games.selectForBlacklist");
            return;
        }

        var shouldBlacklist = !_blacklistedAppIds.Contains(game.AppId);
        if (shouldBlacklist)
        {
            _blacklistedAppIds.Add(game.AppId);
            _idleManager.Stop(game.AppId);
        }
        else
        {
            _blacklistedAppIds.Remove(game.AppId);
        }

        try
        {
            _gameBlacklistStore.Save(_blacklistedAppIds);
            RefreshGamesDisplay();
            IdleStatus.Text = shouldBlacklist
                ? LocalizationService.Instance.Format("games.addedBlacklist", game.Name)
                : LocalizationService.Instance.Format("games.removedBlacklist", game.Name);
        }
        catch (IOException)
        {
            RevertBlacklistChange(game.AppId, shouldBlacklist);
            IdleStatus.Text = LocalizationService.Instance.Translate("games.blacklistSaveFailed");
        }
        catch (UnauthorizedAccessException)
        {
            RevertBlacklistChange(game.AppId, shouldBlacklist);
            IdleStatus.Text = LocalizationService.Instance.Translate("games.blacklistNoPermission");
        }
    }

    private void RevertBlacklistChange(uint appId, bool wasAdded)
    {
        if (wasAdded)
        {
            _blacklistedAppIds.Remove(appId);
        }
        else
        {
            _blacklistedAppIds.Add(appId);
        }

        RefreshGamesDisplay();
    }

    // ---- Oyun sırası ----

    private void AddSelectedGamesToQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectedGames = SteamGamesListBox.SelectedItems?.OfType<SteamGame>().ToArray() ?? [];
        if (selectedGames.Length == 0)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.selectGames");
            return;
        }

        var existingIds = _idleQueue.Select(game => game.AppId).ToHashSet();
        var addedCount = 0;
        var ignoredBlacklistedCount = 0;
        foreach (var game in selectedGames)
        {
            if (_blacklistedAppIds.Contains(game.AppId))
            {
                ignoredBlacklistedCount++;
                continue;
            }

            if (existingIds.Add(game.AppId))
            {
                _idleQueue.Add(game with { IsBlacklisted = false });
                addedCount++;
            }
        }

        UpdateQueueControls();
        var ignoredText = ignoredBlacklistedCount > 0
            ? $" {ignoredBlacklistedCount} kara listedeki oyun eklenmedi."
            : string.Empty;
        IdleStatus.Text = LocalizationService.Instance.Format("queue.addedCount", addedCount, ignoredText);
    }

    private void StartQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_idleQueue.Count == 0)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.emptyStart");
            return;
        }

        _finishedThisRun.Clear();
        _queueRunning = true;
        FillSlots();
    }

    /// <summary>Boş idle yuvalarını (en fazla "aynı anda oyun" kadar) sıradaki oyunlarla doldurur.</summary>
    private void FillSlots()
    {
        if (!_queueRunning)
        {
            UpdateQueueControls();
            return;
        }

        while (_idleManager.Count < MaxParallel)
        {
            var next = NextQueuedGame();
            if (next is null)
            {
                break;
            }

            if (!StartIdle(next.AppId, next.Name))
            {
                _queueRunning = false;
                break;
            }
        }

        if (_queueRunning && _idleManager.Count == 0 && _recovering.Count == 0)
        {
            _queueRunning = false;
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.finished");
            NotifyBot("queue",
                LocalizationService.Instance.Translate("notify.queueFinishedTitle"),
                LocalizationService.Instance.Translate("notify.queueFinishedBody"));
        }

        UpdateQueueControls();
    }

    private SteamGame? NextQueuedGame()
    {
        var eligible = _idleQueue.Where(game =>
            !_blacklistedAppIds.Contains(game.AppId)
            && !_idleManager.IsRunning(game.AppId)
            && !_recovering.Contains(game.AppId)
            && !_finishedThisRun.Contains(game.AppId));

        return QueuePriorityComboBox.SelectedIndex switch
        {
            1 => eligible.OrderBy(game => GetRemainingCards(game) ?? int.MaxValue).FirstOrDefault(),
            2 => eligible.OrderByDescending(game => GetRemainingCards(game) ?? -1).FirstOrDefault(),
            _ => eligible.FirstOrDefault()
        };
    }

    private int? GetRemainingCards(SteamGame game)
    {
        var sessionRemaining = _idleManager.Get(game.AppId)?.Remaining;
        if (sessionRemaining is not null)
        {
            return sessionRemaining;
        }

        return _ownedGames.FirstOrDefault(item => item.AppId == game.AppId)?.RemainingCards
            ?? game.RemainingCards;
    }

    /// <summary>Çalışan en eski oyunu bitmiş sayıp durdurur; sıradaki oyun yerine geçer.</summary>
    private void NextQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        var oldest = _idleManager.Sessions.FirstOrDefault(session => !session.Stopping);
        if (oldest is null)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.noRunningGame");
            return;
        }

        FinishSession(oldest, "elle atlandı");
    }

    private void MoveQueueItemUpButton_Click(object? sender, RoutedEventArgs e) => MoveSelectedQueueItem(-1);

    private void MoveQueueItemDownButton_Click(object? sender, RoutedEventArgs e) => MoveSelectedQueueItem(1);

    private void MoveSelectedQueueItem(int offset)
    {
        var currentIndex = QueueListBox.SelectedIndex;
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _idleQueue.Count)
        {
            return;
        }

        _idleQueue.Move(currentIndex, targetIndex);
        QueueListBox.SelectedIndex = targetIndex;
        UpdateQueueControls();
    }

    private void RemoveQueueItemsButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectedItems = QueueListBox.SelectedItems?.OfType<SteamGame>().ToArray() ?? [];
        if (selectedItems.Length == 0)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.selectToRemove");
            return;
        }

        if (selectedItems.Any(game => _idleManager.IsRunning(game.AppId)))
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.removeRunningFirst");
            return;
        }

        foreach (var game in selectedItems)
        {
            _idleQueue.Remove(game);
        }

        UpdateQueueControls();
        IdleStatus.Text = LocalizationService.Instance.Format("queue.removedCount", selectedItems.Length);
    }

    private void ClearQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        _queueRunning = false;
        _idleManager.StopAll();
        _idleQueue.Clear();
        UpdateQueueControls();
        IdleStatus.Text = LocalizationService.Instance.Translate("queue.cleared");
    }

    private void UpdateQueueControls()
    {
        StartQueueButton.IsEnabled = _idleQueue.Count > 0 && !_queueRunning;
        NextQueueButton.IsEnabled = _idleManager.Sessions.Any(session => !session.Stopping);

        var waiting = _idleQueue.Count(game =>
            !_blacklistedAppIds.Contains(game.AppId)
            && !_idleManager.IsRunning(game.AppId)
            && !_finishedThisRun.Contains(game.AppId));
        QueueSummaryText.Text = _queueRunning
            ? LocalizationService.Instance.Format("queue.runningSummary", _idleManager.Count, waiting)
            : LocalizationService.Instance.Format("queue.countSummary", _idleQueue.Count);
    }

    // ---- Idle başlatma ve durdurma ----

    private void StartSelectedIdleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SteamGamesListBox.SelectedItem is not SteamGame game)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.selectGameFirst");
            return;
        }

        if (_blacklistedAppIds.Contains(game.AppId))
        {
            IdleStatus.Text = LocalizationService.Instance.Format("queue.blacklistedStartDenied", game.Name);
            return;
        }

        StartIdle(game.AppId, game.Name);
    }

    private void StartIdleByAppIdButton_Click(object? sender, RoutedEventArgs e)
    {
        var appIdText = AppIdTextBox.Text?.Trim();
        if (!uint.TryParse(appIdText, out var appId) || appId == 0)
        {
            SteamActionStatus.Text = LocalizationService.Instance.Translate("idle.appIdRequired");
            return;
        }

        if (_blacklistedAppIds.Contains(appId))
        {
            IdleStatus.Text = LocalizationService.Instance.Format("queue.blacklistedStartDenied", $"Steam App {appId}");
            return;
        }

        var selectedGame = SteamGamesListBox.SelectedItem as SteamGame;
        var name = selectedGame?.AppId == appId ? selectedGame.Name : $"Steam App {appId}";
        StartIdle(appId, name);
    }

    private bool StartIdle(uint appId, string gameName)
    {
        UpdateSteamStatus();

        if (!NativeSteamApiPresent())
        {
            _queueRunning = false;
            IdleStatus.Text = LocalizationService.Instance.Format("idle.nativeMissing", NativeSteamApiFileName);
            UpdateQueueControls();
            return false;
        }

        if (!TryGetSession(out _, out var sessionError))
        {
            _queueRunning = false;
            IdleStatus.Text = sessionError;
            UpdateQueueControls();
            return false;
        }

        if (!_idleManager.TryStart(appId, gameName, out var session, out var error) || session is null)
        {
            IdleStatus.Text = error;
            AppLog.Warn($"Idle başlatılamadı: {gameName} ({appId}): {error}");
            return false;
        }

        AppLog.Info($"Idle başladı: {gameName} ({appId}); çalışan oyun sayısı {_idleManager.Count}.");

        if (_idleManager.Count == 1)
        {
            _lastDropUtc = session.StartedUtc;
        }

        StopIdleButton.IsEnabled = true;
        EnsureDropMonitor();
        IdleStatus.Text = LocalizationService.Instance.Format("idle.startedWarning", gameName, _idleManager.Count);
        SteamActionStatus.Text = LocalizationService.Instance.Format("idle.started", gameName);
        UpdatePresence();
        UpdateQueueControls();
        return true;
    }

    /// <summary>Oyunu bitmiş sayar, idle sürecini durdurur; sıra çalışıyorsa çıkış işleyicisi sıradakini başlatır.</summary>
    private void FinishSession(IdleSession session, string reason)
    {
        if (session.Stopping)
        {
            return;
        }

        _finishedThisRun.Add(session.AppId);
        AppLog.Info($"Oyun tamamlandı: {session.Name} ({session.AppId}): {reason}; düşen kart {session.DropCount}.");
        DropStatusText.Text = $"{session.Name}: {reason}.";
        NotifyBot("finish",
            LocalizationService.Instance.Format("notify.gameFinishedTitle", session.Name),
            LocalizationService.Instance.Format("notify.gameFinishedBody", reason, session.DropCount));
        _idleManager.Stop(session.AppId);
    }

    private void HandleSessionExited(IdleSession session, int exitCode)
    {
        var wasStopping = session.Stopping;
        AppLog.Info($"Idle süreci çıktı: {session.Name} ({session.AppId}), çıkış kodu {exitCode}{(wasStopping ? " (istenen durdurma)" : string.Empty)}{(string.IsNullOrEmpty(session.LastError) ? string.Empty : $", hata: {session.LastError}")}.");
        _idleManager.Remove(session);

        if (_idleManager.Count == 0)
        {
            StopDropMonitor();
            StopIdleButton.IsEnabled = false;
        }

        if (wasStopping)
        {
            if (_idleManager.Count == 0 && !_queueRunning)
            {
                IdleStatus.Text = LocalizationService.Instance.Translate("idle.stopped");
            }
        }
        else if (exitCode == 0)
        {
            _finishedThisRun.Add(session.AppId);
            IdleStatus.Text = LocalizationService.Instance.Format("idle.sessionEnded", session.Name);
        }
        else
        {
            var text = DescribeExit(session, exitCode);
            if (!TryScheduleRecovery(session, exitCode, text))
            {
                _queueRunning = false;
                IdleStatus.Text = text;
                var stoppedTitle = LocalizationService.Instance.Translate("notify.idleStoppedTitle");
                NotifyBot("error", stoppedTitle, text);
                if (NotifyDropCheckBox.IsChecked == true)
                {
                    DesktopNotifier.Show(stoppedTitle, text);
                }
            }
        }

        if (_queueRunning)
        {
            FillSlots();
        }
        else
        {
            UpdateQueueControls();
        }

        UpdatePresence();
    }

    // ---- Otomatik toparlanma ----

    private static readonly TimeSpan[] RecoveryDelays =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];

    private readonly Dictionary<uint, int> _recoveryAttempts = [];
    private readonly HashSet<uint> _recovering = [];
    private int _recoveryGeneration;

    /// <summary>
    /// Idle süreci beklenmedik şekilde çıktıysa (Steam kapandı, süreç çöktü) oyunu gecikmeyle yeniden başlatmayı planlar.
    /// DLL/mimari hataları (4, 5) kalıcı olduğundan denenmez. Süreç 10 dakikadan uzun çalıştıysa deneme sayacı sıfırlanır.
    /// </summary>
    private bool TryScheduleRecovery(IdleSession session, int exitCode, string reason)
    {
        if (AutoRecoverCheckBox.IsChecked != true || exitCode is 4 or 5 || _cleanedUp)
        {
            return false;
        }

        if (DateTime.UtcNow - session.StartedUtc >= TimeSpan.FromMinutes(10))
        {
            _recoveryAttempts.Remove(session.AppId);
        }

        var attempts = _recoveryAttempts.GetValueOrDefault(session.AppId);
        if (attempts >= RecoveryDelays.Length)
        {
            AppLog.Warn($"Otomatik toparlanma vazgeçildi: {session.Name} ({session.AppId}), {attempts} deneme başarısız.");
            _recoveryAttempts.Remove(session.AppId);
            return false;
        }

        var delay = RecoveryDelays[attempts];
        _recoveryAttempts[session.AppId] = attempts + 1;
        _recovering.Add(session.AppId);

        var appId = session.AppId;
        var name = session.Name;
        var generation = _recoveryGeneration;
        var text = LocalizationService.Instance.Format("notify.idleRetryBody", name, FormatLocalizedDelay(delay), attempts + 1, RecoveryDelays.Length);
        IdleStatus.Text = text;
        AppLog.Warn($"Idle beklenmedik şekilde durdu: {name} ({appId}), kod {exitCode}. {FormatDelay(delay)} sonra yeniden denenecek (deneme {attempts + 1}). Neden: {reason}");
        NotifyBot("error", LocalizationService.Instance.Translate("notify.idleRetryTitle"), text);

        DispatcherTimer.RunOnce(() => RecoverStart(appId, name, generation), delay);
        return true;
    }

    private void RecoverStart(uint appId, string name, int generation)
    {
        if (generation != _recoveryGeneration || _cleanedUp || !_recovering.Remove(appId))
        {
            return;
        }

        if (_idleManager.IsRunning(appId) || _finishedThisRun.Contains(appId) || _blacklistedAppIds.Contains(appId))
        {
            return;
        }

        AppLog.Info($"Otomatik yeniden deneme: {name} ({appId}).");
        if (_queueRunning)
        {
            FillSlots();
        }
        else if (_idleManager.Count < MaxParallel)
        {
            StartIdle(appId, name);
        }
    }

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} dakika" : $"{delay.TotalSeconds:0} saniye";

    private static string FormatLocalizedDelay(TimeSpan delay) =>
        LocalizationService.Instance.Format(
            delay.TotalMinutes >= 1 ? "time.minutes" : "time.seconds",
            delay.TotalMinutes >= 1 ? (object)(int)delay.TotalMinutes : (int)delay.TotalSeconds);

    private static string DescribeExit(IdleSession session, int exitCode) => exitCode switch
    {
        4 => LocalizationService.Instance.Translate("idle.exitNativeMissing"),
        3 => LocalizationService.Instance.Translate("idle.exitSteamInitFailed"),
        5 => LocalizationService.Instance.Translate("idle.exitArchMismatch"),
        _ => LocalizationService.Instance.Format("idle.exitFailed", session.Name, exitCode,
            string.IsNullOrWhiteSpace(session.LastError) ? string.Empty : $" {session.LastError}")
    };

    private void StopIdleButton_Click(object? sender, RoutedEventArgs e) => StopAllIdle("düğmeyle");

    private void StopAllIdle(string source)
    {
        _queueRunning = false;
        _recoveryGeneration++;
        _recovering.Clear();
        _recoveryAttempts.Clear();
        _idleManager.StopAll();
        AppLog.Info($"Tüm idle işlemleri durduruldu ({source}).");
        IdleStatus.Text = LocalizationService.Instance.Translate("idle.stopping");
        UpdateQueueControls();
    }

    private void UpdateSteamStatus()
    {
        var isRunning = Process.GetProcessesByName("steam").Length > 0;
        var status = LocalizationService.Instance.Translate(isRunning ? "status.steamClientOpen" : "status.steamClientClosed");
        SidebarSteamStatus.Text = status;
        HeaderSteamStatus.Text = LocalizationService.Instance.Translate(isRunning ? "status.steamOpen" : "status.steamClosed");
    }

    private void LaunchGameButton_Click(object? sender, RoutedEventArgs e)
    {
        var appId = AppIdTextBox.Text?.Trim();
        if (appId is null || !AppIdPattern.IsMatch(appId))
        {
            SteamActionStatus.Text = LocalizationService.Instance.Translate("steam.appIdInvalid");
            return;
        }

        SteamActionStatus.Text = OpenUrl($"steam://run/{appId}")
            ? LocalizationService.Instance.Format("steam.launchSent", appId)
            : LocalizationService.Instance.Translate("steam.launchFailed");
    }

    private void OpenBadgePageButton_Click(object? sender, RoutedEventArgs e)
    {
        var appId = AppIdTextBox.Text?.Trim();
        if (appId is null || !AppIdPattern.IsMatch(appId))
        {
            SteamActionStatus.Text = LocalizationService.Instance.Translate("steam.badgeAppIdInvalid");
            return;
        }

        SteamActionStatus.Text = OpenUrl($"https://steamcommunity.com/my/gamecards/{appId}/")
            ? LocalizationService.Instance.Translate("steam.badgeOpened")
            : LocalizationService.Instance.Translate("browser.unavailable");
    }

    private static string NativeSteamApiFileName =>
        OperatingSystem.IsWindows() ? "steam_api64.dll"
        : OperatingSystem.IsMacOS() ? "libsteam_api.dylib"
        : "libsteam_api.so";

    private static bool NativeSteamApiPresent() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, NativeSteamApiFileName));

    private void RefreshNativeDllStatus() =>
        NativeDllStatusText.Text = NativeSteamApiPresent()
            ? LocalizationService.Instance.Format("native.present", NativeSteamApiFileName)
            : LocalizationService.Instance.Format("native.missing", NativeSteamApiFileName, AppContext.BaseDirectory);

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_Opened;
        if (OperatingSystem.IsWindows() && !NativeSteamApiPresent())
        {
            await ChooseSteamworksRuntimeAsync();
        }
    }

    private async void ChooseSteamworksRuntime_Click(object? sender, RoutedEventArgs e) =>
        await ChooseSteamworksRuntimeAsync();

    private async Task ChooseSteamworksRuntimeAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = LocalizationService.Instance.Translate("native.chooseTitle"),
            FileTypeFilter =
            [
                new FilePickerFileType(NativeSteamApiFileName)
                {
                    Patterns = [NativeSteamApiFileName]
                }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var sourcePath = files[0].Path.LocalPath;
        if (!Path.GetFileName(sourcePath).Equals(NativeSteamApiFileName, StringComparison.OrdinalIgnoreCase))
        {
            NativeDllStatusText.Text = LocalizationService.Instance.Format("native.chooseWrongFile", NativeSteamApiFileName);
            return;
        }

        var destinationPath = Path.Combine(AppContext.BaseDirectory, NativeSteamApiFileName);
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            RefreshNativeDllStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Error("Steamworks çalışma zamanı kopyalanamadı.", ex);
            NativeDllStatusText.Text = LocalizationService.Instance.Format("native.chooseFailed", ex.Message);
        }
    }

}
