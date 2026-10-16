using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Net.Http;

namespace FarmPhantom;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _badgeGamesCancellation;

    /// <summary>steamLoginSecure çerezinin başındaki 17 haneli SteamID64'ü çıkarır.</summary>
    private static bool TryExtractSteamId(string? loginSecure, out string steamId)
    {
        steamId = string.Empty;
        var text = (loginSecure ?? string.Empty).Trim().Trim('"');
        const string prefix = "steamLoginSecure=";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }

        var end = text.IndexOf("%7C%7C", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = text.IndexOf("||", StringComparison.Ordinal);
        }

        if (end != 17 || !text[..17].All(char.IsAsciiDigit))
        {
            return false;
        }

        steamId = text[..17];
        return true;
    }

    private async void LoadBadgeGamesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSession(out var session, out var sessionError))
        {
            IdleStatus.Text = sessionError;
            return;
        }

        _badgeGamesCancellation?.Cancel();
        _badgeGamesCancellation?.Dispose();
        _badgeGamesCancellation = new CancellationTokenSource();
        var ct = _badgeGamesCancellation.Token;

        LoadBadgeGamesButton.IsEnabled = false;
        IdleStatus.Text = LocalizationService.Instance.Translate("badges.loading");

        try
        {
            var badgeGames = await _badgeClient.GetBadgeGamesAsync(
                session,
                page => IdleStatus.Text = LocalizationService.Instance.Format("badges.pageLoading", page),
                ct);

            string? installScanNote = null;
            _installedAppIds.Clear();
            try
            {
                _installedAppIds.UnionWith(SteamLibraryScanner.FindInstalledGames().Select(game => game.AppId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                installScanNote = "kurulu oyun durumu belirlenemedi";
            }

            _blacklistedAppIds.UnionWith(LoadBlacklistSafely());
            _ownedGames = badgeGames
                .Select(game => new SteamGame(
                    game.AppId,
                    game.Name,
                    _blacklistedAppIds.Contains(game.AppId),
                    _installedAppIds.Contains(game.AppId),
                    game.Remaining))
                .ToArray();

            GameSortComboBox.SelectedIndex = 4;
            RefreshGamesDisplay();
            UpdateSteamStatus();

            var withCards = badgeGames.Count(game => game.Remaining is > 0);
            var remainingTotal = badgeGames.Sum(game => game.Remaining ?? 0);
            IdleStatus.Text = LocalizationService.Instance.Format("badges.summary", badgeGames.Count, withCards, remainingTotal);
            if (installScanNote is not null)
            {
                IdleStatus.Text += $" ({installScanNote}.)";
            }

            if (AutoQueueCheckBox.IsChecked == true)
            {
                var queuedCount = QueueGamesWithRemainingCards(removeFinished: true, out var removedCount, out _);
                var removedText = removedCount > 0 ? $", {removedCount} bitmiş oyun sıradan çıkarıldı" : string.Empty;
                IdleStatus.Text += $" {queuedCount} oyun sıraya eklendi{removedText}.";

                if (!_queueRunning && _idleQueue.Count > 0)
                {
                    _finishedThisRun.Clear();
                    _queueRunning = true;
                    FillSlots();
                }
            }
        }
        catch (InvalidDataException ex)
        {
            IdleStatus.Text = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            IdleStatus.Text = LocalizationService.Instance.Format("badges.fetchFailed", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("badges.cancelled");
        }
        catch (OperationCanceledException)
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("common.steamTimeout");
        }
        finally
        {
            LoadBadgeGamesButton.IsEnabled = true;
        }
    }

    private IEnumerable<uint> LoadBlacklistSafely()
    {
        try
        {
            return _gameBlacklistStore.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Kalan kart düşüşü olan (kara listede olmayan) oyunları, kalan kart sayısı çoktan aza, sıraya ekler.</summary>
    private int QueueGamesWithRemainingCards(bool removeFinished, out int removed, out int skippedBlacklisted)
    {
        removed = 0;
        skippedBlacklisted = 0;

        if (removeFinished && !_queueRunning)
        {
            var finishedIds = _ownedGames
                .Where(game => game.RemainingCards == 0)
                .Select(game => game.AppId)
                .ToHashSet();
            for (var i = _idleQueue.Count - 1; i >= 0; i--)
            {
                if (finishedIds.Contains(_idleQueue[i].AppId) && !_idleManager.IsRunning(_idleQueue[i].AppId))
                {
                    _idleQueue.RemoveAt(i);
                    removed++;
                }
            }
        }

        var existingIds = _idleQueue.Select(game => game.AppId).ToHashSet();
        var added = 0;
        var candidates = _ownedGames
            .Where(game => game.RemainingCards is > 0)
            .OrderByDescending(game => game.RemainingCards)
            .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var game in candidates)
        {
            if (_blacklistedAppIds.Contains(game.AppId))
            {
                skippedBlacklisted++;
                continue;
            }

            if (existingIds.Add(game.AppId))
            {
                _idleQueue.Add(game with { IsBlacklisted = false });
                added++;
            }
        }

        UpdateQueueControls();
        return added;
    }

    private void QueueRemainingButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_ownedGames.Any(game => game.RemainingCards is not null))
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("queue.remainingUnknownFirst");
            return;
        }

        var added = QueueGamesWithRemainingCards(removeFinished: false, out _, out var skipped);
        var skippedText = skipped > 0 ? $" {skipped} kara listedeki oyun eklenmedi." : string.Empty;
        IdleStatus.Text = added == 0
            ? LocalizationService.Instance.Format("queue.noRemainingGames", skippedText)
            : LocalizationService.Instance.Format("queue.gamesAdded", added, skippedText);
    }

    /// <summary>İzleme sırasında rozet sayfasından okunan güncel kalan kart sayısını oyun listesine yansıtır.</summary>
    private void UpdateRemainingCards(uint appId, int remaining)
    {
        if (!_ownedGames.Any(game => game.AppId == appId && game.RemainingCards != remaining))
        {
            return;
        }

        _ownedGames = _ownedGames
            .Select(game => game.AppId == appId ? game with { RemainingCards = remaining } : game)
            .ToArray();
        RefreshGamesDisplay();
    }
}
