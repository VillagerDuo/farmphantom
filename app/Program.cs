using Avalonia;

namespace FarmPhantom;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--idle")
        {
            return SteamIdleRunner.Run(args[1]);
        }

        if (args.Length == 1 && args[0] == "--list-installed")
        {
            foreach (var game in SteamLibraryScanner.FindInstalledGames())
            {
                Console.WriteLine($"{game.AppId}\t{game.Name}");
            }

            return 0;
        }

        AppLog.Initialize();

        // Tek kopya: Windows ile başlatılan tepsi örneği varken ikinci bir kopya aynı oyunları ikinci kez idle etmesin.
        _singleInstance = new Mutex(initiallyOwned: true, "Local\\FarmPhantom.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            AppLog.Info("FarmPhantom zaten çalışıyor; ikinci kopya kapatıldı. Çalışan kopyayı sistem tepsisinden aç.");
            return 0;
        }

        try
        {
            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLog.Info($"FarmPhantom kapandı (çıkış kodu {code}).");
            return code;
        }
        catch (Exception ex)
        {
            AppLog.Error("Uygulama beklenmedik şekilde sonlandı", ex);
            throw;
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
