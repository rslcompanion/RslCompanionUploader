using Microsoft.Win32;

namespace RslCompanionUploader;

/// <summary>
/// Registers the <c>rslcompanion-extractor://</c> URI scheme so rslcompanion.com can launch this
/// app ("RSL Companion Account Data Extractor") from the browser, and parses the launch URI.
///
/// The website hands over the signed-in user's Firebase refresh token in the URI
/// (<c>rslcompanion-extractor://sync?rt=&lt;refresh token&gt;</c>) so the user does not have to
/// sign in again inside the app.
/// </summary>
internal static class ProtocolHandler
{
    public const string Scheme = "rslcompanion-extractor";

    /// <summary>
    /// (Re-)registers the URI scheme under HKCU\Software\Classes — per-user, no admin rights
    /// needed. Called on every startup so the registration self-heals when the exe moves.
    /// </summary>
    public static void RegisterCurrentUser()
    {
        try
        {
            var exe = Application.ExecutablePath;

            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            root.SetValue(null, "URL:RSL Companion Account Data Extractor");
            root.SetValue("URL Protocol", string.Empty);

            using var icon = root.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");

            using var command = root.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        catch
        {
            // Registration is best-effort: the app must still work when launched directly.
        }
    }

    /// <summary>
    /// Extracts the Firebase refresh token from a protocol launch, or null when the app was
    /// started normally (no protocol argument / no token in it).
    /// </summary>
    public static string? TryGetRefreshToken(string[] args)
    {
        var uriArg = args.FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));
        if (uriArg is null || !Uri.TryCreate(uriArg, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair.AsSpan(0, eq).Equals("rt", StringComparison.OrdinalIgnoreCase)) continue;

            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
