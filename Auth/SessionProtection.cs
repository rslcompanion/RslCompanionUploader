using System.Text.Json.Serialization;

namespace RslCompanionUploader.Auth;

/// <summary>
/// How much protection the user asked for on the session kept between launches. One value rather
/// than a pair of booleans, because these are three points on a single scale and the user picks
/// exactly one — the sign-in window and the "session security" dialog both render this directly.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionProtection>))]
public enum SessionProtection
{
    /// <summary>
    /// Don't stay signed in. Nothing whatsoever is written to disk — no token, no email, no display
    /// name — and the session ends when the app closes. The default, so persistence only ever begins
    /// with someone choosing it.
    /// </summary>
    None,

    /// <summary>
    /// Stay signed in, protected by the Windows account (DPAPI, CurrentUser). Another Windows user
    /// and anyone holding the disk get nothing; code already running as this user can still read it.
    /// </summary>
    WindowsAccount,

    /// <summary>
    /// Stay signed in, additionally locked to a Windows Hello key held by the TPM, so unlocking needs
    /// a face, fingerprint or PIN. Which of those is offered is Windows' decision, not ours — the
    /// system prompt lists whatever the user enrolled. Closes the same-user gap left by
    /// <see cref="WindowsAccount"/>, at the cost of one prompt per launch.
    /// </summary>
    WindowsHello,
}

public static class SessionProtectionInfo
{
    /// <summary>Whether this level keeps anything at all between launches.</summary>
    public static bool Persists(this SessionProtection level) => level != SessionProtection.None;

    /// <summary>Short label for the picker.</summary>
    public static string Title(this SessionProtection level) => level switch
    {
        SessionProtection.None => "Don't stay signed in",
        SessionProtection.WindowsAccount => "Stay signed in on this device",
        SessionProtection.WindowsHello => "Stay signed in, unlock with Windows Hello",
        _ => level.ToString(),
    };

    /// <summary>
    /// The honest one-line consequence. Each says what it does <i>not</i> protect against as well as
    /// what it does — a security choice presented without its limit is not a choice.
    /// </summary>
    public static string Detail(this SessionProtection level) => level switch
    {
        SessionProtection.None =>
            "Nothing is saved. You'll sign in again next time you open the app.",
        SessionProtection.WindowsAccount =>
            "Saved and encrypted for your Windows account. Other users of this PC can't read it, but software running as you could.",
        SessionProtection.WindowsHello =>
            "Saved and locked to this PC's security chip. Asks for your face, fingerprint or PIN each time the app starts.",
        _ => string.Empty,
    };
}
