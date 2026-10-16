using Avalonia.Media.Imaging;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace FarmPhantom;

/// <summary>
/// Steam görsellerini (oyun kapakları, kart resimleri) indirir, diske önbelleğe alır ve küçültülmüş Bitmap olarak verir.
/// Aynı adres aynı anda istenirse tek indirme yapılır; eşzamanlı indirme sayısı sınırlıdır.
/// </summary>
internal static class ImageCache
{
    private const int MaxMemoryEntries = 400;
    private static readonly SemaphoreSlim Gate = new(6);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Memory = new();
    private static readonly HttpClient Http = CreateClient();
    private static readonly string Directory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom",
        "imgcache");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FarmPhantom/1.0");
        return client;
    }

    /// <summary>Önce birinci adresi, olmazsa ikincisini dener. İkisi de başarısızsa null döner.</summary>
    public static async Task<Bitmap?> GetAsync(string? url, string? fallbackUrl, int decodeWidth)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            var bitmap = await GetOneAsync(url, decodeWidth);
            if (bitmap is not null)
            {
                return bitmap;
            }
        }

        return string.IsNullOrWhiteSpace(fallbackUrl) ? null : await GetOneAsync(fallbackUrl, decodeWidth);
    }

    private static Task<Bitmap?> GetOneAsync(string url, int decodeWidth)
    {
        if (Memory.Count > MaxMemoryEntries)
        {
            // Kullanımda olan görseller kontrollerde tutulur; burada yalnızca önbellek boşaltılır.
            Memory.Clear();
        }

        var key = $"{decodeWidth}|{url}";
        var task = Memory.GetOrAdd(key, _ => LoadAsync(url, decodeWidth));
        if (task.IsCompletedSuccessfully && task.Result is null)
        {
            // Başarısız sonuç kalıcı olmasın; bir sonraki istekte yeniden denensin.
            Memory.TryRemove(key, out _);
        }

        return task;
    }

    private static async Task<Bitmap?> LoadAsync(string url, int decodeWidth)
    {
        try
        {
            var path = System.IO.Path.Combine(Directory, Hash(url) + ".img");
            byte[]? bytes = null;
            if (File.Exists(path))
            {
                bytes = await File.ReadAllBytesAsync(path);
            }

            if (bytes is null || bytes.Length == 0)
            {
                await Gate.WaitAsync();
                try
                {
                    using var response = await Http.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    bytes = await response.Content.ReadAsByteArrayAsync();
                }
                finally
                {
                    Gate.Release();
                }

                if (bytes.Length == 0)
                {
                    return null;
                }

                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    await File.WriteAllBytesAsync(path, bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Önbelleğe yazılamazsa görsel yine de gösterilir.
                }
            }

            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return (Bitmap?)Bitmap.DecodeToWidth(stream, decodeWidth);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
}
