using System.Reflection;

namespace RslCompanionUploader;

/// <summary>
/// A release's version as its tag names it: <c>1.40.0</c>, or a pre-release such as
/// <c>1.40.0-dev.2</c>. <see cref="Version"/> cannot hold the label, and the label is the whole
/// difference between a build every user is offered and one only a dev install is.
///
/// <para>Ordered the semver way: numbers first; at equal numbers a pre-release comes
/// <b>before</b> the release it leads up to (<c>1.40.0-dev.2 &lt; 1.40.0</c>), so a dev install is
/// offered the production release of the version it was testing; labels compare segment by
/// segment, numeric segments as numbers (<c>dev.10 &gt; dev.9</c>).</para>
///
/// <para>The exe's own version resource carries the numbers only (Windows, Inno and MSIX accept
/// nothing else), so the running build's label is read from its informational version — the value
/// CI passes as <c>-p:Version</c>.</para>
/// </summary>
public sealed record ReleaseVersion(Version Numeric, string? Label) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Label is not null;

    /// <summary>The build that is running.</summary>
    public static ReleaseVersion Current { get; } = ReadCurrent();

    /// <summary>Parses a tag or version: an optional leading <c>v</c>, X.Y.Z, an optional <c>-label</c>.</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        s = s.Split('+')[0]; // build metadata ("+<sha>") never affects ordering

        var dash = s.IndexOf('-');
        var numberPart = dash < 0 ? s : s[..dash];
        var label = dash < 0 ? null : s[(dash + 1)..];
        if (label is { Length: 0 }) return false;

        if (!Version.TryParse(numberPart, out var n) || n.Build < 0) return false;
        version = new ReleaseVersion(new Version(n.Major, n.Minor, n.Build), label);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var byNumber = Numeric.CompareTo(other.Numeric);
        if (byNumber != 0) return byNumber;
        if (Label is null) return other.Label is null ? 0 : 1;
        if (other.Label is null) return -1;

        var a = Label.Split('.');
        var b = other.Label.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = int.TryParse(a[i], out var x);
            var bNum = int.TryParse(b[i], out var y);
            var c = (aNum, bNum) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1, // numeric identifiers sort below alphanumeric ones (semver)
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Label is null ? Numeric.ToString() : $"{Numeric}-{Label}";

    private static ReleaseVersion ReadCurrent()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(informational, out var v)) return v;

        var n = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new ReleaseVersion(new Version(n.Major, n.Minor, Math.Max(n.Build, 0)), null);
    }
}
