#if EXTRACTION
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RslCompanionUploader;

/// <summary>
/// The extraction engine, as this app ships it: <c>RslCompanionEngine.dll</c>, a native DLL compiled
/// ahead of time from the private submodule's <c>extraction/Native</c> project and copied beside the exe
/// by the <c>BuildNativeEngine</c> target. This class is the whole of the app's contact with it.
///
/// <para><b>Why native (1.48).</b> The engine used to ship as two managed assemblies, which decompile back
/// to near-source in seconds. Native code needs a disassembler and real effort. The public half of the
/// contract is only what is here: entry-point names and JSON shapes, and the payload's shape is public
/// anyway (<c>docs/export-schema.md</c>).</para>
///
/// <para><b>The C API</b> (the engine's <c>Native/Exports.cs</c> is the source of truth): a call returns 0
/// and writes UTF-8 JSON to <c>result</c>, or non-zero and writes the engine exception's message, which
/// is thrown here as <see cref="EngineException"/> so <c>DescribeExtractionFailure</c> keeps matching on
/// the same text. Every <c>result</c> is released with <c>rsl_free</c>. The calls block; callers already
/// ran the engine on a worker thread and still do.</para>
/// </summary>
internal static partial class Engine
{
    private const string Dll = "RslCompanionEngine";

    /// <summary>The engine's <c>Exports.AbiVersion</c> this app was written against.</summary>
    private const int ExpectedAbi = 1;

    public sealed record GameBuildInfo(string GameAssemblyHash, string? GameVersion, bool CoveredByShippedCatalog);

    public enum AccountDiscoveryStatus { Found, NotReady, NeedsCalibration, SignedOut }

    public sealed record AccountDiscoveryResult(AccountDiscoveryStatus Status, string? AccountId = null, string? Name = null);

    public sealed record CalibrationResult(
        bool Success,
        string? AccountId = null,
        string? Name = null,
        string? GameAssemblyHash = null,
        string? ExportedCatalogPath = null,
        string? Error = null);

    /// <summary>What a served base-stats catalog holds, from the engine's own parser.</summary>
    public sealed record HeroBaseStatsInfo(DateTimeOffset? GeneratedAt, string? GameVersion, int Count);

    private sealed record EnginePaths(string LocalCatalogPath, string KnownOffsetsFileName, string HeroBaseStatsLocalPath);

    /// <summary>
    /// One export: the payload exactly as the engine serialized it, plus the handful of facts the app logs
    /// and checks before sending it.
    /// </summary>
    public sealed class Profile
    {
        public required JsonObject Payload { get; init; }
        public string AccountId => Payload["accountId"]?.GetValue<string>() ?? "";
        public string AccountName => Payload["account"]?["name"]?.GetValue<string>() ?? "";
        public int Count(string array) => Payload[array] is JsonArray a ? a.Count : 0;
    }

    public sealed class EngineException(string message) : Exception(message);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    // ─── Calls ───────────────────────────────────────────────────────────────

    /// <summary>The running game's build, or null when Raid can't be inspected.</summary>
    public static GameBuildInfo? TryGetGameBuild()
    {
        try { return Read<GameBuildInfo?>(Call(rsl_game_build)); }
        catch { return null; }
    }

    public static AccountDiscoveryResult DiscoverAccount(string? cachePath)
        => Read<AccountDiscoveryResult>(Call((out nint r) => rsl_discover_account(cachePath, out r)))!;

    public static CalibrationResult Calibrate(string? cachePath, string? exportCatalogPath)
        => Read<CalibrationResult>(Call((out nint r) => rsl_calibrate(cachePath, exportCatalogPath, out r)))!;

    /// <summary>
    /// Reads the whole account. <paramref name="log"/> receives every line the engine writes while it
    /// runs, on the calling thread.
    /// </summary>
    public static Profile ExtractConsolidated(string? cachePath, bool includeArtifacts, Action<string> log)
    {
        lock (LogGate)
        {
            _log = log;
            SetLogSink(true);
            try
            {
                var json = Call((out nint r) => rsl_extract_consolidated(cachePath, includeArtifacts ? 1 : 0, out r));
                return new Profile { Payload = JsonNode.Parse(json)!.AsObject() };
            }
            finally
            {
                SetLogSink(false);
                _log = null;
            }
        }
    }

    public static DateTimeOffset? HeroBaseStatsGeneratedAt()
        => Read<DateTimeOffset?>(Call(rsl_hero_base_stats_generated_at));

    /// <summary>
    /// Parses a served catalog with the engine's own reader. Null when it parsed but is incomplete; throws
    /// <see cref="EngineException"/> when it doesn't parse.
    /// </summary>
    public static HeroBaseStatsInfo? InspectHeroBaseStats(string json)
        => Read<HeroBaseStatsInfo?>(Call((out nint r) => rsl_hero_base_stats_inspect(json, out r)));

    /// <summary>
    /// Whether runs write <c>logs/extract_*.log</c> (admins only). Never throws: it is set from the
    /// constructor and on every sign-in, and a missing engine must not cost the window.
    /// </summary>
    public static void SetWriteLogFiles(bool enabled)
    {
        try
        {
            EnsureLoaded();
            rsl_set_write_log_files(enabled ? 1 : 0);
        }
        catch
        {
        }
    }

    private static readonly Lazy<EnginePaths> PathsLazy = new(() => Read<EnginePaths>(Call(rsl_paths))!);

    /// <summary><c>%LOCALAPPDATA%\RslCompanion\calibrated-offsets.json</c>: this PC's own memory maps.</summary>
    public static string LocalCatalogPath => PathsLazy.Value.LocalCatalogPath;

    /// <summary>The shipped catalog's file name, beside the exe.</summary>
    public static string KnownOffsetsFileName => PathsLazy.Value.KnownOffsetsFileName;

    /// <summary>Where a downloaded base-stats catalog goes, to win over the bundled one.</summary>
    public static string HeroBaseStatsLocalPath => PathsLazy.Value.HeroBaseStatsLocalPath;

    // ─── Plumbing ────────────────────────────────────────────────────────────

    private delegate int Invoke(out nint result);

    private static string Call(Invoke invoke)
    {
        EnsureLoaded();
        int rc = invoke(out nint p);
        string text;
        try { text = p == 0 ? "" : Marshal.PtrToStringUTF8(p) ?? ""; }
        finally { if (p != 0) rsl_free(p); }
        if (rc != 0) throw new EngineException(text);
        return text;
    }

    private static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);

    private static readonly Lazy<Exception?> LoadFailure = new(() =>
    {
        try
        {
            int abi = rsl_abi_version();
            return abi == ExpectedAbi
                ? null
                : new EngineException($"The extraction engine in this install is the wrong version ({abi}, expected {ExpectedAbi}).");
        }
        catch (DllNotFoundException)
        {
            return new EngineException("The extraction engine (RslCompanionEngine.dll) is missing from this install.");
        }
        catch (EntryPointNotFoundException)
        {
            return new EngineException("The extraction engine (RslCompanionEngine.dll) in this install is not one this app can use.");
        }
    });

    private static void EnsureLoaded()
    {
        if (LoadFailure.Value is { } failure) throw failure;
    }

    // The engine's console output, forwarded line by line while an export runs (it used to be the app
    // redirecting its own Console around the same call). One export at a time owns it.
    private static readonly object LogGate = new();
    private static Action<string>? _log;

    private static unsafe void SetLogSink(bool on)
        => rsl_set_log_sink(on ? &OnEngineLine : null);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnEngineLine(byte* line)
    {
        // An exception must not unwind into native code: that terminates the process.
        try { _log?.Invoke(Marshal.PtrToStringUTF8((nint)line) ?? ""); }
        catch { }
    }

    [LibraryImport(Dll)] private static partial int rsl_abi_version();
    [LibraryImport(Dll)] private static partial void rsl_free(nint p);
    [LibraryImport(Dll)] private static unsafe partial void rsl_set_log_sink(delegate* unmanaged[Cdecl]<byte*, void> sink);
    [LibraryImport(Dll)] private static partial void rsl_set_write_log_files(int enabled);
    [LibraryImport(Dll)] private static partial int rsl_paths(out nint result);
    [LibraryImport(Dll)] private static partial int rsl_game_build(out nint result);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int rsl_discover_account(string? cachePath, out nint result);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int rsl_calibrate(string? cachePath, string? exportCatalogPath, out nint result);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int rsl_extract_consolidated(string? cachePath, int includeArtifacts, out nint result);
    [LibraryImport(Dll)] private static partial int rsl_hero_base_stats_generated_at(out nint result);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int rsl_hero_base_stats_inspect(string json, out nint result);
}
#endif
