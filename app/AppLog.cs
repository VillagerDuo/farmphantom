using System.Text;
using System.Text.RegularExpressions;

namespace FarmPhantom;

/// <summary>
/// Basit dosya günlüğü: %LocalAppData%\FarmPhantom\logs\farmphantom-YYYYMMDD.log. Günde bir dosya, 14 günden eskiler silinir.
/// Çerez, anahtar ve token benzeri değerler yazılmadan önce maskelenir. Günlük hatası uygulamayı hiçbir zaman etkilemez.
/// </summary>
internal static class AppLog
{
    private const int RetentionDays = 14;
    private const int MaxMessageLength = 2000;

    private static readonly object Gate = new();
    private static readonly Regex Secret = new(
        @"(access_token|sessionid|steamLoginSecure|token|key|password)=[^&\s""';]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string LogDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmPhantom",
        "logs");

    public static string CurrentFile => System.IO.Path.Combine(LogDirectory, $"farmphantom-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Uygulama başında bir kez çağrılır: eski günlükleri temizler ve işlenmeyen hataları yakalar.</summary>
    public static void Initialize()
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogDirectory);
            var limit = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.GetFiles(LogDirectory, "farmphantom-*.log"))
            {
                if (File.GetLastWriteTime(file) < limit)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temizlik başarısızsa günlük yine de yazılmaya çalışılır.
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error("İşlenmeyen hata" + (e.IsTerminating ? " (uygulama kapanıyor)" : string.Empty), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("Gözlenmeyen görev hatası", e.Exception);
            e.SetObserved();
        };

        Info($"FarmPhantom başladı (sürüm {typeof(AppLog).Assembly.GetName().Version}, {Environment.OSVersion}).");
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? exception = null) => Write("WARN ", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ');
            builder.Append(Clean(message));
            if (exception is not null)
            {
                builder.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(Clean(exception.GetBaseException().Message));
            }

            builder.AppendLine();

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentFile, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Günlük yazılamıyorsa sessizce vazgeç.
        }
    }

    private static string Clean(string text)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ');
        if (singleLine.Length > MaxMessageLength)
        {
            singleLine = singleLine[..MaxMessageLength] + "…";
        }

        return Secret.Replace(singleLine, "$1=***");
    }
}
