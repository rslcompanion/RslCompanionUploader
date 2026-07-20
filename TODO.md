# TODO / follow-ups

## Fold the native menu into the web top bar

The native `MenuStrip` (File / Help) still sits above the WebView2 page — the one remaining
"native" seam now that the whole UI is a full-window web page ([Forms/AppShell.cs](Forms/AppShell.cs)).

If we commit to a fully web UI, the natural next step is to fold File/Help into the web top bar
(the user chip in the design mockup) and drop the `MenuStrip` from [Forms/MainForm.cs](Forms/MainForm.cs).
That means routing the remaining menu actions through the shell bridge instead of native handlers:
Refresh accounts, Sign out, Check for updates, Recalibrate (EXTRACTION-only), Open rslcompanion.com,
and About. Export / report-build / open-url already go through the bridge, so the pattern is set.

Deferred until we've lived with the current layout and confirmed the web-UI direction.

## Bundle the WebView2 runtime in the installer (Windows 10)

The whole UI is now WebView2, so the runtime is load-bearing. Windows 11 ships it in-box, but a
fresh Windows 10 machine may not have it — without it the app shows only the fallback label.
Chain the **Evergreen WebView2 bootstrapper** (the small Microsoft-hosted stub) in
[installer/setup.iss](installer/setup.iss) so setup installs it when missing.
