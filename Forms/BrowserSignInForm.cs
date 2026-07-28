using System.Diagnostics;
using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Browser-based sign-in, shown as a lightweight splash. Instead of collecting credentials in-app,
/// this opens the user's real default browser to rslcompanion.com. If they already have an active
/// session there, the site immediately hands a Firebase refresh token back via the
/// <c>rslcompanion-extractor://</c> protocol; otherwise the user signs in on the website once and the
/// same handoff happens. The forwarded launch reaches this window through
/// <see cref="SingleInstance.SecondInstanceLaunched"/>.
///
/// The window is deliberately bare — it is a transient "waiting for the browser" state, not a form the
/// user fills in. The only affordances are a "remember me" checkbox (<see cref="RememberMe"/>, read by
/// the caller once <see cref="Session"/> is set, to decide whether to persist a refresh token for next
/// launch), a retry link (in case the browser never opened), and cancel.
/// </summary>
public sealed class BrowserSignInForm : Form
{
    private readonly AppConfig _config;
    private readonly FirebaseAuthClient _auth;

    private readonly Label _status = new()
    {
        Text = "Opening your browser to sign in…",
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.DimGray,
    };
    private readonly ProgressBar _spinner = new()
    {
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 30,
        Dock = DockStyle.Fill,
        Height = 6,
    };
    private readonly CheckBox _rememberMe = new()
    {
        Text = "Remember me on this device",
        AutoSize = true,
        Checked = true,
        ForeColor = Color.DimGray,
    };
    private readonly LinkLabel _retry = new()
    {
        Text = "Open browser again",
        AutoSize = true,
        LinkColor = Color.DimGray,
        ActiveLinkColor = Color.Black,
    };
    private readonly LinkLabel _cancel = new()
    {
        Text = "Cancel",
        AutoSize = true,
        LinkColor = Color.DimGray,
        ActiveLinkColor = Color.Black,
    };

    private bool _completing;

    /// <summary>Set when sign-in succeeds; the caller uses this to open the main window.</summary>
    public AuthSession? Session { get; private set; }

    /// <summary>
    /// Whether the user asked to stay signed in on this device. When true, the caller persists a
    /// refresh token so the next launch skips this browser handoff entirely; when false, the session
    /// only lives for this run of the app.
    /// </summary>
    public bool RememberMe => _rememberMe.Checked;

    public BrowserSignInForm(AppConfig config, FirebaseAuthClient auth)
    {
        _config = config;
        _auth = auth;

        Text = "RSL Companion";
        Icon = AppIcon.Value;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(360, 288);
        Padding = new Padding(1); // room for the hairline border painted in OnPaint

        BuildLayout();

        _retry.LinkClicked += (_, _) => OpenBrowser();
        _cancel.LinkClicked += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        // Esc cancels, matching the removed Cancel button's old shortcut.
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

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
            Padding = new Padding(28, 30, 28, 22),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.White,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // logo + brand
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // spinner
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // remember me
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // links

        layout.Controls.Add(BuildBrand(), 0, 0);

        _status.Height = 24;
        _status.Margin = new Padding(0, 4, 0, 10);
        layout.Controls.Add(_status, 0, 1);

        _spinner.Margin = new Padding(4, 0, 4, 14);
        layout.Controls.Add(_spinner, 0, 2);

        _rememberMe.Anchor = AnchorStyles.None;
        _rememberMe.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(_rememberMe, 0, 3);

        var links = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
        };
        var sep = new Label { Text = "·", AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(6, 0, 6, 0) };
        links.Controls.Add(_retry);
        links.Controls.Add(sep);
        links.Controls.Add(_cancel);
        layout.Controls.Add(links, 0, 4);

        Controls.Add(layout);
    }

    private Control BuildBrand()
    {
        // TableLayoutPanel (not FlowLayoutPanel): a cell control with Anchor=None is centered in its
        // cell, so the 56px logo sits centered above the wider brand label.
        var panel = new TableLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var logo = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(56, 56),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 10),
        };
        if (AppIcon.Value is { } icon)
        {
            try { logo.Image = new Icon(icon, 56, 56).ToBitmap(); }
            catch { /* fall back to no logo */ }
        }
        panel.Controls.Add(logo, 0, 0);

        var brand = new Label
        {
            Text = "RSL Companion",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            Margin = new Padding(0),
        };
        panel.Controls.Add(brand, 0, 1);

        return panel;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(224, 224, 224));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
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
        SetBusy("Signing you in…");
        try
        {
            Session = await _auth.SignInWithRefreshTokenAsync(refreshToken);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _completing = false;
            _retry.Enabled = true;
            SetStatus("Sign-in failed: " + ex.Message + " Try again from your browser.", isError: true);
        }
    }

    private void SetBusy(string message)
    {
        _retry.Enabled = false;
        UseWaitCursor = true;
        SetStatus(message, isError: false);
    }

    private void SetStatus(string text, bool isError)
    {
        _status.ForeColor = isError ? Color.Firebrick : Color.DimGray;
        _status.Text = text;
    }
}
