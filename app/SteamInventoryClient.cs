using System.Globalization;
using System.Net;
using System.Text.Json;

namespace FarmPhantom;

public sealed record SteamInventoryCard(
    string AssetId,
    string Name,
    string MarketHashName,
    int Amount,
    bool IsFoil,
    uint AppId = 0,
    string IconUrl = "")
{
    /// <summary>Kart resmi. Steam'in ekonomi görsel adresi; icon_url yoksa boş döner.</summary>
    public string ImageUrl => string.IsNullOrEmpty(IconUrl)
        ? string.Empty
        : $"https://community.cloudflare.steamstatic.com/economy/image/{IconUrl}";

    public override string ToString() =>
        $"{Name}  ×{Amount}{(IsFoil ? "  ·  FOIL" : string.Empty)}";
}

internal sealed class SteamInventoryClient
{
    private const int PageSize = 500;
    private const int MaximumPages = 40;
    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(700);
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

    /// <summary>Satışa uygun (pazarlanabilir ve takas edilebilir) kartlar.</summary>
    public Task<IReadOnlyList<SteamInventoryCard>> GetTradableCardsAsync(
        MarketSession session,
        CancellationToken cancellationToken) =>
        GetCardsAsync(session, marketableOnly: true, cancellationToken);

    /// <summary>
    /// Envanterdeki Steam kartları. marketableOnly=false ise yeni düşen, henüz
    /// satılamayan kartlar da dahil edilir (kart düşüş tespiti için).
    /// </summary>
    public async Task<IReadOnlyList<SteamInventoryCard>> GetCardsAsync(
        MarketSession session,
        bool marketableOnly,
        CancellationToken cancellationToken)
    {
        var cardsByAssetId = new Dictionary<string, SteamInventoryCard>(StringComparer.Ordinal);
        string? lastAssetId = null;

        for (var page = 0; page < MaximumPages; page++)
        {
            if (page > 0)
            {
                await Task.Delay(PageDelay, cancellationToken);
            }

            var query = $"l=english&count={PageSize}";
            if (!string.IsNullOrEmpty(lastAssetId))
            {
                query += $"&start_assetid={Uri.EscapeDataString(lastAssetId)}";
            }

            var uri = new Uri($"https://steamcommunity.com/inventory/{session.SteamId}/753/6?{query}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation(
                "Cookie",
                $"sessionid={session.SessionId}; steamLoginSecure={session.SteamLoginSecure}");
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException("Steam envanter isteklerini sınırladı. Biraz bekle.");
            }

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new InvalidDataException("Steam oturumu kabul etmedi. Çerezleri tarayıcıdan yeniden kopyala.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new HttpRequestException("Steam envanter erişimini reddetti. Çerezleri yenilemeyi dene.");
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (TryGetBoolean(root, "success", out var success) && !success)
            {
                throw new InvalidDataException("Steam envanteri döndürmedi. Çerezlerin geçerli olduğundan emin ol.");
            }

            var descriptions = new Dictionary<(string ClassId, string InstanceId), JsonElement>();
            if (root.TryGetProperty("descriptions", out var descriptionArray)
                && descriptionArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var description in descriptionArray.EnumerateArray())
                {
                    var classId = GetString(description, "classid");
                    var instanceId = GetString(description, "instanceid") ?? "0";
                    if (classId is not null)
                    {
                        descriptions[(classId, instanceId)] = description;
                    }
                }
            }

            if (root.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var classId = GetString(asset, "classid");
                    var instanceId = GetString(asset, "instanceid") ?? "0";
                    var assetId = GetString(asset, "assetid");
                    if (classId is null
                        || assetId is null
                        || !descriptions.TryGetValue((classId, instanceId), out var description))
                    {
                        continue;
                    }

                    if (marketableOnly && !(IsTrue(description, "marketable") && IsTrue(description, "tradable")))
                    {
                        continue;
                    }

                    var itemType = GetString(description, "type") ?? string.Empty;
                    if (!itemType.Contains("trading card", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var marketHashName = GetString(description, "market_hash_name");
                    if (string.IsNullOrWhiteSpace(marketHashName))
                    {
                        continue;
                    }

                    var name = GetString(description, "name") ?? marketHashName;
                    var amountText = GetString(asset, "amount");
                    var amount = int.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAmount)
                        ? Math.Max(parsedAmount, 1)
                        : 1;
                    var appId = uint.TryParse(
                        GetString(description, "market_fee_app"),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedAppId)
                        ? parsedAppId
                        : 0u;

                    cardsByAssetId[assetId] = new SteamInventoryCard(
                        assetId,
                        name,
                        marketHashName,
                        amount,
                        itemType.Contains("foil", StringComparison.OrdinalIgnoreCase),
                        appId,
                        GetString(description, "icon_url") ?? string.Empty);
                }
            }

            if (!TryGetBoolean(root, "more_items", out var moreItems) || !moreItems)
            {
                return cardsByAssetId.Values
                    .OrderBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(card => card.IsFoil)
                    .ToArray();
            }

            lastAssetId = GetString(root, "last_assetid");
            if (string.IsNullOrWhiteSpace(lastAssetId))
            {
                throw new InvalidDataException("Steam inventory indicated more pages but did not provide a continuation cursor.");
            }
        }

        throw new InvalidDataException($"Inventory exceeds the safe scan limit of {MaximumPages * PageSize} items.");
    }

    private static bool IsTrue(JsonElement element, string name) =>
        TryGetBoolean(element, name, out var value) && value;

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numericValue))
        {
            value = numericValue != 0;
            return true;
        }

        return false;
    }
}
