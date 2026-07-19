using System.Diagnostics;
using System.Reflection;

namespace RslCompanionUploader.Forms;

/// <summary>
/// "Help → About" dialog. Its main job is answering "which build do I actually have installed?" —
/// support requests are usually about a stale version, so the version string is the headline and is
/// selectable/copyable, alongside the facts that change behaviour (extraction engine present or not,
/// install folder, API origin).
/// </summary>
public sealed class AboutForm : Form
{
    public static string DisplayVersion
    {
        get
        {
            // InformationalVersion carries CI's -p:Version verbatim (and any "+<sha>" suffix, which
            // we trim); it is the value users see on the releases page. AssemblyVersion is the
            // 4-part fallback for local builds.
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+')[0];
            return UpdateChecker.CurrentVersion.ToString();
        }
    }

    public AboutForm(AppConfig config)
    {
        Text = "About RSL Companion Account Data Extractor";
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 300);
        Font = new Font("Segoe UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // product
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // version
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // details
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // download link
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons

        root.Controls.Add(new Label
        {
            Text = "RSL Companion Account Data Extractor",
            AutoSize = true,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        });

        // Read-only TextBox rather than a Label so users can select and paste the version into a
        // support message.
        root.Controls.Add(new TextBox
        {
            Text = $"Version {DisplayVersion}",
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Width = 400,
            Margin = new Padding(0, 0, 0, 12),
        });

        var details = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Text = string.Join(Environment.NewLine, Details(config)),
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(details);

        var download = new LinkLabel
        {
            Text = "Download the latest version — get.rslcompanion.com",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        download.LinkClicked += (_, _) => OpenUrl("https://get.rslcompanion.com");
        root.Controls.Add(download);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        var copy = new Button { Text = "Copy details", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText($"Version {DisplayVersion}{Environment.NewLine}{details.Text}");
            copy.Text = "Copied";
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = close;
        CancelButton = close;
    }

    private static IEnumerable<string> Details(AppConfig config)
    {
        yield return $"Build date:  {BuildDate():yyyy-MM-dd HH:mm} UTC";
#if EXTRACTION
        yield return "Game extraction:  available (“Export account” reads the running game)";
#else
        yield return "Game extraction:  not included in this build — file upload only";
#endif
        yield return $"API:  {config.ApiBaseUrl}";
        yield return $".NET runtime:  {Environment.Version}";
        yield return $"Windows:  {Environment.OSVersion.Version}";
        yield return $"Installed at:  {AppContext.BaseDirectory}";
    }

    /// <summary>
    /// Deterministic builds stamp the PE header's timestamp with a content hash rather than a real
    /// time, so read the exe's last-write time instead — for an installed build that is the install
    /// time, which is what a user comparing against a release date wants.
    /// </summary>
    private static DateTime BuildDate()
    {
        try
        {
            return File.GetLastWriteTimeUtc(Application.ExecutablePath);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser / blocked — the URL is visible in the link text either way.
        }
    }
}
