using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FarmPhantom;

public sealed record MarketPrice(int LowestMinor, string DisplayText);

public sealed record SteamCurrency(int Code, string Label)
{
    public override string ToString() => Label;
}

public sealed record SellResult(
    bool Success,
    bool NeedsMobileConfirmation,
    bool NeedsEmailConfirmation,
    string Message,
    bool StopBatch = false);

/// <summary>Fiyat ayrıştırma, biçimlendirme ve Steam komisyon hesapları (kuruş/cent cinsinden).</summary>
internal static class SteamPrice
{
    // Steam komisyonu %5, oyun komisyonu %10; her biri en az 1 birim.
    public static int SellerReceives(int buyerPaysMinor)
    {
        for (var received = buyerPaysMinor; received >= 1; received--)
        {
            if (BuyerPays(received) <= buyerPaysMinor)
            {
                return received;
            }
        }

        return 0;
    }

    public static int BuyerPays(int sellerReceivesMinor) =>
        sellerReceivesMinor
        + Math.Max((int)Math.Floor(sellerReceivesMinor * 0.05), 1)
        + Math.Max((int)Math.Floor(sellerReceivesMinor * 0.10), 1);

    public static string FormatMinor(int minor) =>
        (minor / 100m).ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>"$0.03", "₺1,25", "1.234,50 TL" gibi metinleri küçük birime (x100) çevirir.</summary>
    public static int? ParseMinor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = new string(text.Where(c => char.IsAsciiDigit(c) || c is ',' or '.').ToArray());
        if (cleaned.Length == 0)
        {
            return null;
        }

        var separator = cleaned.LastIndexOfAny([',', '.']);
        var whole = cleaned;
        var fraction = string.Empty;
        if (separator >= 0 && cleaned.Length - separator - 1 is 1 or 2)
        {
            whole = cleaned[..separator];
            fraction = cleaned[(separator + 1)..];
        }

        whole = whole.Replace(",", string.Empty).Replace(".", string.Empty);
        if (whole.Length == 0)
        {
            whole = "0";
        }

        fraction = fraction.PadRight(2, '0');
        if (!long.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var wholePart)
            || wholePart > 10_000_000
            || !int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out var fractionPart))
        {
            return null;
        }

        return (int)(wholePart * 100 + fractionPart);
    }
}

/// <summary>Steam oturum çerezleri. Yalnızca bellekte tutulur; ToString değerleri sızdırmaz.</summary>
internal sealed class MarketSession
{
    private MarketSession(string steamId, string sessionId, string steamLoginSecure)
    {
        SteamId = steamId;
        SessionId = sessionId;
        SteamLoginSecure = steamLoginSecure;
    }

    public string SteamId { get; }
    public string SessionId { get; }
    public string SteamLoginSecure { get; }

    public override string ToString() => "MarketSession(gizli)";

    public static bool TryCreate(
        string steamId,
        string? sessionId,
        string? steamLoginSecure,
        [NotNullWhen(true)] out MarketSession? session,
        out string error)
    {
        session = null;
        error = string.Empty;

        if (steamId.Length != 17 || !steamId.All(char.IsAsciiDigit))
        {
            error = "Önce Oyunlar sayfasına geçerli 17 haneli SteamID64 gir.";
            return false;
        }

        var sid = Clean(sessionId, "sessionid=");
        var login = Clean(steamLoginSecure, "steamLoginSecure=");

        if (sid.Length is < 12 or > 64 || !sid.All(Uri.IsHexDigit))
        {
            error = "sessionid çerezi geçersiz görünüyor (onaltılık karakterlerden oluşmalı).";
            return false;
        }

        if (login.Length < 20 || !login.All(c => char.IsAsciiLetterOrDigit(c) || c is '%' or '.' or '_' or '-' or '|' or '=' or '+' or '/'))
        {
            error = "steamLoginSecure çerezi geçersiz görünüyor. Değeri tarayıcıdan olduğu gibi kopyala.";
            return false;
        }

        if (!login.StartsWith(steamId + "%7C%7C", StringComparison.OrdinalIgnoreCase)
            && !login.StartsWith(steamId + "||", StringComparison.Ordinal))
        {
            error = "Bu steamLoginSecure çerezi girdiğin SteamID64'e ait görünmüyor.";
            return false;
        }

        session = new MarketSession(steamId, sid, login);
        return true;
    }

    private static string Clean(string? value, string namePrefix)
    {
        var text = (value ?? string.Empty).Trim().Trim('"');
        if (text.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[namePrefix.Length..];
        }

        return text.TrimEnd(';').Trim();
    }
}

internal sealed partial class SteamMarketClient
{
    private static readonly HttpClient HttpClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FarmPhantom/1.0");
        return client;
    }

    public async Task<MarketPrice?> GetLowestPriceAsync(
        string marketHashName,
        int currencyCode,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://steamcommunity.com/market/priceoverview/?appid=753&currency={currencyCode}"
            + $"&market_hash_name={Uri.EscapeDataString(marketHashName)}");

        using var response = await HttpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Steam fiyat isteklerini sınırladı. Birkaç dakika bekleyip yeniden dene.");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var text = GetString(root, "lowest_price") ?? GetString(root, "median_price");
        var minor = SteamPrice.ParseMinor(text);
        return text is null || minor is null ? null : new MarketPrice(minor.Value, text);
    }

    [GeneratedRegex(@"Market_LoadOrderSpread\(\s*(\d+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex NameIdPattern();

    private readonly Dictionary<string, long> _nameIds = new(StringComparer.Ordinal);
    private readonly string _nameIdsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom",
        "nameids.json");

    public SteamMarketClient()
    {
        try
        {
            if (File.Exists(_nameIdsPath))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_nameIdsPath));
                if (saved is not null)
                {
                    foreach (var pair in saved)
                    {
                        _nameIds[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Önbellek okunamadıysa boş başla; kimlikler gerektikçe yeniden alınır.
        }
    }

    /// <summary>
    /// Kart için en yüksek satın alma talimatı (alıcının ödeyeceği tutar, küçük birim).
    /// Bu fiyattan listelenen kart, bekleyen talimata anında satılır. Talimat yoksa null döner.
    /// </summary>
    public async Task<int?> GetHighestBuyOrderAsync(
        string marketHashName,
        int currencyCode,
        CancellationToken cancellationToken)
    {
        if (!_nameIds.TryGetValue(marketHashName, out var nameId))
        {
            var fetched = await FetchItemNameIdAsync(marketHashName, cancellationToken);
            if (fetched is null)
            {
                return null;
            }

            nameId = fetched.Value;
            _nameIds[marketHashName] = nameId;
            SaveNameIds();
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
        }

        var uri = new Uri(
            "https://steamcommunity.com/market/itemordershistogram?country=US&language=english"
            + $"&currency={currencyCode}&item_nameid={nameId}&two_factor=0");

        using var response = await HttpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Steam talimat isteklerini sınırladı. Birkaç dakika bekleyip yeniden dene.");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !GetFlag(root, "success"))
        {
            return null;
        }

        var text = GetString(root, "highest_buy_order");
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && minor >= 1
            ? minor
            : null;
    }

    private async Task<long?> FetchItemNameIdAsync(string marketHashName, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://steamcommunity.com/market/listings/753/{Uri.EscapeDataString(marketHashName)}");
        using var response = await HttpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Steam Market sayfası isteklerini sınırladı. Birkaç dakika bekleyip yeniden dene.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = NameIdPattern().Match(html);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    private void SaveNameIds()
    {
        try
        {
            var directory = Path.GetDirectoryName(_nameIdsPath);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = _nameIdsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_nameIds));
            File.Move(temporaryPath, _nameIdsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Önbellek yazılamazsa bellekteki kopya bu oturum için yeterli.
        }
    }

    public async Task<SellResult> SellAsync(
        MarketSession session,
        string assetId,
        int sellerReceivesMinor,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://steamcommunity.com/market/sellitem/");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"sessionid={session.SessionId}; steamLoginSecure={session.SteamLoginSecure}");
        request.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
        request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{session.SteamId}/inventory");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["sessionid"] = session.SessionId,
            ["appid"] = "753",
            ["contextid"] = "6",
            ["assetid"] = assetId,
            ["amount"] = "1",
            ["price"] = sellerReceivesMinor.ToString(CultureInfo.InvariantCulture)
        });

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400 or 401 or 403)
        {
            return new SellResult(false, false, false,
                "Steam oturumu kabul etmedi. Çerezlerin süresi dolmuş olabilir; tarayıcıdan yeniden kopyala.", StopBatch: true);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new SellResult(false, false, false,
                "Steam ilan isteklerini sınırladı. Birkaç dakika bekle.", StopBatch: true);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var success = GetFlag(root, "success");
            var message = GetString(root, "message") ?? (success ? "İlan verildi." : "Steam ilanı reddetti.");
            return new SellResult(
                success,
                GetFlag(root, "needs_mobile_confirmation"),
                GetFlag(root, "needs_email_confirmation"),
                message);
        }
        catch (JsonException)
        {
            return new SellResult(false, false, false, $"Steam beklenmeyen bir yanıt verdi (HTTP {code}).");
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetFlag(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.True
            || (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) && number == 1);
    }
}
