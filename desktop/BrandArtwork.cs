using System.Drawing.Drawing2D;

namespace MishaWeb;

/// <summary>
/// Loads the generated bunny mark once for the whole process. The 256 px
/// runtime image keeps the decoded footprint small while remaining crisp at
/// every size used by the browser chrome and native start page.
/// </summary>
internal static class BrandArtwork
{
    internal const string LogoResourceName = "MishaWeb.Brand.BunnyLogo.png";

    private static readonly Lazy<Bitmap?> LogoImage = new(
        LoadLogoImage,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool TryDrawGeneratedLogo(Graphics graphics, Rectangle bounds)
    {
        if (SystemInformation.HighContrast || bounds.Width <= 1 || bounds.Height <= 1)
        {
            return false;
        }

        var image = LogoImage.Value;
        if (image is null) return false;

        var previousSmoothing = graphics.SmoothingMode;
        var previousInterpolation = graphics.InterpolationMode;
        var previousPixelOffset = graphics.PixelOffsetMode;
        var previousCompositing = graphics.CompositingQuality;
        var clipBounds = new RectangleF(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height);
        var radius = Math.Min(clipBounds.Width, clipBounds.Height) * 0.25f;
        using var squirclePath = CreateRoundedPath(clipBounds, radius);
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.SetClip(squirclePath, CombineMode.Intersect);
            var inset = Math.Min(image.Width, image.Height) * (8f / 256f);
            var sourceRect = new RectangleF(
                inset,
                inset,
                Math.Max(1f, image.Width - (inset * 2f)),
                Math.Max(1f, image.Height - (inset * 2f)));
            graphics.DrawImage(
                image,
                clipBounds,
                sourceRect,
                GraphicsUnit.Pixel);

            var glassHeight = Math.Max(8f, clipBounds.Height * 0.48f);
            var glassRect = new RectangleF(
                clipBounds.X - (clipBounds.Width * 0.14f),
                clipBounds.Y - (glassHeight * 0.22f),
                clipBounds.Width * 1.28f,
                glassHeight * 1.18f);
            using var glassPath = new GraphicsPath();
            glassPath.AddEllipse(glassRect);
            using var glassBrush = new LinearGradientBrush(
                new RectangleF(clipBounds.X, clipBounds.Y, clipBounds.Width, glassHeight + 1f),
                Color.FromArgb(92, 255, 255, 255),
                Color.FromArgb(0, 255, 240, 250),
                LinearGradientMode.Vertical);
            graphics.FillPath(glassBrush, glassPath);

            var causticRect = new RectangleF(
                clipBounds.X,
                clipBounds.Bottom - Math.Max(6f, clipBounds.Height * 0.34f),
                clipBounds.Width,
                Math.Max(6f, clipBounds.Height * 0.34f) + 1f);
            using var causticBrush = new LinearGradientBrush(
                causticRect,
                Color.FromArgb(0, NativeUiTheme.Focus),
                Color.FromArgb(46, NativeUiTheme.Focus),
                LinearGradientMode.Vertical);
            graphics.FillRectangle(causticBrush, causticRect);
        }
        finally
        {
            graphics.Restore(state);
        }

        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var borderBounds = new RectangleF(
                bounds.X + 0.5f,
                bounds.Y + 0.5f,
                Math.Max(1f, bounds.Width - 1f),
                Math.Max(1f, bounds.Height - 1f));
            var borderRadius = Math.Min(borderBounds.Width, borderBounds.Height) * 0.25f;
            using var borderPath = CreateRoundedPath(borderBounds, borderRadius);
            using var outerRimBrush = new LinearGradientBrush(
                borderBounds,
                Color.FromArgb(205, 255, 232, 246),
                Color.FromArgb(95, NativeUiTheme.Accent),
                LinearGradientMode.Vertical);
            using var outerRimPen = new Pen(outerRimBrush, Math.Max(1.1f, bounds.Width / 42f));
            graphics.DrawPath(outerRimPen, borderPath);

            var innerBounds = borderBounds;
            innerBounds.Inflate(-1.1f, -1.1f);
            if (innerBounds.Width > 4f && innerBounds.Height > 4f)
            {
                var innerRadius = Math.Max(2f, borderRadius - 1.1f);
                using var innerPath = CreateRoundedPath(innerBounds, innerRadius);
                using var innerRimBrush = new LinearGradientBrush(
                    innerBounds,
                    Color.FromArgb(115, 255, 255, 255),
                    Color.FromArgb(28, 255, 196, 232),
                    LinearGradientMode.Vertical);
                using var innerRimPen = new Pen(innerRimBrush, 1f);
                graphics.DrawPath(innerRimPen, innerPath);
            }
            return true;
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousPixelOffset;
            graphics.CompositingQuality = previousCompositing;
        }
    }

    private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 1f || bounds.Height <= 1f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
        var diameter = safeRadius * 2f;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap? LoadLogoImage()
    {
        try
        {
            using var stream = typeof(BrandArtwork).Assembly.GetManifestResourceStream(LogoResourceName);
            if (stream is null) return null;

            using var decoded = Image.FromStream(
                stream,
                useEmbeddedColorManagement: false,
                validateImageData: true);
            return new Bitmap(decoded);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class BrandLogoControl : Control
{
    private bool privateMode;

    public BrandLogoControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "MishaWeb";
    }

    public bool PrivateMode
    {
        get => privateMode;
        set
        {
            if (privateMode == value) return;
            privateMode = value;
            AccessibleName = value ? "Private MishaWeb" : "MishaWeb";
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var side = Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height));
        var inset = Math.Max(1, side / 16);
        var logoBounds = new Rectangle(
            (ClientSize.Width - side) / 2 + inset,
            (ClientSize.Height - side) / 2 + inset,
            Math.Max(1, side - (inset * 2)),
            Math.Max(1, side - (inset * 2)));
        StartPageArtwork.DrawBrandMark(e.Graphics, logoBounds);

        if (!privateMode) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var badgeSize = Math.Max(8, side / 3);
        var badgeBounds = new Rectangle(
            logoBounds.Right - badgeSize,
            logoBounds.Bottom - badgeSize,
            badgeSize,
            badgeSize);
        using var fill = new SolidBrush(
            SystemInformation.HighContrast ? SystemColors.Highlight : NativeUiTheme.Lavender);
        using var border = new Pen(
            SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(120, 60, 148),
            1f);
        e.Graphics.FillEllipse(fill, badgeBounds);
        e.Graphics.DrawEllipse(border, badgeBounds);
        TextRenderer.DrawText(
            e.Graphics,
            "I",
            Font,
            badgeBounds,
            SystemInformation.HighContrast ? SystemColors.HighlightText : NativeUiTheme.AccentText,
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding
            | TextFormatFlags.SingleLine);
    }
}
