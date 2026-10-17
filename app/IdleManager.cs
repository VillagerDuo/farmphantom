using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace FarmPhantom;

/// <summary>Çalışan tek bir idle sürecini (tek oyun) temsil eder.</summary>
internal sealed class IdleSession
{
    public IdleSession(uint appId, string name, Process process)
    {
        AppId = appId;
        Name = name;
        Process = process;
        StartedUtc = DateTime.UtcNow;
        LastDropUtc = StartedUtc;
    }

    public uint AppId { get; }
    public string Name { get; }
    public Process Process { get; }
    public DateTime StartedUtc { get; }
    public DateTime LastDropUtc { get; set; }
    public int DropCount { get; set; }
    public int? Remaining { get; set; }
    public bool Stopping { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Birden çok idle sürecini (her oyun için ayrı süreç) yönetir. Yalnızca arayüz iş parçacığından çağrılmalıdır;
/// süreç çıkış bildirimi <c>onExited</c> ile bildirilir ve çağıran taraf onu arayüz iş parçacığına taşımalıdır.
/// </summary>
internal sealed class IdleManager
{
    /// <summary>Steam aynı anda en fazla 32 oyunu oynuyor sayar.</summary>
    public const int HardLimit = 32;

    private readonly Dictionary<uint, IdleSession> _sessions = [];
    private readonly Action<IdleSession, int> _onExited;

    public IdleManager(Action<IdleSession, int> onExited) => _onExited = onExited;

    public int Count => _sessions.Count;

    public IReadOnlyList<IdleSession> Sessions =>
        _sessions.Values.OrderBy(session => session.StartedUtc).ToArray();

    public bool IsRunning(uint appId) => _sessions.ContainsKey(appId);

    public IdleSession? Get(uint appId) => _sessions.GetValueOrDefault(appId);

    public bool TryStart(uint appId, string name, out IdleSession? session, out string error)
    {
        session = null;
        error = string.Empty;

        if (_sessions.ContainsKey(appId))
        {
            error = $"{name} zaten çalışıyor.";
            return false;
        }

        if (_sessions.Count >= HardLimit)
        {
            error = $"Steam aynı anda en fazla {HardLimit} oyunu destekler.";
            return false;
        }

        try
        {
            var process = Process.Start(CreateStartInfo(appId));
            if (process is null)
            {
                error = "Idle işlemi başlatılamadı.";
                return false;
            }

            var created = new IdleSession(appId, name, process);
            _sessions[appId] = created;
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    created.LastError = args.Data;
                }
            };
            process.Exited += (_, _) =>
            {
                int code;
                try
                {
                    process.WaitForExit();
                    code = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    code = -1;
                }

                _onExited(created, code);
            };
            process.EnableRaisingEvents = true;
            process.BeginErrorReadLine();

            session = created;
            return true;
        }
        catch (Win32Exception ex)
        {
            AppLog.Error($"Idle işlemi başlatılamadı: {name} ({appId}).", ex);
            error = $"FarmPhantom idle işlemini başlatamadı: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Error($"Idle işlemi açılamadı: {name} ({appId}).", ex);
            error = $"FarmPhantom idle işlemi açılamadı: {ex.Message}";
            return false;
        }
    }

    public void Stop(uint appId)
    {
        if (_sessions.TryGetValue(appId, out var session))
        {
            Stop(session);
        }
    }

    public void StopAll()
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            Stop(session);
        }
    }

    /// <summary>Süreç bittikten sonra oturumu listeden çıkarır.</summary>
    public void Remove(IdleSession session)
    {
        if (_sessions.TryGetValue(session.AppId, out var current) && ReferenceEquals(current, session))
        {
            _sessions.Remove(session.AppId);
        }

        session.Process.Dispose();
    }

    private static void Stop(IdleSession session)
    {
        session.Stopping = true;
        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Süreç zaten bitmiş.
        }
        catch (Win32Exception ex)
        {
            AppLog.Error($"Idle işlemi durdurulamadı: {session.Name} ({session.AppId}).", ex);
        }
    }

    private static ProcessStartInfo CreateStartInfo(uint appId)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                ?? throw new InvalidOperationException("Application assembly path is unavailable.");
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("Application assembly path is unavailable.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--idle");
        startInfo.ArgumentList.Add(appId.ToString());
        return startInfo;
    }
}
