using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace FarmPhantom;

public sealed record DropEntry(DateTime Time, string GameName, string CardName, bool IsFoil, string ImageUrl = "")
{
    public string TimeText => Time.ToString("HH:mm", CultureInfo.CurrentCulture);
}

public sealed record RunningGame(uint AppId, string Name)
{
    public string HeaderUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
    public string HeaderFallbackUrl => $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";
}

public sealed partial class MainWindow
{
    private string _lastTrayTip = string.Empty;
    private uint _nowPlayingAppId;
    private string _runningStripKey = string.Empty;

    private static readonly int[] IntervalOptions = [2, 3, 5];
    private static readonly int[] TimeoutOptions = [20, 30, 45, 60, 90];
    private static readonly IBrush LiveBrush = Brush.Parse("#3DF0B0");
    private static readonly IBrush IdleBrush = Brush.Parse("#5A5F86");

    private readonly ObservableCollection<DropEntry> _dropLog = [];
    private readonly HashSet<string> _knownCardAssetIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _dropMonitorCts;
    private DispatcherTimer? _uiTimer;
    private DateTime _lastDropUtc;
    private DateTime _lastInventoryReadUtc = DateTime.MinValue;
    private bool _dropBaselineReady;
    private int _sessionDrops;

    private TimeSpan CurrentInterval() =>
        TimeSpan.FromMinutes(IntervalOptions[Math.Clamp(DropIntervalComboBox.SelectedIndex, 0, IntervalOptions.Length - 1)]);

    private TimeSpan CurrentTimeout() =>
        TimeSpan.FromMinutes(TimeoutOptions[Math.Clamp(DropTimeoutComboBox.SelectedIndex, 0, TimeoutOptions.Length - 1)]);

    private void InitializeDrops()
    {
        DropLogListBox.ItemsSource = _dropLog;
        _dropLog.CollectionChanged += (_, _) => DropLogEmptyText.IsVisible = _dropLog.Count == 0;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateLiveStrip();
        _uiTimer.Start();
        UpdateLiveStrip();
    }

    private void UpdateLiveStrip()
    {
        SessionDropsText.Text = _sessionDrops.ToString(CultureInfo.CurrentCulture);

        var sessions = _idleManager.Sessions;
        UpdateOverview(sessions);

        var tip = sessions.Count == 0
            ? LocalizationService.Instance.Translate("tray.idleOff")
            : LocalizationService.Instance.Format("tray.running", sessions.Count, _sessionDrops);
        if (tip != _lastTrayTip)
        {
            _lastTrayTip = tip;
            (Application.Current as App)?.SetTrayToolTip(tip);
        }

        if (sessions.Count == 0)
        {
            LiveDot.Fill = IdleBrush;
            NowPlayingText.Text = LocalizationService.Instance.Translate("overview.idleOff");
            ElapsedText.Text = "—";
            SinceDropText.Text = "—";
            NowPlayingArt.IsVisible = false;
            RunningGamesStrip.IsVisible = false;
            _nowPlayingAppId = 0;
            _runningStripKey = string.Empty;
            return;
        }

        var first = sessions[0];
        NowPlayingArt.IsVisible = true;
        if (_nowPlayingAppId != first.AppId)
        {
            _nowPlayingAppId = first.AppId;
            var game = new RunningGame(first.AppId, first.Name);
            NowPlayingImage.FallbackUrl = game.HeaderFallbackUrl;
            NowPlayingImage.Url = game.HeaderUrl;
        }

        // Paralel idle: çalışan tüm oyunların küçük kapakları (yalnızca küme değişince yeniden kurulur).
        var stripKey = string.Join(',', sessions.Select(item => item.AppId));
        RunningGamesStrip.IsVisible = sessions.Count > 1;
        if (stripKey != _runningStripKey)
        {
            _runningStripKey = stripKey;
            RunningGamesStrip.ItemsSource = sessions
                .Take(10)
                .Select(item => new RunningGame(item.AppId, item.Name))
                .ToArray();
        }

        var now = DateTime.UtcNow;
        LiveDot.Fill = LiveBrush;
        NowPlayingText.Text = sessions.Count == 1
            ? $"{sessions[0].Name} çalışıyor"
            : $"{sessions.Count} oyun çalışıyor";
        ElapsedText.Text = FormatSpan(now - sessions[0].StartedUtc);
        SinceDropText.Text = _dropMonitorCts is null ? "izlenmiyor" : FormatSpan(now - _lastDropUtc);
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>İzleme döngüsü tüm çalışan oyunlar için tek bir envanter okuması yapar; oyun kalmayınca kendiliğinden biter.</summary>
    private void EnsureDropMonitor()
    {
        if (_dropMonitorCts is not null)
        {
            return;
        }

        RemainingDropsText.Text = LocalizationService.Instance.Translate("drops.remainingChecking");

        if (DropMonitorCheckBox.IsChecked != true)
        {
            DropStatusText.Text = LocalizationService.Instance.Translate("drops.monitorDisabled");
            return;
        }

        // Uzun bir aradan sonra envanter değişmiş olabilir; temiz bir başlangıç noktası al.
        if (DateTime.UtcNow - _lastInventoryReadUtc > TimeSpan.FromMinutes(15))
        {
            _dropBaselineReady = false;
        }

        var cts = new CancellationTokenSource();
        _dropMonitorCts = cts;
        _ = MonitorDropsAsync(cts);
    }

    private void StopDropMonitor()
    {
        var cts = _dropMonitorCts;
        _dropMonitorCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task MonitorDropsAsync(CancellationTokenSource source)
    {
        var ct = source.Token;
        try
        {
            var first = true;
            while (!ct.IsCancellationRequested && _idleManager.Count > 0)
            {
                if (!first)
                {
                    await Task.Delay(CurrentInterval(), ct);
                }

                first = false;
                if (_idleManager.Count == 0)
                {
                    break;
                }

                // Çerezler izleme sırasında yenilenmiş olabilir; her turda yeniden oku.
                if (!TryGetSession(out var session, out var sessionError))
                {
                    DropStatusText.Text = LocalizationService.Instance.Format("drops.inventoryReadFailed", sessionError,
                        LocalizationService.Instance.Translate("drops.retryNext"));
                    continue;
                }

                DropStatusText.Text = LocalizationService.Instance.Translate("drops.readingInventory");
                IReadOnlyList<SteamInventoryCard> cards;
                try
                {
                    cards = await _steamInventoryClient.GetCardsAsync(session, marketableOnly: false, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException
                    || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    var reason = ex is OperationCanceledException ? "Steam yanıt vermedi." : ex.Message;
                    AppLog.Warn("Envanter okunamadı", ex);
                    DropStatusText.Text = LocalizationService.Instance.Format("drops.inventoryReadFailed", reason,
                        LocalizationService.Instance.Translate("drops.retryNext"));
                    continue;
                }

                _lastInventoryReadUtc = DateTime.UtcNow;
                var interval = CurrentInterval();

                if (!_dropBaselineReady)
                {
                    _knownCardAssetIds.Clear();
                    foreach (var card in cards)
                    {
                        _knownCardAssetIds.Add(card.AssetId);
                    }

                    _dropBaselineReady = true;
                }
                else
                {
                    ProcessNewCards(cards);
                }

                DropStatusText.Text = LocalizationService.Instance.Format("drops.monitoring", _idleManager.Count, interval.TotalMinutes);
                await CheckSessionsAsync(session, CurrentTimeout(), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Idle durdu veya pencere kapandı.
        }
        finally
        {
            if (ReferenceEquals(_dropMonitorCts, source))
            {
                _dropMonitorCts = null;
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// Yeni görülen kartlar yalnızca şu an idle edilen bir oyuna aitse düşüş sayılır;
    /// takas veya başka yolla gelen kartlar sessizce bilinenlere eklenir.
    /// </summary>
    private void ProcessNewCards(IReadOnlyList<SteamInventoryCard> cards)
    {
        foreach (var card in cards)
        {
            if (!_knownCardAssetIds.Add(card.AssetId))
            {
                continue;
            }

            var session = card.AppId == 0 ? null : _idleManager.Get(card.AppId);
            if (session is null)
            {
                continue;
            }

            var now = DateTime.UtcNow;
            session.LastDropUtc = now;
            session.DropCount++;
            _lastDropUtc = now;
            _sessionDrops++;

            AppLog.Info($"Kart düştü: {card.Name} ({session.Name}, {card.AppId}){(card.IsFoil ? " foil" : string.Empty)}.");
            _recoveryAttempts.Remove(card.AppId);
            var entry = new DropEntry(DateTime.Now, session.Name, card.Name, card.IsFoil, card.ImageUrl);
            _dropLog.Insert(0, entry);
            AddRecentDrop(entry);
            if (NotifyDropCheckBox.IsChecked == true)
            {
                DesktopNotifier.Show(
                    LocalizationService.Instance.Translate(card.IsFoil ? "notify.foilDropTitle" : "notify.cardDropTitle"),
                    $"{card.Name}\n{session.Name}");
            }

            if (SoundDropCheckBox.IsChecked == true)
            {
                DesktopNotifier.Beep();
            }

            RecordDrop(new DropRecord(now, card.AppId, session.Name, card.Name, card.MarketHashName, card.IsFoil));
            DropStatusText.Text = LocalizationService.Instance.Format("drops.newCard", card.Name, session.Name);

            NotifyBot("drop",
                LocalizationService.Instance.Translate(card.IsFoil ? "notify.foilDropTitle" : "notify.cardDropTitle"),
                $"{card.Name}\n{session.Name}");
            UpdatePresence();
        }
    }

    /// <summary>
    /// Her oyun için rozet sayfasından kalan düşüşü okur: sıfırsa oyun hemen bitirilir. Okunamazsa
    /// (ya da kalan sayı sıfırdan büyükken uzun süre kart gelmezse) süre sınırı devreye girer.
    /// </summary>
    private async Task CheckSessionsAsync(MarketSession session, TimeSpan timeout, CancellationToken ct)
    {
        var sessions = _idleManager.Sessions.Where(item => !item.Stopping).ToArray();
        for (var i = 0; i < sessions.Length; i++)
        {
            var current = sessions[i];
            if (i > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            int? remaining = null;
            try
            {
                remaining = await _badgeClient.GetRemainingDropsAsync(session, current.AppId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                AppLog.Warn($"Rozet düşüş sayısı okunamadı: {current.Name} ({current.AppId}).", ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLog.Warn($"Rozet sayfası zaman aşımına uğradı: {current.Name} ({current.AppId}).");
            }

            if (remaining is int count)
            {
                current.Remaining = count;
                UpdateRemainingCards(current.AppId, count);
                if (count == 0)
                {
                    FinishSession(current, "rozet sayfasına göre kalan kart düşüşü yok");
                    continue;
                }
            }

            if (DateTime.UtcNow - current.LastDropUtc >= timeout)
            {
                FinishSession(current, $"{timeout.TotalMinutes:0} dakikadır yeni kart gelmedi; kartların bittiği varsayıldı");
            }
        }

        var known = _idleManager.Sessions.Where(item => item.Remaining is not null).ToArray();
        RemainingDropsText.Text = known.Length == 0
            ? LocalizationService.Instance.Translate("drops.remainingUnknown")
            : LocalizationService.Instance.Format("drops.remainingTotal", known.Sum(item => item.Remaining ?? 0), known.Length);
    }

}
