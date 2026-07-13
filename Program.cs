using RslCompanionUploader;
using RslCompanionUploader.Api;
using RslCompanionUploader.Auth;
using RslCompanionUploader.Forms;

namespace RslCompanionUploader;

internal static class Program
{
    // [STAThread] is mandatory: WebView2 (used for social sign-in) requires the UI thread to be a
    // single-threaded COM apartment. We must NOT await before Application.Run, or the continuation
    // would resume on an MTA thread-pool thread and WebView2 would fail with RPC_E_CHANGED_MODE.
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        ProtocolHandler.RegisterCurrentUser();

        var config = AppConfig.Load();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        var auth = new FirebaseAuthClient(http, config.FirebaseApiKey);

        AuthSession? session = TrySignInFromLaunchUri(auth, args) ?? TrySilentSignIn(auth);

        if (session is null)
        {
            using var login = new LoginForm(config, auth);
            if (login.ShowDialog() != DialogResult.OK || login.Session is null)
                return;

            session = login.Session;
            if (login.RememberMe)
                Persist(session);
            else
                CredentialStore.ClearSession();
        }

        var api = new RslCompanionApiClient(http, config, auth, session);
        Application.Run(new MainForm(config, api));
    }

    // When launched from rslcompanion.com via rslcompanion-extractor://, the site passes the
    // browser session's Firebase refresh token so the user is signed in without logging in again.
    private static AuthSession? TrySignInFromLaunchUri(FirebaseAuthClient auth, string[] args)
    {
        var refreshToken = ProtocolHandler.TryGetRefreshToken(args);
        if (string.IsNullOrEmpty(refreshToken))
            return null;

        try
        {
            var session = auth.SignInWithRefreshTokenAsync(refreshToken).GetAwaiter().GetResult();
            Persist(session);
            return session;
        }
        catch
        {
            return null; // token expired/revoked — fall back to the normal sign-in flow
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

    private static void Persist(AuthSession s) => CredentialStore.Save(new SavedCredentials
    {
        Email = s.Email,
        Uid = s.Uid,
        DisplayName = s.DisplayName,
        RefreshToken = s.RefreshToken,
        RememberSession = true,
    });
}
