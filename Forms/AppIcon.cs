namespace RslCompanionUploader.Forms;

/// <summary>
/// The app icon embedded into the exe via <c>ApplicationIcon</c> (icon.ico), read back once so
/// every window (login, main) shows it in the title bar/taskbar instead of the default WinForms icon.
/// </summary>
internal static class AppIcon
{
    public static readonly Icon? Value = TryLoad();

    private static Icon? TryLoad()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }
}
