using System.Diagnostics;

namespace RslCompanionUploader;

/// <summary>
/// Polls for the running Raid: Shadow Legends process and raises an event when it appears or
/// disappears, so the UI can show a live "connected / not connected" status and react when the game
/// becomes available. Detection is a cheap process-name lookup — the same name the extraction engine
/// attaches to (<c>ProcessReader.AttachToRaid</c> → <c>Process.GetProcessesByName("Raid")</c>) — so
/// it works without the private engine and never touches game memory.
///
/// The timer is a WinForms timer, so ticks and <see cref="ConnectionChanged"/> fire on the UI thread;
/// handlers can update controls directly.
/// </summary>
public sealed class RaidProcessMonitor : IDisposable
{
    private const string ProcessName = "Raid";

    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>True while a Raid process is running.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised (on the UI thread) only when the connection state changes.</summary>
    public event Action<bool>? ConnectionChanged;

    public RaidProcessMonitor(int pollIntervalMs = 3000)
    {
        _timer = new System.Windows.Forms.Timer { Interval = pollIntervalMs };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>
    /// Reports the current state immediately (always raising <see cref="ConnectionChanged"/> once so
    /// the UI can render its initial status, even when the state hasn't "changed"), then keeps polling.
    /// </summary>
    public void Start()
    {
        IsConnected = IsRaidRunning();
        ConnectionChanged?.Invoke(IsConnected);
        _timer.Start();
    }

    private void Poll()
    {
        var connected = IsRaidRunning();
        if (connected == IsConnected) return; // only fire on an actual change after startup
        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private static bool IsRaidRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            // GetProcessesByName returns live Process objects that hold handles until disposed.
            foreach (var p in processes) p.Dispose();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
