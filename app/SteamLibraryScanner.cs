using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace FarmPhantom;

public sealed record SteamGame(
    uint AppId,
    string Name,
    bool IsBlacklisted = false,
    bool IsInstalled = false,
    int? RemainingCards = null)
{
    public string RemainingText => RemainingCards switch
    {
        null => string.Empty,
        0 => "kart kalmadı",
        var count => $"{count} kart kaldı"
    };

    /// <summary>Mağaza kapak görseli (460×215). Önce eski CDN, olmazsa yeni mağaza varlık adresi denenir.</summary>
    public string HeaderUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";

    public string HeaderFallbackUrl => $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";

    public override string ToString() => IsBlacklisted ? $"{Name}  —  KARA LİSTE" : Name;
}

internal static class SteamLibraryScanner
{
    private static readonly Regex LibraryPathPattern = new(
        "\"path\"\\s+\"((?:\\\\\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppIdPattern = new(
        "^\\s*\"appid\"\\s+\"([0-9]+)\"",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = new(
        "^\\s*\"name\"\\s+\"((?:\\\\\\\\.|[^\"])*)\"",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static IReadOnlyList<SteamGame> FindInstalledGames()
    {
        var steamRoot = FindSteamRoot();
        if (steamRoot is null)
        {
            throw new DirectoryNotFoundException("Steam installation was not found.");
        }

        var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            var vdf = File.ReadAllText(libraryFile);
            foreach (Match match in LibraryPathPattern.Matches(vdf))
            {
                var path = UnescapeVdfString(match.Groups[1].Value);
                if (Directory.Exists(path))
                {
                    libraryPaths.Add(path);
                }
            }
        }

        return libraryPaths
            .SelectMany(path => ReadInstalledGames(Path.Combine(path, "steamapps")))
            .GroupBy(game => game.AppId)
            .Select(group => group.First())
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<SteamGame> ReadInstalledGames(string steamAppsPath)
    {
        if (!Directory.Exists(steamAppsPath))
        {
            yield break;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf"))
        {
            var content = File.ReadAllText(manifestPath);
            var appIdMatch = AppIdPattern.Match(content);
            var nameMatch = NamePattern.Match(content);
            if (!appIdMatch.Success || !uint.TryParse(appIdMatch.Groups[1].Value, out var appId) || appId == 0)
            {
                continue;
            }

            var name = nameMatch.Success
                ? UnescapeVdfString(nameMatch.Groups[1].Value)
                : $"Steam App {appId}";
            yield return new SteamGame(appId, name);
        }
    }

    private static string? FindSteamRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = OperatingSystem.IsMacOS()
            ? new[] { Path.Combine(home, "Library", "Application Support", "Steam") }
            : new[]
            {
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".local", "share", "Steam")
            };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string UnescapeVdfString(string value) =>
        value.Replace(@"\\", @"\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
}
