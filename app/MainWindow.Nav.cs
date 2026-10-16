using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FarmPhantom;

public sealed partial class MainWindow
{
    private string _currentPage = "overview";

    private void InitializeNavigation() => ShowPage("overview");

    private void NavButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
        {
            ShowPage(page);
        }
    }

    private void ShowPage(string page)
    {
        _currentPage = page;
        PageTitleText.Text = page switch
        {
            "games" => LocalizationService.Instance.Translate("page.games"),
            "drops" => LocalizationService.Instance.Translate("page.drops"),
            "sales" => LocalizationService.Instance.Translate("page.sales"),
            "settings" => LocalizationService.Instance.Translate("page.settings"),
            _ => LocalizationService.Instance.Translate("page.overview")
        };

        OverviewPage.IsVisible = page == "overview";
        GamesPage.IsVisible = page == "games";
        DropsPage.IsVisible = page == "drops";
        SalesPage.IsVisible = page == "sales";
        SettingsPage.IsVisible = page == "settings";

        SetActive(NavOverviewButton, page == "overview");
        SetActive(NavGamesButton, page == "games");
        SetActive(NavDropsButton, page == "drops");
        SetActive(NavSalesButton, page == "sales");
        SetActive(NavSettingsButton, page == "settings");

        if (page == "overview")
        {
            UpdateOverview(_idleManager.Sessions);
        }

        if (page == "settings")
        {
            RefreshNativeDllStatus();
        }
    }

    private static void SetActive(Button button, bool active)
    {
        if (active)
        {
            if (!button.Classes.Contains("active"))
            {
                button.Classes.Add("active");
            }
        }
        else
        {
            button.Classes.Remove("active");
        }
    }
}
