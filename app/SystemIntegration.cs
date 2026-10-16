using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FarmPhantom;

/// <summary>Windows ile birlikte başlatma: HKCU\...\Run altına tepsiye küçültülmüş (--tray) başlatan bir kayıt yazar.</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FarmPhantom";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return IsEnabledWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static bool TrySet(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return TrySetWindows(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static bool TrySetWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{path}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            AppLog.Info($"Windows ile başlatma {(enabled ? "açıldı" : "kapatıldı")}.");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("Windows ile başlatma ayarı yazılamadı", ex);
            return false;
        }
    }
}

/// <summary>FarmPhantom adıyla Windows toast bildirimi ve sistem sesi. Windows dışında hiçbir şey yapmaz.</summary>
internal static class DesktopNotifier
{
    private const string AppUserModelId = "FarmPhantom.Desktop";
    private const string ToastScript = """
        $ErrorActionPreference = 'Stop'
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $title = [System.Security.SecurityElement]::Escape($env:FP_TITLE)
        $body = [System.Security.SecurityElement]::Escape($env:FP_BODY)
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$title</text><text>$body</text></binding></visual></toast>")
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('__APP_ID__').Show($toast)
        """;

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    public static void Show(string title, string body)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (!EnsureStartMenuShortcut())
            {
                return;
            }

            var script = ToastScript.Replace("__APP_ID__", AppUserModelId, StringComparison.Ordinal);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var info = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Metinler komut satırına değil ortam değişkenine konur; böylece içerik komut olarak yorumlanamaz.
            info.Environment["FP_TITLE"] = title;
            info.Environment["FP_BODY"] = body;

            AppLog.Info($"Windows bildirimi gönderiliyor: {title}");
            var process = Process.Start(info);
            if (process is null)
            {
                AppLog.Warn("FarmPhantom bildirimi için PowerShell süreci başlatılamadı.");
                return;
            }

            _ = ObserveToastProcessAsync(process, title);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("FarmPhantom Windows bildirimi gösterilemedi", ex);
        }
    }

    private static async Task ObserveToastProcessAsync(Process process, string title)
    {
        using (process)
        {
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode == 0)
                {
                    AppLog.Info($"Windows bildirimi gönderildi: {title}");
                    return;
                }

                var diagnostic = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                if (diagnostic.Length > 700)
                {
                    diagnostic = diagnostic[..700] + "…";
                }

                AppLog.Warn($"FarmPhantom bildirimi gösterilemedi (PowerShell çıkış kodu {process.ExitCode}). {diagnostic}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException
                or TimeoutException or System.ComponentModel.Win32Exception)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    // Süreç zaten kapandı veya sonlandırılamadı.
                }

                AppLog.Warn("FarmPhantom bildirimi sonucu alınamadı", ex);
            }
        }
    }

    private static bool EnsureStartMenuShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        object? shellLinkObject = null;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            Directory.CreateDirectory(programs);
            var shortcutPath = Path.Combine(programs, "FarmPhantom.lnk");

            var shellLinkType = Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true)!;
            shellLinkObject = Activator.CreateInstance(shellLinkType)
                ?? throw new InvalidOperationException("Windows shortcut object could not be created.");

            var shellLink = (IShellLinkW)shellLinkObject;
            Marshal.ThrowExceptionForHR(shellLink.SetPath(executable));
            Marshal.ThrowExceptionForHR(shellLink.SetWorkingDirectory(AppContext.BaseDirectory));
            Marshal.ThrowExceptionForHR(shellLink.SetDescription("FarmPhantom Steam kart takibi"));
            Marshal.ThrowExceptionForHR(shellLink.SetIconLocation(executable, 0));
            Marshal.ThrowExceptionForHR(shellLink.SetShowCmd(1));

            var appId = new PropVariant(AppUserModelId);
            var appIdKey = AppUserModelIdKey;
            try
            {
                var propertyStore = (IPropertyStore)shellLinkObject;
                Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref appIdKey, ref appId));
                Marshal.ThrowExceptionForHR(propertyStore.Commit());
            }
            finally
            {
                PropVariantClear(ref appId);
            }

            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLinkObject)
                .Save(shortcutPath, fRemember: true);
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("FarmPhantom bildirim kimliği için Başlat menüsü kısayolu oluşturulamadı", ex);
            return false;
        }
        finally
        {
            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    /// <summary>Windows'un standart bildirim sesi (MB_ICONASTERISK).</summary>
    public static void Beep()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            MessageBeep(0x40);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Ses çalınamazsa sessiz kal.
        }
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant propVariant);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        private ushort _type;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private IntPtr _value;

        public PropVariant(string value)
        {
            _type = 31; // VT_LPWSTR
            _reserved1 = 0;
            _reserved2 = 0;
            _reserved3 = 0;
            _value = Marshal.StringToCoTaskMemUni(value);
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr itemIdList);
        [PreserveSig] int SetIDList(IntPtr itemIdList);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCommand);
        [PreserveSig] int SetShowCmd(int showCommand);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
