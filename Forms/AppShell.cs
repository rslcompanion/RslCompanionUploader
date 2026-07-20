using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RslCompanionUploader.Forms;

/// <summary>
/// The whole application UI, rendered as one full-window WebView2 page so it matches rslcompanion.com
/// rather than looking like native WinForms chrome. The C# side stays the backend: it owns all data
/// and pushes a single view-state into the page, and receives back only the few actions the page can
/// trigger (export, report a game build, open a link). Everything else — sign out, refresh, check for
/// updates, recalibrate, about — stays on the native menu, which calls into <see cref="MainForm"/>
/// directly and needs no bridge.
///
/// The page is a top bar (brand + connection pill + identity), optional update / uncovered-build
/// banners, the accounts grid with its contextual export button, and a collapsible activity console.
///
/// Initialization is async and degrades gracefully: if the WebView2 runtime is missing, a plain label
/// is shown instead of throwing. State and log lines pushed before the view is ready are buffered and
/// flushed once it loads.
/// </summary>
public sealed class AppShell : Panel
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _fallback = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Gray,
        Text = "Loading…",
    };

    private bool _ready;

    // The full view-state pushed into the page. Plain fields; PushState serializes a snapshot.
    private string? _user;
    private object? _status;                 // { kind, text } or null (public builds have no game status)
    private object? _update;                 // { version, url } or null
    private string? _report;                 // uncovered-build prompt text, or null
    private IReadOnlyList<Tile> _accounts = Array.Empty<Tile>();
    private int? _identified;
    private int? _detectedUserId;
    private string? _detectedName;
    private bool _busy;
    private bool _exportAvailable;

    // Log lines produced before the page is ready, flushed on load.
    private readonly List<string> _pendingLog = new();

    /// <summary>Raised (on the UI thread) when the accounts pane's export button is clicked.</summary>
    public event Action? ExportRequested;

    /// <summary>Raised when the uncovered-build banner is clicked.</summary>
    public event Action? ReportBuildRequested;

    /// <summary>Raised with a URL the page asked to open (e.g. the update-download link).</summary>
    public event Action<string>? OpenUrlRequested;

    public AppShell()
    {
        Controls.Add(_fallback);
        Controls.Add(_web);
    }

    /// <summary>Async-initializes the WebView2 and loads the page. Call once, from the UI thread.</summary>
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
                foreach (var line in _pendingLog) Post(new { type = "log", line });
                _pendingLog.Clear();
                PushState();
            };

            _web.CoreWebView2.NavigateToString(Html);
            _fallback.Visible = false;
            _web.Visible = true;
        }
        catch (Exception ex)
        {
            _fallback.Visible = true;
            _fallback.Text = "This app needs the WebView2 runtime, which appears to be missing.\n\n" + ex.Message;
        }
    }

    public void SetUser(string user) { _user = user; PushState(); }

    /// <summary>Sets the connection pill. Pass null (public builds) to hide it and treat the game as unknown.</summary>
    public void SetStatus(string? kind, string? text)
    {
        _status = kind is null ? null : new { kind, text };
        PushState();
    }

    /// <summary>Shows (or clears, with null) the "update available" banner.</summary>
    public void SetUpdate(string? version, string? url)
    {
        _update = version is null ? null : new { version, url };
        PushState();
    }

    /// <summary>Shows (or clears, with null) the uncovered-build report banner.</summary>
    public void SetReport(string? text) { _report = text; PushState(); }

    /// <summary>Replaces the tile list. Drops the in-game highlight if its account is no longer present.</summary>
    public void SetAccounts(IReadOnlyList<Tile> accounts)
    {
        _accounts = accounts;
        if (_identified is int id && accounts.All(a => a.UserId != id)) _identified = null;
        PushState();
    }

    /// <summary>Marks the tile that matches the currently identified running game account.</summary>
    public void SetIdentified(int? userId) { _identified = userId; PushState(); }

    /// <summary>Shows (or clears, with null userId) a tile for a running game account not imported yet.</summary>
    public void SetDetectedAccount(int? userId, string? name)
    {
        _detectedUserId = userId;
        _detectedName = name;
        PushState();
    }

    /// <summary>Reflects an in-flight export: the export button shows progress and is disabled.</summary>
    public void SetBusy(bool busy) { _busy = busy; PushState(); }

    /// <summary>Whether the export action exists at all (false in public builds without the engine).</summary>
    public void SetExportAvailable(bool available) { _exportAvailable = available; PushState(); }

    /// <summary>Appends a timestamped line to the activity console.</summary>
    public void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (!_ready || _web.CoreWebView2 is null) { _pendingLog.Add(line); return; }
        Post(new { type = "log", line });
    }

    private void PushState()
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        Post(new
        {
            type = "state",
            user = _user,
            status = _status,
            update = _update,
            report = _report,
            accounts = _accounts,
            identifiedUserId = _identified,
            detected = _detectedUserId is int id ? new { userId = id, name = _detectedName ?? $"Account {id}" } : null,
            busy = _busy,
            exportAvailable = _exportAvailable,
        });
    }

    private void Post(object payload) => _web.CoreWebView2!.PostWebMessageAsJson(JsonSerializer.Serialize(payload));

    // WebView2 raises this on the UI thread, so it is safe to raise events straight through.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return;
            switch (t.GetString())
            {
                case "export": ExportRequested?.Invoke(); break;
                case "reportBuild": ReportBuildRequested?.Invoke(); break;
                case "openUrl" when root.TryGetProperty("url", out var u) && u.GetString() is string url:
                    OpenUrlRequested?.Invoke(url);
                    break;
            }
        }
        catch
        {
            // Malformed message — ignore.
        }
    }

    /// <summary>Compact per-account payload sent to the page.</summary>
    public sealed record Tile(
        [property: JsonPropertyName("userId")] int UserId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("clanName")] string? ClanName,
        [property: JsonPropertyName("heroCount")] int HeroCount,
        [property: JsonPropertyName("artifactCount")] int ArtifactCount);

    private const string Html = @"<!doctype html>
<html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>
<style>
  :root { color-scheme: light dark;
          --bg:#fafafa; --fg:#1e1e1e; --sub:#6b6b6b; --mut:#8a8a8a; --card:#fff; --panel:#f2f2f2; --line:#000;
          --accent:#2563eb; --accentbg:rgba(37,99,235,.10);
          --ok:#16a34a; --okbg:rgba(22,163,74,.10);
          --warn:#d97706; --warnbg:rgba(217,119,6,.12);
          --bad:#dc2626; --badbg:rgba(220,38,38,.08); }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#1b1b1b; --fg:#e8e8e8; --sub:#a0a0a0; --mut:#808080; --card:#262626; --panel:#202020; --line:#0a0a0a;
            --accent:#60a5fa; --accentbg:rgba(96,165,250,.14);
            --ok:#4ade80; --okbg:rgba(74,222,128,.12);
            --warn:#fbbf24; --warnbg:rgba(251,191,36,.14);
            --bad:#f87171; --badbg:rgba(248,113,113,.12); } }
  * { box-sizing:border-box; }
  html, body { height:100%; }
  body { margin:0; font-family:'Segoe UI', system-ui, sans-serif; background:var(--bg); color:var(--fg);
         display:flex; flex-direction:column; font-size:13px; }

  #topbar { flex:none; display:flex; align-items:center; gap:12px; padding:10px 16px;
            border-bottom:1px solid var(--line); background:var(--card); }
  #brand { display:flex; align-items:center; gap:8px; font-weight:600; font-size:14px; }
  #logo { width:24px; height:24px; border-radius:6px; background:var(--accent); color:#fff;
          display:flex; align-items:center; justify-content:center; font-size:12px; font-weight:700; }
  #pill { display:none; align-items:center; gap:6px; padding:4px 11px; border-radius:999px;
          font-size:12px; font-weight:600; }
  #pill .dot { width:8px; height:8px; border-radius:50%; background:currentColor; }
  #pill.connected { color:var(--ok); background:var(--okbg); }
  #pill.loading, #pill.needsCalibration { color:var(--warn); background:var(--warnbg); }
  #pill.calibrating { color:var(--accent); background:var(--accentbg); }
  #pill.notRunning { color:var(--mut); background:var(--panel); }
  #who { margin-left:auto; font-size:12px; color:var(--sub); white-space:nowrap; }

  .banner { flex:none; display:none; padding:9px 16px; font-size:12px; font-weight:600; cursor:pointer;
            border-bottom:1px solid var(--line); }
  #updateBanner { color:var(--accent); background:var(--accentbg); }
  #reportBanner { color:var(--warn); background:var(--warnbg); }
  .banner:hover { text-decoration:underline; }

  #scroll { flex:1 1 auto; overflow:auto; padding:16px; }
  #secHdr { display:none; align-items:baseline; justify-content:space-between; margin-bottom:12px; }
  #secHdr .lbl { font-size:12px; font-weight:600; color:var(--sub); text-transform:uppercase; letter-spacing:.04em; }
  #secHdr .cnt { font-size:12px; color:var(--mut); }
  #grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(190px, 1fr)); gap:12px; }

  /* Tiles are status, not controls — not clickable. The export target is chosen automatically. */
  .tile { position:relative; border:1px solid var(--line); border-radius:12px; background:var(--card);
          padding:14px; transition:border-color .12s, background .12s, box-shadow .12s; }
  .tile.identified { border-color:var(--ok); border-width:2px; background:var(--okbg); }
  .tile.selected { box-shadow:0 0 0 2px var(--accent) inset; }
  .tile.selected:not(.identified) { border-color:var(--accent); }
  .tile .head { display:flex; align-items:center; gap:10px; }
  .tile .avatar { width:34px; height:34px; border-radius:9px; background:var(--panel); color:var(--fg);
                  display:flex; align-items:center; justify-content:center; font-weight:600; font-size:13px; flex:none; }
  .tile.identified .avatar { background:var(--card); color:var(--ok); }
  .tile .name { font-size:15px; font-weight:600; word-break:break-word; }
  .tile.identified .name { color:var(--ok); }
  .tile .clan { font-size:12px; color:var(--sub); margin-top:2px; }
  .tile .meta { font-size:12px; color:var(--sub); margin-top:10px; display:flex; gap:16px; }
  .tile.identified .meta { color:var(--ok); }
  .tile .badge { position:absolute; top:12px; right:12px; display:inline-flex; align-items:center; gap:5px;
                 font-size:11px; font-weight:600; color:var(--ok); }
  .tile .badge::before { content:''; width:7px; height:7px; border-radius:50%; background:var(--ok); }

  .tile.detected { border:1px dashed var(--accent); background:var(--accentbg); }
  .tile .badge-new { display:inline-block; margin-bottom:8px; padding:2px 8px; border-radius:999px;
                     background:var(--accent); color:#fff; font-size:10px; font-weight:700;
                     letter-spacing:.02em; text-transform:uppercase; }
  .tile .attach-note { margin-top:8px; font-size:11px; color:var(--accent); font-weight:600; }

  .tile.offline { grid-column:1/-1; border:2px solid var(--bad); background:var(--badbg); color:var(--bad);
                  font-size:13px; font-weight:600; line-height:1.5; }
  .empty { grid-column:1/-1; padding:8px 2px; color:var(--sub); font-size:13px; }

  #actionBar { flex:none; display:none; padding:10px 16px; border-top:1px solid var(--line); background:var(--card); }
  #action { width:100%; padding:12px 14px; border:none; border-radius:10px; color:#fff; cursor:pointer;
            font-family:inherit; font-size:14px; font-weight:600; background:var(--accent); transition:filter .12s; }
  #action.update { background:var(--ok); }
  #action:hover:not(:disabled) { filter:brightness(1.06); }
  #action:disabled { opacity:.6; cursor:default; }

  #console { flex:none; border-top:1px solid var(--line); background:var(--panel); }
  #consoleHdr { display:flex; align-items:center; gap:8px; padding:8px 16px; cursor:pointer;
                font-size:12px; color:var(--sub); }
  #consoleHdr .last { font-family:'Consolas', ui-monospace, monospace; color:var(--fg); overflow:hidden;
                      text-overflow:ellipsis; white-space:nowrap; flex:1; }
  #consoleHdr .chev { color:var(--mut); }
  #consoleBody { display:none; max-height:150px; overflow:auto; padding:6px 16px 12px;
                 font-family:'Consolas', ui-monospace, monospace; font-size:12px; line-height:1.55; }
  #console.open #consoleBody { display:block; }
  #consoleBody .ln { color:var(--sub); white-space:pre-wrap; word-break:break-word; }
</style></head>
<body>
  <div id='topbar'>
    <div id='brand'><span id='logo'>RC</span><span>RSL Companion</span></div>
    <div id='pill'><span class='dot'></span><span class='txt'></span></div>
    <div id='who'></div>
  </div>
  <div id='updateBanner' class='banner'></div>
  <div id='reportBanner' class='banner'></div>

  <div id='scroll'>
    <div id='secHdr'><span class='lbl'>Your accounts</span><span class='cnt'></span></div>
    <div id='grid'></div>
  </div>

  <div id='actionBar'><button id='action' type='button'></button></div>

  <div id='console'>
    <div id='consoleHdr'><span style='opacity:.7'>Activity</span><span class='last'></span><span class='chev'>&#9650;</span></div>
    <div id='consoleBody'></div>
  </div>

<script>
  var state = { user:null, status:null, update:null, report:null, accounts:[], identifiedUserId:null, detected:null, busy:false, exportAvailable:false };
  var logLines = [];
  var $ = function(id){ return document.getElementById(id); };
  function esc(s){ return (s||'').replace(/[&<>]/g, function(c){ return {'&':'&amp;','<':'&lt;','>':'&gt;'}[c]; }); }
  function initials(s){ s=(s||'').trim(); if(!s) return '?'; var p=s.split(/\s+/); return (p.length>1 ? p[0][0]+p[1][0] : s.slice(0,2)).toUpperCase(); }

  function liveSelection() {
    if (state.detected) return { userId: state.detected.userId, kind: 'add' };
    if (state.identifiedUserId != null) return { userId: state.identifiedUserId, kind: 'update' };
    return null;
  }

  function renderTopbar() {
    $('who').textContent = state.user ? ('Signed in as ' + state.user) : '';
    var pill = $('pill');
    if (state.status) {
      pill.style.display = 'inline-flex';
      pill.className = state.status.kind;
      pill.querySelector('.txt').textContent = state.status.text || '';
    } else {
      pill.style.display = 'none';
    }
  }

  function renderBanners() {
    var ub = $('updateBanner');
    if (state.update) { ub.style.display = 'block'; ub.textContent = 'A new version (' + esc(state.update.version) + ') is available — click to download'; }
    else ub.style.display = 'none';
    var rb = $('reportBanner');
    if (state.report) { rb.style.display = 'block'; rb.textContent = esc(state.report); }
    else rb.style.display = 'none';
  }

  function tileMeta(a) {
    return ""<div class='meta'><span>"" + a.heroCount + "" champions</span><span>"" + a.artifactCount + "" gear</span></div>"";
  }

  function renderGrid(sel) {
    var grid = $('grid');
    grid.innerHTML = '';
    var gameClosed = state.status && state.status.kind === 'notRunning';

    if (gameClosed) {
      var off = document.createElement('div');
      off.className = 'tile offline';
      off.textContent = 'Open the Raid game to fetch account details.';
      grid.appendChild(off);
    }

    if (state.detected) {
      var d = document.createElement('div');
      d.className = 'tile detected selected';
      d.innerHTML = ""<div class='badge-new'>New account detected</div>""
        + ""<div class='name'>"" + esc(state.detected.name) + ""</div>""
        + ""<div class='meta'><span>Playing now · not imported yet</span></div>""
        + ""<div class='attach-note'>Can be attached to your signed-in account</div>"";
      grid.appendChild(d);
    }

    var sec = $('secHdr');
    sec.style.display = state.accounts.length ? 'flex' : 'none';
    sec.querySelector('.cnt').textContent = state.accounts.length + (state.accounts.length === 1 ? ' profile' : ' profiles');

    if (!state.accounts.length) {
      if (!gameClosed && !state.detected) {
        var empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = 'No accounts in your profile.';
        grid.appendChild(empty);
      }
      return;
    }

    state.accounts.forEach(function(a) {
      var el = document.createElement('div');
      el.className = 'tile'
        + (a.userId === state.identifiedUserId ? ' identified' : '')
        + (sel && sel.kind === 'update' && a.userId === sel.userId ? ' selected' : '');
      var html = ""<div class='head'><div class='avatar'>"" + esc(initials(a.name)) + ""</div><div><div class='name'>"" + esc(a.name) + ""</div>"";
      html += a.clanName ? (""<div class='clan'>"" + esc(a.clanName) + ""</div>"") : '';
      html += ""</div></div>"" + tileMeta(a);
      if (a.userId === state.identifiedUserId) html += ""<div class='badge'>In game</div>"";
      el.innerHTML = html;
      grid.appendChild(el);
    });
  }

  function renderAction(sel) {
    var bar = $('actionBar'), btn = $('action');
    if (!state.exportAvailable || !sel) { bar.style.display = 'none'; return; }
    bar.style.display = 'block';
    if (state.busy) { btn.disabled = true; btn.className = ''; btn.textContent = 'Exporting…'; return; }
    btn.disabled = false;
    if (sel.kind === 'add') { btn.className = ''; btn.textContent = 'Add current game account to RSL Companion account'; }
    else { btn.className = 'update'; btn.textContent = 'Update account'; }
  }

  function render() {
    var sel = liveSelection();
    renderTopbar();
    renderBanners();
    renderGrid(sel);
    renderAction(sel);
  }

  function renderLog() {
    var last = logLines.length ? logLines[logLines.length - 1] : '';
    $('consoleHdr').querySelector('.last').textContent = last;
    var body = $('consoleBody');
    body.innerHTML = logLines.map(function(l){ return ""<div class='ln'>"" + esc(l) + ""</div>""; }).join('');
    body.scrollTop = body.scrollHeight;
  }

  $('consoleHdr').onclick = function(){ $('console').classList.toggle('open'); };
  $('action').onclick = function(){ if (!$('action').disabled) window.chrome.webview.postMessage({ type:'export' }); };
  $('updateBanner').onclick = function(){ if (state.update && state.update.url) window.chrome.webview.postMessage({ type:'openUrl', url: state.update.url }); };
  $('reportBanner').onclick = function(){ window.chrome.webview.postMessage({ type:'reportBuild' }); };

  window.chrome.webview.addEventListener('message', function(e) {
    var m = e.data;
    if (!m) return;
    if (m.type === 'state') { state = m; render(); }
    else if (m.type === 'log') { logLines.push(m.line); if (logLines.length > 500) logLines.shift(); renderLog(); }
  });
  render();
  renderLog();
</script>
</body></html>";
}
