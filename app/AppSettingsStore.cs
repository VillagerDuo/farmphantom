using System.Text.Json;

namespace FarmPhantom;

internal sealed class QueuedGame
{
    public uint AppId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Diske yazılan kullanıcı tercihleri. Steam çerezleri ve Discord bot tokenı burada tutulmaz.</summary>
internal sealed class AppSettings
{
    // Süreler seçenek sırası değil dakika olarak saklanır; açılır liste değişse bile ayar bozulmaz.
    public int DropIntervalMinutes { get; set; } = 3;
    public int DropTimeoutMinutes { get; set; } = 45;
    public int MaxParallel { get; set; } = 1;
    public bool DropMonitorEnabled { get; set; } = true;
    public bool DiscordBotNotify { get; set; } = true;
    // Previous releases used this preference for Relay bot DMs.
    public bool RelayNotify { get; set; } = true;
    public bool AutoQueueEnabled { get; set; }
    public int QueuePriorityIndex { get; set; }
    public string LanguageCulture { get; set; } = "tr-TR";
    public int GameFilterIndex { get; set; }
    public int GameSortIndex { get; set; }
    public int PriceModeIndex { get; set; }
    public int CurrencyCode { get; set; } = 1;
    public string MinPrice { get; set; } = string.Empty;
    public bool PresenceEnabled { get; set; } = true;
    public bool PresenceShowGame { get; set; } = true;
    public bool TrayOnClose { get; set; } = true;
    public bool NotifyOnDrop { get; set; } = true;
    public bool SoundOnDrop { get; set; } = true;
    public bool AutoRecover { get; set; } = true;
    public List<QueuedGame> Queue { get; set; } = [];
}

internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Could not resolve settings storage directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
