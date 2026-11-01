using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FarmPhantom;

internal sealed record SteamCardCatalogGame(
    uint AppId,
    string Name,
    int CardsInSet,
    int? FinalPriceMinor,
    int? OriginalPriceMinor,
    int DiscountPercent,
    string? FinalPriceDisplay = null,
    string? OriginalPriceDisplay = null,
    int? CardSetValueMinor = null,
    DateTimeOffset? CardSetValueUpdatedAt = null)
{
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    [JsonIgnore]
    public bool HasPrice => FinalPriceMinor.HasValue;
    [JsonIgnore]
    public bool HasNoPrice => !HasPrice;
    [JsonIgnore]
    public bool IsFree => FinalPriceMinor == 0;
    [JsonIgnore]
    public bool HasPaidPrice => FinalPriceMinor > 0;
    [JsonIgnore]
    public bool HasDiscount => DiscountPercent > 0 && (OriginalPriceDisplay is not null || OriginalPriceMinor.HasValue);
    [JsonIgnore]
    public string FinalPriceText => FinalPriceDisplay ?? FormatPrice(FinalPriceMinor);
    [JsonIgnore]
    public string OriginalPriceText => OriginalPriceDisplay ?? FormatPrice(OriginalPriceMinor);
    [JsonIgnore]
    public bool HasCardSetValue => CardSetValueMinor.HasValue;
    [JsonIgnore]
    public bool HasNoCardSetValue => !HasCardSetValue;
    [JsonIgnore]
    public string CardSetValueText => CardSetValueMinor is int value
        ? (value / 100m).ToString("C2", UsCulture)
        : string.Empty;
    [JsonIgnore]
    public string CardSetValueUpdatedText => CardSetValueUpdatedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        ?? string.Empty;
    [JsonIgnore]
    public string DiscountText => $"-{DiscountPercent}%";
    [JsonIgnore]
    public string HeaderUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
    [JsonIgnore]
    public string HeaderFallbackUrl => $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";

    private static string FormatPrice(int? minor) =>
        minor is null or 0 ? string.Empty : (minor.Value / 100m).ToString("C2", UsCulture);
}

internal sealed record SteamCardCatalogSnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SteamCardCatalogGame> Games,
    string? CountryCode = null);

internal sealed record SteamCardCatalogRefreshResult(SteamCardCatalogSnapshot Snapshot, bool CacheSaved);

internal sealed partial class SteamCardCatalogClient
{
    private const int StorePageSize = 100;
    private const int MaximumRateLimitRetries = 5;
    private static readonly TimeSpan StorePageDelay = TimeSpan.FromSeconds(1);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom");

    [GeneratedRegex(
        @"<a\b[^>]*\bdata-ds-appid=""(?<appId>\d+)""[^>]*>(?<body>.*?)</a>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex StoreResultPattern();

    [GeneratedRegex(@"<span class=""title"">(?<name>[^<]+)</span>", RegexOptions.CultureInvariant)]
    private static partial Regex StoreTitlePattern();

    [GeneratedRegex(@"data-price-final=""(?<price>\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex FinalPricePattern();

    [GeneratedRegex(
        @"<div class=""discount_final_price free""[^>]*>\s*Free\s*</div>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FreePricePattern();

    [GeneratedRegex(
        @"<div class=""discount_final_price(?:\s+free)?""[^>]*>(?<price>.*?)</div>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FinalPriceDisplayPattern();

    [GeneratedRegex(@"data-discount=""(?<discount>\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex DiscountPattern();

    [GeneratedRegex(
        @"<div class=""discount_original_price"">(?<price>.*?)</div>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex OriginalPricePattern();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FarmPhantom/1.0");
        return client;
    }

    public SteamCardCatalogSnapshot? ReadCache(string countryCode)
    {
        try
        {
            var cachePath = GetCachePath(countryCode);
            if (!File.Exists(cachePath))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<SteamCardCatalogSnapshot>(
                File.ReadAllText(cachePath),
                CacheJsonOptions);
            return snapshot is { Games.Count: > 0 }
                && (string.Equals(snapshot.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)
                    || countryCode == "us" && snapshot.CountryCode is null)
                ? snapshot
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Kart kataloğu önbelleği okunamadı: {ex.Message}");
            return null;
        }
    }

    public async Task<SteamCardCatalogRefreshResult> RefreshAsync(
        string countryCode,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken)
    {
        var badgeGames = await FetchBadgeGamesAsync(cancellationToken);
        var storeGames = await FetchStoreGamesAsync(badgeGames.Keys, countryCode, onProgress, cancellationToken);
        var games = badgeGames.Values
            .Select(game => storeGames.TryGetValue(game.AppId, out var storeGame)
                ? game with
                {
                    Name = storeGame.Name,
                    FinalPriceMinor = storeGame.FinalPriceMinor,
                    OriginalPriceMinor = storeGame.OriginalPriceMinor,
                    DiscountPercent = storeGame.DiscountPercent,
                    FinalPriceDisplay = storeGame.FinalPriceDisplay,
                    OriginalPriceDisplay = storeGame.OriginalPriceDisplay
                }
                : game)
            .ToArray();
        var snapshot = new SteamCardCatalogSnapshot(DateTimeOffset.UtcNow, games, countryCode);

        var cacheSaved = await TryWriteCacheAsync(snapshot, countryCode, cancellationToken);
        return new SteamCardCatalogRefreshResult(snapshot, cacheSaved);
    }

    private async Task<Dictionary<uint, SteamCardCatalogGame>> FetchBadgeGamesAsync(
        CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(
            new Uri("https://www.steamcardexchange.net/api/request.php?GetBadgePrices_Guest"),
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Steam Card Exchange returned an unrecognized catalog response.");
        }

        var games = new Dictionary<uint, SteamCardCatalogGame>();
        var invalidRows = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array
                || row.GetArrayLength() < 2
                || row[0].ValueKind != JsonValueKind.Array
                || row[0].GetArrayLength() < 2
                || row[1].ValueKind != JsonValueKind.Number
                || !row[1].TryGetInt32(out var cardsInSet)
                || cardsInSet <= 0)
            {
                invalidRows++;
                continue;
            }

            var appIdText = GetString(row[0][0]);
            var name = WebUtility.HtmlDecode(GetString(row[0][1]) ?? string.Empty).Trim();
            if (!uint.TryParse(appIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                || appId == 0
                || name.Length == 0)
            {
                invalidRows++;
                continue;
            }

            var cardSetValueMinor = row.GetArrayLength() > 2 && row[2].ValueKind == JsonValueKind.String
                ? SteamPrice.ParseMinor(row[2].GetString())
                : null;
            DateTimeOffset? cardSetValueUpdatedAt = null;
            if (row.GetArrayLength() > 3
                && row[3].ValueKind == JsonValueKind.Number
                && row[3].TryGetInt64(out var updatedAt)
                && updatedAt > 0
                && updatedAt <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                cardSetValueUpdatedAt = DateTimeOffset.FromUnixTimeSeconds(updatedAt);
            }

            games[appId] = new SteamCardCatalogGame(
                appId,
                name,
                cardsInSet,
                null,
                null,
                0,
                CardSetValueMinor: cardSetValueMinor,
                CardSetValueUpdatedAt: cardSetValueUpdatedAt);
        }

        if (invalidRows > 0)
        {
            AppLog.Warn($"Steam Card Exchange catalog contained {invalidRows} invalid rows.");
        }

        if (games.Count == 0)
        {
            throw new InvalidDataException("Steam Card Exchange returned no usable card-game entries.");
        }

        return games;
    }

    private async Task<Dictionary<uint, SteamCardCatalogGame>> FetchStoreGamesAsync(
        IEnumerable<uint> cardAppIds,
        string countryCode,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken)
    {
        var cardAppIdSet = cardAppIds.ToHashSet();
        var storeGames = new Dictionary<uint, SteamCardCatalogGame>();
        var start = 0;
        var totalCount = int.MaxValue;

        while (start < totalCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(StorePageDelay, cancellationToken);

            var query = new Dictionary<string, string>
            {
                ["query"] = string.Empty,
                ["start"] = start.ToString(CultureInfo.InvariantCulture),
                ["count"] = StorePageSize.ToString(CultureInfo.InvariantCulture),
                ["category1"] = "998",
                ["category2"] = "29",
                ["infinite"] = "1",
                ["cc"] = countryCode,
                ["l"] = "en"
            };
            var queryString = string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            var uri = new Uri($"https://store.steampowered.com/search/results/?{queryString}");
            var json = await GetStringAsync(uri, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success)
                || (success.ValueKind != JsonValueKind.True
                    && !(success.ValueKind == JsonValueKind.Number && success.TryGetInt32(out var code) && code == 1)))
            {
                throw new InvalidDataException("Steam Store returned an unsuccessful card-game search.");
            }

            if (!root.TryGetProperty("total_count", out var total)
                || !total.TryGetInt32(out totalCount)
                || totalCount <= 0
                || !root.TryGetProperty("results_html", out var resultHtml)
                || resultHtml.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Steam Store returned an unrecognized search response.");
            }

            var parsedRows = 0;
            foreach (Match match in StoreResultPattern().Matches(resultHtml.GetString() ?? string.Empty))
            {
                parsedRows++;
                if (!uint.TryParse(match.Groups["appId"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                    || !cardAppIdSet.Contains(appId))
                {
                    continue;
                }

                var body = match.Groups["body"].Value;
                var title = StoreTitlePattern().Match(body);
                if (!title.Success)
                {
                    continue;
                }

                var name = WebUtility.HtmlDecode(title.Groups["name"].Value).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var finalPrice = FinalPricePattern().Match(body);
                int? finalPriceMinor = FreePricePattern().IsMatch(body)
                    ? 0
                    : finalPrice.Success
                        && int.TryParse(finalPrice.Groups["price"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var finalMinor)
                        ? finalMinor
                        : null;
                var finalPriceDisplay = GetPriceDisplay(FinalPriceDisplayPattern().Match(body));

                var discount = DiscountPattern().Match(body);
                var discountPercent = discount.Success
                    && int.TryParse(discount.Groups["discount"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var discountValue)
                    ? discountValue
                    : 0;
                int? originalPriceMinor = null;
                string? originalPriceDisplay = null;
                if (discountPercent > 0)
                {
                    var originalPrice = OriginalPricePattern().Match(body);
                    if (originalPrice.Success)
                    {
                        originalPriceDisplay = GetPriceDisplay(originalPrice);
                        originalPriceMinor = SteamPrice.ParseMinor(originalPriceDisplay);
                    }
                }

                storeGames[appId] = new SteamCardCatalogGame(
                    appId,
                    name,
                    0,
                    finalPriceMinor,
                    originalPriceMinor,
                    discountPercent,
                    finalPriceDisplay,
                    originalPriceDisplay);
            }

            if (parsedRows == 0)
            {
                throw new InvalidDataException($"Steam Store returned no game rows at result offset {start}.");
            }

            start += StorePageSize;
            onProgress?.Invoke(Math.Min(start, totalCount), totalCount);
        }

        if (storeGames.Count == 0)
        {
            throw new InvalidDataException("No Steam Store games matched the card-set catalog.");
        }

        return storeGames;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumRateLimitRetries; attempt++)
        {
            using var response = await HttpClient.GetAsync(uri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == MaximumRateLimitRetries - 1)
                {
                    throw new HttpRequestException(
                        $"The catalog source rate-limited the request to {uri.Host}.",
                        null,
                        response.StatusCode);
                }

                var retryDelay = GetRetryDelay(response, attempt);
                await Task.Delay(retryDelay, cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        throw new InvalidOperationException("Catalog retry loop ended unexpectedly.");
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return TimeSpan.FromSeconds(Math.Clamp(delta.TotalSeconds, 1, 30));
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            return TimeSpan.FromSeconds(Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 1, 30));
        }

        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)));
    }

    private async Task<bool> TryWriteCacheAsync(
        SteamCardCatalogSnapshot snapshot,
        string countryCode,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var json = JsonSerializer.Serialize(snapshot, CacheJsonOptions);
            await File.WriteAllTextAsync(GetCachePath(countryCode), json, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Kart kataloğu önbelleğe yazılamadı: {ex.Message}");
            return false;
        }
    }

    private static string? GetString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private string GetCachePath(string countryCode) =>
        Path.Combine(
            _cacheDirectory,
            countryCode == "us" ? "card-catalog.json" : $"card-catalog-{countryCode}.json");

    private static string? GetPriceDisplay(Match match) =>
        match.Success
            ? WebUtility.HtmlDecode(Regex.Replace(match.Groups["price"].Value, "<[^>]+>", string.Empty)).Trim()
            : null;
}
