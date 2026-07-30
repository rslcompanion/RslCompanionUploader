# TODO / follow-ups

## Fold the native menu into the web top bar

The native `MenuStrip` (File / Help) still sits above the WebView2 page — the one remaining
"native" seam now that the whole UI is a full-window web page ([Forms/AppShell.cs](Forms/AppShell.cs)).

If we commit to a fully web UI, the natural next step is to fold File/Help into the web top bar
(the user chip in the design mockup) and drop the `MenuStrip` from [Forms/MainForm.cs](Forms/MainForm.cs).
That means routing the remaining menu actions through the shell bridge instead of native handlers:
Check for updates, Recalibrate (EXTRACTION-only), and About. Everything else already goes through
the bridge — export, export-clan, refresh, sign out, report-build, open-url — so the pattern is set.
Help ▸ "Open rslcompanion.com" is now a duplicate of the page's "Open RSL Helper" button and can
simply be dropped with the menu.

Deferred until we've lived with the current layout and confirmed the web-UI direction.

## Bundle the WebView2 runtime in the installer (Windows 10)

The whole UI is now WebView2, so the runtime is load-bearing. Windows 11 ships it in-box, but a
fresh Windows 10 machine may not have it — without it the app shows only the fallback label.
Chain the **Evergreen WebView2 bootstrapper** (the small Microsoft-hosted stub) in
[installer/setup.iss](installer/setup.iss) so setup installs it when missing.
