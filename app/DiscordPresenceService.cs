using DiscordRPC;

namespace FarmPhantom;

/// <summary>
/// Discord profilinde "FarmPhantom" durumunu (Rich Presence) gösterir. Discord masaüstü uygulaması kapalıysa
/// ya da BrandConfig doldurulmamışsa hiçbir şey yapmaz ve hata vermez. Butonlar yalnızca BAŞKALARINA görünür.
/// </summary>
internal sealed class DiscordPresenceService : IDisposable
{
    private DiscordRpcClient? _client;

    public bool IsActive => _client is not null;

    /// <summary>Discord masaüstü uygulamasıyla bağlantı kuruldu mu?</summary>
    public bool IsReady { get; private set; }

    private string _lastLogged = string.Empty;

    /// <summary>Son bağlantı/protokol hatası (arayüzde teşhis için).</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_client is not null || !BrandConfig.HasPresence)
        {
            return;
        }

        try
        {
            var client = new DiscordRpcClient(BrandConfig.DiscordApplicationId);
            client.OnReady += (_, _) =>
            {
                IsReady = true;
                LastError = null;
                AppLog.Info("Presence: Discord'a bağlandı.");
            };
            client.OnConnectionFailed += (_, _) =>
            {
                IsReady = false;
                LastError = "Discord masaüstü uygulamasına bağlanılamadı (Discord açık mı?).";
                AppLog.Warn("Presence: Discord masaüstü uygulamasına bağlanılamadı.");
            };
            client.OnError += (_, e) =>
            {
                LastError = e.Message;
                AppLog.Warn($"Presence hatası: {e.Message}");
            };
            client.Initialize();
            _client = client;
            AppLog.Info($"Presence başlatıldı (uygulama ID …{BrandConfig.DiscordApplicationId[^Math.Min(4, BrandConfig.DiscordApplicationId.Length)..]}, görsel anahtarı '{BrandConfig.PresenceImageKey}').");
        }
        catch (Exception ex)
        {
            _client = null;
            AppLog.Error("Presence başlatılamadı", ex);
        }
    }

    public void Update(string details, string state, DateTime startedUtc)
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            var buttons = new List<DiscordRPC.Button>();
            if (BrandConfig.HasInvite)
            {
                buttons.Add(new DiscordRPC.Button { Label = "Discord'a katıl", Url = BrandConfig.DiscordInviteUrl });
            }

            if (!string.IsNullOrWhiteSpace(BrandConfig.ProjectUrl))
            {
                buttons.Add(new DiscordRPC.Button { Label = "Projeye bak", Url = BrandConfig.ProjectUrl });
            }

            var presence = new RichPresence
            {
                Details = Trim(details),
                State = Trim(state),
                Timestamps = new Timestamps(startedUtc),
                Assets = new Assets
                {
                    LargeImageKey = BrandConfig.PresenceImageKey,
                    LargeImageText = "FarmPhantom"
                }
            };

            if (buttons.Count > 0)
            {
                presence.Buttons = buttons.ToArray();
            }

            _client.SetPresence(presence);
            var line = $"{presence.Details} | {presence.State}";
            if (line != _lastLogged)
            {
                _lastLogged = line;
                AppLog.Info($"Presence güncellendi: {line} (hazır: {IsReady})");
            }
        }
        catch (Exception ex)
        {
            // Presence yalnızca süs; hata farm'ı etkilememeli.
            AppLog.Warn("Presence güncellenemedi", ex);
        }
    }

    public void Clear()
    {
        try
        {
            _client?.ClearPresence();
        }
        catch (Exception)
        {
            // Yoksay.
        }
    }

    public void Stop()
    {
        try
        {
            _client?.ClearPresence();
            _client?.Dispose();
        }
        catch (Exception)
        {
            // Yoksay.
        }

        _client = null;
        IsReady = false;
    }

    public void Dispose() => Stop();

    private static string Trim(string text) => text.Length <= 120 ? text : text[..120];
}
