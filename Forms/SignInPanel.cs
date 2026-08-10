using System.Diagnostics;
using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Sign-in: the app opens the user's real browser at rslcompanion.com's <c>/connect-extractor</c>,
/// and this panel is what the window shows while that happens. The page mints a one-time handoff
/// code, launches <c>rslcompanion-extractor://sync?code=…</c>, Windows routes it to this app, and the
/// forwarded launch arrives here through <see cref="SingleInstance.SecondInstanceLaunched"/>.
///
/// <para><b>The browser, not an embedded one.</b> Hosting the page in a WebView2 was tried and
/// abandoned: Google and Microsoft will not complete a consent flow in a chromeless embedded browser,
/// the popup they open has no opener to report back to, and the failures are silent — the user
/// reaches a provider page that then does nothing. The real browser already has the user's session,
/// their password manager and their 2FA, and it is the surface the providers actually support.</para>
///
/// <para><b>Why the site page at all, rather than a login form here?</b> Because the app must end up
/// with its <i>own</i> Firebase session, not a copy of the browser's. The page hands over a code, the
/// app redeems it for a custom token, and the two sessions are independently revocable. Collecting a
/// password in-app would also lock out everyone who signs in with Google or Microsoft.</para>
///
/// <para><b>This panel exists for one reason beyond status: the checkbox.</b> The browser cannot ask
/// whether the app should stay signed in — that is the app's own decision about its own disk — so
/// the question is asked here, while the browser works, and read when the code comes back.</para>
/// </summary>
public sealed class SignInPanel : Panel
{
    private readonly AppConfig _config;
    private readonly ExtractorHandoff _handoff;

    private readonly ToolTip _tips = HelpGlyph.CreateToolTip();
    private readonly CheckBox _staySignedIn = new()
    {
        Text = "Keep me signed in on this device",
        AutoSize = true,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0),
    };

    private readonly Label _status = new()
    {
        Text = "Finish signing in in your browser…",
        AutoSize = false,
        Height = 44,
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
    private readonly LinkLabel _retry = new()
    {
        Text = "Open my browser again",
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

    /// <summary>
    /// Raised on success with the new session and the protection level the user chose. At
    /// <see cref="SessionProtection.None"/> the session lives only for this run of the app and
    /// <b>nothing at all</b> is written to disk — the handler must not persist identity "for
    /// convenience" either.
    /// </summary>
    public event Action<AuthSession, SessionProtection>? Completed;

    /// <summary>Raised when the user backs out; the host puts the signed-out UI back.</summary>
    public event Action? Cancelled;

    public SignInPanel(AppConfig config, ExtractorHandoff handoff)
    {
        _config = config;
        _handoff = handoff;

        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);

        BuildLayout();

        _retry.LinkClicked += (_, _) => OpenBrowser();
        _cancel.LinkClicked += (_, _) => Cancelled?.Invoke();

        SingleInstance.SecondInstanceLaunched += OnSecondInstance;
    }

    /// <summary>Opens the browser. Called by the host once the panel is on screen.</summary>
    public void Start() => OpenBrowser();

    private void BuildLayout()
    {
        // A single centred column: this is a waiting state, so everything sits in the middle of the
        // window rather than being stretched across a full-width page that has nothing in it.
        var centre = new TableLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Width = 420,
        };
        centre.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        for (var i = 0; i < 5; i++) centre.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        centre.Controls.Add(BuildBrand(), 0, 0);

        _status.Margin = new Padding(0, 18, 0, 10);
        centre.Controls.Add(_status, 0, 1);

        _spinner.Margin = new Padding(4, 0, 4, 22);
        centre.Controls.Add(_spinner, 0, 2);

        centre.Controls.Add(BuildStaySignedIn(), 0, 3);

        var links = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 18, 0, 0),
        };
        var sep = new Label { Text = "·", AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(6, 0, 6, 0) };
        links.Controls.Add(_retry);
        links.Controls.Add(sep);
        links.Controls.Add(_cancel);
        centre.Controls.Add(links, 0, 4);

        // A host panel with the column anchored to nothing is what centres it both ways.
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(centre, 0, 0);
        Controls.Add(host);
    }

    private Control BuildBrand()
    {
        var panel = new TableLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
            Margin = new Padding(0, 0, 0, 12),
        };
        if (AppIcon.Value is { } icon)
        {
            try { logo.Image = new Icon(icon, 56, 56).ToBitmap(); }
            catch { /* fall back to no logo */ }
        }
        panel.Controls.Add(logo, 0, 0);

        panel.Controls.Add(new Label
        {
            Text = "Signing in to RSL Companion",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Margin = new Padding(0),
        }, 0, 1);

        return panel;
    }

    /// <summary>
    /// The app's one persistence control, and the reason this panel is worth showing at all: the
    /// browser is signing the <i>user</i> in, but only the app can be asked whether the <i>app</i>
    /// should stay signed in, because only the app writes to this disk.
    ///
    /// <para>Ticking it means <see cref="SessionProtection.WindowsAccount"/> (DPAPI) — the sensible
    /// default, with no prompt at startup. The Windows Hello level lives where someone would go
    /// looking for it, Help ▸ Session security, and is preserved rather than downgraded if it was
    /// already chosen (see <see cref="Protection"/>).</para>
    /// </summary>
    private Control BuildStaySignedIn()
    {
        _staySignedIn.Checked = UserSettings.Current.SessionProtection.Persists();

        var row = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };
        row.Controls.Add(_staySignedIn);
        row.Controls.Add(HelpGlyph.Create(_tips,
            SessionProtection.WindowsAccount.Detail()
            + "\n\nTo require Windows Hello instead, use Help ▸ Session security once you're signed in."));
        return row;
    }

    /// <summary>
    /// The level to save at. Unticked means <see cref="SessionProtection.None"/> — nothing at all is
    /// written. Ticked means DPAPI, <i>unless</i> the user had already chosen Windows Hello, which is
    /// preserved: signing in again is not a request to weaken it.
    /// </summary>
    private SessionProtection Protection
    {
        get
        {
            if (!_staySignedIn.Checked) return SessionProtection.None;
            var existing = UserSettings.Current.SessionProtection;
            return existing == SessionProtection.WindowsHello ? existing : SessionProtection.WindowsAccount;
        }
    }

    private void OpenBrowser()
    {
        SetStatus("Finish signing in in your browser, and this window will take over.", isError: false);
        _spinner.Visible = true;
        try
        {
            Process.Start(new ProcessStartInfo(_config.ConnectExtractorUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open your browser: " + ex.Message, isError: true);
            _spinner.Visible = false;
        }
    }

    // Fires on the single-instance pipe thread — marshal onto the UI thread before touching anything.
    private void OnSecondInstance(string[] args)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => HandleForwardedArgs(args)); }
        catch { /* handle destroyed between the guard and the marshal — ignore */ }
    }

    private void HandleForwardedArgs(string[] args)
    {
        var code = ProtocolHandler.TryGetHandoffCode(args);
        if (string.IsNullOrEmpty(code)) return; // some other launch arg (e.g. ping) — keep waiting
        _ = RedeemAsync(code);
    }

    private async Task RedeemAsync(string code)
    {
        if (_completing) return;
        _completing = true;

        _retry.Enabled = false;
        SetStatus("Signing you in…", isError: false);

        try
        {
            var session = await _handoff.SignInAsync(code);
            Completed?.Invoke(session, Protection);
        }
        catch (HandoffException ex)
        {
            // Already phrased for the user and already says what to do next; the retry link mints a
            // fresh code, which is exactly what a used or expired one needs.
            Recover(ex.Message);
        }
        catch (Exception ex)
        {
            Recover("Sign-in failed: " + ex.Message);
        }
    }

    private void Recover(string message)
    {
        _completing = false;
        _retry.Enabled = true;
        _spinner.Visible = false;
        SetStatus(message, isError: true);
    }

    private void SetStatus(string text, bool isError)
    {
        _status.ForeColor = isError ? Color.Firebrick : Color.DimGray;
        _status.Text = text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SingleInstance.SecondInstanceLaunched -= OnSecondInstance;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
