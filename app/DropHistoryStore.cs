using System.Text.Json;

namespace FarmPhantom;

public sealed record DropRecord(
    DateTime TimeUtc,
    uint AppId,
    string GameName,
    string CardName,
    string MarketHashName,
    bool IsFoil);

/// <summary>Düşen kartların kalıcı geçmişi. Satır başına bir JSON kaydı (drops.jsonl).</summary>
internal sealed class DropHistoryStore
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom",
        "drops.jsonl");

    public List<DropRecord> Load()
    {
        var records = new List<DropRecord>();
        if (!File.Exists(_filePath))
        {
            return records;
        }

        foreach (var line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<DropRecord>(line);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // Bozuk satırı atla; geri kalan geçmiş okunmaya devam eder.
            }
        }

        return records;
    }

    public void Append(DropRecord record)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Could not resolve drop history storage directory.");
        Directory.CreateDirectory(directory);
        File.AppendAllText(_filePath, JsonSerializer.Serialize(record) + Environment.NewLine);
    }
}
