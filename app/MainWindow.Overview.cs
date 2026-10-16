using System.Collections.ObjectModel;
using System.ComponentModel;

namespace FarmPhantom;

/// <summary>Genel bakış sayfasında çalışan bir oyunun satırı. Metin her saniye güncellenir, görsel yeniden yüklenmez.</summary>
public sealed class RunningRow : INotifyPropertyChanged
{
    private string _summary = string.Empty;

    public RunningRow(uint appId, string name)
    {
        AppId = appId;
        Name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public uint AppId { get; }
    public string Name { get; }
    public string HeaderUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
    public string HeaderFallbackUrl => $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";

    public string Summary
    {
        get => _summary;
        set
        {
            if (_summary == value)
            {
                return;
            }

            _summary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }
}

public sealed partial class MainWindow
{
    private const int RecentDropLimit = 8;

    private readonly ObservableCollection<RunningRow> _overviewRunning = [];
    private readonly ObservableCollection<DropEntry> _recentDrops = [];

    private void InitializeOverview()
    {
        OverviewRunningList.ItemsSource = _overviewRunning;
        OverviewRecentList.ItemsSource = _recentDrops;
        _overviewRunning.CollectionChanged += (_, _) => OverviewRunningEmpty.IsVisible = _overviewRunning.Count == 0;
        _recentDrops.CollectionChanged += (_, _) => OverviewRecentEmpty.IsVisible = _recentDrops.Count == 0;
    }

    /// <summary>Kayıtlı geçmişten son düşenleri doldurur (kart resmi o kayıtlarda yoktur).</summary>
    private void SeedRecentDrops()
    {
        foreach (var record in _dropHistory.OrderByDescending(item => item.TimeUtc).Take(RecentDropLimit))
        {
            _recentDrops.Add(new DropEntry(record.TimeUtc.ToLocalTime(), record.GameName, record.CardName, record.IsFoil));
        }
    }

    private void AddRecentDrop(DropEntry entry)
    {
        _recentDrops.Insert(0, entry);
        while (_recentDrops.Count > RecentDropLimit)
        {
            _recentDrops.RemoveAt(_recentDrops.Count - 1);
        }
    }

    /// <summary>Genel bakış görünürken her saniye çağrılır; yalnızca değişen metinler güncellenir.</summary>
    private void UpdateOverview(IReadOnlyList<IdleSession> sessions)
    {
        if (!OverviewPage.IsVisible)
        {
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var row in _overviewRunning.Where(item => sessions.All(session => session.AppId != item.AppId)).ToArray())
        {
            _overviewRunning.Remove(row);
        }

        foreach (var session in sessions)
        {
            var row = _overviewRunning.FirstOrDefault(item => item.AppId == session.AppId);
            if (row is null)
            {
                row = new RunningRow(session.AppId, session.Name);
                _overviewRunning.Add(row);
            }

            var remaining = session.Remaining is int left
                ? LocalizationService.Instance.Format("overview.cardsRemaining", left)
                : LocalizationService.Instance.Translate("overview.remainingUnknownShort");
            row.Summary = LocalizationService.Instance.Format(
                "overview.runningRow",
                FormatSpan(now - session.StartedUtc),
                session.DropCount,
                remaining);
        }

        OverviewRunningText.Text = sessions.Count.ToString();
        OverviewRunningHint.Text = LocalizationService.Instance.Format("overview.runningHint", MaxParallel);

        var today = DateTime.Today;
        OverviewTodayText.Text = _dropHistory.Count(record => record.TimeUtc.ToLocalTime().Date == today).ToString();
        OverviewTotalHint.Text = LocalizationService.Instance.Format("overview.totalHint", _dropHistory.Count);
        OverviewSessionText.Text = _sessionDrops.ToString();

        var sum = 0;
        var unknown = 0;
        foreach (var game in _idleQueue)
        {
            var live = sessions.FirstOrDefault(session => session.AppId == game.AppId)?.Remaining;
            if ((live ?? game.RemainingCards) is int value)
            {
                sum += value;
            }
            else
            {
                unknown++;
            }
        }

        if (_idleQueue.Count == 0)
        {
            OverviewRemainingText.Text = "—";
            OverviewRemainingHint.Text = LocalizationService.Instance.Translate("overview.remainingAddGames");
        }
        else
        {
            OverviewRemainingText.Text = sum.ToString();
            OverviewRemainingHint.Text = unknown > 0
                ? LocalizationService.Instance.Format("overview.remainingUnknown", unknown)
                : LocalizationService.Instance.Format("overview.remainingQueue", _idleQueue.Count);
        }
    }
}
