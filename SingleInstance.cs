using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RslCompanionUploader;

/// <summary>
/// Guarantees a single running copy of the uploader and lets a second launch hand its command-line
/// arguments to the already-running instance instead of opening a second window.
///
/// This is what makes every launch from the website work once the app is open: the site fires
/// <c>rslcompanion-extractor://sync?code=...</c>, Windows starts a *second* copy of the exe with that
/// URI, the second copy fails to take the mutex, forwards its args over a named pipe to the primary,
/// and exits. The primary hands them to <b>one</b> receiver — <c>MainForm</c>, which decides
/// whether a waiting sign-in panel or the window itself redeems the code.
///
/// <para><b>Before 1.40 the only receiver was the sign-in panel</b>, so a window that was already
/// signed in — the ordinary case for someone pressing "Update Data" on the site with the app open —
/// received the code, raised an event nobody was listening to, and dropped it. The site never saw an
/// exchange, the window never came forward, and the user was told to close the app and retry.
/// That is why there is now a single handler, and why launches that arrive before it is set are
/// queued rather than raised into the void.</para>
/// </summary>
internal static class SingleInstance
{
    // Per-user names so two Windows users on the same machine (or a Local\ session) never collide.
    // The suffix MUST be identical across separate process launches for the same user: the whole
    // single-instance handoff depends on the browser-launched process computing the same mutex/pipe
    // names as the already-running primary. String.GetHashCode() is randomized per process in modern
    // .NET, so it CANNOT be used here — it made every launch look like a fresh primary, which is why
    // browser sign-in opened a second window instead of forwarding the token to the waiting splash.
    private static readonly string Suffix = StableSuffix(Environment.UserName);
    private static readonly string MutexName = $@"Local\RslCompanionUploader.Instance.{Suffix}";
    private static readonly string PipeName = $"RslCompanionUploader.Ipc.{Suffix}";

    /// <summary>
    /// How long a second launch tries to reach the primary. Generous on purpose: the primary may be
    /// starting up (mutex taken, pipe not yet listening), and giving up loses a code that cannot be
    /// re-sent.
    /// </summary>
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long to wait for a primary that would not answer to finish exiting.</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    // Held for the primary process's lifetime; releasing it (on exit) frees the single-instance slot.
    private static Mutex? _mutex;
    private static LaunchChannel? _channel;

    private static readonly object Gate = new();
    private static Action<string[]>? _handler;
    private static readonly Queue<string[]> Pending = new();

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
            Listen();
            return true;
        }

        if (LaunchChannel.TrySend(PipeName, args, ForwardTimeout))
            return false;

        // The mutex is held but nobody answered: almost always a primary that is closing (it stops
        // listening before it releases the mutex). Wait for it to go and take over, so the launch is
        // handled here instead of being lost along with the window that was closing.
        try
        {
            if (!_mutex.WaitOne(ShutdownWait))
            {
                MessageBox.Show(
                    "RSL Companion Account Data Extractor is already running but didn't respond.\n\n"
                    + "Close it fully (check the system tray and Task Manager), then launch it again from rslcompanion.com.",
                    "RSL Companion", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it; the wait still made us the owner.
        }

        Listen();
        return true;
    }

    /// <summary>
    /// Sets the one receiver of forwarded launches (null to stop receiving). Launches that arrived
    /// while there was none are delivered to it now, in order, on the calling thread; later ones
    /// arrive on the pipe thread, so the receiver must marshal to the UI thread itself.
    /// </summary>
    public static void SetHandler(Action<string[]>? handler)
    {
        List<string[]> backlog;
        lock (Gate)
        {
            _handler = handler;
            if (handler is null) return;
            backlog = [.. Pending];
            Pending.Clear();
        }
        foreach (var args in backlog) handler(args);
    }

    private static void Listen() => _channel = LaunchChannel.Listen(PipeName, Deliver);

    internal static void Deliver(string[] args)
    {
        Action<string[]>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null)
            {
                Pending.Enqueue(args);
                return;
            }
        }
        handler(args);
    }

    // Deterministic across processes (unlike String.GetHashCode): first 4 bytes of SHA-256 of the
    // user name, hex-encoded. Same user → same suffix on every launch, so the second instance finds
    // the primary's mutex and pipe.
    private static string StableSuffix(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}

/// <summary>
/// The named pipe a second launch uses to hand its arguments to the primary. Separate from
/// <see cref="SingleInstance"/> (which owns the names and the mutex) so it can be exercised end to
/// end under a throwaway pipe name in tests.
///
/// <para>The exchange is: the client connects, writes the argument list, and waits for a one-byte
/// acknowledgement. <b>The acknowledgement is what makes "forwarded" mean delivered</b> — a client
/// that only wrote and hung up could report success for a message the server never finished reading,
/// and then exit with the only copy of a single-use code.</para>
///
/// <para>Both ends are <see cref="PipeOptions.CurrentUserOnly"/>: pipe names are machine-wide and
/// this one is derived from the user name, so another account could otherwise create it first and
/// collect the sign-in codes meant for this user.</para>
/// </summary>
internal sealed class LaunchChannel : IDisposable
{
    private const byte Ack = 0x06;

    /// <summary>Upper bound on a forwarded message: a launch URI is a few hundred bytes.</summary>
    private const int MaxArgs = 16;
    private const int MaxArgLength = 8 * 1024;

    private readonly CancellationTokenSource _stop = new();

    private LaunchChannel() { }

    /// <summary>
    /// Starts accepting launches on <paramref name="pipeName"/>. <paramref name="received"/> runs on a
    /// background thread, after the sender has been acknowledged.
    /// </summary>
    public static LaunchChannel Listen(string pipeName, Action<string[]> received)
    {
        var channel = new LaunchChannel();
        var thread = new Thread(() => channel.ServerLoop(pipeName, received))
        {
            IsBackground = true,
            Name = "RslIpcServer",
        };
        thread.Start();
        return channel;
    }

    private void ServerLoop(string pipeName, Action<string[]> received)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            string[]? args = null;
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5)); // a client that connects and stalls must not block the next launch
                args = ReadArgsAsync(server, timeout.Token).GetAwaiter().GetResult();
                server.WriteAsync(new[] { Ack }, timeout.Token).AsTask().GetAwaiter().GetResult();
                server.Flush();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Pipe faulted mid-transfer, or the message was malformed — accept the next connection.
                // A message read in full is still delivered below: the sender may simply have hung up
                // before reading the acknowledgement.
            }

            if (args is { Length: > 0 })
            {
                try { received(args); }
                catch { /* a receiver fault must not stop the next launch from being heard */ }
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="args"/> to whoever is listening on <paramref name="pipeName"/> and
    /// waits for the acknowledgement. Returns false when nobody answered in time.
    ///
    /// <para>Before writing, it lets the listening process take the foreground. This process was
    /// started by the browser in response to a click, so it is allowed to; the primary is not, and
    /// without the grant Windows reduces its attempt to come forward to a taskbar flash.</para>
    /// </summary>
    public static bool TrySend(string pipeName, string[] args, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.ConnectAsync(cts.Token).GetAwaiter().GetResult();

            if (NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid))
                NativeMethods.AllowSetForegroundWindow(serverPid);

            client.WriteAsync(Encode(args), cts.Token).AsTask().GetAwaiter().GetResult();
            client.Flush();

            var reply = new byte[1];
            var read = client.ReadAsync(reply, cts.Token).AsTask().GetAwaiter().GetResult();
            return read == 1 && reply[0] == Ack;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Count, then each argument as a length-prefixed UTF-8 string.</summary>
    internal static byte[] Encode(string[] args)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(args.Length);
            foreach (var arg in args) writer.Write(arg);
        }
        return buffer.ToArray();
    }

    private static async Task<string[]> ReadArgsAsync(Stream stream, CancellationToken ct)
    {
        var count = BitConverter.ToInt32(await ReadExactlyAsync(stream, 4, ct));
        if (count is < 0 or > MaxArgs) throw new InvalidDataException("Too many arguments.");

        var args = new string[count];
        for (var i = 0; i < count; i++)
        {
            var length = await Read7BitLengthAsync(stream, ct);
            if (length > MaxArgLength) throw new InvalidDataException("Argument too long.");
            args[i] = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, length, ct));
        }
        return args;
    }

    /// <summary>The length prefix <see cref="BinaryWriter.Write(string)"/> writes.</summary>
    private static async Task<int> Read7BitLengthAsync(Stream stream, CancellationToken ct)
    {
        var value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = (await ReadExactlyAsync(stream, 1, ct))[0];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Bad length prefix.");
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    public void Dispose() => _stop.Cancel();

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeServerProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(uint processId);
    }
}
