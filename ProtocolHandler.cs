using Microsoft.Win32;

namespace RslCompanionUploader;

/// <summary>
/// Registers the <c>rslcompanion-extractor://</c> URI scheme so rslcompanion.com can launch this
/// app ("RSL Companion Account Data Extractor") from the browser, and parses the launch URI.
///
/// The website hands over a <b>one-time handoff code</b>
/// (<c>rslcompanion-extractor://sync?code=&lt;handoff code&gt;</c>) which this app redeems for a
/// Firebase custom token, so the user does not have to sign in again inside the app.
///
/// <para>It used to be the Firebase <i>refresh token</i> (<c>?rt=</c>). Windows delivers a protocol
/// URI to its handler as <b>process arguments</b>, which any local process can read and which EDR
/// agents, Sysmon and crash reporters routinely log — so what travels here must be worth as little
/// as possible. A refresh token mints ID tokens indefinitely; the code buys a single sign-in for
/// about a minute. <c>rt</c> is no longer sent by the site and is deliberately not read here:
/// accepting it would keep the old credential path alive on the one surface it was removed from.</para>
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
    /// Extracts the one-time handoff code from a protocol launch, or null when the app was started
    /// normally (no protocol argument, or one without a code — the site's install check launches a
    /// bare <c>rslcompanion-extractor://ping</c>, which must not be mistaken for a sign-in).
    /// </summary>
    public static string? TryGetHandoffCode(string[] args)
    {
        var uriArg = args.FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));
        if (uriArg is null || !Uri.TryCreate(uriArg, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair.AsSpan(0, eq).Equals("code", StringComparison.OrdinalIgnoreCase)) continue;

            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
