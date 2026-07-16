using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Right-hand accounts view: renders the signed-in user's uploader-created accounts as clickable
/// tiles inside a WebView2. The C# side owns the data — it pushes the full view state (account list,
/// which tile is selected, which one matches the running game account) into the page and receives
/// tile clicks back. Clicking a tile raises <see cref="AccountSelected"/>; when the running game
/// account is identified, its tile shows a distinct "in game" style.
///
/// Initialization is async and degrades gracefully: if the WebView2 runtime is missing, the panel
/// shows a message instead of throwing, and the rest of the app keeps working. State pushed before
/// the view is ready is buffered and flushed once it loads.
/// </summary>
public sealed class AccountsPanel : Panel
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _fallback = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Gray,
        Text = "Loading accounts…",
    };

    private bool _ready;
    private IReadOnlyList<Tile> _accounts = Array.Empty<Tile>();
    private int? _selected;
    private int? _identified;

    /// <summary>Raised (on the UI thread) with the account's UserId when a tile is clicked.</summary>
    public event Action<int>? AccountSelected;

    public AccountsPanel()
    {
        Controls.Add(_fallback);
        Controls.Add(_web);
    }

    /// <summary>Async-initializes the WebView2 and loads the tiles template. Call once, from the UI thread.</summary>
    public async void Start()
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RslCompanionUploader", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                _ready = true;
                PushState(); // flush any state set before the view was ready
            };

            _web.CoreWebView2.NavigateToString(Html);
            _fallback.Visible = false;
            _web.Visible = true;
        }
        catch (Exception ex)
        {
            _fallback.Visible = true;
            _fallback.Text = "Accounts view unavailable — the WebView2 runtime may be missing.\n\n" + ex.Message;
        }
    }

    /// <summary>Replaces the tile list. Drops the current selection if it is no longer present.</summary>
    public void SetAccounts(IReadOnlyList<Tile> accounts)
    {
        _accounts = accounts;
        if (_selected is int sel && accounts.All(a => a.UserId != sel)) _selected = null;
        if (_identified is int id && accounts.All(a => a.UserId != id)) _identified = null;
        PushState();
    }

    /// <summary>Highlights the tile the user has picked (drives the upload buttons' target).</summary>
    public void SetSelected(int? userId) { _selected = userId; PushState(); }

    /// <summary>Marks the tile that matches the currently identified running game account.</summary>
    public void SetIdentified(int? userId) { _identified = userId; PushState(); }

    private void PushState()
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        var payload = new { type = "state", accounts = _accounts, selectedUserId = _selected, identifiedUserId = _identified };
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    // WebView2 raises this on the UI thread, so it is safe to touch the form / raise events directly.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() == "select" &&
                root.TryGetProperty("userId", out var u) && u.TryGetInt32(out var userId))
            {
                _selected = userId;
                AccountSelected?.Invoke(userId);
                PushState();
            }
        }
        catch
        {
            // Malformed message — ignore.
        }
    }

    /// <summary>Compact per-account payload sent to the tiles view.</summary>
    public sealed record Tile(
        [property: JsonPropertyName("userId")] int UserId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("clanName")] string? ClanName,
        [property: JsonPropertyName("heroCount")] int HeroCount,
        [property: JsonPropertyName("artifactCount")] int ArtifactCount);

    private const string Html = @"<!doctype html>
<html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>
<style>
  :root { color-scheme: light dark; --bg:#fafafa; --fg:#222; --sub:#6b6b6b; --card:#fff; --line:#e6e6e6;
          --accent:#3b82f6; --ok:#16a34a; --okbg:rgba(22,163,74,.10); }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#1e1e1e; --fg:#e8e8e8; --sub:#9a9a9a; --card:#262626; --line:#343434;
            --accent:#60a5fa; --ok:#4ade80; --okbg:rgba(74,222,128,.12); } }
  * { box-sizing: border-box; }
  body { margin:0; font-family:'Segoe UI', system-ui, sans-serif; background:var(--bg); color:var(--fg); }
  #hdr { padding:16px 16px 8px; font-size:13px; font-weight:600; color:var(--sub); text-transform:uppercase; letter-spacing:.04em; }
  #grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(180px, 1fr)); gap:12px; padding:8px 16px 20px; }
  .tile { position:relative; border:1px solid var(--line); border-radius:12px; background:var(--card);
          padding:14px 14px 12px; cursor:pointer; transition:border-color .12s, box-shadow .12s, transform .06s; }
  .tile:hover { border-color:var(--accent); }
  .tile:active { transform:scale(.99); }
  .tile.selected { border-color:var(--accent); box-shadow:0 0 0 2px var(--accent) inset; }
  .tile.identified { border-color:var(--ok); background:var(--okbg); }
  .tile .name { font-size:15px; font-weight:600; word-break:break-word; }
  .tile .clan { font-size:12px; color:var(--sub); margin-top:2px; }
  .tile .meta { font-size:12px; color:var(--sub); margin-top:8px; }
  .tile .badge { display:inline-flex; align-items:center; gap:5px; margin-top:10px; font-size:11px;
                 font-weight:600; color:var(--ok); }
  .tile .badge::before { content:''; width:7px; height:7px; border-radius:50%; background:var(--ok); }
  .empty { padding:24px 16px; color:var(--sub); font-size:13px; line-height:1.6; }
</style></head>
<body>
  <div id='hdr'>Accounts</div>
  <div id='grid'></div>
<script>
  var state = { accounts: [], selectedUserId: null, identifiedUserId: null };
  var grid = document.getElementById('grid');
  function esc(s){ return (s||'').replace(/[&<>]/g, function(c){ return {'&':'&amp;','<':'&lt;','>':'&gt;'}[c]; }); }
  function render() {
    grid.innerHTML = '';
    if (!state.accounts.length) {
      grid.innerHTML = ""<div class='empty'>No accounts yet.<br>Open Raid and click <b>Export account</b> to create one.</div>"";
      return;
    }
    state.accounts.forEach(function(a) {
      var el = document.createElement('div');
      el.className = 'tile'
        + (a.userId === state.selectedUserId ? ' selected' : '')
        + (a.userId === state.identifiedUserId ? ' identified' : '');
      var html = ""<div class='name'>"" + esc(a.name) + ""</div>"";
      if (a.clanName) html += ""<div class='clan'>"" + esc(a.clanName) + ""</div>"";
      html += ""<div class='meta'>"" + a.heroCount + "" champions · "" + a.artifactCount + "" gear</div>"";
      if (a.userId === state.identifiedUserId) html += ""<div class='badge'>In game</div>"";
      el.innerHTML = html;
      el.onclick = function(){ window.chrome.webview.postMessage({ type:'select', userId:a.userId }); };
      grid.appendChild(el);
    });
  }
  window.chrome.webview.addEventListener('message', function(e) {
    if (e.data && e.data.type === 'state') { state = e.data; render(); }
  });
  render();
</script>
</body></html>";
}
