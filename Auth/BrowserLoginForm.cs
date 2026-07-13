using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RslCompanionUploader.Auth;

/// <summary>Which social provider the embedded browser should drive.</summary>
public enum LoginProvider { Google, Microsoft, Discord }

/// <summary>
/// Hosts the live rslcompanion.com login inside an embedded browser so the user can sign in with
/// Google, Microsoft, or Discord exactly as they would on the website. It opens the site's
/// <c>/login</c> modal and auto-clicks the chosen provider button, then reads the resulting Firebase
/// ID token + refresh token straight out of the page's IndexedDB.
/// </summary>
public sealed class BrowserLoginForm : Form
{
    private readonly AppConfig _config;
    private readonly LoginProvider _provider;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 900 };
    private CoreWebView2Environment? _env;
    private bool _finished;

    public HarvestedTokens? Result { get; private set; }

    public BrowserLoginForm(AppConfig config, LoginProvider provider)
    {
        _config = config;
        _provider = provider;
        Text = $"Sign in with {provider}";
        Width = 520;
        Height = 720;
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_web);
        Load += OnLoad;
        _poll.Tick += async (_, _) => await PollForTokensAsync();
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RslCompanionUploader", "WebView2");
            Directory.CreateDirectory(userData);

            _env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(_env);

            // Re-inject the harvester on every document so it survives OAuth redirects.
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HarvestScript);
            // Auto-open the provider on the site's /login modal.
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildAutoClickScript(_provider));

            // Let OAuth popups (Google/Microsoft) open in a child window sharing this session.
            _web.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            // /login makes the landing page pop the auth modal with the social buttons.
            _web.CoreWebView2.Navigate($"{_config.FrontendUrl}/login");
            _poll.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Could not start the embedded browser. The WebView2 runtime may be missing.\n\n" + ex.Message,
                "Browser sign-in", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var popup = new Form { Width = 480, Height = 640, StartPosition = FormStartPosition.CenterParent, Text = "Sign in" };
        var popupWeb = new WebView2 { Dock = DockStyle.Fill };
        popup.Controls.Add(popupWeb);

        popup.Shown += async (_, _) =>
        {
            try
            {
                await popupWeb.EnsureCoreWebView2Async(_env);
                e.NewWindow = popupWeb.CoreWebView2;
                popupWeb.CoreWebView2.WindowCloseRequested += (_, _) => popup.Close();
            }
            finally
            {
                deferral.Complete();
            }
        };
        popup.FormClosed += (_, _) => popupWeb.Dispose();
        popup.Show(this);
    }

    private async Task PollForTokensAsync()
    {
        if (_finished || _web.CoreWebView2 is null) return;

        string raw;
        try
        {
            raw = await _web.CoreWebView2.ExecuteScriptAsync("window.__rslAuthResult");
        }
        catch
        {
            return;
        }

        // ExecuteScriptAsync returns a JSON-encoded value: "null" or a JSON-encoded string.
        if (string.IsNullOrEmpty(raw) || raw == "null") return;

        try
        {
            // First decode the outer JSON string, then parse the inner JSON object.
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner)) return;

            using var doc = JsonDocument.Parse(inner);
            var root = doc.RootElement;
            var idToken = root.GetProperty("idToken").GetString();
            var refresh = root.GetProperty("refreshToken").GetString();
            if (string.IsNullOrEmpty(idToken) || string.IsNullOrEmpty(refresh)) return;

            var expMs = root.TryGetProperty("expirationTime", out var ex) && ex.ValueKind == JsonValueKind.Number
                ? ex.GetInt64()
                : DateTimeOffset.UtcNow.AddMinutes(55).ToUnixTimeMilliseconds();

            _finished = true;
            _poll.Stop();

            Result = new HarvestedTokens
            {
                IdToken = idToken,
                RefreshToken = refresh,
                ExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(expMs).UtcDateTime,
                Uid = root.TryGetProperty("uid", out var u) ? u.GetString() : null,
                Email = root.TryGetProperty("email", out var em) ? em.GetString() : null,
                DisplayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
            };
            DialogResult = DialogResult.OK;
            Close();
        }
        catch
        {
            // partial / malformed state — keep polling
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _poll.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Builds a script that waits for the site's auth modal and clicks the requested provider's
    /// button. Google/Microsoft are the 1st/2nd <c>.social-btn</c> in <c>.providers</c>; Discord is
    /// <c>.discord-btn</c>. If the buttons never appear (site markup changed), it simply gives up and
    /// the user can click manually.
    /// </summary>
    private static string BuildAutoClickScript(LoginProvider provider)
    {
        // JS expression that resolves to the target button element (or null).
        var selector = provider switch
        {
            LoginProvider.Discord =>
                "document.querySelector('.discord-btn') || " +
                "Array.from(document.querySelectorAll('button')).find(b => /discord/i.test(b.textContent||''))",
            LoginProvider.Microsoft =>
                "(document.querySelectorAll('.providers .social-btn')[1]) || " +
                "Array.from(document.querySelectorAll('button')).find(b => /microsoft/i.test(b.textContent||''))",
            _ => // Google
                "(document.querySelectorAll('.providers .social-btn')[0]) || " +
                "Array.from(document.querySelectorAll('button')).find(b => /google/i.test(b.textContent||''))",
        };

        return @"
(function () {
  if (window.__rslAutoClick) return;
  var tries = 0;
  var iv = setInterval(function () {
    tries++;
    try {
      var btn = " + selector + @";
      if (btn) { window.__rslAutoClick = true; clearInterval(iv); btn.click(); }
    } catch (e) { }
    if (tries > 40) clearInterval(iv); // ~20s then give up, user clicks manually
  }, 500);
})();
";
    }

    /// <summary>
    /// Injected into every page. Repeatedly scans Firebase's IndexedDB store and, once a signed-in
    /// user with tokens exists, stashes a compact JSON blob on <c>window.__rslAuthResult</c>.
    /// </summary>
    private const string HarvestScript = @"
(function () {
  if (window.__rslPollStarted) return;
  window.__rslPollStarted = true;
  window.__rslAuthResult = null;
  function check() {
    try {
      var req = indexedDB.open('firebaseLocalStorageDb');
      req.onsuccess = function () {
        var db = req.result;
        if (!db.objectStoreNames.contains('firebaseLocalStorage')) return;
        var tx = db.transaction('firebaseLocalStorage', 'readonly');
        var all = tx.objectStore('firebaseLocalStorage').getAll();
        all.onsuccess = function () {
          var rows = all.result || [];
          for (var i = 0; i < rows.length; i++) {
            var r = rows[i];
            var key = (r && r.fbase_key) ? r.fbase_key : '';
            if (key.indexOf('firebase:authUser:') === 0 && r.value && r.value.stsTokenManager) {
              var v = r.value, t = v.stsTokenManager;
              if (t.accessToken && t.refreshToken) {
                window.__rslAuthResult = JSON.stringify({
                  idToken: t.accessToken,
                  refreshToken: t.refreshToken,
                  expirationTime: t.expirationTime,
                  uid: v.uid, email: v.email, displayName: v.displayName
                });
              }
            }
          }
        };
      };
    } catch (e) { }
  }
  setInterval(check, 1000);
  check();
})();
";
}
