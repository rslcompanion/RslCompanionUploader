using System.Diagnostics;
using System.Text;
#if EXTRACTION
using System.Text.Json;
using NewParserOpus;
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
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly AccountsPanel _accountsPanel = new() { Dock = DockStyle.Fill };
    private SplitContainer _split = null!;

#if EXTRACTION
    // Watches for the Raid process so we can show a live connected/not-connected status.
    private readonly RaidProcessMonitor _raidMonitor = new();
#endif

    // The accounts currently shown as tiles, and the one the user has selected (upload target).
    private List<AccountSummary> _loadedAccounts = new();
    private AccountSummary? _selected;

    // Guards so live-account detection never runs twice, or while an upload/export owns the process.
    private bool _busy;
    private bool _detecting;

    public MainForm(AppConfig config, RslCompanionApiClient api)
    {
        _config = config;
        _api = api;

        Text = "RSL Companion — Uploader";
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
        _raidStatus.Visible = true;
        _raidMonitor.ConnectionChanged += UpdateRaidStatus;
        FormClosed += (_, _) => _raidMonitor.Dispose();
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
            _raidMonitor.Start(); // reports current state immediately, then polls
#endif

            var who = _api.Session.DisplayName ?? _api.Session.Email ?? _api.Session.Uid ?? "signed in";
            _user.Text = $"Signed in as {who}";
            await LoadAccountsAsync();
            _ = CheckForUpdateAsync(silent: true);
        };
    }

#if EXTRACTION
    /// <summary>Reflects the live Raid connection state, and reconciles the live account on connect.</summary>
    private void UpdateRaidStatus(bool connected)
    {
        _raidStatus.Text = connected ? "🟢  Connected to Raid" : "⚪  Raid not running — start the game to export";
        _raidStatus.ForeColor = connected ? Color.ForestGreen : Color.Gray;

        if (connected)
        {
            _ = DetectLiveAccountAsync();
        }
        else
        {
            // Game closed — nothing is live any more.
            _accountsPanel.SetDetectedAccount(null, null);
            _accountsPanel.SetIdentified(null);
        }
    }

    /// <summary>
    /// Reads just the account identity from the running game (no resources/champions) and reconciles
    /// it with the imported tiles: an already-imported account gets the "In game" badge, an unknown
    /// one is surfaced as a "new account detected" tile. Detection only — importing it is a separate
    /// action. Retries briefly because the game may still be loading when the process appears.
    /// </summary>
    private async Task DetectLiveAccountAsync()
    {
        if (_detecting || _busy) return;
        _detecting = true;
        try
        {
            const int attempts = 3;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    var cachePath = Path.Combine(AppContext.BaseDirectory, "offsets_cache.json");
                    var account = await Task.Run(() =>
                        ExtractionService.ExtractAccountAsync(cachePath: cachePath).GetAwaiter().GetResult());

                    if (GameUserId(account.AccountId) is not int uid) return;
                    var name = string.IsNullOrWhiteSpace(account.Name) ? $"Account {uid}" : account.Name;

                    if (_loadedAccounts.Any(a => a.UserId == uid))
                    {
                        _accountsPanel.SetDetectedAccount(null, null);
                        _accountsPanel.SetIdentified(uid);
                        Log($"Playing as {name} (#{uid}) — already imported.");
                    }
                    else
                    {
                        _accountsPanel.SetIdentified(null);
                        _accountsPanel.SetDetectedAccount(uid, name);
                        Log($"New account detected: {name} (#{uid}) — not imported yet.");
                    }
                    return;
                }
                catch (Exception ex)
                {
                    // A game update changing the data format won't fix itself on retry — report now.
                    if (IsUnsupportedGameVersion(ex))
                    {
                        Log(DescribeExtractionFailure(ex));
                        return;
                    }
                    // The game is often still loading right after the process appears; give it time.
                    if (attempt == attempts)
                    {
                        Log($"Couldn't read the live account: {DescribeExtractionFailure(ex)}");
                        return;
                    }
                    await Task.Delay(5000);
                    if (!_raidMonitor.IsConnected) return; // game closed while we waited
                }
            }
        }
        finally
        {
            _detecting = false;
        }
    }
#endif

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // update banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // header (user + actions)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Raid connection status
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

        root.Controls.Add(_raidStatus); // hidden in public builds; driven by RaidProcessMonitor under EXTRACTION

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
                    if (!silent) Log($"Update available: {result.Info.Version}.");
                    break;
                case UpdateCheckStatus.UpToDate:
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
                if (gameUserId is int id2 && _loadedAccounts.Any(a => a.UserId == id2))
                {
                    _selected = _loadedAccounts.First(a => a.UserId == id2);
                    _accountsPanel.SetDetectedAccount(null, null); // it's imported now, not "new"
                    _accountsPanel.SetIdentified(id2);
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
    /// True when the failure is the game having changed its IL2CPP data format (a game update the
    /// engine hasn't been ported to). Retrying can never help, so callers should give up immediately.
    /// </summary>
    private static bool IsUnsupportedGameVersion(Exception ex)
        => ex.Message.Contains("Unsupported metadata version", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns raw engine errors into something a user can act on — the internals ("Unsupported
    /// metadata version: 39") mean nothing to them and read like a crash.
    /// </summary>
    private static string DescribeExtractionFailure(Exception ex)
    {
        if (IsUnsupportedGameVersion(ex))
        {
            return "This Raid version isn't supported yet — the game changed its data format in a "
                 + "recent update. Reading accounts will work again after an app update; "
                 + "file upload is unaffected.";
        }

        if (ex.Message.Contains("Raid process not found", StringComparison.OrdinalIgnoreCase))
            return "Raid isn't running — start the game, wait for it to load, then try again.";

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
