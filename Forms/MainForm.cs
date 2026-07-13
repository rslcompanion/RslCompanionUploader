using System.Text;
#if EXTRACTION
using System.Text.Json;
using NewParserOpus;
#endif
using RslCompanionUploader.Api;
using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Main window: shows who is signed in, a dropdown of the accounts linked to the user, and the two
/// upload buttons. Each upload lets the user pick a JSON file and POSTs it to the configured
/// endpoint for the selected account.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly RslCompanionApiClient _api;

    private readonly Label _user = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    private readonly ComboBox _accounts = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _refresh = new() { Text = "Refresh", Width = 90 };
    private readonly Button _uploadResources = new() { Text = "Upload account resources", Height = 44 };
    private readonly Button _uploadChampions = new() { Text = "Upload champions", Height = 44 };
    private readonly Button _syncFromGame = new() { Text = "Sync from game  (extract resources && champions)", Height = 44 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };

    public MainForm(AppConfig config, RslCompanionApiClient api)
    {
        _config = config;
        _api = api;

        Text = "RSL Companion — Uploader";
        Width = 640;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 460);
        Font = new Font("Segoe UI", 9.5f);

        BuildLayout();

        _refresh.Click += async (_, _) => await LoadAccountsAsync();
        _uploadResources.Click += async (_, _) => await UploadAsync(isResources: true);
        _uploadChampions.Click += async (_, _) => await UploadAsync(isResources: false);
#if EXTRACTION
        _syncFromGame.Click += async (_, _) => await SyncFromGameAsync();
#else
        // Built without the private extraction engine (public clone) — game sync unavailable.
        _syncFromGame.Visible = false;
#endif
        _accounts.SelectedIndexChanged += (_, _) => UpdateButtonState();

        Load += async (_, _) =>
        {
            var who = _api.Session.DisplayName ?? _api.Session.Email ?? _api.Session.Uid ?? "signed in";
            _user.Text = $"Signed in as {who}";
            await LoadAccountsAsync();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // user
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // "Account" label
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // account row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // file-upload buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // sync-from-game button
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // log label
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _user.Anchor = AnchorStyles.Left;
        _user.Margin = new Padding(0, 6, 0, 0);
        var signOut = new Button { Text = "Sign out", AutoSize = true, Anchor = AnchorStyles.Right };
        signOut.Click += (_, _) => SignOut();
        header.Controls.Add(_user, 0, 0);
        header.Controls.Add(signOut, 1, 0);
        root.Controls.Add(header);

        root.Controls.Add(new Label { Text = "Account", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });

        var accountRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 14), Height = 30 };
        accountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        accountRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        accountRow.Controls.Add(_accounts, 0, 0);
        accountRow.Controls.Add(_refresh, 1, 0);
        _refresh.Margin = new Padding(8, 0, 0, 0);
        root.Controls.Add(accountRow);

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

        _syncFromGame.Dock = DockStyle.Fill;
        _syncFromGame.Margin = new Padding(0, 0, 0, 12);
        _syncFromGame.Font = new Font(Font, FontStyle.Bold);
        root.Controls.Add(_syncFromGame);

        root.Controls.Add(new Label { Text = "Activity", AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
        root.Controls.Add(_log);

        Controls.Add(root);
    }

    private async Task LoadAccountsAsync()
    {
        SetBusy(true);
        Log("Loading linked accounts…");
        try
        {
            var accounts = await _api.GetAccountsAsync();
            _accounts.Items.Clear();
            foreach (var a in accounts.OrderBy(a => a.Name))
                _accounts.Items.Add(a);

            if (_accounts.Items.Count > 0)
            {
                _accounts.SelectedIndex = 0;
                Log($"Loaded {accounts.Count} account(s).");
            }
            else
            {
                Log("No linked accounts found for this user.");
            }
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

    private async Task UploadAsync(bool isResources)
    {
        if (_accounts.SelectedItem is not AccountSummary account)
        {
            Log("Select an account first.");
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
    /// Reads the live Raid process (private extraction engine), builds a ConsolidatedProfile
    /// (resources + champions + artifacts), and POSTs it to the RSL Companion sync endpoint.
    /// Requires the game to be running. Engine console output is mirrored into the activity log.
    /// </summary>
    private async Task SyncFromGameAsync()
    {
        SetBusy(true);
        Log("Extracting from the running Raid process… make sure the game is open and loaded.");
        try
        {
            string cachePath = Path.Combine(AppContext.BaseDirectory, "offsets_cache.json");

            var profile = await Task.Run(() =>
            {
                var originalOut = Console.Out;
                var originalError = Console.Error;
                using var writer = new ConsoleLogWriter(Log);
                Console.SetOut(writer);
                Console.SetError(writer);
                try
                {
                    // Artifacts are ECS-blocked in the current game build (see CLAUDE.md);
                    // ship resources + champions and skip the futile artifact scan.
                    return ExtractionService.ExtractConsolidatedAsync(cachePath: cachePath, includeArtifacts: false)
                        .GetAwaiter().GetResult();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                }
            });

            Log($"Extracted {profile.Resources.Count} resources and {profile.Heroes.Count} champions " +
                $"for {profile.Account.Name} (#{profile.AccountId}). (Artifacts: pending ECS support.)");
            Log("Uploading to RSL Companion…");

            var json = JsonSerializer.Serialize(profile);
            var result = await _api.UploadConsolidatedAsync(json);
            Log(result.Message);
        }
        catch (Exception ex)
        {
            Log($"Sync from game failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }
#endif

    private void SignOut()
    {
        CredentialStore.ClearSession();
        Application.Restart();
    }

    private void UpdateButtonState()
    {
        var hasAccount = _accounts.SelectedItem is AccountSummary;
        _uploadResources.Enabled = hasAccount;
        _uploadChampions.Enabled = hasAccount;
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _refresh.Enabled = !busy;
        _accounts.Enabled = !busy;
        _syncFromGame.Enabled = !busy;
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
