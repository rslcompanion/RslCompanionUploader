using System.ComponentModel;
using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Forms;

/// <summary>
/// The "how should we keep you signed in?" choice: three radio buttons, one short line each, with the
/// consequences behind a <c>?</c> next to them.
///
/// <para><b>Why the detail is hidden.</b> Spelled out inline it ran to nine lines and pushed the
/// sign-in page itself down to a sliver — the options crowded out the thing the window is actually
/// for. All three choices stay visible, which is what a security choice needs; only the explanation
/// is on demand, where it can also be longer than it could ever be inline.</para>
///
/// <para>Windows Hello is offered only when the machine can actually do it. With no PIN or biometric
/// enrolled the option stays visible but disabled, with the reason in its tooltip — hiding it would
/// leave someone looking for it puzzled, and enabling it would promise protection the machine cannot
/// deliver.</para>
///
/// <para>Used by <see cref="SessionSecurityForm"/>, where all three levels are worth showing. The
/// sign-in panel deliberately shows only a checkbox — at sign-in the single question worth asking is
/// whether to stay signed in at all — so the wording lives here and is shared rather than retyped.</para>
/// </summary>
internal sealed class SessionProtectionPicker : FlowLayoutPanel
{
    private static readonly SessionProtection[] Levels =
    {
        SessionProtection.None, SessionProtection.WindowsAccount, SessionProtection.WindowsHello,
    };

    private readonly Dictionary<SessionProtection, RadioButton> _buttons = new();
    private readonly ToolTip _tips = HelpGlyph.CreateToolTip();

    public SessionProtectionPicker()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;

        foreach (var level in Levels)
        {
            var radio = new RadioButton
            {
                Text = level.Title(),
                AutoSize = true,
                Margin = new Padding(0, 0, 4, 0),
            };
            _buttons[level] = radio;
            _tips.SetToolTip(radio, level.Detail());

            var help = HelpGlyph.Create(_tips, level.Detail());

            // One row per option: the radio, then the "?" immediately after its label.
            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, level == SessionProtection.None ? 0 : 6, 0, 0),
            };
            row.Controls.Add(radio);
            row.Controls.Add(help);
            Controls.Add(row);
        }

        _buttons[SessionProtection.None].Checked = true;
    }

    /// <summary>The level currently selected.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SessionProtection Selected
    {
        get => _buttons.FirstOrDefault(p => p.Value.Checked).Key;
        set { if (_buttons.TryGetValue(value, out var radio) && radio.Enabled) radio.Checked = true; }
    }

    /// <summary>
    /// Probes for Hello and enables or disables that option accordingly. Async because the check is a
    /// WinRT call; until it answers the option stays disabled — briefly offering something we then
    /// take away is worse than showing it a moment late.
    /// </summary>
    public async Task InitializeAsync(SessionProtection preferred)
    {
        var hello = _buttons[SessionProtection.WindowsHello];
        hello.Enabled = false;

        var available = await HelloProtector.IsAvailableAsync();
        if (IsDisposed) return;

        hello.Enabled = available;
        if (!available)
        {
            hello.ForeColor = Color.Gray;
            _tips.SetToolTip(hello, "Windows Hello isn't set up on this PC. Add a PIN, fingerprint or "
                                    + "face sign-in in Windows Settings to use this option.");
        }

        // Falls back one level when Hello was the stored preference on a machine that has since lost
        // it (PIN removed, TPM cleared) — the saved session is unreadable anyway, so re-signing in at
        // the strongest level still available is the useful outcome.
        Selected = preferred == SessionProtection.WindowsHello && !available
            ? SessionProtection.WindowsAccount
            : preferred;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
