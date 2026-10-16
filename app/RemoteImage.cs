using Avalonia;
using Avalonia.Controls;

namespace FarmPhantom;

/// <summary>
/// Adresten asenkron yüklenen görsel. Liste öğeleri yeniden kullanıldığında adres değişir ve eski yükleme yok sayılır.
/// Yüklenene kadar (ya da yüklenemezse) boş kalır; arkasındaki kutu yer tutucu olarak görünür.
/// </summary>
public sealed class RemoteImage : Image
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(Url));

    public static readonly StyledProperty<string?> FallbackUrlProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(FallbackUrl));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<RemoteImage, int>(nameof(DecodeWidth), 240);

    private int _version;

    static RemoteImage()
    {
        UrlProperty.Changed.AddClassHandler<RemoteImage>((image, _) => image.Reload());
        FallbackUrlProperty.Changed.AddClassHandler<RemoteImage>((image, _) => image.Reload());
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public string? FallbackUrl
    {
        get => GetValue(FallbackUrlProperty);
        set => SetValue(FallbackUrlProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Image);

    private async void Reload()
    {
        var version = ++_version;
        Source = null;

        var url = Url;
        var fallback = FallbackUrl;
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(fallback))
        {
            return;
        }

        var bitmap = await ImageCache.GetAsync(url, fallback, DecodeWidth);
        if (version == _version)
        {
            Source = bitmap;
        }
    }
}
