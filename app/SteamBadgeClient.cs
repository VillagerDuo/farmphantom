using System.Net;
using System.Text.RegularExpressions;

namespace FarmPhantom;

internal sealed record BadgeGame(uint AppId, string Name, int? Remaining);

/// <summary>
/// Steam rozet sayfalarından kalan kart düşüşü sayısını ve rozetli oyun listesini okur.
/// Oturum çerezleri gerekir; yalnızca isteğin Cookie başlığına konur, hiçbir yere kaydedilmez veya günlüğe yazılmaz.
/// Sayfa biçimini anlayamazsa null döner (çağıran taraf süre sınırına geri döner).
/// </summary>
internal sealed class SteamBadgeClient
{
    private const int MaximumBadgePages = 40;
    private static readonly HttpClient HttpClient = CreateClient();

    private static readonly Regex ProgressPattern = new(
        @"<span class=""progress_info_bold"">\s*([^<]*?)\s*</span>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RemainingPattern = new(
        @"(\d+)\s+card\s+drops?\s+remaining",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RowStartPattern = new(
        @"<[a-zA-Z][a-zA-Z0-9]*\b[^>]*\bclass\s*=\s*[""'][^""']*\bbadge_row\b[^""']*[""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PageTitlePattern = new(
        @"<title>\s*([^<]*)</title>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LoggedInPattern = new(
        @"g_steamID\s*=\s*""\d{17}""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GameCardsLinkPattern = new(
        @"/gamecards/(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BadgeTitlePattern = new(
        @"<div class=""badge_title"">\s*([^<]*?)(?:&nbsp;|\s)*<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FarmPhantom/1.0");
        return client;
    }

    public async Task<int?> GetRemainingDropsAsync(
        MarketSession session,
        uint appId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://steamcommunity.com/profiles/{session.SteamId}/gamecards/{appId}/?l=english");
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(uri, session, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException("Steam rozet sayfası isteklerini sınırladı.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseRemaining(html);
        }
        catch (InvalidDataException)
        {
            // Giriş sayfasına yönlendirme dahil: kalan kart bilinmiyor say, süre sınırına dön.
            return null;
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>Rozet listesindeki tüm oyunları ve her birinin kalan kart sayısını getirir.</summary>
    public async Task<IReadOnlyList<BadgeGame>> GetBadgeGamesAsync(
        MarketSession session,
        Action<int>? onPage,
        CancellationToken cancellationToken)
    {
        var games = new Dictionary<uint, BadgeGame>();
        var diagnostics = string.Empty;

        for (var page = 1; page <= MaximumBadgePages; page++)
        {
            if (page > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            onPage?.Invoke(page);
            var uri = new Uri($"https://steamcommunity.com/profiles/{session.SteamId}/badges/?l=english&p={page}");
            using var response = await SendAsync(uri, session, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException("Steam rozet sayfası isteklerini sınırladı. Birkaç dakika bekle.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new InvalidDataException(
                    $"Steam isteği reddetti (HTTP {(int)response.StatusCode}). Çerezleri aynı tarayıcı oturumundan yeniden kopyalamayı dene.");
            }

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (page == 1)
            {
                diagnostics = Describe(response, html);
            }

            var added = 0;
            foreach (var chunk in RowStartPattern.Split(html).Skip(1))
            {
                var link = GameCardsLinkPattern.Match(chunk);
                if (!link.Success
                    || !uint.TryParse(link.Groups[1].Value, out var appId)
                    || appId == 0
                    || games.ContainsKey(appId))
                {
                    continue;
                }

                var title = BadgeTitlePattern.Match(chunk);
                var name = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim() : string.Empty;
                games[appId] = new BadgeGame(
                    appId,
                    name.Length == 0 ? $"Steam App {appId}" : name,
                    ParseRemaining(chunk));
                added++;
            }

            if (added == 0)
            {
                break;
            }
        }

        if (games.Count == 0)
        {
            throw new InvalidDataException(
                "Rozet listesi okunamadı. Çerezlerin geçerli olduğundan ve rozet sayfana erişilebildiğinden emin ol. "
                + diagnostics);
        }

        return games.Values
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// GET isteği gönderir ve yönlendirmeleri (örn. /profiles/ID → /id/özel-ad) kendisi takip eder.
    /// Çerezler yalnızca steamcommunity.com adreslerine gider. Giriş sayfasına yönlendirme gerçekten
    /// oturumun kabul edilmediği anlamına gelir ve InvalidDataException fırlatır.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        MarketSession session,
        CancellationToken cancellationToken)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation(
                "Cookie",
                $"sessionid={session.SessionId}; steamLoginSecure={session.SteamLoginSecure}");

            var response = await HttpClient.SendAsync(request, cancellationToken);
            var code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException($"Steam boş bir yönlendirme döndürdü (HTTP {code}).");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!next.Host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Steam beklenmeyen bir adrese yönlendirdi ({next.Host}).");
            }

            if (next.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Steam seni giriş sayfasına yönlendirdi; çerezler kabul edilmedi. "
                    + "sessionid ve steamLoginSecure değerlerini aynı tarayıcı oturumundan yeniden kopyala.");
            }

            uri = next;
        }

        throw new InvalidDataException("Steam çok fazla yönlendirme yaptı.");
    }

    /// <summary>Sayfa beklenen biçimde okunamadığında gösterilen, kişisel veri içermeyen kısa teşhis.</summary>
    private static string Describe(HttpResponseMessage response, string html)
    {
        var title = PageTitlePattern.Match(html);
        var pageTitle = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim() : "yok";
        if (pageTitle.Length > 60)
        {
            pageTitle = pageTitle[..60];
        }

        var loggedIn = LoggedInPattern.IsMatch(html) ? "evet" : "hayır";
        return $"[HTTP {(int)response.StatusCode}, {html.Length} karakter, başlık: {pageTitle}, "
            + $"badge_row: {Regex.Count(html, "badge_row")}, gamecards: {Regex.Count(html, "/gamecards/")}, "
            + $"giriş yapılmış: {loggedIn}]";
    }

    private static int? ParseRemaining(string html)
    {
        foreach (Match match in ProgressPattern.Matches(html))
        {
            var text = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (text.Contains("No card drops remaining", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var remaining = RemainingPattern.Match(text);
            if (remaining.Success && int.TryParse(remaining.Groups[1].Value, out var count))
            {
                return count;
            }
        }

        return null;
    }
}
