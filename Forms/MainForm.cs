using System.Diagnostics;
using System.Text;
#if EXTRACTION
using System.Text.Json;
using NewParserOpus;
using NewParserOpus.Il2Cpp;
using NewParserOpus.Models;
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
    private readonly FirebaseAuthClient _auth;
    private readonly RslCompanionApiClient _api;

    private readonly AppShell _shell = new() { Dock = DockStyle.Fill };

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
    /// Whether this is the newest uploader — null until the check succeeds. Gates the "report this
    /// game version" prompt, which only makes sense on the latest build: if the user is behind, the
    /// release they haven't installed may already cover their game, so prompting would generate
    /// reports for something already fixed. Unknown (offline, GitHub down) is treated as "don't
    /// prompt" — a wrong report costs more than a missed one.
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

    public MainForm(AppConfig config, FirebaseAuthClient auth, RslCompanionApiClient api)
    {
        _config = config;
        _auth = auth;
        _api = api;

        // The running version is in the title bar as well as Help → About: "which build am I on?"
        // is the first question in almost every support thread. "RSL Companion" itself is left out —
        // the WebView2 page's own top bar already shows that brand right underneath.
        Text = $"Uploader  v{AboutForm.DisplayVersion}";
        Icon = AppIcon.Value;
        Width = 1210;   // ~10% larger than the previous 1100×680 default
        Height = 748;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 480);
        // Open windowed (not maximized) and centered. The size below is generous enough that the
        // accounts grid fits at a glance on a typical display without claiming the whole screen;
        // the user can still maximize manually if they want the full work area.
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
#if EXTRACTION
        // Both export actions live on the shell's live tile; each reads the running game and routes
        // by the in-game id it finds there, regardless of which tile drove the label.
        _shell.ExportRequested += async () => await ExportAccountAsync();
        _shell.ExportClanRequested += async () => await ExportClanAsync();
        // Deferred for the same reason as SignIn below: this path can open a modal TaskDialog, and
        // doing that inside the WebView2 message handler crashes the host.
        _shell.ReportBuildRequested += () => BeginInvoke(new Action(ReportUncoveredBuild));
        // Export availability is gated on being signed in — set in EnterSignedInAsync, not here.
        FormClosed += (_, _) => _statusCts.Cancel(); // stop the poll touching a disposed form
#endif

        Load += async (_, _) =>
        {
            _shell.Start();
#if EXTRACTION
            ApplyGameState(GameState.NotRunning, force: true); // render a status before the first poll
            _ = PollGameStatusAsync(_statusCts.Token);
#endif
            // The game-status poll above runs regardless of sign-in. Everything account-related waits
            // until there is a session — the window opens signed-out and the user signs in from the
            // top bar (see SignIn).
            if (_api.IsAuthenticated)
                await EnterSignedInAsync();
            else
                _shell.SetSignedOut();
        };
    }

    /// <summary>
    /// Opens the browser sign-in splash and, on success, adopts the session and switches the UI into
    /// the signed-in state. Invoked from the shell's "Sign In" button.
    /// </summary>
    private async void SignIn()
    {
        using var dlg = new BrowserSignInForm(_config, _auth);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Session is null)
            return;

        // The browser held the foreground while the user signed in; pull this existing window back to
        // the front so the freshly signed-in state is what they see, not the leftover browser tab.
        BringToForeground();

        _api.SignIn(dlg.Session);
        Program.Persist(dlg.Session, dlg.RememberMe);
        await EnterSignedInAsync();
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
#if EXTRACTION
        _shell.SetExportAvailable(true);
#endif
        await LoadAccountsAsync();

        // Packaged (MSIX/Store) builds get updates via the Store or App Installer instead of this
        // GitHub-release poll, so the banner/menu item would be confusing there.
        if (!PackagedAppInfo.IsPackaged)
            _ = CheckForUpdateAsync(silent: true);
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
        Log("This game version isn't in the shipped memory map yet — working it out from the running "
          + "game. This takes about a minute, happens once per game update, and only needs doing "
          + "while Raid is fully loaded.");

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
                Log($"Calibration succeeded — identified {result.Name ?? "the account"} (#{result.AccountId}). "
                  + "This game version won't need calibrating again on this PC.");
                if (result.ExportedCatalogPath is string p)
                    Log($"Saved to {p} — sending that file to RSL Companion gets this game version "
                      + "recognised out of the box for everyone in the next release.");
            }
            else
            {
                Log($"Calibration didn't succeed: {result.Error}. If Raid was still loading, wait for "
                  + "the roster to appear and use Help → Recalibrate for this game version.");
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing or game gone — nothing to report.
        }
        catch (Exception ex)
        {
            Log($"Calibration failed: {DescribeExtractionFailure(ex)}");
        }
        finally
        {
            _calibrating = false;
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
    /// Shows the banner for a game build nothing on this PC covers yet.
    ///
    /// Only when this is the newest uploader — on an older build the release the user hasn't
    /// installed may already carry the map. A build the user has already certified or calibrated is
    /// covered locally even though the shipped catalog still doesn't know it, and must not keep
    /// prompting.
    /// </summary>
    private void UpdateReportPrompt()
    {
        bool show = _buildInfo is { CoveredByShippedCatalog: false } b
                 && _isLatestUploader == true
                 && !BuildCertification.HasLocalMap(b.GameAssemblyHash);
        if (show)
        {
            _shell.SetReport($"Raid {BuildLabel(_buildInfo!)} isn't covered by this release yet — " +
                             "click to check for a compatible memory map");
        }
        else
        {
            _shell.SetReport(null);
        }
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
    /// The banner's action: ask the server for a map for this build, and fall back to deriving one
    /// locally. Ordered that way because a download is a second or two against a ~50 s scan.
    /// </summary>
    private async void ReportUncoveredBuild()
    {
        if (!RaidProcess.IsRunning())
        {
            Log("Start Raid and let it load to the roster first, then try again.");
            return;
        }

        if (await TryCertifyBuildAsync(_statusCts.Token, force: true)) return;

        await TrySelfCalibrateAsync(
            Path.Combine(AppContext.BaseDirectory, "offsets_cache.json"),
            _statusCts.Token,
            force: true);
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
            Log("Skipped the compatibility check — the memory map will be worked out locally instead.");
            return false;
        }

        var label = BuildLabel(build);
        Log($"Checking whether RSL Companion has a memory map for Raid {label}…");

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
            Log($"Compatibility check couldn't reach RSL Companion: {ex.Message}");
            return false;
        }

        if (response.Status == CertificationStatus.NotPublished)
        {
            Log($"RSL Companion has no map for Raid {label} yet.");
            return false;
        }

        if (response.Status == CertificationStatus.Failed)
        {
            Log(response.Error ?? "Compatibility check failed.");
            return false;
        }

        var applied = BuildCertification.Apply(response.Body!, build.GameAssemblyHash, AboutForm.DisplayVersion);
        switch (applied.Outcome)
        {
            case BuildCertification.Outcome.Applied:
                Log($"Compatible map installed — {applied.Message} Reading the account now.");
                // Let the next poll re-probe against the map that now exists rather than asserting a
                // state from here.
                _gameState = GameState.NotRunning;
                RefreshBuildInfo();
                return true;

            case BuildCertification.Outcome.NeedsNewerUploader:
                Log($"A map for Raid {label} exists, but {applied.Message} " +
                    "Use Help → Check for updates, then try again.");
                return false;

            default:
                Log($"Couldn't use the published map: {applied.Message}");
                return false;
        }
    }

    /// <summary>
    /// The consent prompt. A native TaskDialog rather than a page banner because it carries the
    /// "don't ask again" checkbox, and because it is asking permission to send the build the user is
    /// running to the server — that is a question, not a notification.
    /// </summary>
    private bool AskToCheckCompatibility(ExtractionService.GameBuildInfo build)
    {
        var check = new TaskDialogButton("Check compatibility");
        var page = new TaskDialogPage
        {
            Caption = "RSL Companion",
            Heading = $"Raid {BuildLabel(build)} isn't certified yet",
            Text = "This release doesn't ship a memory map for the game version you're running. "
                 + "RSL Companion may already have one — checking sends this game build's identifier "
                 + "and installs the map if it fits, which takes a moment instead of the minute-long "
                 + "local scan.",
            Icon = TaskDialogIcon.Information,
            Buttons = { check, TaskDialogButton.Cancel },
            DefaultButton = check,
            Verification = new TaskDialogVerificationCheckBox("Check automatically from now on"),
        };

        var clicked = TaskDialog.ShowDialog(this, page);

        // Recorded on consent only: ticking the box and cancelling is a refusal, not a standing yes.
        if (clicked == check && page.Verification!.Checked)
        {
            UserSettings.Current.AutoCheckBuildCertification = true;
            UserSettings.Current.Save();
            Log("Future game updates will be checked for a compatible memory map automatically.");
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
                ("calibrating", "New game version — mapping memory (about a minute)…"),
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
        // The entire UI is the WebView2 shell; the form only adds the native menu strip on top.
        Controls.Add(_shell);

        // Added after the Fill control on purpose: WinForms resolves docking from the highest child
        // index down, so the menu must be last to claim the top strip before the shell fills.
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
        }
#if EXTRACTION
        // The manual retry for a build whose automatic attempt ran too early (game still loading).
        help.DropDownItems.Add(new ToolStripMenuItem("&Recalibrate for this game version", null,
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

    private async Task LoadAccountsAsync()
    {
        SetBusy(true);
        Log("Loading your accounts…");
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
                    a.HeroCount, a.ArtifactCount, FormatLastSync(a.LastSyncDate)))
                .ToList());

            Log(_loadedAccounts.Count > 0
                ? $"Loaded {_loadedAccounts.Count} account(s)."
                : "No accounts in your profile yet — open Raid and click Export account to create one.");
        }
        catch (Exception ex)
        {
            Log("Failed to load accounts: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
#if EXTRACTION
            // The tiles just changed, so the live account's imported/new status may have too.
            ReconcileLiveAccount();
#endif
        }
    }

    /// <summary>Friendly, local-time "last synced" label for a tile, or null when unknown.</summary>
    private static string? FormatLastSync(DateTimeOffset? when)
    {
        if (when is not { } dt) return null;
        var local = dt.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age < TimeSpan.Zero) return local.ToString("MMM d, yyyy");     // clock skew — show the date
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (local.Date == DateTimeOffset.Now.Date) return $"today at {local:h:mm tt}";
        if (local.Date == DateTimeOffset.Now.Date.AddDays(-1)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} days ago";
        return local.ToString("MMM d, yyyy");
    }

    /// <summary>
    /// On startup (<paramref name="silent"/> = true) failures and "already up to date" are not
    /// reported — only a real update lights up the banner. A manual click always logs the outcome.
    /// </summary>
    private async Task CheckForUpdateAsync(bool silent)
    {
        try
        {
            var result = await UpdateChecker.CheckForUpdateAsync();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    _shell.SetUpdate(result.Info!.Version.ToString(), result.Info.ReleaseUrl);
                    _isLatestUploader = false;
                    if (!silent) Log($"Update available: {result.Info.Version}.");
                    break;
                case UpdateCheckStatus.UpToDate:
                    _shell.SetUpdate(null, null);
                    _isLatestUploader = true;
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
        Log("Reading the running Raid account… make sure the game is open and loaded.");
        try
        {
            var profile = await ExtractProfileAsync();

            var gameId = profile.AccountId;
            var gameName = string.IsNullOrWhiteSpace(profile.Account.Name) ? $"account {gameId}" : profile.Account.Name;
            Log($"Extracted {gameName} (game ID {gameId}): {profile.Resources.Count} resources and {profile.Heroes.Count} champions" +
                (ExportArtifacts
                    ? $", {profile.Artifacts.Count} artifacts and {profile.Accessories.Count} accessories."
                    : ". (Artifacts: not yet available from the game — will be included in a future update.)"));

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
                ? $"This game account is already registered as “{match.Name ?? gameName}” — updating it."
                : "This game account isn't registered yet — a new account will be created for it.");

            Log("Exporting to RSL Companion…");
            // Stamp uploader-side provenance onto every export/update. These are the uploader's own
            // concern (not the shared extraction model), so they are injected here at serialization
            // time rather than baked into ConsolidatedProfile. The export just read the live game, so
            // read its build version now (cheap — the GameAssembly hash is memoized).
            var gameVersion = (_buildInfo ?? ExtractionService.TryGetGameBuild())?.GameVersion;
            var json = SerializeWithProvenance(profile, gameVersion);
            var result = await _api.UploadConsolidatedAsync(json);
            Log(result.Message);

            // Refresh the tiles (a new account now exists). LoadAccountsAsync reconciles the live
            // account against them, so the exported one comes back highlighted as the in-game tile.
            if (result.Success)
                await LoadAccountsAsync();
        }
        catch (Exception ex)
        {
            Log($"Export account failed: {DescribeExtractionFailure(ex)}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Exports the running account's clan — the record, and the roster with each member's display
    /// name — to the separate clan sync endpoint. Self-identifying in the same way as the account
    /// export: the payload carries the in-game <c>accountId</c> and the server routes by it.
    ///
    /// <para>Its own action because it is its own cost. Neither object it needs is reachable from
    /// the game's account data, so the engine finds both by scanning the whole process: 18–31 s the
    /// first time in a game session, ~7 s after, against ~4 s for the entire account snapshot.
    /// Folding that into "Update user data" would have made the routine export seven times slower
    /// for data most users want occasionally.</para>
    /// </summary>
    private async Task ExportClanAsync()
    {
        SetBusy(true, "clan");
        Log("Reading your clan from the running game. This searches the game's memory and can take "
          + "up to a minute — the game keeps no direct route to the clan roster.");
        try
        {
            var profile = await ExtractClanProfileAsync();

            if (profile.Clan is null)
            {
                // Not a failure: an account in no clan, or a client that hasn't cached the record,
                // both land here. Posting an empty roster would be worse than posting nothing.
                Log("No clan found for this account. If you are in one, open the in-game Clan screen "
                  + "so the game loads it, then try again.");
                return;
            }

            var clan = profile.Clan;
            int named = clan.Members.Count(m => !string.IsNullOrEmpty(m.Name));
            Log($"Found {clan.Name} (clan ID {clan.Id}) — {clan.Members.Count} members, {named} with names.");

            Log("Exporting the clan to RSL Companion…");
            var gameVersion = (_buildInfo ?? ExtractionService.TryGetGameBuild())?.GameVersion;
            var result = await _api.UploadClanAsync(SerializeWithProvenance(profile, gameVersion));
            Log(result.Message);

            // The clan name is shown on the tiles, so a successful export can change what they say.
            if (result.Success)
                await LoadAccountsAsync();
        }
        catch (Exception ex)
        {
            Log($"Clan export failed: {DescribeExtractionFailure(ex)}");
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
    /// Both sync payloads (consolidated and clan) carry the same two provenance fields, which is why
    /// this takes an object: when a payload turns out to be wrong, the first question is always which
    /// uploader against which game build produced it.
    /// </summary>
    private static string SerializeWithProvenance(object payload, string? gameVersion)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(payload, payload.GetType()))!.AsObject();
        node["uploaderVersion"] = AboutForm.DisplayVersion;
        node["gameVersion"] = gameVersion;
        return node.ToJsonString();
    }

    /// <summary>
    /// Runs the private extraction engine against the live Raid process on a background thread,
    /// mirroring its console diagnostics into the activity log. Backs "Update user data"; always
    /// pulls resources + champions, and artifacts when <see cref="ExportArtifacts"/> is enabled.
    /// </summary>
    private Task<ConsolidatedProfile> ExtractProfileAsync() =>
        RunEngineAsync(cachePath =>
            ExtractionService.ExtractConsolidatedAsync(cachePath: cachePath, includeArtifacts: ExportArtifacts)
                             .GetAwaiter().GetResult());

    /// <summary>Backs "Export clan" — the slow, scan-based clan export. See <see cref="ExportClanAsync"/>.</summary>
    private Task<ClanProfile> ExtractClanProfileAsync() =>
        RunEngineAsync(cachePath =>
            ExtractionService.ExtractClanAsync(cachePath: cachePath).GetAwaiter().GetResult());

    /// <summary>
    /// Runs one engine entry point off the UI thread with its <c>Console</c> output redirected into
    /// the activity log. The redirect is process-wide, so it is restored in a finally — and it is
    /// what makes the clan export's progress lines visible while it scans.
    /// </summary>
    private Task<T> RunEngineAsync<T>(Func<string, T> extract)
    {
        string cachePath = Path.Combine(AppContext.BaseDirectory, "offsets_cache.json");
        return Task.Run(() =>
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var writer = new ConsoleLogWriter(Log);
            Console.SetOut(writer);
            Console.SetError(writer);
            try
            {
                return extract(cachePath);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        });
    }
#endif

    private void SignOut()
    {
        CredentialStore.ClearSession();
        _api.SignOut();
        _loadedAccounts.Clear();
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
    /// Marks the app busy. <paramref name="kind"/> ("export" / "clan") tells the page which button
    /// is the one running, so it can show progress there instead of only greying everything out —
    /// which matters for the clan export, where nothing visibly happens for up to a minute.
    /// </summary>
    private void SetBusy(bool busy, string? kind = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _shell.SetBusy(busy, kind);
    }

    // Marshals to the UI thread: the extraction engine logs from a background thread (see
    // ConsoleLogWriter), and the shell may only be touched on the UI thread.
    private void Log(string message)
    {
        if (InvokeRequired) BeginInvoke(() => _shell.Log(message));
        else _shell.Log(message);
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
