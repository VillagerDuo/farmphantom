using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FarmPhantom;

/// <summary>Satışa çıkarılacak bir kartın taslağı. Kullanıcı onaylamadan hiçbir şey gönderilmez.</summary>
public sealed class SaleDraft : INotifyPropertyChanged
{
    private string _priceText;
    private string _netText = string.Empty;
    private string _statusText;
    private bool _isApproved;

    public SaleDraft(SteamInventoryCard card, int? suggestedBuyerMinor, string statusText)
    {
        Card = card;
        _statusText = statusText;
        _priceText = suggestedBuyerMinor is int minor ? SteamPrice.FormatMinor(minor) : string.Empty;
        Recalculate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SteamInventoryCard Card { get; }
    public string AssetId => Card.AssetId;
    public string ImageUrl => Card.ImageUrl;
    public string Name => Card.IsFoil ? $"{Card.Name} (foil)" : Card.Name;
    public int BuyerPaysMinor { get; private set; }
    public int SellerReceivesMinor { get; private set; }
    public bool HasValidPrice => SellerReceivesMinor >= 1;

    public string PriceText
    {
        get => _priceText;
        set
        {
            if (_priceText == value)
            {
                return;
            }

            _priceText = value;
            Recalculate();
            OnPropertyChanged();
        }
    }

    public string NetText
    {
        get => _netText;
        private set
        {
            if (_netText == value)
            {
                return;
            }

            _netText = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsApproved
    {
        get => _isApproved;
        set
        {
            var next = value && HasValidPrice;
            if (_isApproved == next)
            {
                if (value != next)
                {
                    OnPropertyChanged();
                }

                return;
            }

            _isApproved = next;
            OnPropertyChanged();
        }
    }

    private void Recalculate()
    {
        var previousBuyerPays = BuyerPaysMinor;
        var previousSellerReceives = SellerReceivesMinor;
        var previousHasValidPrice = HasValidPrice;
        var buyer = SteamPrice.ParseMinor(_priceText);
        var localization = LocalizationService.Instance;
        if (buyer is null or < 3)
        {
            BuyerPaysMinor = 0;
            SellerReceivesMinor = 0;
            NetText = localization.Translate(
                string.IsNullOrWhiteSpace(_priceText) ? "sales.netEnterPrice" : "sales.netInvalidPrice");
        }
        else
        {
            SellerReceivesMinor = SteamPrice.SellerReceives(buyer.Value);
            BuyerPaysMinor = SteamPrice.BuyerPays(SellerReceivesMinor);
            NetText = localization.Format(
                "sales.netBreakdown",
                SteamPrice.FormatMinor(SellerReceivesMinor),
                SteamPrice.FormatMinor(BuyerPaysMinor));
        }

        if (BuyerPaysMinor != previousBuyerPays)
        {
            OnPropertyChanged(nameof(BuyerPaysMinor));
        }

        if (SellerReceivesMinor != previousSellerReceives)
        {
            OnPropertyChanged(nameof(SellerReceivesMinor));
        }

        if (HasValidPrice != previousHasValidPrice)
        {
            OnPropertyChanged(nameof(HasValidPrice));
        }

        if (_isApproved && !HasValidPrice)
        {
            _isApproved = false;
            OnPropertyChanged(nameof(IsApproved));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
