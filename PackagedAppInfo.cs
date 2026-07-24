using System.Runtime.InteropServices;
using System.Text;

namespace RslCompanionUploader;

/// <summary>
/// Detects whether the process is running with an MSIX package identity. Packaged builds get the
/// protocol handler and app updates from the manifest/Store instead of the runtime registry write
/// and GitHub-release poll that unpackaged (Inno-installed) builds use.
/// </summary>
internal static class PackagedAppInfo
{
    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    public static readonly bool IsPackaged = ComputeIsPackaged();

    private static bool ComputeIsPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }
}
