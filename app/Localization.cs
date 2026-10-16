using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace FarmPhantom;

/// <summary>Loads culture packs from the Locales folder. Any BCP-47 culture can be added without code changes.</summary>
internal sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, LocalizationPack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedCulture = "tr-TR";

    private LocalizationService()
    {
        LoadPacks();
    }

    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LanguageOption> Languages => new[] { new LanguageOption("system", this["settings.systemLanguage"]) }
        .Concat(_packs.Values
        .Select(pack => new LanguageOption(pack.Culture, pack.DisplayName))
        .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToArray())
        .ToArray();

    public string SelectedCulture => _selectedCulture;

    public Avalonia.Media.FlowDirection FlowDirection => CultureInfo.GetCultureInfo(ResolveCulture(_selectedCulture)).TextInfo.IsRightToLeft
        ? Avalonia.Media.FlowDirection.RightToLeft
        : Avalonia.Media.FlowDirection.LeftToRight;

    public string this[string key]
    {
        get
        {
            var culture = ResolveCulture(_selectedCulture);
            if (_packs.TryGetValue(culture, out var selected)
                && selected.Strings.TryGetValue(key, out var translated))
            {
                return translated;
            }

            if (_packs.TryGetValue("en-US", out var english)
                && english.Strings.TryGetValue(key, out var englishFallback))
            {
                return englishFallback;
            }

            if (_packs.TryGetValue("tr-TR", out var turkish)
                && turkish.Strings.TryGetValue(key, out var fallback))
            {
                return fallback;
            }

            return key;
        }
    }

    public void SetLanguage(string? culture)
    {
        var requested = string.IsNullOrWhiteSpace(culture) ? "tr-TR" : culture.Trim();
        if (!requested.Equals("system", StringComparison.OrdinalIgnoreCase) && !_packs.ContainsKey(requested))
        {
            requested = FindCulture(requested) ?? "tr-TR";
        }

        var changed = !string.Equals(_selectedCulture, requested, StringComparison.OrdinalIgnoreCase);
        _selectedCulture = requested;
        var selectedCulture = CultureInfo.GetCultureInfo(ResolveCulture(requested));
        CultureInfo.DefaultThreadCurrentCulture = selectedCulture;
        CultureInfo.DefaultThreadCurrentUICulture = selectedCulture;
        CultureInfo.CurrentCulture = selectedCulture;
        CultureInfo.CurrentUICulture = selectedCulture;
        if (!changed)
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCulture)));
    }

    public string Translate(string key) => this[key];

    public string TranslateBotStatus(string status)
    {
        if (status.StartsWith("Bot çevrimiçi: ", StringComparison.Ordinal))
        {
            return Format("discord.botOnline", status["Bot çevrimiçi: ".Length..]);
        }

        if (status.StartsWith("Bildirim hesabı bağlandı: ", StringComparison.Ordinal))
        {
            return Format("discord.accountLinked", status["Bildirim hesabı bağlandı: ".Length..]);
        }

        var key = status switch
        {
            "Bot tokenını yerelde şifreli saklama özelliği Windows'ta kullanılabilir." => "discord.tokenStorageUnsupported",
            "Discord bot tokenını gir." => "discord.tokenInput",
            "Discord botu zaten çalışıyor." => "discord.alreadyRunning",
            "Discord botu başlatılıyor…" => "discord.botStarting",
            "Discord botu bağlanıyor…" => "discord.botConnecting",
            "Bot durduruldu; yerel token silindi." => "discord.stoppedAndDeleted",
            "Bot çevrimiçi, ancak slash komutları kaydedilemedi. Developer Portal izinlerini kontrol et." => "discord.commandsNotSaved",
            "Discord tokenı geçersiz görünüyor. Developer Portal'dan yeni token alıp tekrar dene." => "discord.invalidToken",
            "Discord botu başlatılamadı. İnternet bağlantısını ve tokenı kontrol et." => "discord.botStartFailed",
            _ => string.Empty
        };

        return key.Length == 0 ? status : Translate(key);
    }

    public string Format(string key, params object?[] args)
    {
        var culture = CultureInfo.GetCultureInfo(ResolveCulture(_selectedCulture));
        return string.Format(culture, this[key], args);
    }

    private string ResolveCulture(string culture)
    {
        if (culture.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return FindCulture(CultureInfo.InstalledUICulture.Name) ?? "en-US";
        }

        return FindCulture(culture) ?? "tr-TR";
    }

    private string? FindCulture(string culture)
    {
        if (_packs.ContainsKey(culture))
        {
            return culture;
        }

        var language = culture.Split('-', '_')[0];
        return _packs.Keys.FirstOrDefault(candidate =>
            candidate.Split('-', '_')[0].Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadPacks()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Locales");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var pack = JsonSerializer.Deserialize<LocalizationPack>(File.ReadAllText(path), JsonOptions);
                if (pack is not null
                    && !string.IsNullOrWhiteSpace(pack.Culture)
                    && !string.IsNullOrWhiteSpace(pack.DisplayName)
                    && pack.Strings is not null)
                {
                    _packs[pack.Culture] = pack;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warn($"Language pack could not be loaded: {Path.GetFileName(path)}");
            }
        }
    }
}

internal sealed class LocalizationPack
{
    public string Culture { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record LanguageOption(string Culture, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocalizationService.Instance, Mode = BindingMode.OneWay };
}
