using System.IO.Pipes;
using System.Text;

namespace RslCompanionUploader;

/// <summary>
/// Guarantees a single running copy of the uploader and lets a second launch hand its command-line
/// arguments to the already-running instance instead of opening a second window.
///
/// This is what makes browser sign-in work: the app opens the user's browser and keeps waiting; when
/// the website fires <c>rslcompanion-extractor://sync?rt=...</c>, Windows starts a *second* copy of
/// the exe with that URI. The second copy fails to take the mutex, forwards its args over a named
/// pipe to the primary, and exits. The primary raises <see cref="SecondInstanceLaunched"/> so the
/// waiting sign-in window can complete the handoff.
///
/// <see cref="SecondInstanceLaunched"/> fires on a background (pipe) thread — subscribers must marshal
/// to the UI thread themselves.
/// </summary>
internal static class SingleInstance
{
    // Per-user names so two Windows users on the same machine (or a Local\ session) never collide.
    private static readonly string Suffix = Environment.UserName.GetHashCode().ToString("x8");
    private static readonly string MutexName = $@"Local\RslCompanionUploader.Instance.{Suffix}";
    private static readonly string PipeName = $"RslCompanionUploader.Ipc.{Suffix}";

    // Held for the primary process's lifetime; releasing it (on exit) frees the single-instance slot.
    private static Mutex? _mutex;

    /// <summary>Raised on a background thread when another instance forwards its launch args.</summary>
    public static event Action<string[]>? SecondInstanceLaunched;

    /// <summary>
    /// Call once at startup. Returns <c>true</c> when this process is the primary (keep running).
    /// Returns <c>false</c> when another instance already owns the slot — the args were forwarded to
    /// it and this process should exit immediately.
    /// </summary>
    public static bool TryBecomePrimary(string[] args)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            StartServer();
            return true;
        }

        TryForward(args);
        return false;
    }

    private static void StartServer()
    {
        var thread = new Thread(ServerLoop) { IsBackground = true, Name = "RslIpcServer" };
        thread.Start();
    }

    private static void ServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8);
                var args = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length > 0)
                    SecondInstanceLaunched?.Invoke(args);
            }
            catch
            {
                // Pipe faulted mid-transfer — loop and accept the next connection.
            }
        }
    }

    private static void TryForward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.Write(string.Join('\n', args));
        }
        catch
        {
            // Primary may be mid-shutdown or not yet listening; nothing more we can do here.
        }
    }
}
