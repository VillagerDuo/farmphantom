using System.Text.Json;

namespace FarmPhantom;

internal sealed class GameBlacklistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom",
        "blacklist.json");

    public HashSet<uint> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var appIds = JsonSerializer.Deserialize<uint[]>(File.ReadAllText(_filePath));
        return appIds is null ? [] : appIds.ToHashSet();
    }

    public void Save(IEnumerable<uint> appIds)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Could not resolve blacklist storage directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(appIds.Distinct().Order(), JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
