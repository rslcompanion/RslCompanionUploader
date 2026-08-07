using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RslCompanionUploader.Forms;

/// <summary>
/// The whole application UI, rendered as one full-window WebView2 page so it matches rslcompanion.com
/// rather than looking like native WinForms chrome. The C# side stays the backend: it owns all data
/// and pushes a single view-state into the page, and receives back only the actions the page can
/// trigger — <c>export</c>, <c>signIn</c>, <c>signOut</c>, <c>refresh</c>, <c>reportBuild</c>,
/// <c>openUrl</c>. Check for updates, recalibrate, and about stay on the native Help menu, which
/// calls into <see cref="MainForm"/> directly and needs no bridge.
///
/// The page is a top bar (brand + connection pill + identity, whose account dropdown holds refresh
/// and sign out), optional update / uncovered-build banners, the accounts grid, and a collapsible
/// activity console, with an "Open RSL Helper" bar above it.
///
/// <para><b>Tiles are status, with one exception: the account the running game is on.</b> That tile
/// — and only that one — carries the game-reading action ("Update user data", or "Add this game
/// account" when it isn't imported yet). It reads the live process, so it can never target any other
/// tile; putting it on the tile it acts on is what makes the target obvious. Every other tile stays
/// unselectable status. When no game is reachable no tile carries buttons at all.</para>
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
    private bool _signedIn;
    private string? _user;
    private string? _email;
    private object? _status;                 // { kind, text } or null (public builds have no game status)
    private object? _update;                 // { version, url } or null
    private string? _report;                 // uncovered-build prompt text, or null
    private IReadOnlyList<Tile> _accounts = Array.Empty<Tile>();
    private int? _identified;
    private int? _detectedUserId;
    private string? _detectedName;
    private bool _busy;
    private string? _busyKind;               // "export" | null — drives which button shows progress
    private bool _exportAvailable;
    private string? _frontendUrl;            // target of the "Open RSL Helper" button

    // Log lines produced before the page is ready, flushed on load.
    private readonly List<string> _pendingLog = new();

    /// <summary>Raised (on the UI thread) when the live tile's "Update user data" button is clicked.</summary>
    public event Action? ExportRequested;

    /// <summary>Raised when the uncovered-build banner is clicked.</summary>
    public event Action? ReportBuildRequested;

    /// <summary>Raised with a URL the page asked to open (e.g. the update-download link).</summary>
    public event Action<string>? OpenUrlRequested;

    /// <summary>Raised when the top-bar (or signed-out CTA) "Sign In" button is clicked.</summary>
    public event Action? SignInRequested;

    /// <summary>Raised when "Sign out" is chosen from the top-bar account menu.</summary>
    public event Action? SignOutRequested;

    /// <summary>Raised when "Refresh accounts" is chosen from the top-bar account menu.</summary>
    public event Action? RefreshRequested;

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

    /// <summary>Marks the UI signed in and sets the identity shown in the top-bar account menu.</summary>
    public void SetUser(string? name, string? email)
    {
        _signedIn = true;
        _user = name;
        _email = email;
        PushState();
    }

    /// <summary>
    /// Resets the UI to the signed-out state: the top bar shows a "Sign In" button, the body shows a
    /// sign-in prompt, and any account/detection state from the previous session is cleared.
    /// </summary>
    public void SetSignedOut()
    {
        _signedIn = false;
        _user = null;
        _email = null;
        _accounts = Array.Empty<Tile>();
        _identified = null;
        _detectedUserId = null;
        _detectedName = null;
        PushState();
    }

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

    /// <summary>
    /// Reflects an in-flight operation: every action button is disabled, and the one named by
    /// <paramref name="kind"/> ("export") shows progress in place, rather than the page only greying
    /// everything out.
    /// </summary>
    public void SetBusy(bool busy, string? kind = null)
    {
        _busy = busy;
        _busyKind = busy ? kind : null;
        PushState();
    }

    /// <summary>Whether the export actions exist at all (false in public builds without the engine).</summary>
    public void SetExportAvailable(bool available) { _exportAvailable = available; PushState(); }

    /// <summary>Target of the "Open RSL Helper" button (<c>AppConfig.FrontendUrl</c>).</summary>
    public void SetFrontendUrl(string? url) { _frontendUrl = url; PushState(); }

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
            signedIn = _signedIn,
            user = _user,
            userEmail = _email,
            status = _status,
            update = _update,
            report = _report,
            accounts = _accounts,
            identifiedUserId = _identified,
            detected = _detectedUserId is int id ? new { userId = id, name = _detectedName ?? $"Account {id}" } : null,
            busy = _busy,
            busyKind = _busyKind,
            exportAvailable = _exportAvailable,
            frontendUrl = _frontendUrl,
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
                case "signIn": SignInRequested?.Invoke(); break;
                case "signOut": SignOutRequested?.Invoke(); break;
                case "refresh": RefreshRequested?.Invoke(); break;
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
        [property: JsonPropertyName("artifactCount")] int ArtifactCount,
        [property: JsonPropertyName("lastSync")] string? LastSync = null);

    private static readonly string Html = HtmlTemplate.Replace("__LOGO_SRC__", BuildLogoDataUri());

    /// <summary>
    /// Renders the exe's own icon into the page's brand mark instead of an "RC" text placeholder, so
    /// the in-page logo actually matches the taskbar/title-bar icon the user already recognises.
    /// </summary>
    private static string BuildLogoDataUri()
    {
        try
        {
            // Do NOT dispose this: AppIcon.Value (and SystemIcons.Application) are shared, long-lived
            // icons reused by every window's title bar. Disposing here left later windows (the sign-in
            // splash) setting Form.Icon to a dead handle → ObjectDisposedException on show.
            var icon = AppIcon.Value ?? SystemIcons.Application;
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return "";
        }
    }

    private const string HtmlTemplate = @"<!doctype html>
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
  /* The shell is a fixed frame: only #scroll scrolls. Locking the outer page prevents a spurious
     vertical scrollbar from fractional-pixel rounding under Windows display scaling (125/150%). */
  html, body { height:100%; overflow:hidden; }
  body { margin:0; font-family:'Segoe UI', system-ui, sans-serif; background:var(--bg); color:var(--fg);
         display:flex; flex-direction:column; font-size:13px; }

  #topbar { position:relative; flex:none; display:flex; align-items:center; gap:12px; padding:10px 16px;
            border-bottom:1px solid var(--line); background:var(--card); }
  #brand { flex:none; display:flex; align-items:center; gap:8px; font-weight:600; font-size:14px; }
  #logo { width:24px; height:24px; border-radius:6px; object-fit:contain; flex:none; }
  /* Shrinkable so the Sign In button always stays on the top row: when width is tight the status
     pill yields space and ellipsises rather than pushing the button off the row. */
  #pill { display:none; align-items:center; gap:6px; padding:4px 11px; border-radius:999px;
          font-size:12px; font-weight:600; flex:0 1 auto; min-width:0; }
  #pill .txt { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  #pill .dot { width:8px; height:8px; border-radius:50%; background:currentColor; flex:none; }
  #pill.connected { color:var(--ok); background:var(--okbg); }
  #pill.loading, #pill.needsCalibration { color:var(--warn); background:var(--warnbg); }
  #pill.calibrating { color:var(--accent); background:var(--accentbg); }
  #pill.notRunning { color:var(--mut); background:var(--panel); }
  #signin { display:none; flex:none; margin-left:auto; padding:6px 18px; border:none; border-radius:8px;
            background:var(--fg); color:var(--bg); font-family:inherit; font-size:13px; font-weight:600;
            cursor:pointer; transition:opacity .12s; }
  #signin:hover { opacity:.85; }

  /* Account avatar button (shown when signed in) + its dropdown menu. */
  #account { display:none; flex:none; margin-left:auto; width:32px; height:32px; border-radius:50%;
             border:none; background:var(--fg); color:var(--bg); font-family:inherit; font-size:12px;
             font-weight:700; cursor:pointer; align-items:center; justify-content:center; padding:0;
             transition:opacity .12s, box-shadow .12s; }
  #account:hover { opacity:.9; }
  #account.open { box-shadow:0 0 0 3px var(--accentbg); }

  #accountMenu { display:none; position:absolute; top:calc(100% + 6px); right:12px; z-index:30; width:264px;
                 background:var(--card); border:1px solid var(--line); border-radius:12px;
                 box-shadow:0 10px 30px rgba(0,0,0,.22); overflow:hidden; }
  #accountMenu.open { display:block; }
  #accountMenu .am-head { display:flex; align-items:center; gap:12px; padding:14px 16px; }
  #accountMenu .am-avatar { width:40px; height:40px; border-radius:50%; background:var(--fg); color:var(--bg);
                            display:flex; align-items:center; justify-content:center; font-weight:700;
                            font-size:15px; flex:none; }
  #accountMenu .am-name { font-size:14px; font-weight:600; color:var(--fg); word-break:break-word; }
  #accountMenu .am-email { font-size:12px; color:var(--sub); word-break:break-word; margin-top:1px; }
  #accountMenu .am-sep { height:1px; background:var(--line); }
  #accountMenu .am-item { display:flex; align-items:center; gap:8px; width:100%; text-align:left;
                          padding:11px 16px; border:none; background:none; font-family:inherit; font-size:13px;
                          color:var(--fg); cursor:pointer; }
  #accountMenu .am-item:hover { background:var(--panel); }

  .banner { flex:none; display:none; padding:9px 16px; font-size:12px; font-weight:600; cursor:pointer;
            border-bottom:1px solid var(--line); }
  #updateBanner { color:var(--accent); background:var(--accentbg); }
  #reportBanner { color:var(--warn); background:var(--warnbg); }
  .banner:hover { text-decoration:underline; }

  #scroll { flex:1 1 auto; min-height:0; overflow:auto; padding:16px; display:flex; flex-direction:column; }
  #secHdr { display:none; align-items:baseline; justify-content:space-between; margin-bottom:12px; }
  #secHdr .lbl { font-size:12px; font-weight:600; color:var(--sub); text-transform:uppercase; letter-spacing:.04em; }
  #secHdr .cnt { font-size:12px; color:var(--mut); }
  #grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(190px, 1fr)); gap:12px; }

  /* Tiles are status and cannot be selected — with one exception: the tile for the account the
     running game is on carries the game-reading action (see .tile .actions below). It reads the
     live process, so no other tile could ever be its target. */
  .tile { position:relative; border:1px solid var(--line); border-radius:12px; background:var(--card);
          padding:14px; transition:border-color .12s, background .12s, box-shadow .12s; }
  .tile.identified { border-color:var(--ok); border-width:2px; background:var(--okbg); }
  .tile.selected:not(.identified) { border-color:var(--accent); box-shadow:0 0 0 2px var(--accent) inset; }
  .tile .head { display:flex; align-items:center; gap:10px; }
  .tile .avatar { width:34px; height:34px; border-radius:9px; background:var(--panel); color:var(--fg);
                  display:flex; align-items:center; justify-content:center; font-weight:600; font-size:13px; flex:none; }
  .tile.identified .avatar { background:var(--card); color:var(--ok); }
  .tile .name { font-size:15px; font-weight:600; word-break:break-word; }
  .tile.identified .name { color:var(--ok); }
  .tile .clan { font-size:12px; color:var(--sub); margin-top:2px; }
  .tile .meta { font-size:12px; color:var(--sub); margin-top:10px; display:flex; gap:16px; }
  .tile.identified .meta { color:var(--ok); }
  .tile .synced { font-size:11px; color:var(--mut); margin-top:8px; }
  .tile.identified .synced { color:var(--ok); }
  .tile .badge { position:absolute; top:12px; right:12px; display:inline-flex; align-items:center; gap:5px;
                 font-size:11px; font-weight:600; color:var(--ok); }
  .tile .badge::before { content:''; width:7px; height:7px; border-radius:50%; background:var(--ok); }

  /* Action row on the live tile only. */
  .tile .actions { display:flex; flex-wrap:wrap; gap:8px; margin-top:12px; }
  .tile .actions .btn { flex:1 1 120px; padding:9px 10px; border-radius:8px; border:1px solid transparent;
                        font-family:inherit; font-size:12px; font-weight:600; cursor:pointer;
                        transition:filter .12s, background .12s; }
  .tile .actions .btn.add { background:var(--accent); color:#fff; }
  .tile .actions .btn.update { background:var(--ok); color:#fff; }
  .tile .actions .btn:hover:not(:disabled) { filter:brightness(1.07); }
  .tile .actions .btn:disabled { opacity:.55; cursor:default; filter:none; }

  .tile.detected { border:1px dashed var(--accent); background:var(--accentbg); }
  .tile .badge-new { display:inline-block; margin-bottom:8px; padding:2px 8px; border-radius:999px;
                     background:var(--accent); color:#fff; font-size:10px; font-weight:700;
                     letter-spacing:.02em; text-transform:uppercase; }
  .tile .attach-note { margin-top:8px; font-size:11px; color:var(--accent); font-weight:600; }

  .empty { grid-column:1/-1; padding:8px 2px; color:var(--sub); font-size:13px; }

  /* Signed-out call-to-action, shown in the body instead of the accounts grid. Centered via
     margin:auto (not height:100%, which overflowed the padded #scroll and forced a scrollbar). */
  #signedOut { display:none; flex-direction:column; align-items:center; justify-content:center;
               text-align:center; gap:8px; margin:auto; color:var(--sub); }
  #signedOut .cta-logo { width:56px; height:56px; border-radius:14px; object-fit:contain; opacity:.9; margin-bottom:6px; }
  #signedOut .cta-title { font-size:17px; font-weight:600; color:var(--fg); }
  #signedOut .cta-sub { font-size:13px; max-width:360px; line-height:1.5; }
  #signedOut .cta-sub strong { color:var(--fg); }

  /* Always present, and deliberately not an export: this is the one action that has nothing to do
     with the running game, so it does not belong on a tile. */
  #actionBar { flex:none; padding:10px 16px; border-top:1px solid var(--line); background:var(--card); }
  #openHelper { width:100%; padding:11px 14px; border:1px solid var(--line); border-radius:10px;
                background:transparent; color:var(--fg); cursor:pointer; font-family:inherit;
                font-size:13px; font-weight:600; transition:background .12s; }
  #openHelper:hover { background:var(--panel); }

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
    <div id='brand'><img id='logo' src='__LOGO_SRC__' alt=''><span>RSL Companion</span></div>
    <div id='pill'><span class='dot'></span><span class='txt'></span></div>
    <button id='signin' type='button'>Sign In</button>
    <button id='account' type='button' aria-label='Account'><span id='accountAvatar'></span></button>
    <div id='accountMenu'>
      <div class='am-head'>
        <span class='am-avatar'></span>
        <div style='min-width:0'>
          <div class='am-name'></div>
          <div class='am-email'></div>
        </div>
      </div>
      <div class='am-sep'></div>
      <button id='refresh' type='button' class='am-item'>Refresh accounts</button>
      <div class='am-sep'></div>
      <button id='signout' type='button' class='am-item'>Sign out</button>
    </div>
  </div>
  <div id='updateBanner' class='banner'></div>
  <div id='reportBanner' class='banner'></div>

  <div id='scroll'>
    <div id='signedOut'>
      <img class='cta-logo' src='__LOGO_SRC__' alt=''>
      <div class='cta-title'>Sign in to RSL Companion</div>
      <div class='cta-sub'>Use the <strong>Sign In</strong> button in the top-right to view the accounts linked to your profile and sync your Raid data.</div>
    </div>
    <div id='secHdr'><span class='lbl'>Your accounts</span><span class='cnt'></span></div>
    <div id='grid'></div>
  </div>

  <div id='actionBar'><button id='openHelper' type='button'>Open RSL Helper</button></div>

  <div id='console'>
    <div id='consoleHdr'><span style='opacity:.7'>Activity</span><span class='last'></span><span class='chev'>&#9650;</span></div>
    <div id='consoleBody'></div>
  </div>

<script>
  var state = { signedIn:false, user:null, status:null, update:null, report:null, accounts:[], identifiedUserId:null, detected:null, busy:false, busyKind:null, exportAvailable:false, frontendUrl:null };
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
    var signin = $('signin'), account = $('account');
    if (state.signedIn) {
      signin.style.display = 'none';
      account.style.display = 'inline-flex';
      var name = state.user || state.userEmail || 'Signed in';
      var email = (state.user && state.userEmail) ? state.userEmail : (state.user ? '' : '');
      $('accountAvatar').textContent = initials(state.user || state.userEmail);
      document.querySelector('#accountMenu .am-avatar').textContent = initials(state.user || state.userEmail);
      document.querySelector('#accountMenu .am-name').textContent = name;
      document.querySelector('#accountMenu .am-email').textContent = email;
    } else {
      signin.style.display = 'inline-block';
      account.style.display = 'none';
      closeAccountMenu();
    }
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

  // 'artifacts', not 'gear': the game counts Gear (slots 1-6) and Accessories (slots 7-9)
  // separately, and artifactCount spans both. See docs/export-schema.md.
  function tileMeta(a) {
    return ""<div class='meta'><span>"" + a.heroCount + "" champions</span><span>"" + a.artifactCount + "" artifacts</span></div>"";
  }

  // The action row for the tile the running game is on. 'add' = the game is on an account that is
  // not imported yet, so the button imports it; 'update' refreshes an account the server already
  // knows. Same export either way — the server create-or-updates by the in-game id in the payload.
  function tileActions(kind) {
    if (!state.exportAvailable) return '';
    var dataBusy = state.busyKind === 'export';
    var dis = state.busy ? ' disabled' : '';
    return ""<div class='actions'>""
      + ""<button id='btnData' type='button' class='btn "" + (kind === 'add' ? 'add' : 'update') + ""'"" + dis + "">""
      + (dataBusy ? 'Updating…' : (kind === 'add' ? 'Add this game account' : 'Update user data'))
      + ""</button></div>"";
  }

  function renderGrid(sel) {
    var grid = $('grid');
    grid.innerHTML = '';

    if (state.detected) {
      var d = document.createElement('div');
      d.className = 'tile detected selected';
      d.innerHTML = ""<div class='badge-new'>New account detected</div>""
        + ""<div class='name'>"" + esc(state.detected.name) + ""</div>""
        + ""<div class='meta'><span>Playing now · not imported yet</span></div>""
        + ""<div class='attach-note'>Can be attached to your signed-in account</div>""
        + tileActions('add');
      grid.appendChild(d);
    }

    var sec = $('secHdr');
    sec.style.display = state.accounts.length ? 'flex' : 'none';
    sec.querySelector('.cnt').textContent = state.accounts.length + (state.accounts.length === 1 ? ' profile' : ' profiles');

    if (!state.accounts.length) {
      if (!state.detected) {
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
      if (a.lastSync) html += ""<div class='synced'>Last synced "" + esc(a.lastSync) + ""</div>"";
      if (a.userId === state.identifiedUserId) html += ""<div class='badge'>In game</div>"";
      if (sel && sel.kind === 'update' && a.userId === sel.userId) html += tileActions('update');
      el.innerHTML = html;
      grid.appendChild(el);
    });
  }

  function render() {
    var sel = liveSelection();
    renderTopbar();
    renderBanners();
    if (!state.signedIn) {
      // Signed out: show the sign-in prompt instead of the accounts grid. The RSL Helper link stays
      // — it is a website, not an export, and works without a session.
      $('signedOut').style.display = 'flex';
      $('secHdr').style.display = 'none';
      $('grid').innerHTML = '';
      return;
    }
    $('signedOut').style.display = 'none';
    renderGrid(sel);
  }

  function renderLog() {
    var last = logLines.length ? logLines[logLines.length - 1] : '';
    $('consoleHdr').querySelector('.last').textContent = last;
    var body = $('consoleBody');
    body.innerHTML = logLines.map(function(l){ return ""<div class='ln'>"" + esc(l) + ""</div>""; }).join('');
    body.scrollTop = body.scrollHeight;
  }

  $('consoleHdr').onclick = function(){ $('console').classList.toggle('open'); };
  $('signin').onclick = function(){ window.chrome.webview.postMessage({ type:'signIn' }); };
  $('openHelper').onclick = function(){
    if (state.frontendUrl) window.chrome.webview.postMessage({ type:'openUrl', url: state.frontendUrl });
  };
  // Delegated: the tile button is rebuilt by renderGrid on every state push, so binding it
  // directly would leave handlers on discarded nodes.
  $('grid').addEventListener('click', function(e){
    var btn = e.target && e.target.closest ? e.target.closest('button') : null;
    if (!btn || btn.disabled) return;
    if (btn.id === 'btnData') window.chrome.webview.postMessage({ type:'export' });
  });

  function closeAccountMenu(){ $('accountMenu').classList.remove('open'); $('account').classList.remove('open'); }
  $('account').onclick = function(e){
    e.stopPropagation();
    var open = $('accountMenu').classList.toggle('open');
    $('account').classList.toggle('open', open);
  };
  $('refresh').onclick = function(){ closeAccountMenu(); window.chrome.webview.postMessage({ type:'refresh' }); };
  $('signout').onclick = function(){ closeAccountMenu(); window.chrome.webview.postMessage({ type:'signOut' }); };
  // Click anywhere else (or Esc) dismisses the menu.
  document.addEventListener('click', function(e){
    if (!$('accountMenu').contains(e.target) && e.target !== $('account') && !$('account').contains(e.target))
      closeAccountMenu();
  });
  document.addEventListener('keydown', function(e){ if (e.key === 'Escape') closeAccountMenu(); });
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
