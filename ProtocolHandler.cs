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
///
/// <para>Since 1.32 the URI may also name the API that minted the code
/// (<c>&amp;api=https%3A%2F%2Fapi-dev.rslcompanion.com</c>), because dev and prod are separate
/// databases and separate Firebase projects. Any web page can open this scheme, so the value is
/// allow-listed (<see cref="ApiTarget.TryResolve"/>) — otherwise a page could name its own host and
/// receive the user's whole account export.</para>
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
    /// Reads a <c>sync</c> launch: the one-time handoff code, plus the optional <c>api</c> naming the
    /// environment that minted it. Returns null when the app was started normally (no protocol
    /// argument, or one without a code — the site's install check launches a bare
    /// <c>rslcompanion-extractor://ping</c>, which must not be mistaken for a sign-in).
    ///
    /// <para><see cref="HandoffLaunch.Api"/> is the raw value, still unvalidated: whether it is
    /// allowed is decided in one place, <see cref="HandoffLaunch.ResolveTarget"/>. A repeated
    /// <c>api</c> is recorded as ambiguous rather than first-wins, so a link cannot put one value in
    /// front of whatever reads the first and another in front of whatever reads the last.</para>
    ///
    /// <para>Unknown parameters are ignored, as every release since 1.8.0 has done — which is what
    /// let the site start sending <c>api</c> before any build read it.</para>
    /// </summary>
    public static HandoffLaunch? TryGetHandoff(string[] args)
    {
        var uriArg = args.FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));
        if (uriArg is null || !Uri.TryCreate(uriArg, UriKind.Absolute, out var uri))
            return null;

        string? code = null;
        string? api = null;
        var apiCount = 0;
        int? account = null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair.AsSpan(0, eq);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);

            if (name.Equals("code", StringComparison.OrdinalIgnoreCase))
                code ??= value; // first wins, as it always has
            else if (name.Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                apiCount++;
                api = value;
            }
            else if (name.Equals("account", StringComparison.OrdinalIgnoreCase)
                     && account is null && int.TryParse(value, System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
                account = id; // first valid wins; anything else is ignored, never an error
        }

        if (string.IsNullOrWhiteSpace(code)) return null;
        return new HandoffLaunch(code, api, ApiAmbiguous: apiCount > 1, AccountId: account);
    }
}

/// <summary>
/// A <c>sync</c> launch as the URI carried it. <see cref="Api"/> is null when the parameter was
/// absent — every site build before 2026-09-30 — which is not the same as present-and-empty.
///
/// <para><see cref="AccountId"/> is the in-game id of the account card whose "Update Data" was
/// clicked (<c>&amp;account=&lt;id&gt;</c>, 1.42+), the same id the app puts on its own
/// "Open RSL Companion" link. <b>It is a label, never a target.</b> The app can only read the
/// account open in Raid, and it syncs that one. When the two differ the user is told which was
/// synced (<c>MainForm.TryRunSiteUpdate</c>). It is not allow-listed or validated beyond being a
/// positive number, because nothing is sent anywhere on its account.</para>
/// </summary>
internal sealed record HandoffLaunch(string Code, string? Api, bool ApiAmbiguous = false, int? AccountId = null)
{
    /// <summary>
    /// The environment this code must be redeemed at, or null when the launch names one this app will
    /// not talk to. An absent <c>api</c> means the built-in target, so older site builds keep working.
    ///
    /// <para><b>A refused value never falls back to the built-in target.</b> The code was minted by
    /// whatever the link names and would only fail at prod, and switching quietly would hide a link
    /// that tried to point the upload somewhere else.</para>
    /// </summary>
    public ApiTarget? ResolveTarget(AppConfig config) =>
        ApiAmbiguous ? null
        : Api is null ? ApiTarget.BuiltIn(config)
        : ApiTarget.TryResolve(Api);

    /// <summary>What to tell the user when <see cref="ResolveTarget"/> refused the launch.</summary>
    public string RefusalMessage =>
        "This sign-in link names a server this app doesn't recognise"
        + (string.IsNullOrWhiteSpace(Api) || ApiAmbiguous ? "" : $" ({Api})")
        + ", so it was not used and nothing was sent. Open the Extractor from rslcompanion.com, "
        + "or use Sign In here.";
}
