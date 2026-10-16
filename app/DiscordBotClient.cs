using Discord;
using Discord.Net;
using Discord.WebSocket;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FarmPhantom;

internal sealed class DiscordBotState
{
    public string ProtectedToken { get; set; } = string.Empty;
    public ulong? OwnerUserId { get; set; }
    public ulong? OwnerGuildId { get; set; }
}

/// <summary>Bot tokenını Windows DPAPI ile yalnızca geçerli Windows kullanıcısının açabileceği biçimde saklar.</summary>
internal sealed class DiscordBotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom",
        "discord-bot.json");

    public bool IsSupported => OperatingSystem.IsWindows();

    public string? LoadToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var state = LoadState();
            if (string.IsNullOrWhiteSpace(state.ProtectedToken))
            {
                return null;
            }

            var encrypted = Convert.FromBase64String(state.ProtectedToken);
            var plain = WindowsDpapi.Unprotect(encrypted);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            AppLog.Warn("Kayıtlı Discord bot tokenı okunamadı; ayarlardan yeniden gir.");
            return null;
        }
    }

    public ulong? LoadOwnerUserId()
    {
        try
        {
            return LoadState().OwnerUserId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void SaveToken(string token)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Bot tokenı Windows DPAPI ile şifreleme yalnızca Windows'ta kullanılabilir.");
        }

        var plain = Encoding.UTF8.GetBytes(token);
        try
        {
            var encrypted = WindowsDpapi.Protect(plain);
            var state = LoadState();
            state.ProtectedToken = Convert.ToBase64String(encrypted);
            SaveState(state);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public ulong? LoadOwnerGuildId()
    {
        try { return LoadState().OwnerGuildId; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void SaveOwnerIdentity(ulong userId, ulong guildId)
    {
        var state = LoadState();
        state.OwnerUserId = userId;
        state.OwnerGuildId = guildId;
        SaveState(state);
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    private DiscordBotState LoadState()
    {
        if (!File.Exists(_filePath))
        {
            return new DiscordBotState();
        }

        return JsonSerializer.Deserialize<DiscordBotState>(File.ReadAllText(_filePath)) ?? new DiscordBotState();
    }

    private void SaveState(DiscordBotState state)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Could not resolve Discord bot storage directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}

/// <summary>FarmPhantom'ın kendi Discord botu. Token yalnızca bu uygulamadan Discord'a bağlanırken kullanılır.</summary>
internal sealed class DiscordBotClient : IAsyncDisposable
{
    private readonly DiscordBotStore _store = new();
    private readonly ConcurrentDictionary<ulong, Queue<DateTime>> _notifyWindows = new();
    private DiscordSocketClient? _client;
    private ulong? _ownerUserId;
    private ulong? _ownerGuildId;
    private int _commandsRegistered;

    public DiscordBotClient()
    {
        _ownerUserId = _store.LoadOwnerUserId();
        _ownerGuildId = _store.LoadOwnerGuildId();
    }

    /// <summary>Slash komutlarını uygulamanın ana pencere bağlamına iletir.</summary>
    public Func<string, string, Task<string>>? CommandHandler { get; set; }

    public event Action<string>? StatusChanged;

    public bool IsSupported => _store.IsSupported;
    public bool IsRunning => _client?.ConnectionState == ConnectionState.Connected;
    public string? LoadSavedToken() => _store.LoadToken();

    public async Task<(bool Ok, string Message)> StartAsync(string token, bool persistToken = true)
    {
        if (!IsSupported)
        {
            return (false, "Bot tokenını yerelde şifreli saklama özelliği Windows'ta kullanılabilir.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, "Discord bot tokenını gir.");
        }

        if (_client is not null)
        {
            return (false, "Discord botu zaten çalışıyor.");
        }

        DiscordSocketClient client;
        try
        {
            if (persistToken)
            {
                _store.SaveToken(token.Trim());
            }

            client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                LogLevel = LogSeverity.Warning,
                AlwaysDownloadUsers = false
            });
            _client = client;
            client.Log += message =>
            {
                if (message.Severity >= LogSeverity.Warning)
                {
                    AppLog.Warn($"Discord bot: {message.Message}");
                }

                return Task.CompletedTask;
            };
            client.Ready += RegisterCommandsAsync;
            client.SlashCommandExecuted += HandleSlashAsync;

            await client.LoginAsync(TokenType.Bot, token.Trim());
            await client.StartAsync();
            StatusChanged?.Invoke("Discord botu bağlanıyor…");
            return (true, "Discord botu başlatılıyor…");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Discord botu başlatılamadı. Tokenı ve internet bağlantısını kontrol et.");
            await StopClientAsync();
            var invalidToken = ex is HttpException { HttpCode: HttpStatusCode.Unauthorized };
            if (persistToken && invalidToken)
            {
                try { _store.Clear(); } catch (IOException) { }
            }

            var message = invalidToken
                ? "Discord tokenı geçersiz görünüyor. Developer Portal'dan yeni token alıp tekrar dene."
                : "Discord botu başlatılamadı. İnternet bağlantısını ve tokenı kontrol et.";
            StatusChanged?.Invoke(message);
            return (false, message);
        }
    }

    public async Task<bool> NotifyAsync(string kind, string title, string body)
    {
        var client = _client;
        var ownerUserId = _ownerUserId;
        if (client?.ConnectionState != ConnectionState.Connected || ownerUserId is null)
        {
            return false;
        }

        return await SendNotificationAsync(client, ownerUserId.Value, kind, title, body);
    }

    private async Task<bool> SendNotificationAsync(
        DiscordSocketClient client,
        ulong recipientId,
        string kind,
        string title,
        string body)
    {
        var color = kind switch
        {
            "drop" => new Color(0x55, 0xD6, 0xB0),
            "error" => new Color(0xEF, 0x62, 0x7A),
            "finish" or "queue" => new Color(0x9B, 0x79, 0xF5),
            _ => Color.LightGrey
        };
        if (!AllowNotify(recipientId)) return false;
        try
        {
            var user = await client.GetUserAsync(recipientId);
            if (user is null)
            {
                AppLog.Warn("Discord bot DM gönderemedi; bildirim alıcısı bulunamadı.");
                return false;
            }

            var dmChannel = await user.CreateDMChannelAsync();
            await dmChannel.SendMessageAsync(
                components: BuildCardComponents(NotificationLabel(kind), title, body, color, includeTimestamp: true),
                allowedMentions: AllowedMentions.None,
                flags: MessageFlags.ComponentsV2);
            return true;
        }
        catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
        {
            AppLog.Warn("Discord bot DM gönderemedi; alıcının sunucu DM ayarlarını kontrol et.");
            return false;
        }
        catch (Exception)
        {
            AppLog.Warn("Discord bot DM gönderemedi.");
            return false;
        }
    }

    public async Task StopAndForgetAsync()
    {
        await StopClientAsync();
        _ownerUserId = null;
        _ownerGuildId = null;
        _store.Clear();
        StatusChanged?.Invoke("Bot durduruldu; yerel token silindi.");
    }

    private async Task RegisterCommandsAsync()
    {
        var client = _client;
        if (client is null || Interlocked.Exchange(ref _commandsRegistered, 1) != 0)
        {
            return;
        }

        var commands = new ApplicationCommandProperties[]
        {
            new SlashCommandBuilder().WithName("bagla").WithDescription("FarmPhantom bot sahibini bu Discord sunucusunda kaydet").Build(),
            new SlashCommandBuilder().WithName("durum").WithDescription("Çalışan oyunları, kalan kartları ve süreyi göster").Build(),
            new SlashCommandBuilder().WithName("baslat").WithDescription("Bir oyun için idle başlat")
                .AddOption("appid", ApplicationCommandOptionType.Integer, "Steam App ID", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("durdur").WithDescription("Tüm idle işlemlerini durdur").Build(),
            new SlashCommandBuilder().WithName("sira").WithDescription("Oyun sırasını göster").Build(),
            new SlashCommandBuilder().WithName("sonraki").WithDescription("Çalışan en eski oyunu atla").Build(),
            new SlashCommandBuilder().WithName("istatistik").WithDescription("Düşen kart sayılarını göster").Build()
        };

        try
        {
            // Bu bot FarmPhantom'a ayrıldığı için yalnızca kendi slash komut setini yazar.
            await client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
            StatusChanged?.Invoke($"Bot çevrimiçi: {client.CurrentUser?.Username ?? "FarmPhantom"}");
            AppLog.Info("Discord botu çevrimiçi; slash komutları kaydedildi.");
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _commandsRegistered, 0);
            StatusChanged?.Invoke("Bot çevrimiçi, ancak slash komutları kaydedilemedi. Developer Portal izinlerini kontrol et.");
            AppLog.Warn("Discord slash komutları kaydedilemedi.");
        }
    }

    private async Task HandleSlashAsync(SocketSlashCommand command)
    {
        try
        {
            await command.DeferAsync(ephemeral: true);
            var userId = command.User.Id;
            string reply;

            if (command.CommandName == "bagla")
            {
                if (command.GuildId is not ulong guildId)
                {
                    reply = "Botu bağlamak için komutu Discord sunucusunda çalıştır.";
                }
                else if (_ownerUserId is null && _client?.GetGuild(guildId)?.OwnerId != userId)
                {
                    reply = "İlk bağlantıyı yalnızca Discord sunucusunun sahibi yapabilir.";
                }
                else if (_ownerUserId is ulong owner && owner != userId)
                {
                    reply = "Bu bot başka bir Discord hesabına bağlı. Yalnızca bot sahibi bu komutu kullanabilir.";
                }
                else if (_ownerGuildId is ulong ownerGuildId && ownerGuildId != guildId)
                {
                    reply = "Bu bot başka bir Discord sunucusuna bağlanmış.";
                }
                else
                {
                    _ownerUserId = userId;
                    _ownerGuildId = guildId;
                    _store.SaveOwnerIdentity(userId, guildId);
                    reply = "Bu Discord hesabı bu bilgisayardaki FarmPhantom botunun sahibi olarak kaydedildi. Bildirimler ve bot komutları yalnızca bu hesap için etkin.";
                    StatusChanged?.Invoke($"Discord bot sahibi kaydedildi: {command.User.Username}");
                }
            }
            else if (_ownerUserId is not ulong expectedOwner
                || expectedOwner != userId
                || command.GuildId != _ownerGuildId)
            {
                reply = _ownerUserId is null
                    ? "Önce bot sahibinin hesabından `/bagla` komutunu kullan."
                    : "Bu bot yalnızca bağlı sahibi ve Discord sunucusundan komut kabul eder.";
            }
            else
            {
                var argument = command.CommandName == "baslat"
                    ? Convert.ToString(command.Data.Options.FirstOrDefault()?.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty;
                var handler = CommandHandler;
                reply = handler is null
                    ? "FarmPhantom komutu şu anda işleyemiyor."
                    : await handler(command.CommandName, argument);
            }

            await command.ModifyOriginalResponseAsync(message =>
            {
                message.Components = BuildCardComponents(
                    "SLASH KOMUTU",
                    $"/{command.CommandName}",
                    reply,
                    new Color(0x9B, 0x79, 0xF5));
                message.Flags = MessageFlags.ComponentsV2 | MessageFlags.Ephemeral;
            });
        }
        catch (Exception)
        {
            AppLog.Warn($"Discord slash komutu işlenemedi: {command.CommandName}");
            try
            {
                await command.ModifyOriginalResponseAsync(message =>
                {
                    message.Components = BuildCardComponents(
                        "İŞLEM TAMAMLANAMADI",
                        $"/{command.CommandName}",
                        "Komut işlenemedi; FarmPhantom'un açık olduğundan emin ol.",
                        new Color(0xEF, 0x62, 0x7A));
                    message.Flags = MessageFlags.ComponentsV2 | MessageFlags.Ephemeral;
                });
            }
            catch (Exception)
            {
                // Etkileşim süresi dolmuş olabilir.
            }
        }
    }

    private bool AllowNotify(ulong userId)
    {
        var window = _notifyWindows.GetOrAdd(userId, _ => new Queue<DateTime>());
        lock (window)
        {
            var now = DateTime.UtcNow;
            while (window.Count > 0 && now - window.Peek() > TimeSpan.FromMinutes(1))
            {
                window.Dequeue();
            }

            if (window.Count >= 20)
            {
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }

    private static string NotificationLabel(string kind) => kind switch
    {
        "drop" => "YENİ KART",
        "error" => "UYARI",
        "finish" => "TAMAMLANDI",
        "queue" => "SIRA GÜNCELLENDİ",
        _ => "FARMPHANTOM BİLDİRİMİ"
    };

    private static MessageComponent BuildCardComponents(
        string label,
        string title,
        string body,
        Color accentColor,
        bool includeTimestamp = false)
    {
        var card = new ContainerBuilder()
            .WithAccentColor(accentColor)
            .WithTextDisplay($"-# {Truncate(label, 80)}")
            .WithTextDisplay($"## {Truncate(title, 200)}")
            .WithSeparator(separator => separator
                .WithIsDivider(true)
                .WithSpacing(SeparatorSpacingSize.Small))
            .WithTextDisplay(Truncate(body, 3500));

        if (includeTimestamp)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            card
                .WithSeparator(separator => separator
                    .WithIsDivider(false)
                    .WithSpacing(SeparatorSpacingSize.Small))
                .WithTextDisplay($"-# FarmPhantom · <t:{timestamp}:R>");
        }

        return new ComponentBuilderV2()
            .WithContainer(card)
            .Build();
    }

    private async Task StopClientAsync()
    {
        var client = _client;
        _client = null;
        Interlocked.Exchange(ref _commandsRegistered, 0);
        if (client is null)
        {
            return;
        }

        try { await client.StopAsync(); } catch (Exception) { }
        try { await client.DisposeAsync(); } catch (Exception) { }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    public async ValueTask DisposeAsync() => await StopClientAsync();
}
