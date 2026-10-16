using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Globalization;
using System.Text;

namespace FarmPhantom;

public sealed partial class MainWindow
{
    private readonly DiscordPresenceService _presence = new();
    private DiscordBotClient? _discordBot;
    private string? _discordBotStatusKey;
    private string? _discordBotStatusRaw;

    private void SetDiscordBotStatus(string key)
    {
        _discordBotStatusKey = key;
        _discordBotStatusRaw = null;
        DiscordBotStatusText.Text = LocalizationService.Instance.Translate(key);
    }

    private void SetDiscordBotRawStatus(string status)
    {
        _discordBotStatusRaw = status;
        _discordBotStatusKey = null;
        DiscordBotStatusText.Text = LocalizationService.Instance.TranslateBotStatus(status);
    }

    private void RefreshDiscordBotStatus()
    {
        if (_discordBotStatusKey is not null)
        {
            DiscordBotStatusText.Text = LocalizationService.Instance.Translate(_discordBotStatusKey);
        }
        else if (_discordBotStatusRaw is not null)
        {
            DiscordBotStatusText.Text = LocalizationService.Instance.TranslateBotStatus(_discordBotStatusRaw);
        }
    }

    private void InitializeDiscord()
    {
        JoinDiscordButton.IsVisible = BrandConfig.HasInvite;
        JoinDiscordSettingsButton.IsVisible = BrandConfig.HasInvite;

        if (!BrandConfig.HasPresence)
        {
            PresenceCheckBox.IsEnabled = false;
            PresenceShowGameCheckBox.IsEnabled = false;
            PresenceStatusText.Text = LocalizationService.Instance.Translate("discord.presenceUnconfigured");
        }

        UpdatePresence();

        // Kişisel Discord botu: token yerelde şifrelenir ve uygulama kapanınca bağlantı kapanır.
        var bot = new DiscordBotClient { CommandHandler = HandleDiscordCommandAsync };
        bot.StatusChanged += text =>
        {
            AppLog.Info($"Discord botu: {text}");
            Dispatcher.UIThread.Post(() =>
            {
                SetDiscordBotRawStatus(text);
            });
        };
        _discordBot = bot;

        if (!bot.IsSupported)
        {
            DiscordBotTokenTextBox.IsEnabled = false;
            ConnectDiscordBotButton.IsEnabled = false;
            DisconnectDiscordBotButton.IsEnabled = false;
            SetDiscordBotStatus("discord.tokenStorageUnsupported");
            return;
        }

        var savedToken = bot.LoadSavedToken();
        if (!string.IsNullOrWhiteSpace(savedToken))
        {
            SetDiscordBotStatus("discord.savedBotConnecting");
            _ = StartSavedDiscordBotAsync(bot, savedToken);
        }
        else
        {
            SetDiscordBotStatus("discord.botHelp");
        }
    }

    private void ShutdownDiscord()
    {
        _presence.Stop();
        if (_discordBot is not null)
        {
            _ = _discordBot.DisposeAsync();
        }
    }

    private async Task StartSavedDiscordBotAsync(DiscordBotClient bot, string token)
    {
        var result = await bot.StartAsync(token, persistToken: false);
        if (!result.Ok)
        {
            SetDiscordBotRawStatus(result.Message);
        }
    }

    // ---- Butonlar ----

    private void JoinDiscordButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!BrandConfig.HasInvite || !OpenUrl(BrandConfig.DiscordInviteUrl))
        {
            IdleStatus.Text = LocalizationService.Instance.Translate("discord.inviteFailed");
        }
    }

    private async void ConnectDiscordBotButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_discordBot is null)
        {
            return;
        }

        var token = DiscordBotTokenTextBox.Text?.Trim() ?? string.Empty;
        ConnectDiscordBotButton.IsEnabled = false;
        SetDiscordBotStatus("discord.connecting");
        try
        {
            var (ok, message) = await _discordBot.StartAsync(token);
            SetDiscordBotRawStatus(message);
            if (ok) DiscordBotTokenTextBox.Text = string.Empty;
        }
        finally
        {
            ConnectDiscordBotButton.IsEnabled = true;
        }
    }

    private async void DisconnectDiscordBotButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_discordBot is null)
        {
            return;
        }

        await _discordBot.StopAndForgetAsync();
    }

    private void PresenceSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        SaveSettings();
        UpdatePresence();
    }

    // ---- Rich Presence ----

    private void UpdatePresence()
    {
        if (!_isUiInitialized || PresenceCheckBox.IsChecked != true || !BrandConfig.HasPresence)
        {
            _presence.Stop();
            return;
        }

        _presence.Start();
        if (!_presence.IsActive)
        {
            PresenceStatusText.Text = LocalizationService.Instance.Translate("discord.presenceUnavailable");
            return;
        }

        var sessions = _idleManager.Sessions;
        if (sessions.Count == 0)
        {
            _presence.Clear();
            PresenceStatusText.Text = LocalizationService.Instance.Translate("discord.presenceIdle");
            return;
        }

        var showGame = PresenceShowGameCheckBox.IsChecked == true && sessions.Count == 1;
        var details = showGame ? sessions[0].Name : "Steam kartı farm ediyor";
        var state = $"{sessions.Count} oyun · oturumda {_sessionDrops} kart";
        _presence.Update(details, state, sessions[0].StartedUtc);
        var id = BrandConfig.DiscordApplicationId;
        var idTail = id.Length >= 4 ? $" [Uygulama ID: …{id[^4..]}, görsel: {BrandConfig.PresenceImageKey}]" : string.Empty;
        PresenceStatusText.Text = (_presence.IsReady
            ? LocalizationService.Instance.Translate("discord.presenceActive")
            : _presence.LastError ?? LocalizationService.Instance.Translate("discord.presenceConnecting")) + idTail;
    }

    // ---- Bot bildirimleri ve uzaktan komutlar ----

    /// <summary>Bot açıksa ve bildirimler etkinse kart olayını bot sahibine DM olarak gönderir.</summary>
    private void NotifyBot(string kind, string title, string body)
    {
        if (BotNotifyCheckBox.IsChecked != true)
        {
            return;
        }

        if (_discordBot?.IsRunning == true)
        {
            _ = _discordBot.NotifyAsync(kind, title, body);
        }
    }

    private async Task<string> HandleDiscordCommandAsync(string command, string argument)
    {
        AppLog.Info($"Bot komutu alındı: {command}");
        return await Dispatcher.UIThread.InvokeAsync(() => ExecuteRemoteCommand(command, argument));
    }

    /// <summary>Discord'dan gelen komutlar. Satış ve ilan verme bilerek yoktur; onlar yalnızca uygulama başında yapılır.</summary>
    private string ExecuteRemoteCommand(string command, string argument)
    {
        switch (command)
        {
            case "durum":
                return BuildStatusText();

            case "baslat":
            {
                if (!uint.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
                {
                    return LocalizationService.Instance.Translate("discord.reply.invalidAppId");
                }

                if (_blacklistedAppIds.Contains(appId))
                {
                    return LocalizationService.Instance.Translate("discord.reply.blacklisted");
                }

                var name = _ownedGames.FirstOrDefault(game => game.AppId == appId)?.Name ?? $"Steam App {appId}";
                return StartIdle(appId, name)
                    ? LocalizationService.Instance.Format("discord.reply.started", name)
                    : LocalizationService.Instance.Format("discord.reply.startFailed", IdleStatus.Text);
            }

            case "durdur":
                if (_idleManager.Count == 0)
                {
                    return LocalizationService.Instance.Translate("discord.reply.noIdle");
                }

                _queueRunning = false;
                _idleManager.StopAll();
                UpdateQueueControls();
                return LocalizationService.Instance.Translate("discord.reply.stopping");

            case "sonraki":
            {
                var oldest = _idleManager.Sessions.FirstOrDefault(session => !session.Stopping);
                if (oldest is null)
                {
                    return LocalizationService.Instance.Translate("discord.reply.noRunningGame");
                }

                FinishSession(oldest, "Discord'dan atlandı");
                return LocalizationService.Instance.Format("discord.reply.skipped", oldest.Name);
            }

            case "sira":
                return BuildQueueText();

            case "istatistik":
                return BuildStatsText();

            default:
                return LocalizationService.Instance.Translate("discord.reply.unknownCommand");
        }
    }

    private string BuildStatusText()
    {
        var sessions = _idleManager.Sessions;
        if (sessions.Count == 0)
        {
            return LocalizationService.Instance.Format("discord.reply.statusIdle", _idleQueue.Count, _sessionDrops);
        }

        var text = new StringBuilder();
        text.AppendLine(LocalizationService.Instance.Format(
            "discord.reply.statusActive", sessions.Count, FormatSpan(DateTime.UtcNow - sessions[0].StartedUtc), _sessionDrops));
        foreach (var session in sessions.Take(15))
        {
            var remaining = session.Remaining is int count
                ? LocalizationService.Instance.Format("discord.reply.gameRemaining", count)
                : LocalizationService.Instance.Translate("discord.reply.gameRemainingUnknown");
            var drops = LocalizationService.Instance.Format("discord.reply.gameDrops", session.DropCount);
            text.AppendLine($"• {session.Name} — {remaining}, {drops}");
        }

        if (sessions.Count > 15)
        {
            text.AppendLine(LocalizationService.Instance.Format("discord.reply.moreGames", sessions.Count - 15));
        }

        return text.ToString().TrimEnd();
    }

    private string BuildQueueText()
    {
        if (_idleQueue.Count == 0)
        {
            return LocalizationService.Instance.Translate("discord.reply.queueEmpty");
        }

        var text = new StringBuilder();
        text.AppendLine(LocalizationService.Instance.Format(
            _queueRunning ? "discord.reply.queueHeadingRunning" : "discord.reply.queueHeadingStopped", _idleQueue.Count));
        foreach (var game in _idleQueue.Take(15))
        {
            var state = _idleManager.IsRunning(game.AppId) ? "▶"
                : _finishedThisRun.Contains(game.AppId) ? "✔"
                : "·";
            text.AppendLine($"{state} {game.Name}");
        }

        if (_idleQueue.Count > 15)
        {
            text.AppendLine(LocalizationService.Instance.Format("discord.reply.moreGames", _idleQueue.Count - 15));
        }

        return text.ToString().TrimEnd();
    }

    private string BuildStatsText()
    {
        var today = DateTime.Now.Date;
        var local = _dropHistory.Select(record => record.TimeUtc.ToLocalTime()).ToArray();
        var todayCount = local.Count(time => time.Date == today);
        var weekCount = local.Count(time => time.Date > today.AddDays(-7));
        return LocalizationService.Instance.Format("discord.reply.stats", todayCount, weekCount, _dropHistory.Count, _sessionDrops);
    }
}
