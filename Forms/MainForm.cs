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

namespace RslCompanionUploader.Forms;

/// <summary>
/// Main window: shows who is signed in, the user's uploader-created accounts as clickable tiles
/// (right pane), and the upload/export actions (left pane). File uploads target the tile the user
/// selects; "Export account" reads the running game and create-or-updates the matching account,
/// highlighting its tile.
/// </summary>
public sealed class MainForm : Form
{
    // Registered accounts are shown only when their last sync came from this desktop app's
    // consolidated export (the extractor) — see SyncMethod.ConsolidatedJson in the RaidTools API.
    private const string UploaderSyncMethod = "ConsolidatedJson";

    private readonly AppConfig _config;
    private readonly RslCompanionApiClient _api;

    private readonly Label _user = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    private readonly LinkLabel _updateBanner = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 0, 0, 10) };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _checkUpdates = new() { Text = "Check for updates", AutoSize = true };
    private readonly Button _uploadResources = new() { Text = "Upload account resources", Height = 44 };
    private readonly Button _uploadChampions = new() { Text = "Upload champions", Height = 44 };
    private readonly Button _exportAccount = new() { Text = "Export account to RSL Companion", Height = 44 };
    private readonly Label _raidStatus = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 0, 0, 8) };
    private readonly LinkLabel _reportBuild = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 0, 0, 8) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly AccountsPanel _accountsPanel = new() { Dock = DockStyle.Fill };
    private SplitContainer _split = null!;

    // The accounts currently shown as tiles, and the one the user has selected (upload target).
    private List<AccountSummary> _loadedAccounts = new();
    private AccountSummary? _selected;

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

    // Stops the status poll when the window closes.
    private readonly CancellationTokenSource _statusCts = new();
#endif

    public MainForm(AppConfig config, RslCompanionApiClient api)
    {
        _config = config;
        _api = api;

        // The running version is in the title bar as well as Help → About: "which build am I on?"
        // is the first question in almost every support thread.
        Text = $"RSL Companion — Uploader  v{AboutForm.DisplayVersion}";
        Icon = AppIcon.Value;
        Width = 1000;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 480);
        Font = new Font("Segoe UI", 9.5f);

        BuildLayout();

        _refresh.Click += async (_, _) => await LoadAccountsAsync();
        _uploadResources.Click += async (_, _) => await UploadAsync(isResources: true);
        _uploadChampions.Click += async (_, _) => await UploadAsync(isResources: false);
#if EXTRACTION
        _exportAccount.Click += async (_, _) => await ExportAccountAsync();
        _reportBuild.LinkClicked += (_, _) => ReportUncoveredBuild();
        _raidStatus.Visible = true;
        FormClosed += (_, _) => _statusCts.Cancel(); // stop the poll touching a disposed form
#else
        // Built without the private extraction engine (public clone) — game extraction unavailable.
        _exportAccount.Visible = false;
#endif
        _accountsPanel.AccountSelected += userId =>
        {
            _selected = _loadedAccounts.FirstOrDefault(a => a.UserId == userId);
            UpdateButtonState();
        };
        _updateBanner.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo(_updateBanner.Tag as string ?? "https://get.rslcompanion.com") { UseShellExecute = true });
        _checkUpdates.Click += async (_, _) => await CheckForUpdateAsync(silent: false);

        Load += async (_, _) =>
        {
            // Apply the pane min-sizes and split position now that the form has a real width. Doing
            // this in BuildLayout throws (SplitterDistance out of range) because the container is
            // still at its tiny default size there. Guarded so no layout edge case can crash the app.
            try
            {
                _split.Panel1MinSize = 380;
                _split.Panel2MinSize = 280;
                _split.SplitterDistance = (int)(_split.Width * 0.55);
            }
            catch { /* window too small for these constraints — keep defaults, never crash */ }
            _accountsPanel.Start();
#if EXTRACTION
            ApplyGameState(GameState.NotRunning, force: true); // render a status before the first poll
            _ = PollGameStatusAsync(_statusCts.Token);
#endif

            var who = _api.Session.DisplayName ?? _api.Session.Email ?? _api.Session.Uid ?? "signed in";
            _user.Text = $"Signed in as {who}";
            await LoadAccountsAsync();
            _ = CheckForUpdateAsync(silent: true);
        };
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
    /// Offers to report a game build the shipped catalog doesn't cover.
    ///
    /// Shown only when this is the newest uploader — on an older build the fix may already be
    /// published — and only once the game build is actually identified. Deliberately a prompt rather
    /// than an automatic report: nothing is transmitted until the user reviews and submits it.
    /// </summary>
    private void UpdateReportPrompt()
    {
        bool uncovered = _buildInfo is { CoveredByShippedCatalog: false };
        _reportBuild.Visible = uncovered && _isLatestUploader == true;

        if (_reportBuild.Visible)
        {
            var label = _buildInfo!.GameVersion is string v && v.Length > 0 ? v : _buildInfo.GameAssemblyHash[..12];
            _reportBuild.Text = $"Raid {label} isn't covered by this release yet — tell RSL Companion about it";
        }
    }

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
    /// Opens a pre-filled report for the user to review and submit, and reveals the calibration file
    /// that makes the build reproducible on our side. Sends nothing by itself.
    /// </summary>
    private void ReportUncoveredBuild()
    {
        if (_buildInfo is not { } build) return;

        var title = $"Game build not covered: Raid {build.GameVersion ?? build.GameAssemblyHash[..12]}";
        var body =
            $"Uploader version: {AboutForm.DisplayVersion}\n" +
            $"Game version: {build.GameVersion ?? "(unknown)"}\n" +
            $"GameAssembly SHA-256: {build.GameAssemblyHash}\n\n" +
            "Attaching calibrated-offsets.json from this PC (opened in Explorer) lets this build " +
            "ship recognised in the next release.";

        OpenUrl("https://github.com/rslcompanion/RslCompanionUploader/issues/new" +
                $"?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}");

        // Reveal, don't attach: the user decides what leaves their machine.
        var catalog = KnownOffsets.LocalCatalogPath;
        if (File.Exists(catalog))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{catalog}\"") { UseShellExecute = true }); }
            catch { Log($"Calibration file is at {catalog}"); }
        }
        else
        {
            Log("No local calibration yet — run Help → Recalibrate for this game version first, "
              + "then report again so the offsets can be included.");
        }
    }

    /// <summary>
    /// Renders <paramref name="state"/> into the status line, and logs only on a real transition so
    /// a 5-second poll doesn't fill the activity log with the same line over and over.
    /// </summary>
    private void ApplyGameState(GameState state, string? detail = null, bool force = false)
    {
        var previous = _gameState;
        _gameState = state;

        (string text, Color colour) = state switch
        {
            GameState.Connected =>
                ($"🟢  Connected to Raid — {_liveName} (#{_liveUserId})", Color.ForestGreen),
            GameState.Loading =>
                ("🟡  Raid is running — waiting for account data…", Color.DarkGoldenrod),
            GameState.Calibrating =>
                ("🔵  New game version — working out the memory map (about a minute)…", Color.RoyalBlue),
            GameState.NeedsCalibration =>
                ("🟠  Raid is running — account can't be identified", Color.DarkOrange),
            _ =>
                ("⚪  Raid not running — start the game to export", Color.Gray),
        };
        _raidStatus.Text = text;
        _raidStatus.ForeColor = colour;

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
            _accountsPanel.SetDetectedAccount(null, null);
            _accountsPanel.SetIdentified(null);
            return;
        }

        if (_loadedAccounts.Any(a => a.UserId == uid))
        {
            _accountsPanel.SetDetectedAccount(null, null);
            _accountsPanel.SetIdentified(uid);
        }
        else
        {
            _accountsPanel.SetIdentified(null);
            _accountsPanel.SetDetectedAccount(uid, _liveName);
        }
    }
#endif

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // update banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // header (user + actions)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Raid connection status
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // uncovered-build report prompt
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // file-upload buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // export-account button
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // log label
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log

        root.Controls.Add(_updateBanner);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _user.Anchor = AnchorStyles.Left;
        _user.Margin = new Padding(0, 6, 0, 0);
        _refresh.Anchor = AnchorStyles.Right;
        _refresh.Margin = new Padding(0, 0, 8, 0);
        _checkUpdates.Anchor = AnchorStyles.Right;
        _checkUpdates.Margin = new Padding(0, 0, 8, 0);
        var signOut = new Button { Text = "Sign out", AutoSize = true, Anchor = AnchorStyles.Right };
        signOut.Click += (_, _) => SignOut();
        header.Controls.Add(_user, 0, 0);
        header.Controls.Add(_refresh, 1, 0);
        header.Controls.Add(_checkUpdates, 2, 0);
        header.Controls.Add(signOut, 3, 0);
        root.Controls.Add(header);

        root.Controls.Add(_raidStatus);  // hidden in public builds; driven by the status poll under EXTRACTION
        root.Controls.Add(_reportBuild); // shown only for a game build this release doesn't cover

        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12) };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _uploadResources.Dock = DockStyle.Fill;
        _uploadResources.Margin = new Padding(0, 0, 6, 0);
        _uploadChampions.Dock = DockStyle.Fill;
        _uploadChampions.Margin = new Padding(6, 0, 0, 0);
        buttonRow.Controls.Add(_uploadResources, 0, 0);
        buttonRow.Controls.Add(_uploadChampions, 1, 0);
        root.Controls.Add(buttonRow);

        _exportAccount.Dock = DockStyle.Fill;
        _exportAccount.Margin = new Padding(0, 0, 0, 12);
        _exportAccount.Font = new Font(Font, FontStyle.Bold);
        root.Controls.Add(_exportAccount);

        root.Controls.Add(new Label { Text = "Activity", AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
        root.Controls.Add(_log);

        // Left = actions (upload/export) + activity log; right = the account tiles picker.
        // Min-sizes are deliberately NOT set here — a SplitContainer at its default width can't
        // satisfy 380+280 and set_SplitterDistance throws. They're applied in Load (see ctor) once
        // the form has a real width.
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        root.Dock = DockStyle.Fill;
        _split.Panel1.Controls.Add(root);
        _split.Panel2.Controls.Add(_accountsPanel);
        Controls.Add(_split);

        // Added after the Fill control on purpose: WinForms resolves docking from the highest
        // child index down, so the menu must be last to claim the top strip before the splitter fills.
        var menu = BuildMenu();
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private MenuStrip BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        var signOutItem = new ToolStripMenuItem("Sign &out", null, (_, _) => SignOut());
        var exitItem = new ToolStripMenuItem("&Close", null, (_, _) => Close()) { ShortcutKeys = Keys.Alt | Keys.F4 };
        file.DropDownItems.Add(signOutItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(exitItem);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("Check for &updates…", null,
            async (_, _) => await CheckForUpdateAsync(silent: false)));
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
        help.DropDownItems.Add(new ToolStripMenuItem("Open rslcompanion.com", null,
            (_, _) => OpenUrl(_config.FrontendUrl)));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) =>
        {
            using var about = new AboutForm(_config);
            about.ShowDialog(this);
        }));

        var menu = new MenuStrip { Dock = DockStyle.Top };
        menu.Items.Add(file);
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

            // Drop a stale selection, then render the tiles.
            if (_selected is not null && _loadedAccounts.All(a => a.UserId != _selected.UserId))
                _selected = null;

            _accountsPanel.SetAccounts(_loadedAccounts
                .Select(a => new AccountsPanel.Tile(a.UserId, a.Name ?? $"Account {a.UserId}", a.ClanName, a.HeroCount, a.ArtifactCount))
                .ToList());
            _accountsPanel.SetSelected(_selected?.UserId);

            Log(_loadedAccounts.Count > 0
                ? $"Loaded {_loadedAccounts.Count} account(s). Pick one on the right to upload a file to it."
                : "No uploader accounts yet — open Raid and click Export account to create one.");
        }
        catch (Exception ex)
        {
            Log("Failed to load accounts: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            UpdateButtonState();
#if EXTRACTION
            // The tiles just changed, so the live account's imported/new status may have too.
            ReconcileLiveAccount();
#endif
        }
    }

    /// <summary>
    /// On startup (<paramref name="silent"/> = true) failures and "already up to date" are not
    /// reported — only a real update lights up the banner. A manual click always logs the outcome.
    /// </summary>
    private async Task CheckForUpdateAsync(bool silent)
    {
        _checkUpdates.Enabled = false;
        try
        {
            var result = await UpdateChecker.CheckForUpdateAsync();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    _updateBanner.Text = $"A new version ({result.Info!.Version}) is available — click to download";
                    _updateBanner.Tag = result.Info.ReleaseUrl;
                    _updateBanner.Visible = true;
                    _isLatestUploader = false;
                    if (!silent) Log($"Update available: {result.Info.Version}.");
                    break;
                case UpdateCheckStatus.UpToDate:
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
            _checkUpdates.Enabled = true;
#if EXTRACTION
            // The prompt is gated on being the latest uploader, which is only known once this
            // finishes — it runs asynchronously well after the first status poll.
            UpdateReportPrompt();
#endif
        }
    }

    private async Task UploadAsync(bool isResources)
    {
        if (_selected is not AccountSummary account)
        {
            Log("Pick an account tile on the right first.");
            return;
        }

        var kind = isResources ? "resources" : "champions";
        using var dialog = new OpenFileDialog
        {
            Title = $"Choose the {kind} JSON to upload for {account.Name}",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log($"Could not read file: {ex.Message}");
            return;
        }

        SetBusy(true);
        Log($"Uploading {kind} for {account.Name} (#{account.UserId}) from {Path.GetFileName(dialog.FileName)}…");
        try
        {
            var profileName = account.Name ?? $"Account {account.UserId}";
            var result = isResources
                ? await _api.UploadResourcesAsync(account.UserId, profileName, json)
                : await _api.UploadChampionsAsync(account.UserId, profileName, json);
            Log(result.Message);
        }
        catch (Exception ex)
        {
            Log($"Upload error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
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
        SetBusy(true);
        Log("Reading the running Raid account… make sure the game is open and loaded.");
        try
        {
            var profile = await ExtractProfileAsync();

            var gameId = profile.AccountId;
            var gameName = string.IsNullOrWhiteSpace(profile.Account.Name) ? $"account {gameId}" : profile.Account.Name;
            Log($"Extracted {gameName} (game ID {gameId}): {profile.Resources.Count} resources and {profile.Heroes.Count} champions" +
                (ExportArtifacts
                    ? $" and {profile.Artifacts.Count} artifacts."
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
            var json = JsonSerializer.Serialize(profile);
            var result = await _api.UploadConsolidatedAsync(json);
            Log(result.Message);

            if (result.Success)
            {
                // Refresh the tiles (a new account now exists), then light up and select this one.
                await LoadAccountsAsync();
                // LoadAccountsAsync already reconciled the live account against the refreshed tiles
                // (it's imported now, so it shows the "In game" badge rather than "new"); all that's
                // left is to select it as the upload target.
                if (gameUserId is int id2 && _loadedAccounts.Any(a => a.UserId == id2))
                {
                    _selected = _loadedAccounts.First(a => a.UserId == id2);
                    _accountsPanel.SetSelected(id2);
                    UpdateButtonState();
                }
            }
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

    // Artifacts are only partially recoverable in the current game build: equipped artifact ids
    // exist, but their stats moved to Unity ECS storage the engine can't decode yet (see
    // extraction/CLAUDE.md). Until upstream ECS decoding lands we skip the futile artifact scan.
    // Flip this to true to include artifacts — the consolidated profile already carries an
    // "artifacts" slice and the export path posts the whole profile, so nothing else changes.
    private const bool ExportArtifacts = false;

    /// <summary>
    /// Runs the private extraction engine against the live Raid process on a background thread,
    /// mirroring its console diagnostics into the activity log. Shared by "Sync from game" and
    /// "Export account"; always pulls resources + champions, and artifacts when
    /// <see cref="ExportArtifacts"/> is enabled.
    /// </summary>
    private Task<ConsolidatedProfile> ExtractProfileAsync()
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

    private void SignOut()
    {
        CredentialStore.ClearSession();
        Application.Restart();
    }

    private void UpdateButtonState()
    {
        var hasAccount = _selected is not null;
        _uploadResources.Enabled = hasAccount;
        _uploadChampions.Enabled = hasAccount;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _refresh.Enabled = !busy;
        _exportAccount.Enabled = !busy;
        if (busy)
        {
            _uploadResources.Enabled = false;
            _uploadChampions.Enabled = false;
        }
        else
        {
            UpdateButtonState();
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (_log.InvokeRequired) _log.BeginInvoke(() => _log.AppendText(line));
        else _log.AppendText(line);
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
