using Svg;

namespace RslCompanionUploader.Forms;

/// <summary>
/// Renders the official Google / Microsoft / Discord brand marks (their standard sign-in-button
/// logos) to DPI-sized bitmaps by rasterizing the vector marks, so the buttons stay crisp at any
/// scaling and need no external image files.
/// </summary>
public static class ProviderIcons
{
    public static Image Google(int size) => Render(GoogleSvg, size);
    public static Image Microsoft(int size) => Render(MicrosoftSvg, size);
    public static Image Discord(int size) => Render(DiscordSvg, size);

    private static Image Render(string svg, int size)
    {
        var doc = SvgDocument.FromSvg<SvgDocument>(svg);
        var vb = doc.ViewBox;
        float aw = vb.Width > 0 ? vb.Width : size;
        float ah = vb.Height > 0 ? vb.Height : size;
        float scale = Math.Min(size / aw, size / ah);
        int w = Math.Max(1, (int)Math.Round(aw * scale));
        int h = Math.Max(1, (int)Math.Round(ah * scale));
        return doc.Draw(w, h);
    }

    // Standard multicolor Google "G".
    private const string GoogleSvg = @"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'>
<path fill='#EA4335' d='M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z'/>
<path fill='#4285F4' d='M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z'/>
<path fill='#FBBC05' d='M10.53 28.59c-.48-1.45-.76-2.99-.76-4.59s.27-3.14.76-4.59l-7.98-6.19C.92 16.46 0 20.12 0 24c0 3.88.92 7.54 2.56 10.78l7.97-6.19z'/>
<path fill='#34A853' d='M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z'/>
</svg>";

    // Microsoft four-square mark.
    private const string MicrosoftSvg = @"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 23 23'>
<rect x='1' y='1' width='10' height='10' fill='#F25022'/>
<rect x='12' y='1' width='10' height='10' fill='#7FBA00'/>
<rect x='1' y='12' width='10' height='10' fill='#00A4EF'/>
<rect x='12' y='12' width='10' height='10' fill='#FFB900'/>
</svg>";

    // Discord logo mark (blurple).
    private const string DiscordSvg = @"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 127.14 96.36'>
<path fill='#5865F2' d='M107.7 8.07A105.15 105.15 0 0 0 81.47 0a72.06 72.06 0 0 0-3.36 6.83 97.68 97.68 0 0 0-29.11 0A72.37 72.37 0 0 0 45.64 0a105.89 105.89 0 0 0-26.25 8.09C2.79 32.65-1.71 56.6.54 80.21a105.73 105.73 0 0 0 32.17 16.15 77.7 77.7 0 0 0 6.89-11.11 68.42 68.42 0 0 1-10.85-5.18c.91-.66 1.8-1.34 2.66-2a75.57 75.57 0 0 0 64.32 0c.87.71 1.76 1.39 2.66 2a68.68 68.68 0 0 1-10.87 5.19 77 77 0 0 0 6.89 11.1 105.25 105.25 0 0 0 32.19-16.14c2.64-27.38-4.51-51.11-18.9-72.15ZM42.45 65.69C36.18 65.69 31 60 31 53s5-12.74 11.43-12.74S54 46 53.89 53s-5.05 12.69-11.44 12.69Zm42.24 0C78.41 65.69 73.25 60 73.25 53s5-12.74 11.44-12.74S96.23 46 96.12 53s-5 12.69-11.43 12.69Z'/>
</svg>";
}
