using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Sign-in screen: native email/password (Firebase REST) plus a "Sign in with browser" button
/// that covers Google, Microsoft, and Discord via the embedded rslcompanion login. A "Remember me"
/// checkbox persists the session (encrypted refresh token) for silent sign-in next launch.
/// </summary>
public sealed class LoginForm : Form
{
    private readonly AppConfig _config;
    private readonly FirebaseAuthClient _auth;

    private readonly TextBox _email = new() { PlaceholderText = "you@example.com", Height = 28 };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, PlaceholderText = "Password", Height = 28 };
    private readonly CheckBox _remember = new() { Text = "Remember me on this PC", AutoSize = true };
    private readonly Button _emailSignIn = new() { Text = "Sign in", Height = 36 };
    private readonly Button _google = new() { Text = "Continue with Google", Height = 34 };
    private readonly Button _microsoft = new() { Text = "Continue with Microsoft", Height = 34 };
    private readonly Button _discord = new() { Text = "Continue with Discord", Height = 34 };
    private readonly Label _status = new() { AutoSize = false, Height = 38, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Firebrick };

    /// <summary>Set when sign-in succeeds; the caller uses this to open the main window.</summary>
    public AuthSession? Session { get; private set; }

    /// <summary>True if the user ticked "Remember me" for the successful sign-in.</summary>
    public bool RememberMe => _remember.Checked;

    public LoginForm(AppConfig config, FirebaseAuthClient auth)
    {
        _config = config;
        _auth = auth;

        Text = "RSL Companion — Sign in";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(430, 520);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 16),
            ColumnCount = 1,
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(Control c, int topMargin = 0, int bottomMargin = 0)
        {
            c.Margin = new Padding(0, topMargin, 0, bottomMargin);
            if (c is TextBox or Button) c.Dock = DockStyle.Fill;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(c);
        }

        var title = new Label { Text = "Sign in to RSL Companion", AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold) };
        var emailLabel = new Label { Text = "Email", AutoSize = true };
        var pwLabel = new Label { Text = "Password", AutoSize = true };
        var orLabel = new Label { Text = "— or —", AutoSize = false, Height = 24, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, ForeColor = Color.Gray };

        AddRow(title, 0, 16);
        AddRow(emailLabel, 0, 2);
        AddRow(_email, 0, 10);
        AddRow(pwLabel, 0, 2);
        AddRow(_password, 0, 10);
        AddRow(_remember, 0, 14);
        AddRow(_emailSignIn, 0, 12);
        AddRow(orLabel, 0, 8);
        AddRow(_google, 0, 8);
        AddRow(_microsoft, 0, 8);
        AddRow(_discord, 0, 12);
        _status.Dock = DockStyle.Fill;
        AddRow(_status);

        Controls.Add(layout);

        AcceptButton = _emailSignIn;
        _emailSignIn.Click += async (_, _) => await SignInWithPasswordAsync();
        _google.Click += (_, _) => SignInWithBrowser(LoginProvider.Google);
        _microsoft.Click += (_, _) => SignInWithBrowser(LoginProvider.Microsoft);
        _discord.Click += (_, _) => SignInWithBrowser(LoginProvider.Discord);

        foreach (var b in new[] { _google, _microsoft, _discord })
        {
            b.TextImageRelation = TextImageRelation.ImageBeforeText;
            b.ImageAlign = ContentAlignment.MiddleCenter;
            b.TextAlign = ContentAlignment.MiddleCenter;
        }

        // Prefill remembered email.
        var saved = CredentialStore.Load();
        if (!string.IsNullOrEmpty(saved.Email))
        {
            _email.Text = saved.Email;
            _remember.Checked = true;
            BeginInvoke(() => _password.Focus());
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var size = LogicalToDeviceUnits(18);
        try
        {
            _google.Image = ProviderIcons.Google(size);
            _microsoft.Image = ProviderIcons.Microsoft(size);
            _discord.Image = ProviderIcons.Discord(size);
            foreach (var b in new[] { _google, _microsoft, _discord })
                b.ImageAlign = ContentAlignment.MiddleLeft;
        }
        catch
        {
            // If icon rendering fails for any reason, the text labels still make the buttons usable.
        }
    }

    private async Task SignInWithPasswordAsync()
    {
        var email = _email.Text.Trim();
        var pw = _password.Text;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pw))
        {
            SetStatus("Enter your email and password.", true);
            return;
        }

        SetBusy(true, "Signing in…");
        try
        {
            Session = await _auth.SignInWithPasswordAsync(email, pw);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (FirebaseAuthException ex)
        {
            SetStatus(ex.Message, true);
        }
        catch (Exception ex)
        {
            SetStatus("Could not reach the sign-in service: " + ex.Message, true);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void SignInWithBrowser(LoginProvider provider)
    {
        SetBusy(true, $"Opening {provider} sign-in…");
        try
        {
            using var browser = new BrowserLoginForm(_config, provider);
            if (browser.ShowDialog(this) != DialogResult.OK || browser.Result is null)
            {
                SetStatus($"{provider} sign-in was cancelled.", false);
                return;
            }

            var h = browser.Result;
            Session = await _auth.FromHarvestedTokensAsync(h.IdToken, h.RefreshToken, h.ExpiresAtUtc);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("Browser sign-in failed: " + ex.Message, true);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _emailSignIn.Enabled = !busy;
        _google.Enabled = !busy;
        _microsoft.Enabled = !busy;
        _discord.Enabled = !busy;
        _email.Enabled = !busy;
        _password.Enabled = !busy;
        _remember.Enabled = !busy;
        if (message != null) SetStatus(message, false);
        UseWaitCursor = busy;
    }

    private void SetStatus(string text, bool isError)
    {
        _status.ForeColor = isError ? Color.Firebrick : Color.DimGray;
        _status.Text = text;
    }
}
