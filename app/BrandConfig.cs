namespace FarmPhantom;

/// <summary>
/// FarmPhantom'ın sabit yayın ve Rich Presence ayarları.
/// </summary>
internal static class BrandConfig
{
    /// <summary>FarmPhantom için önceden belirlenmiş Discord Rich Presence uygulama kimliği.</summary>
    public const string DiscordApplicationId = "1552765056817561660";

    /// <summary>Süresiz, kullanım sınırsız davet bağlantısı. Örn: https://discord.gg/abcdEFG</summary>
    public const string DiscordInviteUrl = "https://discord.gg/Ny9NxMwTHj";

    /// <summary>İsteğe bağlı: proje sayfası (GitHub vb.). Rich Presence'ta ikinci buton olur.</summary>
    public const string ProjectUrl = "https://github.com/EmparorFQ/FarmPhantom";

    /// <summary>Rich Presence görseli: Developer Portal → Rich Presence → Art Assets altındaki anahtar.</summary>
    public const string PresenceImageKey = "logo";

    public static bool HasInvite => !string.IsNullOrWhiteSpace(DiscordInviteUrl);
    public static bool HasPresence => !string.IsNullOrWhiteSpace(DiscordApplicationId);
}
