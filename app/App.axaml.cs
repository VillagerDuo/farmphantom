using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace FarmPhantom;

public sealed partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _main;
    private TrayIcon? _tray;

    /// <summary>Tepsi simgesi oluşturulabildiyse true; yalnızca o zaman pencere tepsiye saklanabilir.</summary>
    public bool TrayAvailable { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            // Çıkış açıkça istenir: pencere tepsiye saklanınca uygulama kapanmasın.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SetupTray();

            if (desktop.Args?.Contains("--tray") == true)
            {
                // Windows ile başlatma: animasyon ve pencere yok, doğrudan tepside çalışır.
                AppLog.Info("Tepsiye küçültülmüş başlatıldı.");
                CreateMain(show: false);
            }
            else
            {
                // Açılış animasyonu; bitince (ya da atlanınca) ana pencere açılır.
                var splash = new SplashWindow();
                desktop.MainWindow = splash;
                splash.Finished += () =>
                {
                    CreateMain(show: true);
                    splash.Close();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateMain(bool show)
    {
        _main = new MainWindow();
        _main.Closed += (_, _) => _desktop?.Shutdown();
        if (show)
        {
            _desktop!.MainWindow = _main;
            _main.Show();
        }
    }

    private void SetupTray()
    {
        try
        {
            var menu = new NativeMenu();

            var show = new NativeMenuItem("FarmPhantom'u göster");
            show.Click += (_, _) => ShowMain();
            menu.Add(show);

            var stop = new NativeMenuItem("Idle'ı durdur");
            stop.Click += (_, _) => _main?.StopAllFromTray();
            menu.Add(stop);

            menu.Add(new NativeMenuItemSeparator());

            var exit = new NativeMenuItem("Çıkış");
            exit.Click += (_, _) => ExitApplication();
            menu.Add(exit);

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FarmPhantom/FarmPhantom.ico"))),
                ToolTipText = "FarmPhantom",
                Menu = menu,
                IsVisible = true
            };
            _tray.Clicked += (_, _) => ShowMain();

            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            TrayAvailable = true;
        }
        catch (Exception ex)
        {
            // Tepsi desteklenmiyorsa uygulama yine de normal pencereyle çalışır.
            AppLog.Warn("Sistem tepsisi simgesi oluşturulamadı", ex);
        }
    }

    public void SetTrayToolTip(string text)
    {
        if (_tray is not null)
        {
            _tray.ToolTipText = text.Length > 120 ? text[..120] : text;
        }
    }

    public void ShowMain()
    {
        if (_main is null)
        {
            return;
        }

        if (_desktop is not null && _desktop.MainWindow != _main)
        {
            _desktop.MainWindow = _main;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    public void ExitApplication()
    {
        AppLog.Info("Çıkış istendi.");
        _main?.CleanupForExit();
        if (_tray is not null)
        {
            _tray.IsVisible = false;
        }

        _desktop?.Shutdown();
    }
}
