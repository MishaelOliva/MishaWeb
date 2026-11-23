using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MishaWeb;

/// <summary>
/// Loads the generated bunny mark once for the whole process. The 256 px
/// runtime image keeps the decoded footprint small while remaining crisp at
/// every size used by the browser chrome and native start page.
/// </summary>
internal static class BrandArtwork
{
    internal const string LogoResourceName = "MishaWeb.Brand.BunnyLogo.png";
    private const int MaximumCachedLogoEntries = 8;
    private const int MaximumCachedLogoSide = 256;

    private readonly record struct RenderedLogoKey(int Width, int Height);
    private sealed record RenderedLogoEntry(RenderedLogoKey Key, Bitmap Bitmap);

    private static readonly object RenderedLogoSync = new();
    private static readonly List<RenderedLogoEntry> RenderedLogoCache = new(MaximumCachedLogoEntries);

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

        if (bounds.Width > MaximumCachedLogoSide || bounds.Height > MaximumCachedLogoSide)
        {
            RenderLogo(graphics, bounds, image);
            return true;
        }

        var key = new RenderedLogoKey(bounds.Width, bounds.Height);
        lock (RenderedLogoSync)
        {
            Bitmap? cached = null;
            for (var i = RenderedLogoCache.Count - 1; i >= 0; i--)
            {
                if (RenderedLogoCache[i].Key != key) continue;
                var hit = RenderedLogoCache[i];
                if (i < RenderedLogoCache.Count - 1)
                {
                    RenderedLogoCache.RemoveAt(i);
                    RenderedLogoCache.Add(hit);
                }
                cached = hit.Bitmap;
                break;
            }

            if (cached is null)
            {
                cached = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                try
                {
                    cached.SetResolution(96f, 96f);
                    using (var renderGraphics = Graphics.FromImage(cached))
                    {
                        renderGraphics.Clear(Color.Transparent);
                        RenderLogo(renderGraphics, new Rectangle(0, 0, bounds.Width, bounds.Height), image);
                    }
                }
                catch
                {
                    cached.Dispose();
                    throw;
                }

                if (RenderedLogoCache.Count >= MaximumCachedLogoEntries)
                {
                    RenderedLogoCache[0].Bitmap.Dispose();
                    RenderedLogoCache.RemoveAt(0);
                }
                RenderedLogoCache.Add(new RenderedLogoEntry(key, cached));
            }

            graphics.DrawImage(
                cached,
                bounds,
                0,
                0,
                cached.Width,
                cached.Height,
                GraphicsUnit.Pixel);
        }

        return true;
    }

    private static void RenderLogo(Graphics graphics, Rectangle bounds, Bitmap image)
    {

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
        using var squirclePath = NativeUiTheme.CreateRoundedPath(clipBounds, radius);
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
            using var borderPath = NativeUiTheme.CreateRoundedPath(borderBounds, borderRadius);
            using var outerRimBrush = new LinearGradientBrush(
                borderBounds,
                Color.FromArgb(205, 255, 232, 246),
                Color.FromArgb(95, NativeUiTheme.Accent),
                LinearGradientMode.Vertical);
            using var outerRimPen = new Pen(outerRimBrush, Math.Max(1f, bounds.Width / 42f));
            graphics.DrawPath(outerRimPen, borderPath);

            if (bounds.Width >= 32 && bounds.Height >= 32)
            {
                var innerInset = Math.Max(1.1f, Math.Min(bounds.Width, bounds.Height) / 36f);
                var innerBounds = borderBounds;
                innerBounds.Inflate(-innerInset, -innerInset);
                if (innerBounds.Width > 4f && innerBounds.Height > 4f)
                {
                    var innerRadius = Math.Max(2f, borderRadius - innerInset);
                    using var innerPath = NativeUiTheme.CreateRoundedPath(innerBounds, innerRadius);
                    using var innerRimBrush = new LinearGradientBrush(
                        innerBounds,
                        Color.FromArgb(115, 255, 255, 255),
                        Color.FromArgb(28, 255, 196, 232),
                        LinearGradientMode.Vertical);
                    using var innerRimPen = new Pen(innerRimBrush, Math.Max(0.9f, bounds.Width / 64f));
                    graphics.DrawPath(innerRimPen, innerPath);
                }
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousPixelOffset;
            graphics.CompositingQuality = previousCompositing;
        }
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
        var scale = Math.Max(1f, DeviceDpi / 96f);
        var badgeSize = Math.Max(8, side / 3);
        var badgeBounds = new Rectangle(
            logoBounds.Right - badgeSize,
            logoBounds.Bottom - badgeSize,
            badgeSize,
            badgeSize);
        var haloPad = Math.Max(1, (int)Math.Round(1.3f * scale));
        var haloBounds = Rectangle.Inflate(badgeBounds, haloPad, haloPad);
        var haloColor = SystemInformation.HighContrast
            ? SystemColors.Window
            : (BackColor.A == 255 && BackColor != Color.Transparent ? BackColor : Color.FromArgb(24, 11, 25));
        e.Graphics.FillEllipse(NativeUiTheme.GetSharedBrush(haloColor), haloBounds);

        if (SystemInformation.HighContrast)
        {
            e.Graphics.FillEllipse(NativeUiTheme.GetSharedBrush(SystemColors.Highlight), badgeBounds);
            e.Graphics.DrawEllipse(NativeUiTheme.GetSharedPen(SystemColors.WindowText, 1f), badgeBounds);
        }
        else
        {
            using var fill = new LinearGradientBrush(
                badgeBounds,
                Color.FromArgb(238, 178, 255),
                Color.FromArgb(164, 84, 224),
                LinearGradientMode.Vertical);
            e.Graphics.FillEllipse(fill, badgeBounds);
            e.Graphics.DrawEllipse(NativeUiTheme.GetSharedPen(Color.FromArgb(156, 240, 214, 255), 1f), badgeBounds);
        }
        var badgePointSize = Math.Clamp((badgeSize * 0.58f * 72f) / Math.Max(96f, e.Graphics.DpiY), 5.5f, 24f);
        var badgeFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", badgePointSize, FontStyle.Bold);
        TextRenderer.DrawText(
            e.Graphics,
            "I",
            badgeFont,
            badgeBounds,
            SystemInformation.HighContrast ? SystemColors.HighlightText : NativeUiTheme.AccentText,
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding
            | TextFormatFlags.SingleLine);
    }
}
