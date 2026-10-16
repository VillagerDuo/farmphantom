using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace FarmPhantom;

public sealed partial class MainWindow
{
    private static readonly SteamCurrency[] Currencies =
    [
        new(1, "USD"),
        new(17, "TRY"),
        new(3, "EUR"),
        new(2, "GBP"),
        new(5, "RUB")
    ];

    private static readonly TimeSpan MarketRequestDelay = TimeSpan.FromMilliseconds(3200);

    private readonly SteamMarketClient _marketClient = new();
    private readonly ObservableCollection<SaleDraft> _saleDrafts = [];
    private CancellationTokenSource? _salesCancellation;

    private void InitializeSales()
    {
        SaleDraftsListBox.ItemsSource = _saleDrafts;
        _saleDrafts.CollectionChanged += (_, _) => DraftsEmptyText.IsVisible = _saleDrafts.Count == 0;
        CurrencyComboBox.ItemsSource = Currencies;
        CurrencyComboBox.SelectedIndex = 0;
    }

    private SteamCurrency SelectedCurrency =>
        Currencies[Math.Clamp(CurrencyComboBox.SelectedIndex, 0, Currencies.Length - 1)];

    private CancellationToken BeginSalesOperation()
    {
        _salesCancellation?.Cancel();
        _salesCancellation?.Dispose();
        _salesCancellation = new CancellationTokenSource();
        CancelSalesButton.IsEnabled = true;
        PrepareDraftsButton.IsEnabled = false;
        ListApprovedButton.IsEnabled = false;
        AutoSellButton.IsEnabled = false;
        return _salesCancellation.Token;
    }

    private void EndSalesOperation()
    {
        CancelSalesButton.IsEnabled = false;
        PrepareDraftsButton.IsEnabled = true;
        ListApprovedButton.IsEnabled = true;
        AutoSellButton.IsEnabled = true;
    }

    private void CancelSalesButton_Click(object? sender, RoutedEventArgs e) => _salesCancellation?.Cancel();

    private void ClearCookiesButton_Click(object? sender, RoutedEventArgs e)
    {
        MarketSessionIdTextBox.Text = string.Empty;
        MarketLoginSecureTextBox.Text = string.Empty;
        SalesStatusText.Text = LocalizationService.Instance.Translate("sales.cookiesCleared");
        IdleStatus.Text = LocalizationService.Instance.Translate("sales.cookiesCleared");
    }

    private void ApproveAllDraftsButton_Click(object? sender, RoutedEventArgs e)
    {
        var approved = 0;
        foreach (var draft in _saleDrafts)
        {
            draft.IsApproved = true;
            if (draft.IsApproved)
            {
                approved++;
            }
        }

        SalesStatusText.Text = approved == 0
            ? LocalizationService.Instance.Translate("sales.approvedNone")
            : LocalizationService.Instance.Format("sales.approvedCount", approved);
    }

    private void ClearDraftsButton_Click(object? sender, RoutedEventArgs e)
    {
        _saleDrafts.Clear();
        SalesStatusText.Text = LocalizationService.Instance.Translate("sales.draftsCleared");
    }

    private async void PrepareDraftsButton_Click(object? sender, RoutedEventArgs e) =>
        await PrepareDraftsAsync(forceBuyOrders: false);

    /// <summary>
    /// Seçili (yoksa tüm) kartlar için taslak üretir. Fiyat kaynağı: en düşük satış ilanı veya
    /// en yüksek satın alma talimatı. Tüm fiyatlar sorunsuz alındıysa yeni taslakları, aksi halde null döndürür.
    /// </summary>
    private async Task<List<SaleDraft>?> PrepareDraftsAsync(bool forceBuyOrders)
    {
        var selected = TradingCardsListBox.SelectedItems?.OfType<SteamInventoryCard>().ToArray() ?? [];
        var source = selected.Length > 0 ? selected : _tradingCards.ToArray();
        if (source.Length == 0)
        {
            SalesStatusText.Text = LocalizationService.Instance.Translate("sales.loadCardsFirst");
            return null;
        }

        var existing = _saleDrafts.Select(draft => draft.AssetId).ToHashSet(StringComparer.Ordinal);
        var cards = source.Where(card => !existing.Contains(card.AssetId)).ToArray();
        if (cards.Length == 0)
        {
            SalesStatusText.Text = LocalizationService.Instance.Translate("sales.alreadyDrafted");
            return null;
        }

        var useBuyOrders = forceBuyOrders || PriceModeComboBox.SelectedIndex == 1;
        var currency = SelectedCurrency;
        var ct = BeginSalesOperation();
        try
        {
            var names = cards.Select(card => card.MarketHashName).Distinct(StringComparer.Ordinal).ToArray();
            var prices = new Dictionary<string, MarketPrice?>(StringComparer.Ordinal);
            var orderPrices = new Dictionary<string, int?>(StringComparer.Ordinal);
            string? failure = null;

            for (var i = 0; i < names.Length && failure is null; i++)
            {
                SalesStatusText.Text = LocalizationService.Instance.Format("sales.pricesLoading", i + 1, names.Length);
                try
                {
                    if (i > 0)
                    {
                        await Task.Delay(MarketRequestDelay, ct);
                    }

                    if (useBuyOrders)
                    {
                        var order = await _marketClient.GetHighestBuyOrderAsync(names[i], currency.Code, ct);
                        orderPrices[names[i]] = order;
                        if (order is null)
                        {
                            // Bekleyen talimat yok: en düşük ilan fiyatına geri dön.
                            await Task.Delay(MarketRequestDelay, ct);
                            prices[names[i]] = await _marketClient.GetLowestPriceAsync(names[i], currency.Code, ct);
                        }
                    }
                    else
                    {
                        prices[names[i]] = await _marketClient.GetLowestPriceAsync(names[i], currency.Code, ct);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException
                    || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    failure = ex is OperationCanceledException ? "Steam yanıt vermedi." : ex.Message;
                }
            }

            var created = new List<SaleDraft>();
            foreach (var card in cards)
            {
                if (useBuyOrders && orderPrices.GetValueOrDefault(card.MarketHashName) is int orderMinor)
                {
                    created.Add(new SaleDraft(
                        card,
                        orderMinor,
                        LocalizationService.Instance.Format("sales.recommendedBuyOrder", SteamPrice.FormatMinor(orderMinor))));
                    continue;
                }

                var price = prices.GetValueOrDefault(card.MarketHashName);
                created.Add(new SaleDraft(
                    card,
                    price?.LowestMinor,
                    price is null
                        ? LocalizationService.Instance.Translate("sales.enterPrice")
                        : useBuyOrders
                            ? LocalizationService.Instance.Format("sales.noBuyOrder", price.DisplayText)
                            : LocalizationService.Instance.Format("sales.recommendedListing", price.DisplayText)));
            }

            foreach (var draft in created)
            {
                _saleDrafts.Add(draft);
            }

            SalesStatusText.Text = failure is null
                ? LocalizationService.Instance.Format("sales.draftsReady", cards.Length)
                : LocalizationService.Instance.Format("sales.draftsPartial", cards.Length, failure);
            return failure is null ? created : null;
        }
        catch (OperationCanceledException)
        {
            SalesStatusText.Text = LocalizationService.Instance.Translate("sales.priceFetchCancelled");
            return null;
        }
        finally
        {
            EndSalesOperation();
        }
    }

    /// <summary>Kartları satın alma talimatına göre fiyatlar, geçerli olanları onaylar ve son onay penceresini açar.</summary>
    private async void AutoSellButton_Click(object? sender, RoutedEventArgs e)
    {
        var created = await PrepareDraftsAsync(forceBuyOrders: true);
        if (created is null)
        {
            return;
        }

        if (created.Count == 0)
        {
            SalesStatusText.Text = LocalizationService.Instance.Translate("sales.noNewCards");
            return;
        }

        foreach (var draft in created)
        {
            draft.IsApproved = true;
        }

        await ListApprovedAsync();
    }

    private async void ListApprovedButton_Click(object? sender, RoutedEventArgs e) => await ListApprovedAsync();

    private async Task ListApprovedAsync()
    {
        if (!TryGetSession(out var session, out var sessionError))
        {
            SalesStatusText.Text = sessionError;
            return;
        }

        var approved = _saleDrafts.Where(draft => draft.IsApproved).ToArray();
        if (approved.Length == 0)
        {
            SalesStatusText.Text = LocalizationService.Instance.Translate("sales.noApprovedDrafts");
            return;
        }

        var floor = SteamPrice.ParseMinor(MinPriceTextBox.Text) ?? 0;
        var valid = new List<SaleDraft>();
        foreach (var draft in approved)
        {
            if (draft.HasValidPrice && draft.SellerReceivesMinor >= floor)
            {
                valid.Add(draft);
            }
            else
            {
                draft.IsApproved = false;
                draft.StatusText = LocalizationService.Instance.Translate("sales.belowFloorDraft");
            }
        }

        if (valid.Count == 0)
        {
            SalesStatusText.Text = LocalizationService.Instance.Translate("sales.belowMinimum");
            return;
        }

        var currency = SelectedCurrency;
        var totalNet = valid.Sum(draft => draft.SellerReceivesMinor);
        var lines = string.Join(
            '\n',
            valid.Take(12).Select(draft => $"• {draft.Name} — {SteamPrice.FormatMinor(draft.BuyerPaysMinor)} {currency.Label}"));
        var more = valid.Count > 12 ? $"\n… ve {valid.Count - 12} kart daha" : string.Empty;
        var message =
            LocalizationService.Instance.Format(
                "sales.confirmMessage",
                valid.Count,
                SteamPrice.FormatMinor(totalNet),
                currency.Label,
                lines,
                more);

        if (!await ConfirmAsync(
            LocalizationService.Instance.Translate("sales.confirmTitle"),
            message,
            LocalizationService.Instance.Translate("sales.confirmButton")))
        {
            SalesStatusText.Text = LocalizationService.Instance.Translate("sales.listCancelled");
            return;
        }

        var ct = BeginSalesOperation();
        var listed = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < valid.Count; i++)
            {
                var draft = valid[i];
                SalesStatusText.Text = LocalizationService.Instance.Format("sales.listingProgress", i + 1, valid.Count, draft.Name);
                if (i > 0)
                {
                    await Task.Delay(MarketRequestDelay, ct);
                }

                SellResult result;
                try
                {
                    result = await _marketClient.SellAsync(session, draft.AssetId, draft.SellerReceivesMinor, ct);
                }
                catch (HttpRequestException ex)
                {
                    result = new SellResult(false, false, false, $"Bağlantı hatası: {ex.Message}", StopBatch: true);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = new SellResult(false, false, false, "Steam yanıt vermedi.", StopBatch: true);
                }

                if (result.Success)
                {
                    listed++;
                    draft.IsApproved = false;
                    draft.StatusText = result.NeedsMobileConfirmation
                        ? LocalizationService.Instance.Translate("sales.mobileConfirmation")
                        : result.NeedsEmailConfirmation
                            ? LocalizationService.Instance.Translate("sales.emailConfirmation")
                            : LocalizationService.Instance.Translate("sales.listingSuccess");
                }
                else
                {
                    failed++;
                    draft.StatusText = LocalizationService.Instance.Format("sales.listingError", result.Message);
                    if (result.StopBatch)
                    {
                        break;
                    }
                }
            }

            SalesStatusText.Text = failed == 0
                ? LocalizationService.Instance.Format("sales.listingFinished", listed)
                : LocalizationService.Instance.Format("sales.listingPartial", listed, failed);
        }
        catch (OperationCanceledException)
        {
            SalesStatusText.Text = LocalizationService.Instance.Format("sales.listingCanceledCount", listed);
        }
        finally
        {
            EndSalesOperation();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 540,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#0B0D1A")
        };

        var confirm = new Button { Content = confirmText, Classes = { "primary" } };
        var cancel = new Button { Content = "Vazgeç" };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" } },
                new ScrollViewer
                {
                    MaxHeight = 340,
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Classes = { "muted" }
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, confirm }
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }
}
