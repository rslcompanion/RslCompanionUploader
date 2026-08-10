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
        // instance (so SignInPanel can complete the handoff) instead of opening a new window.
        if (!SingleInstance.TryBecomePrimary(args))
            return;

        var config = AppConfig.Load();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        var auth = new FirebaseAuthClient(http, config.FirebaseApiKey);
        var handoff = new ExtractorHandoff(http, config, auth);
        var sessions = new SessionManager(auth);
        var api = new RslCompanionApiClient(http, config, auth, session: null);

        // Nothing authenticates here any more. Restoring a session is a network call, and with the
        // Windows Hello option it also prompts the user — neither belongs in front of a window that
        // has not been drawn yet. MainForm does it on Load instead, so the window always opens
        // immediately and fills in the signed-in state when it arrives.
        Application.Run(new MainForm(config, handoff, api, sessions, ProtocolHandler.TryGetHandoffCode(args)));
    }
}
