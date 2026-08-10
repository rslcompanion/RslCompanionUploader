namespace RslCompanionUploader.Forms;

/// <summary>
/// The small ringed <c>?</c> that carries an explanation without spending a line on it.
///
/// <para>Used wherever a choice needs a sentence of justification that would crowd out the thing the
/// window is actually for. The tooltip is deliberately slow to expire — these explanations run to a
/// few sentences and the 5-second default is not long enough to read them.</para>
/// </summary>
internal static class HelpGlyph
{
    /// <summary>
    /// Builds a glyph whose tooltip is <paramref name="detail"/>. The caller owns placement; the
    /// returned label sizes itself and paints its own ring.
    /// </summary>
    public static Label Create(ToolTip tips, string detail)
    {
        var glyph = new Label
        {
            Text = "?",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
            ForeColor = Color.FromArgb(110, 110, 110),
            Cursor = Cursors.Help,
            Margin = new Padding(2, 4, 0, 0), // sits on the neighbouring text's baseline, not its top edge
            Padding = new Padding(4, 1, 4, 1),
        };
        tips.SetToolTip(glyph, detail);

        // The ring is what makes it read as an affordance rather than stray punctuation.
        glyph.Paint += (s, e) =>
        {
            var l = (Label)s!;
            using var pen = new Pen(Color.FromArgb(170, 170, 170));
            var d = Math.Min(l.Width, l.Height) - 1;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawEllipse(pen, (l.Width - d) / 2f, (l.Height - d) / 2f, d, d);
        };
        return glyph;
    }

    /// <summary>A tooltip configured for multi-sentence explanations.</summary>
    public static ToolTip CreateToolTip() => new()
    {
        AutomaticDelay = 100,
        AutoPopDelay = 30_000,
        InitialDelay = 150,
        ReshowDelay = 50,
        ToolTipTitle = "What this means",
    };
}
