using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FarmPhantom;

/// <summary>
/// Açılış animasyonu: logo yumuşakça büyüyerek belirir, etrafında halka atar, yükleme çubuğu dolar ve pencere solar.
/// Tıklayarak ya da bir tuşa basarak atlanabilir. Bittiğinde <see cref="Finished"/> tetiklenir.
/// </summary>
internal sealed class SplashWindow : Window
{
    private const double TotalSeconds = 2.6;
    private const double FadeOutSeconds = 0.4;
    private const double BarWidth = 220;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly Border _logoBox;
    private readonly ScaleTransform _logoScale = new(0.7, 0.7);
    private readonly Border _ring;
    private readonly ScaleTransform _ringScale = new(1, 1);
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly Border _barFill;
    private bool _done;

    public event Action? Finished;

    public SplashWindow()
    {
        Width = 440;
        Height = 340;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "FarmPhantom";
        Background = new SolidColorBrush(Color.Parse("#07080F"));
        Topmost = true;
        TrySetBorderless();

        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FarmPhantom/FarmPhantom.ico")));
        }
        catch (Exception)
        {
            // Simge yüklenemezse sorun değil.
        }

        var logo = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://FarmPhantom/uygulamalogosu.png"))),
            Stretch = Stretch.UniformToFill
        };

        _logoBox = new Border
        {
            Width = 112,
            Height = 112,
            CornerRadius = new CornerRadius(30),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.Parse("#252A55")),
            BorderThickness = new Thickness(1),
            Child = logo,
            Opacity = 0,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = _logoScale
        };

        _ring = new Border
        {
            Width = 112,
            Height = 112,
            CornerRadius = new CornerRadius(30),
            BorderBrush = new SolidColorBrush(Color.Parse("#8B5CFF")),
            BorderThickness = new Thickness(2),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = _ringScale
        };

        var logoHost = new Grid { Width = 180, Height = 150, HorizontalAlignment = HorizontalAlignment.Center };
        logoHost.Children.Add(_ring);
        logoHost.Children.Add(_logoBox);

        _title = new TextBlock
        {
            Text = "FarmPhantom",
            FontFamily = new FontFamily("Bahnschrift, Segoe UI, Arial"),
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#EEEDFF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0
        };

        _subtitle = new TextBlock
        {
            Text = "Hazırlanıyor…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#7C81A8")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0
        };

        _barFill = new Border
        {
            Width = 0,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#8B5CFF"), 0),
                    new GradientStop(Color.Parse("#2DB5FF"), 1)
                }
            }
        };

        var bar = new Border
        {
            Width = BarWidth,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.Parse("#191D42")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0),
            Child = _barFill
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { logoHost, _title, _subtitle, bar }
        };

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#252A55")),
            BorderThickness = new Thickness(1),
            Child = stack
        };

        PointerPressed += (_, _) => Complete();
        KeyDown += (_, _) => Complete();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        Opened += (_, _) => _timer.Start();
    }

    private void Tick()
    {
        var t = _clock.Elapsed.TotalSeconds;

        // Logo: 0–0.9 sn büyüyerek ve belirginleşerek gelir.
        var logoP = EaseOut(Clamp01(t / 0.9));
        _logoBox.Opacity = logoP;
        var scale = 0.7 + 0.3 * logoP;
        _logoScale.ScaleX = scale;
        _logoScale.ScaleY = scale;

        // Halka: logo geldikten sonra her saniye bir kez dışarı doğru yayılıp söner.
        if (t > 0.6)
        {
            var phase = (t - 0.6) % 1.0;
            _ringScale.ScaleX = 1 + 0.45 * EaseOut(phase);
            _ringScale.ScaleY = _ringScale.ScaleX;
            _ring.Opacity = 0.55 * (1 - phase);
        }

        // Başlık ve alt yazı: 0.4–1.1 sn arasında belirir.
        var textP = EaseOut(Clamp01((t - 0.4) / 0.7));
        _title.Opacity = textP;
        _title.Margin = new Thickness(0, 8 * (1 - textP), 0, 0);
        _subtitle.Opacity = textP;

        // Yükleme çubuğu: tüm süre boyunca dolar.
        _barFill.Width = BarWidth * EaseInOut(Clamp01(t / (TotalSeconds - FadeOutSeconds)));
        if (t > TotalSeconds - FadeOutSeconds - 0.3)
        {
            _subtitle.Text = "Hazır";
        }

        // Son bölüm: pencere solar.
        var fadeStart = TotalSeconds - FadeOutSeconds;
        if (t >= fadeStart)
        {
            Opacity = 1 - Clamp01((t - fadeStart) / FadeOutSeconds);
        }

        if (t >= TotalSeconds)
        {
            Complete();
        }
    }

    private void Complete()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        _timer.Stop();
        Finished?.Invoke();
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    private static double EaseInOut(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    /// <summary>Avalonia sürümüne göre özellik adı değişebildiği için (SystemDecorations / WindowDecorations) yansıtmayla ayarlanır.</summary>
    private void TrySetBorderless()
    {
        try
        {
            var property = typeof(Window).GetProperty("WindowDecorations") ?? typeof(Window).GetProperty("SystemDecorations");
            if (property is not null && property.PropertyType.IsEnum)
            {
                property.SetValue(this, Enum.Parse(property.PropertyType, "None"));
            }
        }
        catch (Exception)
        {
            // Çerçeveli açılırsa da animasyon çalışır.
        }
    }
}
