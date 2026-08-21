using System.Diagnostics;
using System.Text;
#if EXTRACTION
using System.Text.Json;
using NewParserOpus;
using NewParserOpus.Il2Cpp;
using NewParserOpus.Models;
using NewParserOpus.StaticData;
#endif
using RslCompanionUploader.Api;
using RslCompanionUploader.Auth;
using RslCompanionUploader;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Main window: a thin native shell (title bar + Help menu) hosting the whole UI as one
/// full-window WebView2 page (<see cref="AppShell"/>). "Export account" reads the running game and
/// create-or-updates the matching account, highlighting its tile.
///
/// This class stays the backend: it runs the status poll, extraction and API calls, and pushes the
/// resulting view-state into the shell. Refresh and sign out live in the shell's own account menu;
/// check for updates, recalibrate, and about stay on the native Help menu — both call straight into
/// these methods.
///
/// File-based imports (resources/champions JSON) deliberately do not live here — they belong to the
/// rslcompanion.com metadata tooling. This app's job is the live game export.
/// </summary>
public sealed class MainForm : Form
{
    // Registered accounts are shown only when their last sync came from this desktop app's
    // consolidated export (the extractor) — see SyncMethod.ConsolidatedJson in the RaidTools API.
    private const string UploaderSyncMethod = "ConsolidatedJson";

    private readonly AppConfig _config;
    private readonly ExtractorHandoff _handoff;
    private readonly RslCompanionApiClient _api;
    private readonly SessionManager _sessions;

    /// <summary>
    /// A one-time handoff code this process was launched with (the website's
    /// <c>rslcompanion-extractor://sync?code=…</c>). Redeemed once, on Load, then discarded — the code
    /// lives about 60 seconds and is single-use, so there is nothing to retry it with.
    /// </summary>
    private string? _launchCode;

    /// <summary>Help ▸ Session security. Enabled only while signed in — it re-saves the live session.</summary>
    private ToolStripMenuItem? _sessionSecurityItem;

    private readonly AppShell _shell = new() { Dock = DockStyle.Fill };

    /// <summary>Hosts whatever occupies the window below the menu: the shell, or sign-in over it.</summary>
    private readonly Panel _content = new() { Dock = DockStyle.Fill };

    /// <summary>The in-window sign-in view while it is up; null the rest of the time.</summary>
    private SignInPanel? _signIn;


    // The accounts currently shown as tiles.
    private List<AccountSummary> _loadedAccounts = new();

    // Guards so live-account detection never runs twice, or while an upload/export owns the process.
    private bool _busy;

#if EXTRACTION
    // The account read out of the running game, kept separately from the imported tiles so the two
    // can be reconciled whenever either side changes (detection finishing, or the tiles reloading).
    private int? _liveUserId;
    private string? _liveName;

    /// <summary>
    /// What the status poll last observed. Reported to the user as a single line, because "is the
    /// game open" and "can we actually read the account" are separate failures that look identical
    /// from the outside — the game can be running for minutes before its account data exists.
    /// </summary>
    private enum GameState
    {
        NotRunning,       // no Raid process
        Loading,          // process is up, account data not readable yet
        Connected,        // account identified
        NeedsCalibration, // process is up, but no memory map fits this game build
        Calibrating       // deriving a memory map for an unrecognised build (~35s, one-off)
    }

    private GameState _gameState = GameState.NotRunning;

    // Set while a calibration is running so the poll doesn't probe underneath it or start a second.
    private bool _calibrating;

    // The running game's build, and whether the shipped catalog covers it. Refreshed on state
    // transitions rather than every poll — it only changes when the game itself changes.
    private ExtractionService.GameBuildInfo? _buildInfo;

    /// <summary>
    /// Whether this is the newest uploader — null until the check succeeds. Gates auto-recalibration
    /// for an uncovered build: if the user is behind, the release they haven't installed may already
    /// cover their game, so calibrating would just repeat work a plain update fixes. Unknown (offline,
    /// GitHub down) is treated as "don't act" — calibrating on a stale premise costs more than waiting.
    /// </summary>
    private bool? _isLatestUploader;

    /// <summary>
    /// Game builds we have already attempted to self-calibrate this session, so a build that cannot
    /// be calibrated (or where the game is only half-loaded) costs one ~35s scan rather than one
    /// every poll. Cleared only by restarting the app or asking for calibration explicitly.
    /// </summary>
    private readonly HashSet<string> _calibrationAttempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Game builds already offered a compatibility check this session. Same bound as above and for
    /// the same reason — the offer is a modal dialog, so re-asking on a 5-second poll would make the
    /// app unusable while a build stays uncovered.
    /// </summary>
    private readonly HashSet<string> _certificationOffered = new(StringComparer.OrdinalIgnoreCase);

    // Stops the status poll when the window closes.
    private readonly CancellationTokenSource _statusCts = new();
#endif

    public MainForm(AppConfig config, ExtractorHandoff handoff, RslCompanionApiClient api,
                    SessionManager sessions, string? launchCode)
    {
        _config = config;
        _handoff = handoff;
        _api = api;
        _sessions = sessions;
        _launchCode = launchCode;

        // The running version is in the title bar as well as Help → About: "which build am I on?"
        // is the first question in almost every support thread. "RSL Companion" itself is left out —
        // the WebView2 page's own top bar already shows that brand right underneath.
        Text = $"Uploader  v{AboutForm.DisplayVersion}";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.Manual; // ApplyStartupBounds centres it against the real work area
        // Open windowed (not maximized) and centered, at a size that shows a full row of account tiles
        // without a scrollbar. The numbers are applied in ApplyStartupBounds, which is where the
        // monitor's DPI is actually known.
        Font = new Font("Segoe UI", 9.5f);

        BuildLayout();

        _shell.RefreshRequested += async () => { if (!_busy) await LoadAccountsAsync(); };
        _shell.OpenUrlRequested += OpenUrl;
        // Backs the page's "Open RSL Helper" button, which posts back openUrl with this value. Not a
        // constant: it names the account being played, so it is re-pushed whenever that changes.
        RefreshHelperUrl();
        // Defer to the message loop: SignInRequested is raised from inside a WebView2 message handler,
        // and opening a modal dialog (a nested message loop) directly inside that callback crashes the
        // WebView2 host. BeginInvoke lets the handler return first, then shows the dialog.
        _shell.SignInRequested += () => BeginInvoke(new Action(SignIn));
        _shell.SignOutRequested += () => BeginInvoke(new Action(SignOut));
        _shell.NoticeDismissRequested += () => _shell.SetNotice(null);
        _shell.InstallUpdateRequested += async () => await InstallUpdateAsync();
        // Persisted: someone who wants the engine trace usually wants it for more than one export
        // (they are reporting a problem), and someone who doesn't should never see it again.
        _shell.SetLogDetail(UserSettings.Current.ActivityLogDetail);
        _shell.LogDetailChanged += detail =>
        {
            UserSettings.Current.ActivityLogDetail = detail;
            UserSettings.Current.Save();
            _shell.SetLogDetail(detail);
        };
#if EXTRACTION
        // The export action lives on the shell's live tile; it reads the running game and routes by
        // the in-game id it finds there, regardless of which tile drove the label.
        _shell.ExportRequested += async () => await ExportAccountAsync();
        // Export availability is gated on being signed in — set in EnterSignedInAsync, not here.
        FormClosed += (_, _) => _statusCts.Cancel(); // stop the poll touching a disposed form
#endif
        FormClosed += (_, _) => _refreshCts.Cancel(); // same, for the account refresh
        // Last thing this process does: hand a downloaded-but-unapplied update to the installer, so
        // quitting is a real way to apply one and not just a way to postpone it.
        FormClosed += (_, _) => ApplyStagedUpdateOnExit();

        Load += async (_, _) =>
        {
            _shell.Start();
#if EXTRACTION
            ApplyGameState(GameState.NotRunning, force: true); // render a status before the first poll
            _ = PollGameStatusAsync(_statusCts.Token);
#endif
            // Being out of date is not an account fact, so this does not wait for a session. It used
            // to run from EnterSignedInAsync, which meant someone who never signed in — or who
            // signed out, or whose saved session had gone — was never told a new version existed,
            // and the release they were missing might be the one covering their game build.
            StartUpdatePolling();

            // The game-status poll above runs regardless of sign-in. Everything account-related waits
            // until there is a session — the window opens signed-out and the user signs in from the
            // top bar (see SignIn).
            _shell.SetSignedOut();
            await RestoreSessionAsync();

            // Asked last, on a painted window with the session already settled: this is a question
            // about the app, and one modal at a time. RestoreSessionAsync may itself have asked about
            // staying signed in, and stacking two dialogs on a first run reads as an interrogation.
            AskAutoUpdateIfUnanswered();
        };
    }

    // Startup window size, in 96-DPI units — i.e. the size the page's own CSS pixels are measured in,
    // which is what decides whether a row of account tiles fits. At this height the accounts pane
    // clears a full tile row (header + tile + its action button) with the update banner showing.
    private const int DesignWidth = 1210;
    private const int DesignHeight = 780;
    private const int DesignMinWidth = 820;
    private const int DesignMinHeight = 560;

    /// <summary>
    /// Sizes and centres the window against the monitor it actually opens on.
    ///
    /// <para><b>Setting <c>Width</c>/<c>Height</c> in the constructor was a HiDPI bug, not a style
    /// choice.</b> A <see cref="Form"/> only rescales assigned bounds when <c>AutoScaleDimensions</c>
    /// is set, which it is not here, so 1210×748 was applied as raw device pixels: on a 200% display
    /// the window opened at 605×374 logical px — half its intended size — and the WebView2 page, which
    /// *is* DPI-aware, got a ~605 px CSS viewport. That is where the scrollbar across the accounts
    /// pane came from; the page was never too big, the window was too small.</para>
    ///
    /// <para>Clamped to the work area because the design height can exceed a short screen, and a
    /// window taller than the desktop reintroduces the same clipping from the other direction.</para>
    /// </summary>
    private void ApplyStartupBounds()
    {
        float scale = DeviceDpi / 96f;
        int Scaled(int v) => (int)Math.Round(v * scale);

        var work = Screen.FromHandle(Handle).WorkingArea;
        MinimumSize = new Size(Math.Min(Scaled(DesignMinWidth), work.Width),
                               Math.Min(Scaled(DesignMinHeight), work.Height));

        int w = Math.Min(Scaled(DesignWidth), work.Width);
        int h = Math.Min(Scaled(DesignHeight), work.Height);
        Bounds = new Rectangle(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Here rather than in the constructor: DeviceDpi and the target monitor are only known once
        // the handle exists.
        ApplyStartupBounds();
    }

    /// <summary>
    /// Re-establishes a session without asking, in the two cases where that is possible: this process
    /// was launched from the website with a handoff code, or a previous run saved one.
    ///
    /// <para>Runs on Load rather than before the window exists. Both paths make a network call, and
    /// the saved-session path may raise a Windows Hello prompt — putting either in front of the first
    /// paint would give the user a hang with nothing on screen to explain it.</para>
    /// </summary>
    private async Task RestoreSessionAsync()
    {
        // The launch code wins: it is fresher than anything on disk, expires in about a minute, and
        // is the reason this process was started at all.
        var code = Interlocked.Exchange(ref _launchCode, null);
        if (!string.IsNullOrEmpty(code))
        {
            try
            {
                var session = await _handoff.SignInAsync(code);
                await AdoptSessionAsync(session);
                // Asked after the UI is up, not before: this path had no sign-in screen to put a
                // checkbox on, so the question arrives once the user can see what it applies to.
                await AskProtectionIfUnansweredAsync(session);
                return;
            }
            catch (Exception ex)
            {
                // Codes die after ~60s, so a launch that queued behind a slow start legitimately
                // arrives dead. Say so in the log and fall through to the saved session.
                Log("Couldn't finish signing in from the website — the link may have expired. "
                  + "Use Sign In to try again.");
                Log("Handoff exchange failed: " + ex.Message, detail: true);
            }
        }

        if (!SessionManager.HasSavedSession) return;

        if (UserSettings.Current.SessionProtection == SessionProtection.WindowsHello)
            Log("Unlocking your saved session with Windows Hello…");

        var restored = await _sessions.TryRestoreAsync();
        if (restored is null)
        {
            // Nothing to act on: TryRestoreAsync has already decided whether the saved session was
            // rejected (and cleared it) or merely unreachable (and kept it for the next launch).
            Log("Couldn't reopen your saved session — please sign in again.");
            return;
        }

        await AdoptSessionAsync(restored);
    }

    /// <summary>
    /// Takes over the window's content area with the sign-in view. Invoked from the shell's "Sign In"
    /// button, which is why it is idempotent — a second click while it is already up is a no-op
    /// rather than a second WebView2 on the same page.
    /// </summary>
    private void SignIn()
    {
        if (_signIn is not null) return;

        var panel = new SignInPanel(_config, _handoff) { Dock = DockStyle.Fill };
        panel.Completed += async (session, protection) => await OnSignInCompletedAsync(session, protection);
        panel.Cancelled += CloseSignIn;

        _signIn = panel;
        _content.Controls.Add(panel);
        panel.BringToFront();
    }

    private async Task OnSignInCompletedAsync(AuthSession session, SessionProtection protection)
    {
        CloseSignIn();

        // Sign-in normally happens inside this window now, but the browser fallback is still there —
        // and when it was used, the browser held the foreground. Pull this window back either way.
        BringToForeground();

        await _sessions.PersistAsync(session, protection);
        await AdoptSessionAsync(session);
    }

    /// <summary>Tears the sign-in view down, revealing the shell underneath it again.</summary>
    private void CloseSignIn()
    {
        if (_signIn is not { } panel) return;
        _signIn = null;
        _content.Controls.Remove(panel);
        panel.Dispose();
    }

    /// <summary>Hands a freshly obtained session to the API client and lights up the signed-in UI.</summary>
    private async Task AdoptSessionAsync(AuthSession session)
    {
        _api.SignIn(session);
        await EnterSignedInAsync();
    }

    /// <summary>
    /// Asks the stay-signed-in question on the one path that has no sign-in screen to carry it: a
    /// launch straight from the website, where the app is handed a code and is simply signed in.
    ///
    /// <para>Asked <b>once ever</b>, and only when it has never been answered — the default is "don't
    /// save", so staying silent would quietly mean never remembering anyone who signs in from the
    /// site. Answering it here (or on the sign-in panel, or in Help ▸ Session security) settles it
    /// for good; there is no version of this that nags.</para>
    /// </summary>
    private async Task AskProtectionIfUnansweredAsync(AuthSession session)
    {
        if (UserSettings.Current.SessionProtectionChosen) return;

        var stay = new TaskDialogCommandLinkButton(
            "Keep me signed in",
            "Saved and encrypted for your Windows account, so this app opens ready to use.");
        var dont = new TaskDialogCommandLinkButton(
            "Ask me every time",
            "Nothing is saved. You'll sign in through your browser again next launch.");

        var page = new TaskDialogPage
        {
            Caption = "RSL Companion",
            Heading = $"Stay signed in as {session.Email ?? session.DisplayName ?? session.Uid}?",
            Text = "You can change this at any time from Help ▸ Session security, which also offers "
                   + "locking the saved session with Windows Hello.",
            Icon = TaskDialogIcon.ShieldBlueBar,
            AllowCancel = false, // there is no third answer; both buttons are a real choice
            Buttons = { stay, dont },
            DefaultButton = stay,
        };

        var chosen = TaskDialog.ShowDialog(this, page) == stay
            ? SessionProtection.WindowsAccount
            : SessionProtection.None;

        await _sessions.PersistAsync(session, chosen);
    }

    /// <summary>
    /// Surfaces this window above whatever currently holds the foreground (typically the browser tab
    /// used for sign-in). Toggling <see cref="Form.TopMost"/> is used deliberately: a plain
    /// <see cref="Form.Activate"/> from a background process is reduced to a taskbar flash by Windows'
    /// foreground lock, whereas the TopMost toggle reliably raises the window without staying pinned.
    /// </summary>
    private void BringToForeground()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        bool wasTopMost = TopMost;
        TopMost = true;
        Activate();
        BringToFront();
        TopMost = wasTopMost;
    }

    /// <summary>Populates the signed-in UI: identity, export availability, accounts, and update check.</summary>
    private async Task EnterSignedInAsync()
    {
        var session = _api.Session!;
        _shell.SetUser(session.DisplayName, session.Email ?? session.Uid);
        if (_sessionSecurityItem is not null) _sessionSecurityItem.Enabled = true;
#if EXTRACTION
        _shell.SetExportAvailable(true);
#endif
        await LoadAccountsAsync();

        // One loop per session, not one per sign-in: signing out and back in must not leave two
        // refreshers running against the same tiles.
        if (!_accountRefreshStarted)
        {
            _accountRefreshStarted = true;
            _ = PollAccountsAsync(_refreshCts.Token);
        }

#if EXTRACTION
        _ = RefreshHeroBaseStatsAsync(_refreshCts.Token);
#endif
    }

    private bool _accountRefreshStarted;

#if EXTRACTION
    /// <summary>Guards the base-stat refresh to once per app session; a re-sign-in must not re-fetch 2.2 MB.</summary>
    private bool _baseStatsChecked;

    /// <summary>
    /// Picks up a newer champion base-stat catalog if RSL Companion is publishing one, so
    /// <c>heroes[].baseStats</c> follows a game rebalance without waiting for a release.
    ///
    /// <para><b>Silent in every direction, deliberately.</b> Offline, 404, malformed, endpoint not
    /// deployed — none of it is worth a line in front of a player, because the bundled catalog is
    /// already correct for the build this release shipped against and the export loses nothing. It
    /// runs after sign-in rather than at export time for the same reason the update check does not
    /// block anything: an export must never wait on a download it can do without.</para>
    /// </summary>
    private async Task RefreshHeroBaseStatsAsync(CancellationToken token)
    {
        if (_baseStatsChecked || !_api.IsAuthenticated) return;
        _baseStatsChecked = true;

        try
        {
            // Both of these parse a 2.2 MB catalog — ~200 ms each, and this method's awaits resume on
            // the UI thread, so left inline they would freeze the window twice on the sign-in path.
            // The parse stays whole rather than skimming for the timestamp because "newer" has to mean
            // newer than a catalog that actually LOADS: a corrupt local file with a future date would
            // otherwise block every update forever while Load quietly used the bundled copy instead.
            var since = await Task.Run(HeroBaseStatsCatalog.EffectiveGeneratedAt, token);

            var response = await _api.GetHeroBaseStatsAsync(since, token);
            if (response.Status != CertificationStatus.Found)
            {
                Log(response.Error ?? "Champion base stats: nothing newer published.", detail: true);
                return;
            }

            var applied = await Task.Run(() => HeroBaseStatsUpdate.Apply(response.Body!), token);
            Log($"Champion base stats: {applied.Outcome} — {applied.Message}", detail: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("Champion base-stat refresh failed: " + ex.Message, detail: true);
        }
    }
#endif

    // Stops the account refresh when the window closes. Its own source rather than the game poll's:
    // the tiles refresh in every build, including the public one with no extraction engine.
    private readonly CancellationTokenSource _refreshCts = new();

    /// <summary>
    /// How often the tiles are re-read from RSL Companion. Counts, clan and last-sync are server-side
    /// facts that change without this app doing anything — another device syncing, or the same account
    /// updated from the website — so a window left open all day otherwise shows the state at the moment
    /// it was opened. Five minutes is well under the interval at which anyone notices, and it is one
    /// small GET.
    /// </summary>
    private static readonly TimeSpan AccountRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Re-reads the accounts on a timer, quietly. Skipped while signed out or while something else
    /// holds the app busy — an export ends with its own reload, so refreshing underneath it would only
    /// race that with staler data.
    /// </summary>
    private async Task PollAccountsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(AccountRefreshInterval, token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested) return;
            if (!_api.IsAuthenticated || _busy) continue;

            // LoadAccountsAsync(silent) already swallows its own failures; this guard is for anything
            // that could escape it, because one throw here would end the refreshing for the session.
            try { await LoadAccountsAsync(silent: true); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log("Background account refresh failed: " + ex.Message, detail: true); }
        }
    }

#if EXTRACTION
    /// <summary>Reflects the live Raid connection state, and reconciles the live account on connect.</summary>
    /// <summary>
    /// The single status poll: every <see cref="PollInterval"/> it answers both halves of "can we
    /// work with the game right now" — is the process up, and is its account data actually readable
    /// — and reports the combined answer as one status line.
    ///
    /// Polling (rather than reacting to process start/stop) is what makes the two halves stay in
    /// sync. The process appears the moment the launcher starts but the account isn't readable until
    /// the roster has loaded, so a process-change event fires far too early and then never again;
    /// re-checking on a timer means the status catches up on its own, and a game that dies mid-session
    /// is noticed within one interval instead of never.
    ///
    /// Identity only — id and name. Nothing here reads resources, champions or artifacts, and it
    /// never runs the expensive calibration scan; both of those are user-triggered actions.
    /// </summary>
    private async Task PollGameStatusAsync(CancellationToken token)
    {
        var cachePath = Path.Combine(AppContext.BaseDirectory, "offsets_cache.json");

        while (!token.IsCancellationRequested)
        {
            try
            {
                await ProbeGameOnceAsync(cachePath, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let a probe failure kill the loop — that would freeze the status line on a
                // stale value for the rest of the session.
                ApplyGameState(RaidProcess.IsRunning() ? GameState.Loading : GameState.NotRunning,
                               detail: DescribeExtractionFailure(ex));
            }

            try { await Task.Delay(PollInterval, token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ProbeGameOnceAsync(string cachePath, CancellationToken token)
    {
        if (!RaidProcess.IsRunning())
        {
            ApplyGameState(GameState.NotRunning);
            return;
        }

        // An export or calibration owns the process while it runs; leave the status as-is rather
        // than probing underneath it. The next tick picks up again.
        if (_busy || _calibrating) return;

        var result = await Task.Run(
            () => ExtractionService.DiscoverAccountAsync(cachePath: cachePath).GetAwaiter().GetResult(), token);
        if (token.IsCancellationRequested) return;

        switch (result.Status)
        {
            case ExtractionService.AccountDiscoveryStatus.Found when GameUserId(result.AccountId) is int uid:
                _liveUserId = uid;
                _liveName = string.IsNullOrWhiteSpace(result.Name) ? $"Account {uid}" : result.Name;
                ReconcileLiveAccount();
                ApplyGameState(GameState.Connected);
                break;

            case ExtractionService.AccountDiscoveryStatus.Found:
                ApplyGameState(GameState.Loading,
                    detail: "the game reported an account id we don't recognise");
                break;

            case ExtractionService.AccountDiscoveryStatus.NeedsCalibration:
                ApplyGameState(GameState.NeedsCalibration);
                // Published map first: it costs a GET against the ~35s scan below, and it is the same
                // answer. Only when the server has nothing does the user pay to derive it.
                if (await TryCertifyBuildAsync(token)) break;
                // Self-calibrate rather than stranding the user until a release ships their build.
                // Once per build per session: it is a ~35s scan, so it must never land on a timer.
                await TrySelfCalibrateAsync(cachePath, token);
                break;

            default:
                ApplyGameState(GameState.Loading);
                break;
        }
    }

    /// <summary>
    /// Derives a memory map for a game build no shipped catalog covers, so a user who updated Raid
    /// before we published a matching release can still export instead of being stranded.
    ///
    /// Bounded hard: at most one attempt per game build per app session (<see cref="_calibrationAttempted"/>).
    /// A build that genuinely cannot be calibrated — or a game still mid-load — must cost one ~35s
    /// scan, not one every poll. <paramref name="force"/> is how the user retries once the game has
    /// finished loading.
    /// </summary>
    private async Task TrySelfCalibrateAsync(string cachePath, CancellationToken token, bool force = false)
    {
        if (_calibrating || _busy) return;

        // Identify the build so one failure doesn't licence an endless retry loop. Cheap: the hash
        // is memoized in the engine, and this is the same file the probe already stat'd.
        string buildKey = GameBuildKey();
        if (!force && !_calibrationAttempted.Add(buildKey)) return;
        if (force) _calibrationAttempted.Add(buildKey);

        _calibrating = true;
        ApplyGameState(GameState.Calibrating, force: true);
        // Disables the tile buttons for the same reason export does: reading the process while a
        // ~35s scan also has it attached would race the two, and neither result would be trustworthy.
        SetBusy(true);
        Log("This Raid version is new to the app — setting it up now. This takes about a minute, "
          + "happens once per game update, and only works while Raid is fully loaded.");

        try
        {
            // Accumulates under LocalAppData and is read back by KnownOffsets, so this build is
            // never calibrated again — and the same file is shareable: sending it in is what gets
            // the build into the next release so nobody else pays for this scan.
            string exportPath = KnownOffsets.LocalCatalogPath;

            var result = await Task.Run(
                () => ExtractionService.CalibrateAsync(cachePath: cachePath, exportCatalogPath: exportPath)
                                       .GetAwaiter().GetResult(), token);

            if (token.IsCancellationRequested) return;

            if (result.Success)
            {
                Log($"All set — found your account, {result.Name ?? "the account"}. This Raid version "
                  + "is ready to go on this PC from now on.");
                if (result.ExportedCatalogPath is not null)
                    Log("This also helps RSL Companion support this Raid version for everyone else "
                      + "in the next update.");

                // A dismissible banner, not just an activity-log line: the console starts collapsed,
                // so a silent ~35-50s pause followed by nothing visible would read as the app having
                // done nothing, when it just quietly fixed the one thing that would have blocked export.
                var versionLabel = _buildInfo is { } b ? BuildLabel(b) : "this Raid version";
                _shell.SetNotice($"Raid {versionLabel} is a new version — RSL Companion set it up "
                                + "automatically so everything keeps working.");
            }
            else
            {
                Log($"Couldn't finish setting up this Raid version: {result.Error}. If Raid was still "
                  + "loading, wait until your heroes are visible, then try again from "
                  + "Help → Set up this Raid version.");
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing or game gone — nothing to report.
        }
        catch (Exception ex)
        {
            Log($"Couldn't set up this Raid version: {DescribeExtractionFailure(ex)}");
        }
        finally
        {
            _calibrating = false;
            SetBusy(false);
            // Let the next poll re-read reality rather than asserting a state from here.
            _gameState = GameState.NotRunning;
        }
    }

    /// <summary>
    /// Identifies the installed game build for retry bookkeeping. Falls back to a constant when the
    /// game can't be inspected — that only makes the once-per-session guard coarser, never looser.
    /// </summary>
    private static string GameBuildKey()
    {
        try
        {
            using var p = Process.GetProcessesByName("Raid").FirstOrDefault();
            var path = p?.MainModule?.FileName;
            if (path is null) return "unknown";
            var dll = Path.Combine(Path.GetDirectoryName(path)!, "GameAssembly.dll");
            var info = new FileInfo(dll);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc:O}" : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Kicks off covering a game build nothing on this PC recognises yet — automatically, not on a
    /// click: the user already knows something is wrong from the status line, so making them also
    /// find and press a button just delays the fix.
    ///
    /// Only when this is the newest uploader — on an older build the release the user hasn't
    /// installed may already carry the map. A build the user has already certified or calibrated is
    /// covered locally even though the shipped catalog still doesn't know it, and must not keep
    /// re-triggering. <see cref="TryCertifyBuildAsync"/> and <see cref="TrySelfCalibrateAsync"/> each
    /// own their once-per-build-per-session guard, so calling both on every state transition is safe.
    /// </summary>
    private void UpdateReportPrompt()
    {
        bool needsCover = _buildInfo is { CoveredByShippedCatalog: false } b
                       && _isLatestUploader == true
                       && !BuildCertification.HasLocalMap(b.GameAssemblyHash);
        if (needsCover) _ = AutoCoverUncoveredBuildAsync();
    }

    /// <summary>
    /// Published map first: it costs a GET against the ~35s local scan, and it is the same answer.
    /// Only when the server has nothing does the user pay to derive it themselves.
    /// </summary>
    private async Task AutoCoverUncoveredBuildAsync()
    {
        if (await TryCertifyBuildAsync(_statusCts.Token)) return;
        await TrySelfCalibrateAsync(Path.Combine(AppContext.BaseDirectory, "offsets_cache.json"), _statusCts.Token);
    }

    private static string BuildLabel(ExtractionService.GameBuildInfo b)
        => b.GameVersion is string v && v.Length > 0 ? v : b.GameAssemblyHash[..12];

    /// <summary>
    /// Re-reads which game build is running and whether the release covers it. Called on state
    /// transitions, not per poll: it only changes when the game itself does.
    /// </summary>
    private void RefreshBuildInfo()
    {
        try
        {
            _buildInfo = RaidProcess.IsRunning() ? ExtractionService.TryGetGameBuild() : null;
        }
        catch
        {
            _buildInfo = null;
        }
        UpdateReportPrompt();
    }

    /// <summary>
    /// Asks RSL Companion whether it has published a memory map for the running game build, and
    /// installs it when it has. Returns true only when a map was actually applied, so callers know
    /// whether the expensive local calibration is still needed.
    ///
    /// Bounded to one offer per build per app session unless <paramref name="force"/>d, for the same
    /// reason calibration is: an unanswerable build must not re-prompt on every poll.
    /// </summary>
    private async Task<bool> TryCertifyBuildAsync(CancellationToken token, bool force = false)
    {
        if (_calibrating || _busy) return false;
        if (_buildInfo is not { CoveredByShippedCatalog: false } build) return false;
        if (BuildCertification.HasLocalMap(build.GameAssemblyHash)) return false;

        // The lookup is an authenticated API call like every other; signed out there is nothing to
        // ask, and the user is already being told to sign in.
        if (!_api.IsAuthenticated) return false;

        if (!_certificationOffered.Add(build.GameAssemblyHash) && !force) return false;

        // Consent is per-user, not per-build: once granted it stands, which is the whole point of the
        // checkbox. Asked before anything leaves the machine, because the request carries the build
        // the user is running.
        if (!UserSettings.Current.AutoCheckBuildCertification && !AskToCheckCompatibility(build))
        {
            Log("No problem — setting this Raid version up from your own game instead.");
            return false;
        }

        // Disables the tile buttons for the duration: applying the result re-probes the account, and
        // a "Update user data" click landing mid-check would race that re-probe.
        SetBusy(true);
        try
        {
            var label = BuildLabel(build);
            Log($"Checking whether RSL Companion already supports Raid {label}…");

            CertificationResult response;
            try
            {
                response = await _api.GetCertifiedBuildAsync(
                    build.GameAssemblyHash, build.GameVersion, AboutForm.DisplayVersion, token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log("Couldn't reach RSL Companion just now — setting this Raid version up from your own game instead.");
                Log($"Certification lookup failed: {ex.Message}", detail: true);
                return false;
            }

            if (response.Status == CertificationStatus.NotPublished)
            {
                Log($"RSL Companion doesn't cover Raid {label} yet — setting it up from your own game "
                  + "instead.");
                return false;
            }

            if (response.Status == CertificationStatus.Failed)
            {
                Log($"Couldn't check Raid {label} online — setting it up from your own game instead.");
                if (response.Error is string err) Log($"Certification lookup: {err}", detail: true);
                return false;
            }

            var applied = BuildCertification.Apply(response.Body!, build.GameAssemblyHash, AboutForm.DisplayVersion);
            switch (applied.Outcome)
            {
                case BuildCertification.Outcome.Applied:
                    Log($"Good news — {applied.Message} Reading your account now.");
                    // Let the next poll re-probe against the result that now exists rather than
                    // asserting a state from here.
                    _gameState = GameState.NotRunning;
                    RefreshBuildInfo();
                    return true;

                case BuildCertification.Outcome.NeedsNewerUploader:
                    Log($"Raid {label} is supported, but {applied.Message} " +
                        "Use Help → Check for updates, then try again.");
                    return false;

                default:
                    Log($"Couldn't set this up from RSL Companion — {applied.Message}");
                    return false;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// The consent prompt. A native TaskDialog rather than a page banner because it carries the
    /// "don't ask again" checkbox, and because it is asking permission to send the build the user is
    /// running to the server — that is a question, not a notification.
    /// </summary>
    private bool AskToCheckCompatibility(ExtractionService.GameBuildInfo build)
    {
        var check = new TaskDialogButton("Verify compatibility");
        var page = new TaskDialogPage
        {
            Caption = "RSL Companion",
            Heading = $"New Raid version detected ({BuildLabel(build)})",
            Text = "This app hasn't been set up for this Raid version yet. We can run a quick "
                 + "compatibility verification online, which usually takes just a few seconds — "
                 + "otherwise it'll be worked out directly from your game, which takes about a minute.",
            Icon = TaskDialogIcon.Information,
            Buttons = { check, TaskDialogButton.Cancel },
            DefaultButton = check,
            Verification = new TaskDialogVerificationCheckBox("Verify compatibility automatically next time"),
        };

        var clicked = TaskDialog.ShowDialog(this, page);

        // Recorded on consent only: ticking the box and cancelling is a refusal, not a standing yes.
        if (clicked == check && page.Verification!.Checked)
        {
            UserSettings.Current.AutoCheckBuildCertification = true;
            UserSettings.Current.Save();
            Log("Future Raid updates will be checked automatically.");
        }

        return clicked == check;
    }

    /// <summary>
    /// Renders <paramref name="state"/> into the status line, and logs only on a real transition so
    /// a 5-second poll doesn't fill the activity log with the same line over and over.
    /// </summary>
    private void ApplyGameState(GameState state, string? detail = null, bool force = false)
    {
        var previous = _gameState;
        _gameState = state;

        (string kind, string text) = state switch
        {
            GameState.Connected =>
                ("connected", $"Connected — {_liveName} (#{_liveUserId})"),
            GameState.Loading =>
                ("loading", "Raid is running — waiting for account data…"),
            GameState.Calibrating =>
                ("calibrating", "New Raid version — setting things up (about a minute)…"),
            GameState.NeedsCalibration =>
                ("needsCalibration", "Raid is running — account can't be identified"),
            _ =>
                ("notRunning", "Raid not running — start the game to fetch account details"),
        };
        _shell.SetStatus(kind, text);

        if (state == previous && !force) return;

        // The build (and whether we cover it) can only have changed across a transition.
        RefreshBuildInfo();

        switch (state)
        {
            case GameState.Connected:
                Log(_loadedAccounts.Any(a => a.UserId == _liveUserId)
                    ? $"Playing as {_liveName} (#{_liveUserId}) — already imported."
                    : $"New account detected: {_liveName} (#{_liveUserId}) — not imported yet.");
                break;

            case GameState.NotRunning when previous is GameState.Connected or GameState.Loading or GameState.NeedsCalibration:
                // Distinguish "never started it" from "it went away underneath us".
                Log("Raid has closed — the game is no longer reachable.");
                _liveUserId = null;
                _liveName = null;
                ReconcileLiveAccount();
                break;

            case GameState.NeedsCalibration:
                // No log line here: TrySelfCalibrateAsync runs straight after and explains itself.
                // Saying "can't identify the account" immediately before "working it out" only reads
                // as a failure the app then contradicts.
                break;

            case GameState.Calibrating:
                break; // TrySelfCalibrateAsync logs the explanation

            case GameState.Loading:
                Log(detail is null
                    ? "Raid is running — waiting for the account to load."
                    : $"Raid is running, but the account isn't readable yet: {detail}");
                break;
        }
    }

    // Fast enough that closing the game is noticed promptly, and affordable because a settled probe
    // is a single memory read against a cached address (see ExtractionService.DiscoverAccountAsync).
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Matches the live game account against the imported tiles: an already-imported account gets the
    /// "In game" badge, an unknown one is surfaced as a "new account detected" tile.
    ///
    /// Called from both sides — when detection finishes and when the tiles reload — because the two
    /// run concurrently at startup. Classifying once inside detection raced the initial account load
    /// and could label an imported account as "new" (or leave a stale "new" tile behind after the
    /// real one arrived).
    /// </summary>
    private void ReconcileLiveAccount()
    {
        if (_liveUserId is not int uid)
        {
            _shell.SetDetectedAccount(null, null);
            _shell.SetIdentified(null);
        }
        else if (_loadedAccounts.Any(a => a.UserId == uid))
        {
            _shell.SetDetectedAccount(null, null);
            _shell.SetIdentified(uid);
        }
        else
        {
            _shell.SetIdentified(null);
            _shell.SetDetectedAccount(uid, _liveName);
        }

        // Which account the site should open on is exactly what this just decided.
        RefreshHelperUrl();
    }
#endif

    /// <summary>
    /// The site URL behind the page's "Open RSL Helper" button and Help → Open rslcompanion.com.
    ///
    /// When the running game is on an account this profile has already imported, that account is
    /// named in the URL (<c>?account=&lt;in-game id&gt;</c>) so the site opens on the account being
    /// played rather than on whatever that browser last looked at. The site's accounts endpoint
    /// reports this same number as both an account's <c>id</c> and its <c>userId</c>, so the in-game
    /// id needs no translation — it is what its dropdown selects by.
    ///
    /// Nothing is appended when the game is closed, unreadable, or on an account that isn't imported
    /// yet: there would be no entry in the site's dropdown to select, and naming a missing account
    /// would only make the site fall back to its first one anyway.
    /// </summary>
    private string HelperUrl()
    {
#if EXTRACTION
        if (_liveUserId is int uid && _loadedAccounts.Any(a => a.UserId == uid))
            return $"{_config.FrontendUrl}/?account={uid}";
#endif
        return _config.FrontendUrl;
    }

    private void RefreshHelperUrl() => _shell.SetFrontendUrl(HelperUrl());

    private void BuildLayout()
    {
        // The shell lives inside a content host rather than directly on the form, so that sign-in can
        // take over the same area (see SignIn) by filling _content and coming to the front. Docking
        // it straight onto the form would put it in a z-order argument with the MenuStrip below.
        _shell.Dock = DockStyle.Fill;
        _content.Controls.Add(_shell);
        Controls.Add(_content);

        // Added after the Fill control on purpose: WinForms resolves docking from the highest child
        // index down, so the menu must be last to claim the top strip before the content fills.
        var menu = BuildMenu();
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private MenuStrip BuildMenu()
    {
        var help = new ToolStripMenuItem("&Help");
        if (!PackagedAppInfo.IsPackaged)
        {
            help.DropDownItems.Add(new ToolStripMenuItem("Check for &updates…", null,
                async (_, _) => await CheckForUpdateAsync(silent: false)));

            // The way out of the automatic checks, and the way back in. CheckOnClick flips the tick
            // before the handler runs, so the handler reads it rather than negating the setting.
            _autoUpdateItem = new ToolStripMenuItem("Check for updates &automatically", null,
                (_, _) => SetAutoUpdate(_autoUpdateItem!.Checked, announce: true))
            {
                CheckOnClick = true,
                Checked = UserSettings.Current.AutoUpdateChecks,
            };
            help.DropDownItems.Add(_autoUpdateItem);
        }
#if EXTRACTION
        // The manual retry for a build whose automatic attempt ran too early (game still loading).
        help.DropDownItems.Add(new ToolStripMenuItem("&Set up this Raid version", null,
            async (_, _) =>
            {
                if (!RaidProcess.IsRunning())
                {
                    Log("Start Raid and let it load to the roster first, then try again.");
                    return;
                }
                await TrySelfCalibrateAsync(
                    Path.Combine(AppContext.BaseDirectory, "offsets_cache.json"),
                    _statusCts.Token,
                    force: true);
            }));
#endif
        // Evaluated per click, not once at build time: the account being played changes underneath it.
        help.DropDownItems.Add(new ToolStripMenuItem("Open rslcompanion.com", null,
            (_, _) => OpenUrl(HelperUrl())));

        // The stay-signed-in choice is made on the sign-in window, which someone with a remembered
        // session may not see for months. This is how they change their mind without the sign-out
        // that would cost them the very thing they were protecting.
        _sessionSecurityItem = new ToolStripMenuItem("Session &security…", null, (_, _) =>
        {
            if (_api.Session is not { } session) return;
            using var dlg = new SessionSecurityForm(_sessions, session);
            dlg.ShowDialog(this);
        })
        { Enabled = false };
        help.DropDownItems.Add(_sessionSecurityItem);

        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) =>
        {
            using var about = new AboutForm(_config);
            about.ShowDialog(this);
        }));

        var menu = new MenuStrip { Dock = DockStyle.Top };
        menu.Items.Add(help);
        return menu;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser or the shell refused — not worth interrupting the user over.
        }
    }

    /// <summary>
    /// Reloads the tiles from RSL Companion.
    ///
    /// <para><paramref name="silent"/> is what the background refresh uses: it skips the busy flag and
    /// the narration. A periodic reload that greyed the action buttons out and announced itself every
    /// few minutes would be worse than the staleness it exists to fix — the user would see the export
    /// button flicker dead under the cursor for no reason they asked for.</para>
    /// </summary>
    private async Task LoadAccountsAsync(bool silent = false)
    {
        if (!silent)
        {
            SetBusy(true);
            Log("Loading your accounts…");
        }
        try
        {
            var accounts = await _api.GetAccountsAsync();

            // Show only accounts created by this uploader (last synced via the consolidated export).
            _loadedAccounts = accounts
                .Where(a => string.Equals(a.LastSyncMethod, UploaderSyncMethod, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name)
                .ToList();

            _shell.SetAccounts(_loadedAccounts
                .Select(a => new AppShell.Tile(a.UserId, a.Name ?? $"Account {a.UserId}", a.ClanName,
                    a.HeroCount, a.ArtifactCount, LastSyncInstant(a.LastSyncDate), a.AccessoryCount))
                .ToList());

            if (silent)
            {
                Log($"Background refresh: {_loadedAccounts.Count} account(s).", detail: true);
            }
            else
            {
                Log(_loadedAccounts.Count switch
                {
                    0 => "No accounts in your profile yet — start Raid, then use the button on your account "
                       + "to add it.",
                    1 => "Found 1 account in your profile.",
                    var n => $"Found {n} accounts in your profile.",
                });
            }
        }
        catch (Exception ex)
        {
            // A silent refresh failing is not news: the tiles simply keep the values they already have,
            // and the next tick tries again. Saying so out loud would turn a flaky minute of Wi-Fi into
            // a recurring error in front of someone who never asked for the reload.
            if (!silent) Log("Couldn't load your accounts — check your internet connection and try again.");
            Log(ex.ToString(), detail: true);
        }
        finally
        {
            if (!silent) SetBusy(false);
#if EXTRACTION
            // The tiles just changed, so the live account's imported/new status may have too.
            ReconcileLiveAccount();
#endif
        }
    }

    /// <summary>
    /// The instant an account was last synced, as ISO-8601 for the page — <b>not</b> a "14 min ago"
    /// label. The page owns that wording and re-derives it on a ticker, because a relative label is
    /// only true at the moment it is written and the tiles outlive that moment by hours.
    /// </summary>
    private static string? LastSyncInstant(DateTimeOffset? when) => when?.ToString("o");

    /// <summary>
    /// The release behind the update banner, so clicking it knows what to fetch. Null when the app is
    /// current (or the check never succeeded).
    /// </summary>
    private UpdateInfo? _pendingUpdate;

    /// <summary>Guards against a second download starting on top of the first.</summary>
    private bool _updating;

    /// <summary>
    /// A verified installer sitting on disk, waiting to be applied. Non-null means the download is
    /// done and the only thing left is a restart — either the user clicking the banner again, or
    /// simply quitting, which applies it on the way out.
    /// </summary>
    private string? _stagedInstaller;

    /// <summary>Set once the installer is running, so the exit hook never starts a second copy.</summary>
    private bool _installerLaunched;

    /// <summary>How often the app re-checks for a release while it is open, once enabled.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);

    /// <summary>Stops the update poll when it is switched off. Null whenever the poll isn't running.</summary>
    private CancellationTokenSource? _updatePollCts;

    /// <summary>Help ▸ Check for updates automatically. Checked state mirrors the saved preference.</summary>
    private ToolStripMenuItem? _autoUpdateItem;

    /// <summary>
    /// Starts the automatic check — once now, then hourly — if the user has turned it on. Safe to
    /// call repeatedly: a second call while one is already running is a no-op, so enabling from the
    /// menu can share this with startup.
    /// </summary>
    private void StartUpdatePolling()
    {
        // Packaged (MSIX/Store) builds are updated by the Store, so polling GitHub could only ever
        // offer them an install they must not perform.
        if (PackagedAppInfo.IsPackaged) return;
        if (!UserSettings.Current.AutoUpdateChecks) return;
        if (_updatePollCts is not null) return;

        _updatePollCts = CancellationTokenSource.CreateLinkedTokenSource(_refreshCts.Token);
        _ = PollUpdatesAsync(_updatePollCts.Token);
    }

    private void StopUpdatePolling()
    {
        _updatePollCts?.Cancel();
        _updatePollCts?.Dispose();
        _updatePollCts = null;
    }

    /// <summary>
    /// Checks now, then every hour for as long as the window is open.
    ///
    /// <para>An app left running for days was the gap this closes: the check used to happen once, at
    /// sign-in, so a session that stayed open never learned about a release — including the one that
    /// covers a Raid build the user is about to be blocked by.</para>
    /// </summary>
    private async Task PollUpdatesAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Silent: a background check that found nothing has nothing to say, and an hourly
                // "you're up to date" would be noise in a console the user reads for exports.
                await CheckForUpdateAsync(silent: true);
                await Task.Delay(UpdateCheckInterval, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Switched off, or the window closed.
        }
    }

    /// <summary>
    /// Asks, once ever, whether the app may look for new versions on its own.
    ///
    /// <para>Off is the default and staying silent is not an answer, so this is what turns the
    /// default into a decision — the same shape as the stay-signed-in question. Both answers are
    /// real: declining leaves Help ▸ Check for updates working, so it costs discovery, not the
    /// ability to update. Either way it is settled for good and never asked again; the menu item is
    /// how anyone changes their mind.</para>
    /// </summary>
    private void AskAutoUpdateIfUnanswered()
    {
        if (PackagedAppInfo.IsPackaged) return;
        if (UserSettings.Current.AutoUpdateChecksChosen) return;

        var auto = new TaskDialogCommandLinkButton(
            "Check automatically",
            "Looks for a new version once an hour and shows a banner when one is ready. Nothing "
            + "downloads or installs until you click it.");
        var manual = new TaskDialogCommandLinkButton(
            "Only when I ask",
            "No background checks. Use Help ▸ Check for updates whenever you want one.");

        var page = new TaskDialogPage
        {
            Caption = "RSL Companion",
            Heading = "Check for updates automatically?",
            Text = "You can change this at any time from Help ▸ Check for updates automatically.",
            Icon = TaskDialogIcon.Information,
            AllowCancel = false, // there is no third answer; both buttons are a real choice
            Buttons = { auto, manual },
            DefaultButton = auto,
        };

        SetAutoUpdate(TaskDialog.ShowDialog(this, page) == auto, announce: false);
    }

    /// <summary>
    /// Records the automatic-check preference and starts or stops the poll to match. Writing
    /// <c>Chosen</c> here means using the menu counts as answering, so nobody is asked a question
    /// they have already acted on.
    /// </summary>
    private void SetAutoUpdate(bool enabled, bool announce)
    {
        UserSettings.Current.AutoUpdateChecks = enabled;
        UserSettings.Current.AutoUpdateChecksChosen = true;
        UserSettings.Current.Save();

        if (_autoUpdateItem is not null) _autoUpdateItem.Checked = enabled;

        if (enabled)
        {
            if (announce) Log("Automatic update checks are on — once an hour.");
            StartUpdatePolling();
        }
        else
        {
            if (announce) Log("Automatic update checks are off. Use Help ▸ Check for updates when you want one.");
            StopUpdatePolling();
        }
    }

    /// <summary>
    /// On a background check (<paramref name="silent"/> = true) failures and "already up to date" are
    /// not reported — only a real update lights up the banner. A manual check always logs the outcome.
    /// </summary>
    private async Task CheckForUpdateAsync(bool silent)
    {
        try
        {
            var result = await UpdateChecker.CheckForUpdateAsync();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    _pendingUpdate = result.Info;
                    _shell.SetUpdate(result.Info!.Version.ToString());
#if EXTRACTION
                    // Only the extraction build has anything to gate on this: it is what stops an
                    // out-of-date uploader burning 35 s calibrating a build the next release covers.
                    _isLatestUploader = false;
#endif
                    if (!silent) Log($"Update available: {result.Info.Version}.");
                    break;
                case UpdateCheckStatus.UpToDate:
                    _pendingUpdate = null;
                    _shell.SetUpdate(null);
#if EXTRACTION
                    _isLatestUploader = true;
#endif
                    if (!silent) Log($"You're on the latest version ({UpdateChecker.CurrentVersion}).");
                    break;
                case UpdateCheckStatus.Failed:
                    if (!silent) Log("Could not check for updates — no internet connection or GitHub is unreachable.");
                    break;
            }
        }
        finally
        {
#if EXTRACTION
            // The prompt is gated on being the latest uploader, which is only known once this
            // finishes — it runs asynchronously well after the first status poll.
            UpdateReportPrompt();
#endif
        }
    }

    /// <summary>
    /// Installs the release behind the update banner: download it, verify it, run it, and get out of
    /// its way.
    ///
    /// <para>The banner used to open the GitHub release page, which handed the user a list of assets
    /// (versioned installer, unversioned copy, two checksums, an MSIX, a certificate) and asked them
    /// to pick, download and run the right one. Clicking "a new version is available" means "give me
    /// the new version", so this does all of it.</para>
    ///
    /// <para><b>Neither dead end opens a browser on the user; both report into the banner and offer a
    /// link.</b> A packaged (MSIX/Store) build must never replace itself with an Inno install — the
    /// Store owns those files — and a release with no installer asset has nothing to run. A download
    /// that fails says so where it was promised, stays clickable as its own retry, and puts the manual
    /// download beside it. Launching a browser instead was worse on both counts: it threw away the
    /// retry, and it answered "the update failed" by silently opening a tab, so the user was left
    /// looking at a page nobody told them why they were on — which read as the banner simply being a
    /// link to GitHub.</para>
    ///
    /// <para><b>Downloading never closes the app.</b> The installer is staged and the banner switches
    /// to saying so; nothing is replaced until the user restarts, because they may well be mid-export
    /// or mid-session when they click. From there either route applies it: clicking the banner again
    /// restarts into the new version, or simply quitting applies it on the way out
    /// (<see cref="ApplyStagedUpdateOnExit"/>) so the next launch is already updated.</para>
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        // Second click, download already done: this is the "restart now" the banner is offering.
        if (_stagedInstaller is { } staged)
        {
            RestartIntoUpdate(staged);
            return;
        }

        if (_updating || _pendingUpdate is not { } info) return;

        if (PackagedAppInfo.IsPackaged || info.InstallerUrl is null)
        {
            _shell.SetUpdateStatus($"Version {info.Version} has to be installed by hand from here:",
                                   clickable: false,
                                   linkUrl: info.ReleaseUrl, linkText: "open the release page");
            Log($"Version {info.Version} can't be installed from here — get it from {info.ReleaseUrl}.");
            return;
        }

        _updating = true;
        try
        {
            _shell.SetUpdateStatus($"Downloading version {info.Version}…");
            Log($"Downloading version {info.Version}…");

            // Constructed on the UI thread, so its callback marshals back there for us.
            var progress = new Progress<int>(p => _shell.SetUpdateStatus($"Downloading version {info.Version}… {p}%"));
            var installer = await UpdateInstaller.DownloadAsync(info, progress, _refreshCts.Token);

            _stagedInstaller = installer;
            _shell.SetUpdateStatus(
                $"Version {info.Version} is downloaded — restart to install it. Click here to restart now.",
                clickable: true);
            Log($"Version {info.Version} is downloaded. It installs when you restart the app — click "
              + "the banner to restart now, or just close the app when you're done and it applies itself.");
        }
        catch (OperationCanceledException) when (_refreshCts.IsCancellationRequested)
        {
            // The window is closing (the token is the form's). Nothing to report and nowhere to
            // report it. The guard matters: HttpClient signals *its own* 15-minute timeout with the
            // same exception type, and a stalled download swallowed here would leave the banner
            // frozen at a percentage and _updating stuck true, so no later click could retry it.
        }
        catch (Exception ex)
        {
            // The banner stays clickable, so it is still the retry — worth having, because the
            // failures seen here are transient (a dropped connection, antivirus holding the finished
            // file). The link beside it is for when retrying keeps failing.
            _updating = false;
            _shell.SetUpdateStatus($"Couldn't download version {info.Version} — click to try again, or",
                                   clickable: true,
                                   linkUrl: UpdateChecker.DownloadPageUrl, linkText: "download it yourself");
            Log($"Couldn't download version {info.Version}: {DescribeDownloadFailure(ex)} You can click the banner to try "
              + $"again, or install it yourself from {UpdateChecker.DownloadPageUrl}.");
            Log(ex.ToString(), detail: true);
        }
    }

    /// <summary>
    /// Applies a staged update now, at the user's request: start the installer asking it to bring the
    /// app back, and close so it can replace these files.
    ///
    /// <para>Refuses while something is running. Restarting mid-export would abandon it — nothing
    /// corrupts, since an interrupted upload is one failed POST, but losing a read at 90% is a
    /// surprise nobody asked for, and the update is already downloaded and in no hurry.</para>
    /// </summary>
    private void RestartIntoUpdate(string installer)
    {
        if (_installerLaunched) return;

        if (_busy)
        {
            Log("Finishing what's running first — click the banner again in a moment to restart.");
            return;
        }

        try
        {
            _installerLaunched = true;
            _shell.SetUpdateStatus("Restarting to install the update…");
            UpdateInstaller.Launch(installer, relaunch: true);
            Close();
        }
        catch (Exception ex)
        {
            _installerLaunched = false;
            _shell.SetUpdateStatus("Update downloaded — restart to install it. Click here to restart now.",
                                   clickable: true);
            Log("Couldn't start the installer. Close the app and open it again, or install it by hand.");
            Log(ex.ToString(), detail: true);
        }
    }

    /// <summary>
    /// Runs a staged installer as the app exits, so "restart to install" is true however the user
    /// restarts — closing the window counts, not just the banner's restart button. Without this the
    /// app would tell them a restart applies the update and then not apply it.
    ///
    /// <para>Deliberately does <b>not</b> pass <c>relaunch</c>: they closed the app. Applying the
    /// update is finishing what they already agreed to; reopening the window is not.</para>
    /// </summary>
    private void ApplyStagedUpdateOnExit()
    {
        if (_installerLaunched || _stagedInstaller is not { } installer) return;

        try
        {
            _installerLaunched = true;
            UpdateInstaller.Launch(installer, relaunch: false);
        }
        catch
        {
            // Nothing left to tell: the window is gone. The staged file survives and the banner
            // offers it again next launch, since the version check will still report an update.
        }
    }

#if EXTRACTION
    /// <summary>
    /// Extracts the live account from the game, checks it against the accounts already created by
    /// this uploader, and exports it to RSL Companion. The consolidated profile carries the in-game
    /// account id — the "handle" identity, deliberately distinct from the signed-in uploader — and
    /// the server create-or-updates the account keyed by that id: an existing account is refreshed,
    /// an unknown one is created. Afterwards the matching tile is highlighted and selected.
    /// </summary>
    private async Task ExportAccountAsync()
    {
        SetBusy(true, "export");
        Log("Reading your account from Raid — keep the game open until this finishes.");
        try
        {
            var profile = await ExtractProfileAsync();

            // A scan can return without throwing even when it read the wrong offsets — a bad
            // extraction looks like an empty or garbage account, not a crash. RSL Companion can't
            // tell "this account really has nothing" from "this reading is wrong", so catch it here
            // rather than shipping it.
            if (GameUserId(profile.AccountId) is not int || string.IsNullOrWhiteSpace(profile.Account.Name))
            {
                Log("Something looks wrong with the data read from Raid, so it wasn't sent to RSL "
                  + "Companion. If Raid just updated, try Help → Set up this Raid version, then "
                  + "Update user data again.");
                return;
            }

            var gameId = profile.AccountId;
            var gameName = string.IsNullOrWhiteSpace(profile.Account.Name) ? $"account {gameId}" : profile.Account.Name;
            // Counts, in the names the game uses on screen — they are the one thing here a player can
            // check against their own account, which is what makes them worth a plain-level line. The
            // in-game id and the resource tally are bookkeeping, so they go to the detail level.
            Log($"Read {gameName}'s account: {profile.Champions.Count} champions" +
                (ExportArtifacts
                    ? $", {profile.Artifacts.Count} pieces of gear and {profile.Accessories.Count} accessories."
                    : ". (Gear isn't included in this version.)"));
            Log($"accountId={gameId} resources={profile.Resources.Count} relics={profile.Relics.Count} "
              + $"gemstones={profile.Gemstones.Count} guardians={profile.FactionGuardians.Count}", detail: true);

            // The server derives an account's numeric UserId from this game accountId (parsed as a
            // uint), so that's how we recognise whether this game account is already registered —
            // keyed by the game handle, never by the signed-in uploader.
            int? gameUserId = GameUserId(gameId);
            // The export just read the game, so this IS the live account — record it even if the
            // detection loop never got a turn, otherwise the next tile reload clears the badge.
            if (gameUserId is int liveId)
            {
                _liveUserId = liveId;
                _liveName = gameName;
            }
            var match = gameUserId is int uid ? _loadedAccounts.FirstOrDefault(a => a.UserId == uid) : null;
            Log(match is not null
                ? $"Sending it to RSL Companion to update “{match.Name ?? gameName}”…"
                : $"Sending it to RSL Companion — this will add “{gameName}” to your profile…");
            // Stamp uploader-side provenance onto every export/update. These are the uploader's own
            // concern (not the shared extraction model), so they are injected here at serialization
            // time rather than baked into ConsolidatedProfile. The export just read the live game, so
            // read its build version now (cheap — the GameAssembly hash is memoized).
            var gameVersion = (_buildInfo ?? ExtractionService.TryGetGameBuild())?.GameVersion;
            var json = SerializeWithProvenance(profile, gameVersion);
            var result = await _api.UploadConsolidatedAsync(json);
            Log(result.Message);
            if (result.Detail is string detail) Log($"Sync response: {detail}", detail: true);

            // Refresh the tiles (a new account now exists). LoadAccountsAsync reconciles the live
            // account against them, so the exported one comes back highlighted as the in-game tile.
            if (result.Success)
                await LoadAccountsAsync();
        }
        catch (Exception ex)
        {
            Log($"Couldn't update your data: {DescribeExtractionFailure(ex)}");
            Log(ex.ToString(), detail: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Mirrors the RaidTools API (ConsolidatedJsonSyncAdapter): an account's numeric UserId is the
    // in-game accountId parsed as a uint. Lets us match the running game account to a registered tile.
    private static int? GameUserId(string? accountId)
        => uint.TryParse(accountId, out var u) ? unchecked((int)u) : null;

    /// <summary>
    /// Turns raw engine errors into something a user can act on — internals like "Offset discovery
    /// failed" mean nothing to them and read like a crash.
    /// </summary>
    private static string DescribeExtractionFailure(Exception ex)
    {
        if (ex.Message.Contains("Raid process not found", StringComparison.OrdinalIgnoreCase))
            return "Raid isn't running — start the game, wait for it to load, then try again.";

        // The engine can't tell "game hasn't finished loading" apart from "game update moved the
        // data around" — both end in discovery failure — so name both rather than guess.
        if (ex.Message.Contains("Offset discovery failed", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Failed to resolve UserContext", StringComparison.OrdinalIgnoreCase))
        {
            return "Couldn't read the account from the game. If Raid is still loading, wait for the "
                 + "roster to appear and try again. If it keeps failing, a recent game update may "
                 + "have changed the data format — file upload still works in the meantime.";
        }

        return ex.Message;
    }

    /// <summary>
    /// Turns a failed installer download into a sentence naming the likely cause. The user-level log
    /// line is all most people will read, and "IOException: The process cannot access the file" tells
    /// them nothing about what to do; the exception itself still goes out at the detail level.
    ///
    /// <para>Antivirus is called out by name because it is the one seen doing this here: security
    /// software opens or quarantines a freshly written executable, and the write or the rename that
    /// follows fails on a download that was otherwise complete and verified.</para>
    /// </summary>
    private static string DescribeDownloadFailure(Exception ex) => ex switch
    {
        HttpRequestException or TaskCanceledException =>
            "the download was interrupted — check your connection and try again.",
        UnauthorizedAccessException or IOException =>
            "Windows wouldn't let the file be saved. Antivirus scanning the finished installer is the "
          + "usual cause; trying again in a moment normally works.",
        InvalidOperationException => ex.Message + ".",
        _ => ex.Message,
    };

    // The export carries the account's ENTIRE artifact vault with real stats since 2026-08-02 —
    // gear in `artifacts[]`, rings/cloaks/banners in `accessories[]`, equipped and vaulted alike.
    //
    // It used to carry equipped ids with every stat field 0, on the belief that artifact stats had
    // moved to Unity ECS. They never did: the scan that "proved" the CachedArtifacts singleton was
    // gone tested one address per 4 KB page. See extraction/docs/artifact-findings.md.
    //
    // The cost of that vault is why this flag still exists: the first export in a game session
    // spends ~5 s locating the vault (~15 s the very first time a game build is seen), against ~4 s
    // for the rest of the snapshot. Later exports in the same session reuse the cached address.
    private const bool ExportArtifacts = true;

    /// <summary>
    /// Serializes an extracted payload and stamps the uploader-side <c>uploaderVersion</c> /
    /// <c>gameVersion</c> fields onto its top level, without touching the shared extraction models.
    /// <paramref name="gameVersion"/> is the live Raid build ("11.67.0"); null when it couldn't be
    /// read. <c>uploaderVersion</c> is this app's own build (the value shown in About).
    ///
    /// Kept because when a payload turns out to be wrong, the first question is always which uploader
    /// against which game build produced it — and the server's own triage log keys off both.
    /// </summary>
    private static string SerializeWithProvenance(object payload, string? gameVersion)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(payload, payload.GetType()))!.AsObject();
        node["uploaderVersion"] = AboutForm.DisplayVersion;
        node["gameVersion"] = gameVersion;
        return node.ToJsonString();
    }

    /// <summary>
    /// Takes one line of the engine's console output and decides what, if anything, the user sees.
    ///
    /// <para>Everything goes to the activity log as a <i>diagnostic</i> line — hidden unless the
    /// console's "Details" toggle is on. That output is written for debugging a memory map (klass
    /// addresses, offset chains, per-phase timings) and runs to hundreds of lines per export; shown
    /// to a player it reads as either an error or a crash in progress.</para>
    ///
    /// <para>What the user gets instead is the engine's own phase markers, translated. They are the
    /// one part of that stream that is genuinely about progress, and an export is slow enough
    /// (~5 s cold) that silence during it is worse than jargon. The phase names are stable
    /// identifiers in <c>ExtractionService</c>, not prose — an unrecognised one simply produces no
    /// plain line rather than leaking the raw name.</para>
    /// </summary>
    private void LogEngineLine(string raw)
    {
        Log(raw, detail: true);

        const string marker = ">>> PHASE START: ";
        int at = raw.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return;

        var phase = raw[(at + marker.Length)..].Trim();
        if (PhaseProgress.TryGetValue(phase, out var friendly)) Log(friendly);
    }

    /// <summary>
    /// Engine phase name → what that phase is doing, in the user's terms. Deliberately partial: the
    /// bookkeeping phases (loading the offset cache, loading the champion name catalog) finish in
    /// milliseconds and naming them adds noise without adding information.
    /// </summary>
    private static readonly Dictionary<string, string> PhaseProgress = new(StringComparer.OrdinalIgnoreCase)
    {
        ["attach"] = "Connecting to Raid…",
        ["calibrate-offsets"] = "First time on this Raid version — working out where the game keeps its data. This takes about a minute.",
        ["resolve-user"] = "Finding your account in the game…",
        ["extract-account"] = "Reading your profile…",
        ["extract-resources"] = "Reading your resources…",
        ["extract-heroes"] = "Reading your champions…",
        ["extract-faction-guardians"] = "Reading your Faction Guardians…",
        ["read-clan"] = "Reading your clan…",
        ["extract-artifacts"] = "Reading your gear and accessories — the slowest part, please wait…",
        ["extract-relics"] = "Reading your relics and gemstones…",
    };

    /// <summary>
    /// Runs the private extraction engine against the live Raid process on a background thread,
    /// mirroring its console diagnostics into the activity log. Backs "Update user data"; always
    /// pulls resources + champions, and artifacts when <see cref="ExportArtifacts"/> is enabled.
    ///
    /// <para>The <c>Console</c> redirect is process-wide, so it is restored in a finally. It is what
    /// makes the engine's own phase lines visible while a cold vault lookup runs for several
    /// seconds.</para>
    /// </summary>
    private Task<ConsolidatedProfile> ExtractProfileAsync()
    {
        string cachePath = Path.Combine(AppContext.BaseDirectory, "offsets_cache.json");
        return Task.Run(() =>
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var writer = new ConsoleLogWriter(LogEngineLine);
            Console.SetOut(writer);
            Console.SetError(writer);
            try
            {
                return ExtractionService.ExtractConsolidatedAsync(cachePath: cachePath, includeArtifacts: ExportArtifacts)
                                        .GetAwaiter().GetResult();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        });
    }
#endif

    /// <summary>
    /// Signs out, offering the stronger form as an explicit second option.
    ///
    /// <para>The two are genuinely different actions, which is why this asks rather than picking.
    /// A plain sign-out forgets the session on this PC. "Everywhere" additionally asks the server to
    /// revoke the Firebase refresh tokens — but Firebase revokes <b>per user, not per device</b>, so
    /// it signs the browser out too, and clears the embedded browser's stored site session. That is
    /// the right answer on a shared or lost machine and the wrong one on a laptop the user is about
    /// to sign back in on, and only they know which they are looking at.</para>
    /// </summary>
    private async void SignOut()
    {
        var everywhere = AskSignOutScope();
        if (everywhere is null) return; // cancelled

        if (everywhere.Value)
        {
            Log("Signing out everywhere…");
            if (await _api.RevokeSessionAsync())
                Log("Your sessions have been revoked, including the website's.");
            else
                Log("The server could not be reached, so other sessions may still be active. This device is signed out.");
        }

        await SessionManager.ForgetAsync();
        UserSettings.Current.SessionProtection = SessionProtection.None;
        UserSettings.Current.Save();

        _api.SignOut();
        _loadedAccounts.Clear();
        if (_sessionSecurityItem is not null) _sessionSecurityItem.Enabled = false;
#if EXTRACTION
        _shell.SetExportAvailable(false);
#endif
        // Drop straight to the signed-out UI in place rather than restarting the process; the
        // game-status poll keeps running, and the top bar shows the "Sign In" button again.
        _shell.SetSignedOut();
        // The tiles are gone, so nothing is "imported" any more — stop naming an account on the
        // helper link, which would otherwise select it for whoever signs in next in that browser.
        RefreshHelperUrl();
    }

    /// <summary>
    /// Asks which kind of sign-out this is. Returns true for "everywhere", false for this device
    /// only, or null if the user backed out.
    ///
    /// <para>The "everywhere" button states the browser consequence up front rather than in a
    /// follow-up confirmation: it is the surprising part, and a user who reads it after the fact has
    /// already been signed out of the site.</para>
    /// </summary>
    private bool? AskSignOutScope()
    {
        // Command links rather than plain buttons: the difference between these two is entirely in
        // the consequence, which needs a sentence, not a verb.
        var thisDevice = new TaskDialogCommandLinkButton(
            "Sign out", "Forget this session on this PC.");
        var everywhere = new TaskDialogCommandLinkButton(
            "Sign out everywhere",
            "Also revoke your other sessions. This signs you out of rslcompanion.com in your browser too.");

        var page = new TaskDialogPage
        {
            Caption = "Sign out",
            Heading = "Sign out of RSL Companion?",
            Text = "Your saved session on this PC will be deleted either way.",
            Icon = TaskDialogIcon.ShieldBlueBar,
            AllowCancel = true,
            Buttons = { thisDevice, everywhere, TaskDialogButton.Cancel },
            DefaultButton = thisDevice,
        };

        var clicked = TaskDialog.ShowDialog(this, page);
        if (clicked == everywhere) return true;
        if (clicked == thisDevice) return false;
        return null;
    }

    /// <summary>
    /// Marks the app busy. <paramref name="kind"/> ("export", or null for background work like an
    /// account reload) tells the page which button is the one running, so it can show progress on it
    /// instead of only greying everything out.
    /// </summary>
    private void SetBusy(bool busy, string? kind = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _shell.SetBusy(busy, kind);
    }

    // Marshals to the UI thread: the extraction engine logs from a background thread (see
    // ConsoleLogWriter), and the shell may only be touched on the UI thread.
    //
    // `detail: true` marks a line as diagnostic — the page keeps it but hides it unless the user turned
    // the console's "Details" toggle on. The default is the user-facing level, so an unmarked call is
    // one a player is meant to read; anything naming an offset, an address, a class or a phase belongs
    // on the other side of the flag.
    private void Log(string message, bool detail = false)
    {
        if (InvokeRequired) BeginInvoke(() => _shell.Log(message, detail));
        else _shell.Log(message, detail);
    }
}

/// <summary>
/// Bridges the extraction engine's <c>Console.WriteLine</c> diagnostics into the activity log.
/// Line-buffered so partial writes are not reported until a newline arrives.
/// </summary>
internal sealed class ConsoleLogWriter : TextWriter
{
    private readonly Action<string> _sink;
    private readonly StringBuilder _buffer = new();

    public ConsoleLogWriter(Action<string> sink) => _sink = sink;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            _sink(_buffer.ToString().TrimEnd('\r'));
            _buffer.Clear();
        }
        else
        {
            _buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (char c in value) Write(c);
    }

    public override void WriteLine(string? value)
    {
        if (_buffer.Length > 0)
        {
            _buffer.Append(value);
            _sink(_buffer.ToString().TrimEnd('\r'));
            _buffer.Clear();
        }
        else
        {
            _sink((value ?? string.Empty).TrimEnd('\r'));
        }
    }
}
