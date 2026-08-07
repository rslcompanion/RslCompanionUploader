using RslCompanionUploader;
using RslCompanionUploader.Api;
using RslCompanionUploader.Auth;
using RslCompanionUploader.Forms;

namespace RslCompanionUploader;

internal static class Program
{
    // [STAThread] is mandatory for WinForms (and WebView2, which requires the UI thread to be a
    // single-threaded COM apartment). We must NOT await before Application.Run, or the continuation
    // could resume on an MTA thread-pool thread.
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Packaged (MSIX) builds declare the protocol in Package.appxmanifest instead; a runtime
        // registry write would be redundant at best and conflict with the manifest at worst.
        if (!PackagedAppInfo.IsPackaged)
            ProtocolHandler.RegisterCurrentUser();

        // Single-instance: when the app is already running and the browser fires
        // rslcompanion-extractor://sync?code=..., that second launch forwards its args to the running
        // instance (so BrowserSignInForm can complete the handoff) instead of opening a new window.
        if (!SingleInstance.TryBecomePrimary(args))
            return;

        var config = AppConfig.Load();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        var auth = new FirebaseAuthClient(http, config.FirebaseApiKey);
        var handoff = new ExtractorHandoff(http, config, auth);

        // Try to restore a session up front, but never block startup on it: the main window always
        // opens. If there is no session, it opens signed-out and the user signs in from its top bar
        // (see MainForm.SignIn), mirroring how Postman opens before you log in.
        AuthSession? session = TrySignInFromLaunchUri(handoff, args) ?? TrySilentSignIn(auth);

        var api = new RslCompanionApiClient(http, config, auth, session);
        Application.Run(new MainForm(config, handoff, api));
    }

    // When launched from rslcompanion.com via rslcompanion-extractor://, the site passes a one-time
    // handoff code; redeeming it signs the user in without logging in again. Silent by design: the
    // code lives ~60s, so a launch that sat in a queue behind a slow start legitimately arrives dead,
    // and the window simply opens signed-out with the Sign In button.
    private static AuthSession? TrySignInFromLaunchUri(ExtractorHandoff handoff, string[] args)
    {
        var code = ProtocolHandler.TryGetHandoffCode(args);
        if (string.IsNullOrEmpty(code))
            return null;

        try
        {
            var session = handoff.SignInAsync(code).GetAwaiter().GetResult();
            Persist(session);
            return session;
        }
        catch
        {
            return null; // code used/expired, or the API is unreachable — fall back to normal sign-in
        }
    }

    // Attempts to refresh a remembered session so the user skips the login screen entirely.
    // Runs synchronously on the STA thread before any message loop exists (no deadlock risk).
    private static AuthSession? TrySilentSignIn(FirebaseAuthClient auth)
    {
        var saved = CredentialStore.Load();
        if (!saved.RememberSession || string.IsNullOrEmpty(saved.RefreshToken))
            return null;

        try
        {
            var seed = new AuthSession
            {
                IdToken = string.Empty,
                RefreshToken = saved.RefreshToken!,
                ExpiresAtUtc = DateTime.MinValue,
                Uid = saved.Uid,
                Email = saved.Email,
                DisplayName = saved.DisplayName,
            };
            var refreshed = auth.RefreshAsync(seed).GetAwaiter().GetResult();
            Persist(refreshed);
            return refreshed;
        }
        catch
        {
            CredentialStore.ClearSession(); // token revoked/expired — force a fresh login
            return null;
        }
    }

    // Internal so MainForm can persist the session obtained from the in-app sign-in flow.
    // rememberMe defaults to true for the launch-uri and silent-refresh paths, which have no UI to
    // ask the question; the in-app browser sign-in passes the user's actual checkbox choice.
    internal static void Persist(AuthSession s, bool rememberMe = true) => CredentialStore.Save(new SavedCredentials
    {
        Email = s.Email,
        Uid = s.Uid,
        DisplayName = s.DisplayName,
        RefreshToken = rememberMe ? s.RefreshToken : null,
        RememberSession = rememberMe,
    });
}
