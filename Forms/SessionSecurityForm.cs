using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Lets a signed-in user change how their session is kept, without signing out and back in.
///
/// <para>That matters more than it sounds: the choice is made on the sign-in window, which a user
/// with a remembered session may not see again for months. Without this, "I ticked stay-signed-in on
/// my laptop and now I'd rather it asked for Hello" would mean signing out — and signing out is the
/// one action that costs them the thing they were trying to protect.</para>
///
/// <para>Applying a change re-saves the <i>current</i> live session at the new level, so switching to
/// Windows Hello prompts once, here, rather than surprising them at the next launch.</para>
/// </summary>
public sealed class SessionSecurityForm : Form
{
    private readonly SessionProtectionPicker _picker = new();
    private readonly Label _status = new()
    {
        AutoSize = true,
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 8, 0, 0),
        Visible = false,
    };
    private readonly Button _save = new() { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

    private readonly SessionManager _sessions;
    private readonly AuthSession _session;

    public SessionSecurityForm(SessionManager sessions, AuthSession session)
    {
        _sessions = sessions;
        _session = session;

        Text = "Session security";
        Icon = AppIcon.Value;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(500, 340);
        AcceptButton = _save;
        CancelButton = _cancel;

        BuildLayout();

        _save.Click += async (_, _) => await ApplyAsync();
        Shown += async (_, _) => await _picker.InitializeAsync(UserSettings.Current.SessionProtection);
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.White,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // heading
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // picker
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons

        layout.Controls.Add(new Label
        {
            Text = $"Signed in as {_session.Email ?? _session.DisplayName ?? _session.Uid}.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        }, 0, 0);

        layout.Controls.Add(_picker, 0, 1);
        layout.Controls.Add(_status, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_cancel);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);
    }

    private async Task ApplyAsync()
    {
        _save.Enabled = false;
        _status.Visible = true;
        _status.ForeColor = Color.DimGray;
        _status.Text = _picker.Selected == SessionProtection.WindowsHello
            ? "Confirm with Windows Hello to lock your session…"
            : "Saving…";

        var ok = await _sessions.PersistAsync(_session, _picker.Selected);
        if (ok)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        // The only way to get here is Hello being declined or failing; PersistAsync has already
        // reset the stored preference, so the picker is re-read rather than left showing a lie.
        _save.Enabled = true;
        _status.ForeColor = Color.Firebrick;
        _status.Text = "Windows Hello didn't confirm, so nothing was saved. Your session is still active for now.";
        await _picker.InitializeAsync(UserSettings.Current.SessionProtection);
    }
}
