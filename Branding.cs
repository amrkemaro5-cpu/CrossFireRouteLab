using System.Drawing.Drawing2D;

namespace CrossFireRouteLab;

public static class Brand
{
    public static Bitmap CreateLogo(int size)
    {
        size = Math.Max(64, size);
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var c = size / 2f;
        var shield = new[]
        {
            new PointF(c, size * .025f),
            new PointF(size * .86f, size * .22f),
            new PointF(size * .73f, size * .74f),
            new PointF(c, size * .975f),
            new PointF(size * .27f, size * .74f),
            new PointF(size * .14f, size * .22f)
        };

        using (var glow = new LinearGradientBrush(new RectangleF(0, 0, size, size), Color.FromArgb(205, 181, 70, 255), Color.FromArgb(205, 0, 224, 255), LinearGradientMode.ForwardDiagonal))
        using (var glowPen = new Pen(glow, Math.Max(3f, size * .045f)))
            g.DrawPolygon(glowPen, shield);

        var inner = shield.Select(p => new PointF(c + (p.X - c) * .86f, c + (p.Y - c) * .86f)).ToArray();
        using (var fill = new SolidBrush(Color.FromArgb(235, 4, 9, 21)))
            g.FillPolygon(fill, inner);

        using (var cyan = new Pen(Color.FromArgb(235, 0, 224, 255), Math.Max(2f, size * .018f)))
            g.DrawPolygon(cyan, inner);

        using (var purple = new Pen(Color.FromArgb(220, 181, 70, 255), Math.Max(1.5f, size * .012f)))
            g.DrawPolygon(purple, shield);

        var ring = new RectangleF(size * .205f, size * .145f, size * .59f, size * .59f);
        using (var ringGlow = new Pen(Color.FromArgb(175, 0, 224, 255), Math.Max(2f, size * .022f)))
            g.DrawEllipse(ringGlow, ring);
        using (var ringPurple = new Pen(Color.FromArgb(165, 181, 70, 255), Math.Max(1f, size * .010f)))
            g.DrawArc(ringPurple, ring, 210, 215);

        var lightning = new[]
        {
            new PointF(size * .31f, size * .20f),
            new PointF(size * .43f, size * .11f),
            new PointF(size * .39f, size * .25f),
            new PointF(size * .51f, size * .17f)
        };
        using (var bolt = new Pen(Color.FromArgb(235, 0, 224, 255), Math.Max(2f, size * .018f)) { LineJoin = LineJoin.Round })
            g.DrawLines(bolt, lightning);

        using var font = new Font("Arial", size * .245f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var text = new LinearGradientBrush(new RectangleF(0, size * .33f, size, size * .30f), Color.White, Color.FromArgb(145, 220, 245), LinearGradientMode.Vertical);
        g.DrawString("GRL", font, text, new RectangleF(0, size * .32f, size, size * .30f), sf);

        using var underline = new Pen(Color.FromArgb(235, 181, 70, 255), Math.Max(2f, size * .016f));
        g.DrawLine(underline, size * .22f, size * .80f, size * .78f, size * .80f);
        return bmp;
    }

    public static Icon CreateIcon()
    {
        using var bmp = CreateLogo(256);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
