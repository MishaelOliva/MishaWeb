using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MishaWeb;

/// <summary>
/// Renderer-free artwork for the native start page. One compact JPEG is decoded
/// lazily and shared by every start-page tab; native vector details remain for
/// controls and the high-contrast-safe fallback. Nothing animates or performs
/// background work after paint.
/// </summary>
internal static class StartPageArtwork
{
    internal const string BackdropResourceName = "MishaWeb.StartPageBackdrop.jpg";

    private static readonly Color BackdropTop = NativeUiTheme.Window;
    private static readonly Color BackdropBottom = Color.FromArgb(42, 16, 36);
    private static readonly Color Pink = NativeUiTheme.Accent;
    private static readonly Color Coral = NativeUiTheme.BrandCoral;
    private static readonly Color Orchid = NativeUiTheme.Lavender;
    private static readonly Color Glyph = NativeUiTheme.Text;
    // Loaded inside BackdropFrameSync while backdropFrameLeases > 0 (released
    // together with backdropFrame when the last start-page tab closes/hides).
    private static Bitmap? backdropSource;
    private static bool backdropSourceAttempted;

    // One process-wide frame is enough because only the visible native start page
    // paints. It is intentionally capped to the embedded artwork dimensions: a 4K
    // window therefore cannot turn each open start-page tab into a 32 MB bitmap.
    // The artwork is opaque, so a 24-bit frame avoids an unused alpha byte per
    // pixel. The backing bitmap grows in small buckets and is reused while resizing.
    private const int FrameGrowthQuantum = 128;
    private const int FallbackFrameWidth = 1600;
    private const int FallbackFrameHeight = 900;
    private static readonly object BackdropFrameSync = new();
    private static Bitmap? backdropFrame;
    private static Size backdropFrameCapacity;
    private static Size renderedFrameSize;
    private static BackdropFrameKey renderedFrameKey;
    private static bool hasRenderedFrame;
    private static int backdropFrameLeases;

    public static void DrawBackdrop(Graphics graphics, Rectangle destination)
    {
        if (destination.Width <= 0 || destination.Height <= 0) return;

        DrawBackdropSlice(
            graphics,
            destination.Size,
            new Rectangle(Point.Empty, destination.Size),
            destination);
    }

    /// <summary>
    /// Draws only the requested viewport slice from a shared pre-scaled frame.
    /// This keeps nested transparent WinForms controls aligned with the root while
    /// avoiding a full JPEG cover-scale and veil pass for every child repaint.
    /// </summary>
    public static void DrawBackdropSlice(
        Graphics graphics,
        Size viewportSize,
        Rectangle viewportSlice,
        Rectangle destination)
    {
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0
            || viewportSlice.Width <= 0 || viewportSlice.Height <= 0
            || destination.Width <= 0 || destination.Height <= 0)
        {
            return;
        }

        if (SystemInformation.HighContrast)
        {
            using var systemBrush = new SolidBrush(SystemColors.Window);
            graphics.FillRectangle(systemBrush, destination);
            return;
        }

        var viewportBounds = new Rectangle(Point.Empty, viewportSize);
        var clippedSlice = Rectangle.Intersect(viewportBounds, viewportSlice);
        if (clippedSlice.IsEmpty) return;

        lock (BackdropFrameSync)
        {
            // Offscreen/hidden DrawToBitmap callers still need correct artwork, but
            // they must not recreate a multi-megabyte shared cache with no owner.
            if (backdropFrameLeases == 0)
            {
                DrawBackdropDirect(graphics, destination);
                return;
            }

            EnsureBackdropFrame(viewportSize, graphics.DpiX, graphics.DpiY);
            if (backdropFrame is null || renderedFrameSize.Width <= 0 || renderedFrameSize.Height <= 0)
            {
                DrawBackdropDirect(graphics, destination);
                return;
            }

            var sourceScaleX = renderedFrameSize.Width / (float)viewportSize.Width;
            var sourceScaleY = renderedFrameSize.Height / (float)viewportSize.Height;
            var source = new RectangleF(
                clippedSlice.X * sourceScaleX,
                clippedSlice.Y * sourceScaleY,
                clippedSlice.Width * sourceScaleX,
                clippedSlice.Height * sourceScaleY);

            var adjustedDestination = destination;
            if (clippedSlice != viewportSlice)
            {
                var destinationScaleX = destination.Width / (float)viewportSlice.Width;
                var destinationScaleY = destination.Height / (float)viewportSlice.Height;
                adjustedDestination = Rectangle.Round(new RectangleF(
                    destination.X + ((clippedSlice.X - viewportSlice.X) * destinationScaleX),
                    destination.Y + ((clippedSlice.Y - viewportSlice.Y) * destinationScaleY),
                    clippedSlice.Width * destinationScaleX,
                    clippedSlice.Height * destinationScaleY));
            }

            var previousCompositingMode = graphics.CompositingMode;
            var previousCompositing = graphics.CompositingQuality;
            var previousInterpolation = graphics.InterpolationMode;
            var previousPixelOffset = graphics.PixelOffsetMode;
            try
            {
                var isUnscaledOneToOneCopy = renderedFrameSize == viewportSize
                    && adjustedDestination.Width == clippedSlice.Width
                    && adjustedDestination.Height == clippedSlice.Height
                    && Math.Abs(source.X - clippedSlice.X) < 0.01f
                    && Math.Abs(source.Y - clippedSlice.Y) < 0.01f;
                // backdropFrame is 100% opaque 24bpp RGB, so SourceCopy avoids alpha
                // blending on every start-page and child slice paint.
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = isUnscaledOneToOneCopy
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = isUnscaledOneToOneCopy
                    ? PixelOffsetMode.None
                    : PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    backdropFrame,
                    adjustedDestination,
                    source.X,
                    source.Y,
                    source.Width,
                    source.Height,
                    GraphicsUnit.Pixel);
            }
            finally
            {
                graphics.CompositingMode = previousCompositingMode;
                graphics.CompositingQuality = previousCompositing;
                graphics.InterpolationMode = previousInterpolation;
                graphics.PixelOffsetMode = previousPixelOffset;
            }
        }
    }

    public static void InvalidateBackdropFrame()
    {
        lock (BackdropFrameSync)
        {
            hasRenderedFrame = false;
        }
    }

    public static void AcquireBackdropFrame()
    {
        lock (BackdropFrameSync)
        {
            backdropFrameLeases++;
        }
    }

    public static void ReleaseBackdropFrame()
    {
        lock (BackdropFrameSync)
        {
            if (backdropFrameLeases > 0) backdropFrameLeases--;
            if (backdropFrameLeases != 0) return;

            backdropFrame?.Dispose();
            backdropFrame = null;
            backdropFrameCapacity = Size.Empty;
            renderedFrameSize = Size.Empty;
            renderedFrameKey = default;
            hasRenderedFrame = false;
            backdropSource?.Dispose();
            backdropSource = null;
            backdropSourceAttempted = false;
        }
    }

    internal static (int Leases, bool Allocated, PixelFormat? PixelFormat)
        GetBackdropFrameStateForTesting()
    {
        lock (BackdropFrameSync)
        {
            return (backdropFrameLeases, backdropFrame is not null, backdropFrame?.PixelFormat);
        }
    }

    private static void EnsureBackdropFrame(Size viewportSize, float dpiX, float dpiY)
    {
        var frameSize = CalculateFrameSize(viewportSize);
        var key = new BackdropFrameKey(
            viewportSize,
            frameSize,
            Math.Clamp((int)Math.Round(dpiX), 48, 768),
            Math.Clamp((int)Math.Round(dpiY), 48, 768));
        if (hasRenderedFrame && renderedFrameKey == key) return;

        // When an unusually large viewport is downsampled into the bounded frame,
        // scale its bitmap DPI by the same ratio. The vector fallback therefore
        // retains its original on-screen size after the frame is enlarged again.
        var frameDpiX = Math.Max(1f, key.DpiX * (frameSize.Width / (float)viewportSize.Width));
        var frameDpiY = Math.Max(1f, key.DpiY * (frameSize.Height / (float)viewportSize.Height));
        EnsureBackdropFrameCapacity(frameSize, frameDpiX, frameDpiY);
        if (backdropFrame is null) return;

        using (var frameGraphics = Graphics.FromImage(backdropFrame))
        {
            DrawBackdropDirect(frameGraphics, new Rectangle(Point.Empty, frameSize));
        }

        renderedFrameSize = frameSize;
        renderedFrameKey = key;
        hasRenderedFrame = true;

        if (SystemResourceInfo.CaptureMemory().IsLowSpecMachine && backdropSource is not null)
        {
            backdropSource.Dispose();
            backdropSource = null;
            backdropSourceAttempted = false;
        }
    }

    private static void EnsureBackdropFrameCapacity(Size requiredSize, float dpiX, float dpiY)
    {
        if (backdropFrame is not null
            && backdropFrameCapacity.Width >= requiredSize.Width
            && backdropFrameCapacity.Height >= requiredSize.Height)
        {
            backdropFrame.SetResolution(dpiX, dpiY);
            return;
        }

        var limit = GetFrameLimit();
        var capacity = new Size(
            Math.Min(limit.Width, RoundUp(requiredSize.Width, FrameGrowthQuantum)),
            Math.Min(limit.Height, RoundUp(requiredSize.Height, FrameGrowthQuantum)));
        var nextFrame = new Bitmap(
            Math.Max(1, capacity.Width),
            Math.Max(1, capacity.Height),
            PixelFormat.Format24bppRgb);
        nextFrame.SetResolution(dpiX, dpiY);

        var previousFrame = backdropFrame;
        backdropFrame = nextFrame;
        backdropFrameCapacity = capacity;
        previousFrame?.Dispose();
    }

    private static Size CalculateFrameSize(Size viewportSize)
    {
        var limit = GetFrameLimit();
        var scale = Math.Min(
            1f,
            Math.Min(
                limit.Width / (float)viewportSize.Width,
                limit.Height / (float)viewportSize.Height));
        return new Size(
            Math.Max(1, Math.Min(limit.Width, (int)Math.Round(viewportSize.Width * scale))),
            Math.Max(1, Math.Min(limit.Height, (int)Math.Round(viewportSize.Height * scale))));
    }

    private static Size GetFrameLimit()
    {
        var backdrop = GetCachedBackdropSource();
        return backdrop is null
            ? new Size(FallbackFrameWidth, FallbackFrameHeight)
            : backdrop.Size;
    }

    private static Bitmap? GetCachedBackdropSource()
    {
        if (!backdropSourceAttempted)
        {
            backdropSourceAttempted = true;
            backdropSource = LoadBackdropImage();
        }
        return backdropSource;
    }

    private static int RoundUp(int value, int quantum)
    {
        if (value <= 0) return 1;
        return ((value + quantum - 1) / quantum) * quantum;
    }

    private static void DrawBackdropDirect(Graphics graphics, Rectangle destination)
    {
        if (destination.Width <= 0 || destination.Height <= 0) return;

        var previousSmoothing = graphics.SmoothingMode;
        var previousCompositing = graphics.CompositingQuality;
        var previousInterpolation = graphics.InterpolationMode;
        var previousPixelOffset = graphics.PixelOffsetMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        Bitmap? onDemandBackdrop = null;
        var backdrop = backdropFrameLeases > 0
            ? GetCachedBackdropSource()
            : (onDemandBackdrop = LoadBackdropImage());
        try
        {
            if (backdrop is not null)
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var source = CalculateCoverSource(backdrop.Size, destination.Size);
                graphics.DrawImage(
                    backdrop,
                    destination,
                    source.X,
                    source.Y,
                    source.Width,
                    source.Height,
                    GraphicsUnit.Pixel);

                // A restrained center veil keeps labels crisp without hiding the
                // edge bunnies, flowers, and v1-inspired light trails.
                using var readabilityVeil = new LinearGradientBrush(
                    destination,
                    Color.FromArgb(3, NativeUiTheme.Window),
                    Color.FromArgb(16, NativeUiTheme.Window),
                    LinearGradientMode.Vertical);
                graphics.FillRectangle(readabilityVeil, destination);
            }
            else
            {
                // Packaging failures still get a branded, renderer-free page.
                using var background = new LinearGradientBrush(
                    destination,
                    BackdropTop,
                    BackdropBottom,
                    LinearGradientMode.Vertical);
                background.InterpolationColors = new ColorBlend
                {
                    Colors =
                    [
                        Color.FromArgb(34, 13, 31),
                        Color.FromArgb(55, 21, 48),
                        Color.FromArgb(36, 14, 34),
                        Color.FromArgb(24, 10, 25)
                    ],
                    Positions = [0f, 0.37f, 0.72f, 1f]
                };
                graphics.FillRectangle(background, destination);

                var fallbackScale = Math.Max(1f, graphics.DpiX / 96f);
                DrawSparkle(
                    graphics,
                    new PointF(destination.Left + (destination.Width * 0.16f), destination.Top + (destination.Height * 0.22f)),
                    6f * fallbackScale,
                    Color.FromArgb(88, NativeUiTheme.Focus));
                DrawSparkle(
                    graphics,
                    new PointF(destination.Left + (destination.Width * 0.84f), destination.Top + (destination.Height * 0.28f)),
                    4f * fallbackScale,
                    Color.FromArgb(74, Orchid));
            }
        }
        finally
        {
            onDemandBackdrop?.Dispose();
        }

        graphics.SmoothingMode = previousSmoothing;
        graphics.CompositingQuality = previousCompositing;
        graphics.InterpolationMode = previousInterpolation;
        graphics.PixelOffsetMode = previousPixelOffset;
    }

    private readonly record struct BackdropFrameKey(
        Size ViewportSize,
        Size FrameSize,
        int DpiX,
        int DpiY);

    private static Bitmap? LoadBackdropImage()
    {
        try
        {
            using var embedded = typeof(StartPageArtwork).Assembly.GetManifestResourceStream(BackdropResourceName);
            if (embedded is null) return null;

            using var decoded = Image.FromStream(
                embedded,
                useEmbeddedColorManagement: false,
                validateImageData: false);
            var rgb = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format24bppRgb);
            rgb.SetResolution(decoded.HorizontalResolution, decoded.VerticalResolution);
            using var graphics = Graphics.FromImage(rgb);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaledAndClipped(decoded, new Rectangle(Point.Empty, decoded.Size));
            return rgb;
        }
        catch
        {
            return null;
        }
    }

    private static RectangleF CalculateCoverSource(Size imageSize, Size destinationSize)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0
            || destinationSize.Width <= 0 || destinationSize.Height <= 0)
        {
            return RectangleF.Empty;
        }

        var imageAspect = imageSize.Width / (float)imageSize.Height;
        var destinationAspect = destinationSize.Width / (float)destinationSize.Height;
        if (destinationAspect > imageAspect)
        {
            var sourceHeight = imageSize.Width / destinationAspect;
            return new RectangleF(0f, (imageSize.Height - sourceHeight) / 2f, imageSize.Width, sourceHeight);
        }

        var sourceWidth = imageSize.Height * destinationAspect;
        return new RectangleF((imageSize.Width - sourceWidth) / 2f, 0f, sourceWidth, imageSize.Height);
    }

    public static void DrawCommandTexture(Graphics graphics, Rectangle destination, bool focused)
    {
        if (destination.Width <= 0 || destination.Height <= 0 || SystemInformation.HighContrast) return;

        using var light = new LinearGradientBrush(
            destination,
            Color.FromArgb(focused ? 38 : 22, Pink),
            Color.FromArgb(0, Orchid),
            LinearGradientMode.Horizontal);
        graphics.FillRectangle(light, destination);

        using var topEdge = new Pen(Color.FromArgb(focused ? 104 : 62, NativeUiTheme.Focus), 1f);
        var inset = Math.Max(16, destination.Height / 3);
        graphics.DrawLine(
            topEdge,
            destination.Left + inset,
            destination.Top + 1,
            Math.Max(destination.Left + inset, destination.Right - inset),
            destination.Top + 1);
    }

    public static void DrawBrandMark(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        if (BrandArtwork.TryDrawGeneratedLogo(graphics, bounds)) return;

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var markBounds = new RectangleF(
            bounds.Left + 0.5f,
            bounds.Top + 0.5f,
            Math.Max(1f, bounds.Width - 1f),
            Math.Max(1f, bounds.Height - 1f));
        using var path = CreateRoundedPath(markBounds, Math.Min(bounds.Width, bounds.Height) * 0.28f);

        if (SystemInformation.HighContrast)
        {
            using var fill = new SolidBrush(SystemColors.Highlight);
            using var border = new Pen(SystemColors.WindowText, 1.5f);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }
        else
        {
            using var fill = new LinearGradientBrush(
                markBounds,
                NativeUiTheme.BrandWine,
                NativeUiTheme.Selection,
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(190, NativeUiTheme.Focus), 1f);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        var scale = Math.Max(1f, graphics.DpiX / 96f);
        var bunnyColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Glyph;
        var detailColor = SystemInformation.HighContrast ? SystemColors.Highlight : NativeUiTheme.BrandWine;
        var cheekColor = SystemInformation.HighContrast ? SystemColors.Highlight : Coral;
        var leftEar = new RectangleF(
            markBounds.Left + (markBounds.Width * 0.24f),
            markBounds.Top + (markBounds.Height * 0.13f),
            markBounds.Width * 0.19f,
            markBounds.Height * 0.43f);
        var rightEar = new RectangleF(
            markBounds.Left + (markBounds.Width * 0.57f),
            markBounds.Top + (markBounds.Height * 0.13f),
            markBounds.Width * 0.19f,
            markBounds.Height * 0.43f);
        using var leftEarPath = CreateRoundedPath(leftEar, leftEar.Width / 2f);
        using var rightEarPath = CreateRoundedPath(rightEar, rightEar.Width / 2f);
        using var bunnyBrush = new SolidBrush(bunnyColor);
        graphics.FillPath(bunnyBrush, leftEarPath);
        graphics.FillPath(bunnyBrush, rightEarPath);

        if (!SystemInformation.HighContrast)
        {
            using var innerEar = new Pen(Color.FromArgb(190, Coral), Math.Max(1.2f, 1.6f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(innerEar, CenterX(leftEar), leftEar.Top + (leftEar.Height * 0.2f), CenterX(leftEar), leftEar.Bottom - (leftEar.Height * 0.25f));
            graphics.DrawLine(innerEar, CenterX(rightEar), rightEar.Top + (rightEar.Height * 0.2f), CenterX(rightEar), rightEar.Bottom - (rightEar.Height * 0.25f));
        }

        var face = new RectangleF(
            markBounds.Left + (markBounds.Width * 0.19f),
            markBounds.Top + (markBounds.Height * 0.37f),
            markBounds.Width * 0.62f,
            markBounds.Height * 0.47f);
        graphics.FillEllipse(bunnyBrush, face);
        using var detailBrush = new SolidBrush(detailColor);
        var eyeSize = Math.Max(1.7f, 2.1f * scale);
        graphics.FillEllipse(detailBrush, face.Left + (face.Width * 0.29f) - (eyeSize / 2f), face.Top + (face.Height * 0.42f), eyeSize, eyeSize);
        graphics.FillEllipse(detailBrush, face.Left + (face.Width * 0.71f) - (eyeSize / 2f), face.Top + (face.Height * 0.42f), eyeSize, eyeSize);

        using var cheekBrush = new SolidBrush(Color.FromArgb(SystemInformation.HighContrast ? 255 : 150, cheekColor));
        var cheekWidth = Math.Max(2.8f, 4.2f * scale);
        var cheekHeight = Math.Max(1.6f, 2.4f * scale);
        graphics.FillEllipse(cheekBrush, face.Left + (face.Width * 0.12f), face.Top + (face.Height * 0.59f), cheekWidth, cheekHeight);
        graphics.FillEllipse(cheekBrush, face.Right - (face.Width * 0.12f) - cheekWidth, face.Top + (face.Height * 0.59f), cheekWidth, cheekHeight);

        var noseBounds = new RectangleF(
            face.Left + (face.Width * 0.44f),
            face.Top + (face.Height * 0.54f),
            face.Width * 0.12f,
            face.Height * 0.13f);
        using var nose = CreateHeartPath(noseBounds);
        graphics.FillPath(cheekBrush, nose);

        graphics.SmoothingMode = previousSmoothing;
    }

    public static void DrawFeatureIcon(
        Graphics graphics,
        Rectangle bounds,
        StartPageAction action,
        bool emphasized)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var iconBounds = new RectangleF(
            bounds.Left + 0.5f,
            bounds.Top + 0.5f,
            Math.Max(1f, bounds.Width - 1f),
            Math.Max(1f, bounds.Height - 1f));
        using var path = CreateRoundedPath(iconBounds, Math.Min(bounds.Width, bounds.Height) * 0.3f);
        var primary = action == StartPageAction.ShowTabs ? Orchid : Pink;
        var secondary = action == StartPageAction.ShowTabs ? Pink : Orchid;

        if (SystemInformation.HighContrast)
        {
            using var fill = new SolidBrush(SystemColors.Highlight);
            graphics.FillPath(fill, path);
        }
        else
        {
            using var fill = new LinearGradientBrush(iconBounds, primary, secondary, LinearGradientMode.ForwardDiagonal);
            using var veil = new SolidBrush(Color.FromArgb(emphasized ? 8 : 30, NativeUiTheme.Window));
            graphics.FillPath(fill, path);
            graphics.FillPath(veil, path);
        }

        var scale = Math.Max(1f, graphics.DpiX / 96f);
        using var glyph = new Pen(
            SystemInformation.HighContrast ? SystemColors.HighlightText : Glyph,
            Math.Max(1.5f, 1.7f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        if (action == StartPageAction.ShowTabs)
        {
            DrawTabsGlyph(graphics, iconBounds, glyph, scale);
        }
        else
        {
            DrawGaugeGlyph(graphics, iconBounds, glyph, scale);
        }

        graphics.SmoothingMode = previousSmoothing;
    }

    private static void DrawGaugeGlyph(Graphics graphics, RectangleF bounds, Pen pen, float scale)
    {
        var gauge = RectangleF.Inflate(bounds, -bounds.Width * 0.24f, -bounds.Height * 0.24f);
        graphics.DrawArc(pen, gauge, 145, 250);
        var center = new PointF(
            bounds.Left + (bounds.Width / 2f),
            bounds.Top + (bounds.Height * 0.56f));
        graphics.DrawLine(
            pen,
            center,
            new PointF(
                bounds.Left + (bounds.Width * 0.68f),
                bounds.Top + (bounds.Height * 0.34f)));
        using var hub = new SolidBrush(pen.Color);
        var hubSize = Math.Max(2.5f, 3.5f * scale);
        graphics.FillEllipse(
            hub,
            center.X - (hubSize / 2f),
            center.Y - (hubSize / 2f),
            hubSize,
            hubSize);
    }

    private static void DrawTabsGlyph(Graphics graphics, RectangleF bounds, Pen pen, float scale)
    {
        var back = new RectangleF(
            bounds.Left + (bounds.Width * 0.23f),
            bounds.Top + (bounds.Height * 0.2f),
            bounds.Width * 0.53f,
            bounds.Height * 0.46f);
        using (var backPath = CreateRoundedPath(back, 3.5f * scale))
        {
            graphics.DrawPath(pen, backPath);
        }

        var front = new RectangleF(
            bounds.Left + (bounds.Width * 0.16f),
            bounds.Top + (bounds.Height * 0.34f),
            bounds.Width * 0.58f,
            bounds.Height * 0.46f);
        using var frontPath = CreateRoundedPath(front, 3.5f * scale);
        graphics.DrawPath(pen, frontPath);
        graphics.DrawLine(
            pen,
            front.Left + (front.Width * 0.18f),
            front.Top + (front.Height * 0.24f),
            front.Right - (front.Width * 0.18f),
            front.Top + (front.Height * 0.24f));
    }

    private static float CenterX(RectangleF bounds)
    {
        return bounds.Left + (bounds.Width / 2f);
    }

    private static void DrawSparkle(Graphics graphics, PointF center, float radius, Color color)
    {
        using var pen = new Pen(color, Math.Max(1f, radius * 0.14f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(pen, center.X, center.Y - radius, center.X, center.Y + radius);
        graphics.DrawLine(pen, center.X - radius, center.Y, center.X + radius, center.Y);
    }

    public static void DrawBunnyPeek(Graphics graphics, Rectangle destination)
    {
        if (destination.Width <= 0 || destination.Height <= 0 || SystemInformation.HighContrast) return;

        var scale = Math.Max(1f, graphics.DpiX / 96f);
        var left = destination.Right - (52f * scale);
        var top = destination.Top + (10f * scale);
        var leftEar = new RectangleF(
            left + (5f * scale),
            top,
            6f * scale,
            12f * scale);
        var rightEar = new RectangleF(
            left + (17f * scale),
            top,
            6f * scale,
            12f * scale);
        using var leftEarPath = CreateRoundedPath(leftEar, leftEar.Width / 2f);
        using var rightEarPath = CreateRoundedPath(rightEar, rightEar.Width / 2f);
        using var outline = new Pen(Color.FromArgb(112, NativeUiTheme.Focus), Math.Max(1f, 1.2f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawPath(outline, leftEarPath);
        graphics.DrawPath(outline, rightEarPath);
        var head = new RectangleF(left + (3f * scale), top + (7f * scale), 22f * scale, 14f * scale);
        graphics.DrawArc(outline, head, 188f, 164f);

        using var eyes = new SolidBrush(Color.FromArgb(126, NativeUiTheme.Border));
        var eyeSize = Math.Max(1.4f, 1.8f * scale);
        graphics.FillEllipse(eyes, left + (8f * scale), top + (14f * scale), eyeSize, eyeSize);
        graphics.FillEllipse(eyes, left + (18f * scale), top + (14f * scale), eyeSize, eyeSize);
    }

    private static GraphicsPath CreateHeartPath(RectangleF bounds)
    {
        var path = new GraphicsPath();
        path.AddPolygon(
        [
            new PointF(bounds.Left + (bounds.Width * 0.5f), bounds.Bottom),
            new PointF(bounds.Left + (bounds.Width * 0.08f), bounds.Top + (bounds.Height * 0.44f)),
            new PointF(bounds.Left + (bounds.Width * 0.2f), bounds.Top + (bounds.Height * 0.12f)),
            new PointF(bounds.Left + (bounds.Width * 0.5f), bounds.Top + (bounds.Height * 0.3f)),
            new PointF(bounds.Left + (bounds.Width * 0.8f), bounds.Top + (bounds.Height * 0.12f)),
            new PointF(bounds.Left + (bounds.Width * 0.92f), bounds.Top + (bounds.Height * 0.44f))
        ]);
        path.CloseFigure();
        return path;
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
}
