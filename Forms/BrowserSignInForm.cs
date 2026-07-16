using System.Diagnostics;
using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Browser-based sign-in. Instead of collecting credentials in-app, this opens the user's real
/// default browser to rslcompanion.com. If they already have an active session there, the site
/// immediately hands a Firebase refresh token back via the <c>rslcompanion-extractor://</c> protocol;
/// otherwise the user signs in on the website once and the same handoff happens. The forwarded launch
/// reaches this window through <see cref="SingleInstance.SecondInstanceLaunched"/>.
/// </summary>
public sealed class BrowserSignInForm : Form
{
    private readonly AppConfig _config;
    private readonly FirebaseAuthClient _auth;

    private readonly Label _title = new()
    {
        Text = "Sign in with your browser",
        AutoSize = true,
        Font = new Font("Segoe UI", 14f, FontStyle.Bold),
    };
    private readonly Label _status = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
    };
    private readonly Button _openBrowser = new() { Text = "Open browser again", Height = 34, AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", Height = 34, AutoSize = true };

    private bool _completing;

    /// <summary>Set when sign-in succeeds; the caller uses this to open the main window.</summary>
    public AuthSession? Session { get; private set; }

    public BrowserSignInForm(AppConfig config, FirebaseAuthClient auth)
    {
        _config = config;
        _auth = auth;

        Text = "RSL Companion — Sign in";
        Icon = AppIcon.Value;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(430, 250);

        BuildLayout();

        _openBrowser.Click += (_, _) => OpenBrowser();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        // The browser-launched instance forwards the token here through the single-instance pipe.
        SingleInstance.SecondInstanceLaunched += OnSecondInstance;
        FormClosed += (_, _) => SingleInstance.SecondInstanceLaunched -= OnSecondInstance;

        // Open the browser as soon as the window is up so the user lands straight on the site.
        Shown += (_, _) => OpenBrowser();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 22, 24, 18),
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // explanation
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons

        _title.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(_title);

        var explain = new Label
        {
            Text = "We opened rslcompanion.com in your browser. If you're already signed in there, " +
                   "this window will finish automatically. Otherwise, sign in on the site and it will " +
                   "hand you back here.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 64,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(explain);
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_openBrowser);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        CancelButton = _cancel;
    }

    private void OpenBrowser()
    {
        SetStatus("Waiting for you to finish signing in in your browser…", isError: false);
        try
        {
            Process.Start(new ProcessStartInfo(_config.ConnectExtractorUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open your browser: " + ex.Message, isError: true);
        }
    }

    // Fires on the single-instance pipe thread — marshal onto the UI thread before touching the form.
    private void OnSecondInstance(string[] args)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => HandleForwardedArgs(args)); }
        catch { /* handle destroyed between the guard and the marshal — ignore */ }
    }

    private async void HandleForwardedArgs(string[] args)
    {
        if (_completing) return;

        var refreshToken = ProtocolHandler.TryGetRefreshToken(args);
        if (string.IsNullOrEmpty(refreshToken)) return; // some other launch arg — keep waiting

        _completing = true;
        SetBusy(true, "Signing you in…");
        try
        {
            Session = await _auth.SignInWithRefreshTokenAsync(refreshToken);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _completing = false;
            SetBusy(false, null);
            SetStatus("Sign-in failed: " + ex.Message + " Try again from your browser.", isError: true);
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _openBrowser.Enabled = !busy;
        UseWaitCursor = busy;
        if (message != null) SetStatus(message, isError: false);
    }

    private void SetStatus(string text, bool isError)
    {
        _status.ForeColor = isError ? Color.Firebrick : Color.DimGray;
        _status.Text = text;
    }
}
