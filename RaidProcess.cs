using System.Diagnostics;

namespace RslCompanionUploader;

/// <summary>
/// Checks whether Raid: Shadow Legends is running. Detection is a cheap process-name lookup — the
/// same name the extraction engine attaches to (<c>ProcessReader.AttachToRaid</c> →
/// <c>Process.GetProcessesByName("Raid")</c>) — so it works without the private engine and never
/// touches game memory.
///
/// Deliberately just a check, not a watcher: "is the game running" is only half of what the UI
/// reports (the other half is whether account data is actually readable yet), so the two are polled
/// together on one timer in MainForm rather than from separate, independently-drifting loops.
/// </summary>
public static class RaidProcess
{
    private const string ProcessName = "Raid";

    public static bool IsRunning()
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
}
