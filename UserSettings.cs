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
