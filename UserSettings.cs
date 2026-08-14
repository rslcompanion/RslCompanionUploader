using System.Text.Json;
using System.Text.Json.Serialization;

namespace RslCompanionUploader;

/// <summary>
/// Per-user preferences, kept apart from <see cref="AppConfig"/>: that file is install-time app
/// configuration shipped next to the exe, this is state the app writes back at runtime. It lives
/// under LocalAppData alongside <c>calibrated-offsets.json</c> because the install directory is not
/// guaranteed writable, and writing into it would invalidate the installer's signed file set.
/// </summary>
public sealed class UserSettings
{
    /// <summary>
    /// When true, an uncovered game build is checked against the certification endpoint without
    /// asking. Set by ticking the verification box on the prompt; the prompt is the only writer, so
    /// the check is never silently enabled.
    /// </summary>
    [JsonPropertyName("autoCheckBuildCertification")]
    public bool AutoCheckBuildCertification { get; set; }

    /// <summary>
    /// How the signed-in session is kept between launches — the user's own choice, made on the
    /// sign-in window and changeable afterwards from Help ▸ Session security.
    ///
    /// <para>It defaults to <see cref="Auth.SessionProtection.None"/>, and that default is load-bearing:
    /// the two paths that have no UI to ask — the website's protocol launch, and the silent restore
    /// at startup — read this before writing anything, so a session is never persisted until someone
    /// has actually chosen to persist it.</para>
    /// </summary>
    [JsonPropertyName("sessionProtection")]
    public Auth.SessionProtection SessionProtection { get; set; } = Auth.SessionProtection.None;

    /// <summary>
    /// Whether the user has ever actually answered the stay-signed-in question, as opposed to
    /// <see cref="SessionProtection"/> merely sitting at its default.
    ///
    /// <para>The two are not the same, and the difference is the whole point. Signing in from the
    /// website launches the app with a handoff code and no sign-in screen — so there is no checkbox
    /// to read, and treating the default as an answer would mean silently never remembering anyone
    /// who arrives that way. This flag lets that path ask once, and only once.</para>
    /// </summary>
    [JsonPropertyName("sessionProtectionChosen")]
    public bool SessionProtectionChosen { get; set; }

    /// <summary>
    /// Whether the app looks for a new version on its own — once at startup, then hourly.
    ///
    /// <para>Off by default, and the default is not the answer: see <see cref="AutoUpdateChecksChosen"/>.
    /// Turning it off leaves Help ▸ Check for updates working, so declining costs discovery, not the
    /// ability to update.</para>
    /// </summary>
    [JsonPropertyName("autoUpdateChecks")]
    public bool AutoUpdateChecks { get; set; }

    /// <summary>
    /// Whether the user has ever answered the automatic-update-checks question, as opposed to
    /// <see cref="AutoUpdateChecks"/> sitting at its default — the same distinction, and for the same
    /// reason, as <see cref="SessionProtectionChosen"/>.
    ///
    /// <para>Asked once ever, on the first run that finds it unanswered. Toggling the menu item counts
    /// as an answer too, so someone who turns it on before ever being asked is not then asked.</para>
    /// </summary>
    [JsonPropertyName("autoUpdateChecksChosen")]
    public bool AutoUpdateChecksChosen { get; set; }

    /// <summary>
    /// Whether the activity console shows the engine's own diagnostics alongside the
    /// plain-language lines. Off by default: those lines are addresses, offsets and phase timings
    /// written for debugging a memory map, and they are the bulk of the volume during an export.
    ///
    /// <para>They are never dropped, only hidden — the page keeps every line and filters on render,
    /// so turning this on explains the export that already ran instead of requiring another.</para>
    /// </summary>
    [JsonPropertyName("activityLogDetail")]
    public bool ActivityLogDetail { get; set; }

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RslCompanion", "settings.json");

    private static UserSettings? _current;

    public static UserSettings Current => _current ??= Load();

    private static UserSettings Load()
    {
        try
        {
            return File.Exists(Path_)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path_)) ?? new UserSettings()
                : new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    /// <summary>Persists the current values. Failure is not worth surfacing — it costs one prompt.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Preference lost; the app still works, it just asks again next time.
        }
    }
}
