using Microsoft.Web.WebView2.Core;

namespace RslCompanionUploader.Forms;

/// <summary>
/// The single <see cref="CoreWebView2Environment"/> every WebView2 in this process shares.
///
/// <para>Only <see cref="AppShell"/> uses it today. It stays a shared factory anyway because WebView2
/// refuses to create a second environment over the same user-data folder with different options — so
/// the moment anything else needs a WebView2, funnelling it through here is what stops that failure
/// happening at runtime, on a machine that isn't this one.</para>
///
/// <para>Note this profile holds no rslcompanion.com session: sign-in goes through the user's real
/// browser, and the shell only ever renders a string. Nothing here is worth clearing on sign-out.</para>
/// </summary>
internal static class WebViewEnvironment
{
    /// <summary>
    /// The profile directory. It holds a live rslcompanion.com session once the user signs in, so it
    /// sits under LocalAppData (never roamed) alongside the rest of this app's per-machine state.
    /// </summary>
    public static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RslCompanionUploader", "WebView2");

    private static Task<CoreWebView2Environment>? _pending;

    /// <summary>
    /// Returns the shared environment, creating it on first use. Every caller is on the UI thread
    /// (WebView2 requires it), so the lazy assignment needs no lock; concurrent callers await the
    /// same task rather than racing to build a second environment.
    /// </summary>
    public static Task<CoreWebView2Environment> GetAsync() => _pending ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(UserDataFolder);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
    }
}
