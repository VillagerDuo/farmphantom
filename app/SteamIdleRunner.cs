using Steamworks;
using System.Globalization;

namespace FarmPhantom;

internal static class SteamIdleRunner
{
    private static readonly TimeSpan CallbackInterval = TimeSpan.FromMilliseconds(250);

    public static int Run(string appIdText)
    {
        if (!uint.TryParse(appIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
            || appId == 0)
        {
            Console.Error.WriteLine("Steam App ID must be a positive integer.");
            return 2;
        }

        Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SteamGameId", appId.ToString(CultureInfo.InvariantCulture));

        var initialized = false;
        try
        {
            initialized = SteamAPI.Init();
            if (!initialized)
            {
                Console.Error.WriteLine("SteamAPI.Init failed. Ensure Steam is running and the official Steamworks redistributable is installed beside FarmPhantom.");
                return 3;
            }

            using var stopSignal = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopSignal.Set();
            };

            while (!stopSignal.Wait(CallbackInterval))
            {
                SteamAPI.RunCallbacks();
            }

            return 0;
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("Steamworks native runtime (steam_api64.dll) is missing. Install the official Steamworks redistributable; do not copy a DLL from another game.");
            return 4;
        }
        catch (BadImageFormatException)
        {
            Console.Error.WriteLine("The Steamworks native runtime architecture does not match FarmPhantom.");
            return 5;
        }
        catch (InvalidOperationException ex)
        {
            // Steamworks.NET fırlatır: steam_api64.dll sürümü Steamworks.NET 2024.8.0 ile uyuşmuyor.
            Console.Error.WriteLine($"Steamworks başlatılamadı; steam_api64.dll sürümü Steamworks.NET 2024.8.0 ile uyuşmuyor olabilir. {OneLine(ex.Message)}");
            return 6;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Beklenmeyen Steamworks hatası ({ex.GetType().Name}): {OneLine(ex.Message)}");
            return 7;
        }
        finally
        {
            if (initialized)
            {
                SteamAPI.Shutdown();
            }
        }
    }

    // Ana pencere yalnızca stderr'in son satırını gösterir; mesajı tek satıra indir.
    private static string OneLine(string message) =>
        message.ReplaceLineEndings(" ").Trim();
}
