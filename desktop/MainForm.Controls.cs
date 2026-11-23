using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MishaWeb;

public sealed partial class MainForm
{
    private sealed class TabDragGhost : Control
    {
        private string title = "New tab";
        private bool highContrast;

        public TabDragGhost()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
            Cursor = Cursors.SizeWE;
            Enabled = false;
            TabStop = false;
            Visible = false;
        }

        public bool HighContrast
        {
            get => highContrast;
            set
            {
                if (highContrast == value) return;
                highContrast = value;
                Invalidate();
            }
        }

        public void SetState(string tabTitle, bool useHighContrast)
        {
            title = string.IsNullOrWhiteSpace(tabTitle) ? "New tab" : tabTitle;
            highContrast = useHighContrast;
            AccessibleName = $"Dragging {title} tab";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 8 || Height < 8) return;

            var scale = Math.Max(1f, DeviceDpi / 96f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var shadowBounds = new RectangleF(
                3f * scale,
                3f * scale,
                Math.Max(2f, Width - (6f * scale)),
                Math.Max(2f, Height - (5f * scale)));
            using (var shadowPath = NativeUiTheme.CreateRoundedPath(shadowBounds, 9f * scale))
            using (var shadowBrush = new SolidBrush(Color.FromArgb(76, Color.Black)))
            {
                e.Graphics.FillPath(shadowBrush, shadowPath);
            }

            var tabBounds = new RectangleF(
                1f * scale,
                1f * scale,
                Math.Max(2f, Width - (5f * scale)),
                Math.Max(2f, Height - (5f * scale)));
            using (var tabPath = NativeUiTheme.CreateRoundedPath(tabBounds, 9f * scale))
            using (var fillBrush = new SolidBrush(highContrast
                ? Color.FromArgb(232, SystemColors.Highlight)
                : Color.FromArgb(224, NativeUiTheme.SurfaceRaised)))
            using (var borderPen = new Pen(
                highContrast ? SystemColors.HighlightText : AccentColor,
                (highContrast ? 2f : 1.5f) * scale))
            {
                e.Graphics.FillPath(fillBrush, tabPath);
                e.Graphics.DrawPath(borderPen, tabPath);
            }

            var foreground = highContrast ? SystemColors.HighlightText : PageTextColor;
            var textBounds = new Rectangle(
                (int)Math.Round(12f * scale),
                (int)Math.Round(1f * scale),
                Math.Max(1, Width - (int)Math.Round(42f * scale)),
                Math.Max(1, Height - (int)Math.Round(6f * scale)));
            TextRenderer.DrawText(
                e.Graphics,
                title,
                TabTitleFont,
                textBounds,
                foreground,
                TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.VerticalCenter);

            using var closePen = new Pen(foreground, Math.Max(1.1f, 1.4f * scale));
            var closeCenterX = Width - (20f * scale);
            var closeCenterY = (Height - (3f * scale)) / 2f;
            var arm = 3f * scale;
            e.Graphics.DrawLine(closePen, closeCenterX - arm, closeCenterY - arm, closeCenterX + arm, closeCenterY + arm);
            e.Graphics.DrawLine(closePen, closeCenterX + arm, closeCenterY - arm, closeCenterX - arm, closeCenterY + arm);

            using var accentPen = new Pen(highContrast ? SystemColors.HighlightText : AccentColor, Math.Max(1.5f, 2f * scale));
            var accentY = Height - (4f * scale);
            var accentLeft = 10f * scale;
            e.Graphics.DrawLine(accentPen, accentLeft, accentY, Math.Max(accentLeft, Width - (12f * scale)), accentY);
        }
    }

    private sealed class TabHeader : UserControl
    {
        private static readonly Font TabMonogramFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8f, FontStyle.Bold);
        [ThreadStatic]
        private static PointF[]? audioSpeakerPoints;
        private readonly ToolTip toolTip;
        private bool isSelected;
        private bool isHovered;
        private float hoverProgress;
        private bool isCloseHovered;
        private bool isClosePressed;
        private bool isAudioHovered;
        private bool isAudioPressed;
        private bool isSuspended;
        private bool isLoading;
        private bool isDiscarded;
        private bool keepAwake;
        private bool isAudible;
        private bool isMuted;
        private bool isPinned;
        private bool isDragging;
        private bool pointerTracking;
        private bool dragStarted;
        private bool isMaximized;
        private Point pointerDownScreenLocation;
        private string currentTitle = "New tab";
        private Bitmap? faviconBitmap;
        private Bitmap? renderedFaviconBitmap;
        private int renderedFaviconDpi;
        private string? hostMonogram;
        private bool isWebTab;
        private bool hasState;

        public bool IsPrivateMode { get; set; }

        public void SetMaximized(bool maximized)
        {
            if (isMaximized == maximized) return;
            isMaximized = maximized;
            Invalidate();
        }

        public TabHeader(ToolTip toolTip)
        {
            this.toolTip = toolTip;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            DoubleBuffered = true;
            Height = 27;
            Width = 190;
            Margin = new Padding(0, 0, 4, 0);
            Padding = new Padding(10, 0, 0, 0);
            BackColor = TabIdleColor;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTab;

            MouseEnter += (_, _) => SetHovered(true);
            MouseLeave += (_, _) =>
            {
                isCloseHovered = false;
                isAudioHovered = false;
                SetHovered(false);
            };
            MouseDown += OnPointerDown;
            MouseMove += OnPointerMove;
            MouseUp += OnMouseUp;
            MouseCaptureChanged += OnMouseCaptureChanged;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode is Keys.Enter or Keys.Space) Activated?.Invoke(this, EventArgs.Empty);
                else if (e.KeyCode == Keys.Delete || (e.Control && e.KeyCode == Keys.W))
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End)
                {
                    KeyboardNavigationRequested?.Invoke(this, e);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F10 && e.Shift)
                {
                    ContextRequested?.Invoke(
                        this,
                        new TabContextEventArgs(PointToScreen(new Point(Width / 2, Height / 2))));
                    e.Handled = true;
                }
            };
        }

        public event EventHandler? Activated;
        public event EventHandler? CloseRequested;
        public event EventHandler? AudioToggleRequested;
        public event EventHandler<TabContextEventArgs>? ContextRequested;
        public event EventHandler<TabDragEventArgs>? DragStarted;
        public event EventHandler<TabDragEventArgs>? DragMoved;
        public event EventHandler<TabDragEventArgs>? DragCompleted;
        public event EventHandler? DragCanceled;
        public event EventHandler<KeyEventArgs>? KeyboardNavigationRequested;
        public bool HighContrast { get; set; }

        public void SetDragging(bool dragging)
        {
            if (isDragging == dragging) return;
            isDragging = dragging;
            Cursor = dragging ? Cursors.SizeWE : Cursors.Hand;
            ApplyVisualState();
        }

        public void SetState(
            string title,
            bool selected,
            bool suspended,
            bool loading,
            bool discarded,
            bool protectedInBackground,
            bool audible,
            bool muted,
            bool pinned)
        {
            if (hasState
                && string.Equals(currentTitle, title, StringComparison.Ordinal)
                && isSelected == selected
                && isSuspended == suspended
                && isLoading == loading
                && isDiscarded == discarded
                && keepAwake == protectedInBackground
                && isAudible == audible
                && isMuted == muted
                && isPinned == pinned)
            {
                return;
            }
            hasState = true;
            currentTitle = title;
            isSelected = selected;
            isSuspended = suspended;
            isLoading = loading;
            isDiscarded = discarded;
            keepAwake = protectedInBackground;
            isAudible = audible;
            isMuted = muted;
            isPinned = pinned;
            ApplyVisualState();
            AccessibleName = $"{title} tab";
            var stateDescription = selected
                ? "Selected tab"
                : discarded ? "Unloaded tab" : suspended ? "Sleeping tab" : loading ? "Loading tab" : "Tab";
            if (protectedInBackground) stateDescription += ", kept active in background";
            if (audible && !muted) stateDescription += ", playing audio";
            if (muted) stateDescription += ", muted";
            if (pinned) stateDescription += ", pinned";
            AccessibleDescription = stateDescription;
            var tooltip = stateDescription == "Tab" || stateDescription == "Selected tab"
                ? title
                : $"{title}\n{stateDescription}";
            toolTip.SetToolTip(this, tooltip);
        }

        public void SetFavicon(Bitmap? ownedFavicon, string? monogram, bool webTab)
        {
            if (!ReferenceEquals(faviconBitmap, ownedFavicon))
            {
                faviconBitmap?.Dispose();
                faviconBitmap = ownedFavicon;
                RebuildRenderedFavicon();
            }
            hostMonogram = string.IsNullOrWhiteSpace(monogram) ? null : monogram;
            isWebTab = webTab;
            ApplyVisualState();
        }

        public void SetFaviconMetadata(string? monogram, bool webTab)
        {
            var normalized = string.IsNullOrWhiteSpace(monogram) ? null : monogram;
            if (string.Equals(hostMonogram, normalized, StringComparison.Ordinal) && isWebTab == webTab) return;
            hostMonogram = normalized;
            isWebTab = webTab;
            ApplyVisualState();
        }

        private void RebuildRenderedFavicon()
        {
            renderedFaviconBitmap?.Dispose();
            renderedFaviconBitmap = null;
            renderedFaviconDpi = 0;
            if (faviconBitmap is null) return;

            var dpi = Math.Max(96, DeviceDpi);
            var scale = dpi / 96f;
            var side = Math.Max(12, (int)Math.Round(16f * scale));
            var prepared = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            try
            {
                prepared.SetResolution(dpi, dpi);
                using var g = Graphics.FromImage(prepared);
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using var clipPath = NativeUiTheme.CreateRoundedPath(new RectangleF(0f, 0f, side, side), 4f * scale);
                g.SetClip(clipPath, CombineMode.Intersect);
                g.DrawImage(faviconBitmap, new RectangleF(0f, 0f, side, side));
                renderedFaviconBitmap = prepared;
                renderedFaviconDpi = dpi;
            }
            catch
            {
                prepared.Dispose();
                throw;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var nextMargin = new Padding(0, 0, ScalePx(4), 0);
            if (Margin != nextMargin) Margin = nextMargin;
            if (faviconBitmap is not null && renderedFaviconDpi != Math.Max(96, DeviceDpi))
            {
                RebuildRenderedFavicon();
            }
            ApplyVisualState();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            var nextMargin = new Padding(0, 0, ScalePx(4), 0);
            if (Margin != nextMargin) Margin = nextMargin;
            if (faviconBitmap is not null)
            {
                RebuildRenderedFavicon();
            }
            ApplyVisualState();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyVisualState();
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TabHeaderAccessibleObject(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NativeUiTheme.UiTransitionAnimator.Cancel(this);
                renderedFaviconBitmap?.Dispose();
                renderedFaviconBitmap = null;
                faviconBitmap?.Dispose();
                faviconBitmap = null;
                toolTip.SetToolTip(this, string.Empty);
            }
            base.Dispose(disposing);
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            Invalidate();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Invalidate();
        }

        private float DpiScale => Math.Max(1f, DeviceDpi / 96f);

        private int ScalePx(int logicalValue) => Math.Max(1, (int)Math.Round(logicalValue * DpiScale));

        private bool IsCompactPinned => isPinned && Width <= ScalePx(56);

        private bool ShouldShowCloseButton() => !isDragging && !isPinned && (isSelected || isHovered || Width >= ScalePx(170));

        private RectangleF GetCloseButtonBounds()
        {
            var s = DpiScale;
            var size = 20f * s;
            return new(Math.Max(4f * s, Width - (25f * s)), Math.Max(1f * s, (Height - size) / 2f), size, size);
        }

        private RectangleF GetCloseButtonHitBounds()
        {
            var s = DpiScale;
            var bounds = GetCloseButtonBounds();
            bounds.Inflate(3f * s, 3f * s);
            return bounds;
        }

        private bool ShouldShowAudioButton() => (isAudible || isMuted) && !isDragging;

        private RectangleF GetAudioButtonBounds()
        {
            var s = DpiScale;
            var size = 16f * s;
            if (IsCompactPinned)
            {
                return new(Math.Max(2f * s, (Width - size) / 2f), Math.Max(1f * s, (Height - size) / 2f), size, size);
            }
            return new(6f * s, Math.Max(1f * s, (Height - size) / 2f), size, size);
        }

        private RectangleF GetAudioButtonHitBounds()
        {
            var s = DpiScale;
            var bounds = GetAudioButtonBounds();
            bounds.Inflate(2f * s, 4f * s);
            return bounds;
        }

        private bool HasFaviconVisual() => faviconBitmap is not null || (isWebTab && !string.IsNullOrEmpty(hostMonogram));

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // All painting happens inside double-buffered OnPaint.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var stripHostColor = Parent?.BackColor
                ?? (HighContrast ? SystemColors.Control : IsPrivateMode ? Color.FromArgb(24, 11, 25) : ChromeColor);
            e.Graphics.Clear(stripHostColor);
            if (Width <= 4 || Height <= 4) return;

            var s = DpiScale;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var effectiveAccent = HighContrast
                ? SystemColors.HighlightText
                : IsPrivateMode ? NativeUiTheme.Lavender : AccentColor;
            var topInset = isMaximized ? 1f : 1.5f * s;
            var pillBounds = new RectangleF(
                1f * s,
                topInset,
                Math.Max(1f, Width - (2f * s)),
                Math.Max(1f, Height - topInset - (1.5f * s)));
            var pillRadius = Math.Min(8f * s, pillBounds.Height / 2f);
            using var pillPath = NativeUiTheme.CreateRoundedPath(pillBounds, pillRadius);

            if (HighContrast)
            {
                var highlighted = isSelected || isHovered;
                e.Graphics.FillPath(NativeUiTheme.GetSharedBrush(highlighted ? SystemColors.Highlight : SystemColors.Control), pillPath);
                e.Graphics.DrawPath(NativeUiTheme.GetSharedPen(highlighted ? SystemColors.HighlightText : SystemColors.WindowText, (isSelected ? 1.5f : 1f) * s), pillPath);
                if (isLoading)
                {
                    var loadY = pillBounds.Bottom - (1f * s);
                    e.Graphics.DrawLine(
                        NativeUiTheme.GetSharedPen(highlighted ? SystemColors.HighlightText : SystemColors.Highlight, 2f * s),
                        pillBounds.Left + (4f * s),
                        loadY,
                        Math.Max(pillBounds.Left + (4f * s), pillBounds.Right - (4f * s)),
                        loadY);
                }
            }
            else
            {
                var hoverT = isSelected ? 1f : Math.Clamp(hoverProgress, 0f, 1f);
                var idleTop = Color.FromArgb(52, 62, 28, 58);
                var hoverTop = Color.FromArgb(164, 74, 34, 68);
                var idleBottom = Color.FromArgb(28, 36, 16, 38);
                var hoverBottom = Color.FromArgb(182, 42, 18, 42);

                var topColor = isSelected
                    ? Color.FromArgb(228, IsPrivateMode ? 68 : 94, IsPrivateMode ? 30 : 40, IsPrivateMode ? 88 : 82)
                    : NativeUiTheme.BlendColor(idleTop, hoverTop, hoverT);
                var bottomColor = isSelected
                    ? Color.FromArgb(242, IsPrivateMode ? 42 : 56, IsPrivateMode ? 18 : 22, IsPrivateMode ? 54 : 52)
                    : NativeUiTheme.BlendColor(idleBottom, hoverBottom, hoverT);

                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SetClip(pillPath, CombineMode.Intersect);
                    using var pillFill = new LinearGradientBrush(
                        pillBounds,
                        topColor,
                        bottomColor,
                        LinearGradientMode.Vertical);
                    e.Graphics.FillPath(pillFill, pillPath);

                    if (isSelected || hoverT > 0.01f)
                    {
                        var specHeight = Math.Max(4f * s, pillBounds.Height * 0.48f);
                        var specRect = new RectangleF(pillBounds.X, pillBounds.Y, pillBounds.Width, specHeight + 1f);
                        var specAlpha = isSelected ? 74 : Math.Clamp((int)MathF.Round(42f * hoverT), 1, 42);
                        using var specBrush = new LinearGradientBrush(
                            specRect,
                            Color.FromArgb(specAlpha, 255, 255, 255),
                            Color.FromArgb(0, 255, 232, 246),
                            LinearGradientMode.Vertical);
                        e.Graphics.FillRectangle(specBrush, specRect);

                        if (isSelected)
                        {
                            var causticHeight = Math.Max(4f * s, pillBounds.Height * 0.28f);
                            var causticRect = new RectangleF(
                                pillBounds.X,
                                pillBounds.Bottom - causticHeight,
                                pillBounds.Width,
                                causticHeight + 1f);
                            using var causticBrush = new LinearGradientBrush(
                                causticRect,
                                Color.FromArgb(0, effectiveAccent),
                                Color.FromArgb(54, NativeUiTheme.Focus),
                                LinearGradientMode.Vertical);
                            e.Graphics.FillRectangle(causticBrush, causticRect);
                        }
                    }

                    if (isLoading && pillBounds.Width > 8f * s)
                    {
                        var shimmerHeight = Math.Max(2f, 2f * s);
                        var shimmerRect = new RectangleF(
                            pillBounds.X,
                            pillBounds.Bottom - shimmerHeight,
                            pillBounds.Width,
                            shimmerHeight + 0.5f);
                        using var shimmerBrush = new LinearGradientBrush(
                            shimmerRect,
                            Color.FromArgb(40, effectiveAccent),
                            Color.FromArgb(40, effectiveAccent),
                            LinearGradientMode.Horizontal)
                        {
                            InterpolationColors = new ColorBlend(4)
                            {
                                Colors =
                                [
                                    Color.FromArgb(48, effectiveAccent),
                                    Color.FromArgb(240, NativeUiTheme.Focus),
                                    Color.FromArgb(228, effectiveAccent),
                                    Color.FromArgb(48, effectiveAccent)
                                ],
                                Positions = [0f, 0.35f, 0.68f, 1f]
                            }
                        };
                        e.Graphics.FillRectangle(shimmerBrush, shimmerRect);
                    }
                }
                finally
                {
                    e.Graphics.Restore(state);
                }

                var rimTopAlpha = isSelected ? 228 : (int)MathF.Round(44f + ((138f - 44f) * hoverT));
                var rimBottomAlpha = isSelected ? 152 : (int)MathF.Round(16f + ((62f - 16f) * hoverT));
                using var rimBrush = new LinearGradientBrush(
                    pillBounds,
                    Color.FromArgb(rimTopAlpha, 255, 230, 246),
                    Color.FromArgb(rimBottomAlpha, effectiveAccent),
                    LinearGradientMode.Vertical);
                using var rimPen = new Pen(rimBrush, (isSelected ? 1.25f : 0.9f) * s);
                e.Graphics.DrawPath(rimPen, pillPath);

                if (isSelected && !isLoading && pillBounds.Width > 26f * s)
                {
                    var barWidth = Math.Min(pillBounds.Width - (20f * s), Math.Max(18f * s, pillBounds.Width * 0.42f));
                    var barLeft = pillBounds.Left + ((pillBounds.Width - barWidth) / 2f);
                    using var glowBar = new Pen(effectiveAccent, 2f * s)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    e.Graphics.DrawLine(glowBar, barLeft, pillBounds.Bottom - (0.5f * s), barLeft + barWidth, pillBounds.Bottom - (0.5f * s));
                }
            }

            if (isDragging)
            {
                using var dragOutline = new Pen(effectiveAccent, 1f * s)
                {
                    DashStyle = DashStyle.Dash
                };
                e.Graphics.DrawRectangle(dragOutline, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            }

            var hasAudioState = isAudible || isMuted;
            var hasFaviconVisual = HasFaviconVisual();
            if (IsCompactPinned)
            {
                if (hasAudioState)
                {
                    var audioBounds = GetAudioButtonBounds();
                    DrawAudioPillBackground(e.Graphics, audioBounds, s);
                    var audioColor = HighContrast
                        ? (isAudioHovered || isAudioPressed || isSelected) ? SystemColors.HighlightText : SystemColors.WindowText
                        : isMuted ? Color.FromArgb(244, 198, 132) : PageTextColor;
                    DrawAudioGlyph(e.Graphics, audioBounds, audioColor, isMuted, s);
                }
                else
                {
                    var iconSide = 16f * s;
                    var iconBounds = new RectangleF(
                        (Width - iconSide) / 2f,
                        Math.Max(1f * s, (Height - iconSide) / 2f),
                        iconSide,
                        iconSide);
                    if (hasFaviconVisual)
                    {
                        DrawFaviconOrMonogram(e.Graphics, iconBounds, s);
                    }
                    else
                    {
                        var fallbackMonogram = !string.IsNullOrWhiteSpace(currentTitle)
                            ? char.ToUpperInvariant(currentTitle.Trim()[0]).ToString()
                            : "M";
                        DrawMonogramPill(e.Graphics, iconBounds, fallbackMonogram, s);
                    }
                }

                // Subtle top-right accent pin dot on compact pinned pills
                var pinDotSize = Math.Max(4f, 4.5f * s);
                e.Graphics.FillEllipse(
                    NativeUiTheme.GetSharedBrush(effectiveAccent),
                    pillBounds.Right - pinDotSize - (3f * s),
                    pillBounds.Top + (3f * s),
                    pinDotSize,
                    pinDotSize);

                if (Focused)
                {
                    ControlPaint.DrawFocusRectangle(
                        e.Graphics,
                        ClientRectangle,
                        HighContrast && isSelected ? SystemColors.HighlightText : ForeColor,
                        BackColor);
                }
                return;
            }

            var showClose = ShouldShowCloseButton();
            var isCompactNormalTab = !hasAudioState && Width < ScalePx(64);
            if (hasAudioState)
            {
                var audioBounds = GetAudioButtonBounds();
                DrawAudioPillBackground(e.Graphics, audioBounds, s);

                var audioColor = HighContrast
                    ? (isAudioHovered || isAudioPressed || isSelected) ? SystemColors.HighlightText : SystemColors.WindowText
                    : isMuted ? Color.FromArgb(244, 198, 132) : PageTextColor;
                DrawAudioGlyph(
                    e.Graphics,
                    audioBounds,
                    audioColor,
                    isMuted,
                    s);

                if (hasFaviconVisual && Width >= ScalePx(115))
                {
                    var iconSide = 16f * s;
                    DrawFaviconOrMonogram(
                        e.Graphics,
                        new RectangleF(26f * s, Math.Max(1f * s, (Height - iconSide) / 2f), iconSide, iconSide),
                        s);
                }
            }
            else if (hasFaviconVisual)
            {
                var iconSide = 16f * s;
                var iconX = isCompactNormalTab
                    ? (showClose ? 6f * s : Math.Max(4f * s, (Width - iconSide) / 2f))
                    : 8f * s;
                var iconBounds = new RectangleF(iconX, Math.Max(1f * s, (Height - iconSide) / 2f), iconSide, iconSide);
                DrawFaviconOrMonogram(e.Graphics, iconBounds, s);
                if (isLoading || isDiscarded || isSuspended || keepAwake)
                {
                    var stateColor = HighContrast
                        ? isSelected ? SystemColors.HighlightText : SystemColors.WindowText
                        : isLoading
                            ? AccentColor
                            : isDiscarded ? NegativeColor : keepAwake ? PositiveColor : MutedTextColor;
                    var dotSize = 5f * s;
                    e.Graphics.FillEllipse(
                        NativeUiTheme.GetSharedBrush(stateColor),
                        iconBounds.Right - (4f * s),
                        iconBounds.Bottom - (4f * s),
                        dotSize,
                        dotSize);
                }
            }
            else if (isLoading || isDiscarded || isSuspended || keepAwake)
            {
                var stateColor = HighContrast
                    ? isSelected ? SystemColors.HighlightText : SystemColors.WindowText
                    : isLoading
                        ? AccentColor
                        : isDiscarded ? NegativeColor : keepAwake ? PositiveColor : MutedTextColor;
                var dotSize = 6f * s;
                e.Graphics.FillEllipse(
                    NativeUiTheme.GetSharedBrush(stateColor),
                    9f * s,
                    Math.Max(1f * s, (Height - dotSize) / 2f),
                    dotSize,
                    dotSize);
            }

            var titleLeft = Math.Max(ScalePx(10), Padding.Left);
            var titleRightReserve = showClose ? ScalePx(29) : ScalePx(10);
            var titleWidth = Width - titleLeft - titleRightReserve;
            if (titleWidth >= ScalePx(12) && !(isCompactNormalTab && hasFaviconVisual))
            {
                var titleBounds = new Rectangle(
                    titleLeft,
                    0,
                    titleWidth,
                    Height);
                var titleColor = HighContrast
                    ? (isSelected || isHovered ? SystemColors.HighlightText : SystemColors.ControlText)
                    : isDragging ? MutedTextColor
                    : isSuspended ? Color.FromArgb(181, 138, 156)
                    : isSelected ? PageTextColor : Color.FromArgb(228, 198, 214);
                TextRenderer.DrawText(
                    e.Graphics,
                    currentTitle,
                    TabTitleFont,
                    titleBounds,
                    titleColor,
                    TextFormatFlags.Left
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix
                    | TextFormatFlags.NoPadding);
            }

            if (showClose)
            {
                var closeBounds = GetCloseButtonBounds();
                if (isCloseHovered || isClosePressed)
                {
                    using var closeFill = new SolidBrush(
                        HighContrast
                            ? SystemColors.Highlight
                            : Color.FromArgb(isClosePressed ? 220 : 182, 182, 48, 92));
                    e.Graphics.FillEllipse(closeFill, closeBounds);
                    if (!HighContrast)
                    {
                        using var closeRim = new Pen(Color.FromArgb(172, 255, 232, 246), 1f * s);
                        e.Graphics.DrawEllipse(closeRim, closeBounds);
                    }
                }

                var crossColor = HighContrast
                    ? (isCloseHovered || isSelected ? SystemColors.HighlightText : SystemColors.ControlText)
                    : (isCloseHovered || isClosePressed) ? Color.White : isSelected ? PageTextColor : MutedTextColor;
                var inset = 5.6f * s;
                using var crossPen = new Pen(crossColor, 1.6f * s)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                e.Graphics.DrawLine(
                    crossPen,
                    closeBounds.Left + inset,
                    closeBounds.Top + inset,
                    closeBounds.Right - inset,
                    closeBounds.Bottom - inset);
                e.Graphics.DrawLine(
                    crossPen,
                    closeBounds.Right - inset,
                    closeBounds.Top + inset,
                    closeBounds.Left + inset,
                    closeBounds.Bottom - inset);
            }

            if (Focused)
            {
                ControlPaint.DrawFocusRectangle(
                    e.Graphics,
                    ClientRectangle,
                    HighContrast && isSelected ? SystemColors.HighlightText : ForeColor,
                    BackColor);
            }
        }

        private void DrawAudioPillBackground(Graphics graphics, RectangleF audioBounds, float scale)
        {
            var pillRect = RectangleF.Inflate(audioBounds, 2f * scale, 2f * scale);
            using var pillPath = NativeUiTheme.CreateRoundedPath(pillRect, 6f * scale);
            if (HighContrast)
            {
                if (isAudioHovered || isAudioPressed)
                {
                    graphics.FillPath(NativeUiTheme.GetSharedBrush(SystemColors.Highlight), pillPath);
                }
                else
                {
                    graphics.DrawPath(
                        NativeUiTheme.GetSharedPen(isSelected ? SystemColors.HighlightText : SystemColors.WindowText, 1f),
                        pillPath);
                }
                return;
            }

            var fillColor = isAudioPressed
                ? Color.FromArgb(196, 138, 52, 108)
                : isAudioHovered
                    ? Color.FromArgb(132, 138, 52, 108)
                    : Color.FromArgb(isSelected ? 78 : 54, 118, 46, 96);
            graphics.FillPath(NativeUiTheme.GetSharedBrush(fillColor), pillPath);
            var rimColor = isAudioHovered || isAudioPressed
                ? Color.FromArgb(156, 255, 224, 244)
                : Color.FromArgb(88, 255, 224, 244);
            graphics.DrawPath(NativeUiTheme.GetSharedPen(rimColor, 0.9f * scale), pillPath);
        }

        private void DrawFaviconOrMonogram(Graphics graphics, RectangleF bounds, float scale)
        {
            if (faviconBitmap is not null)
            {
                var dpi = Math.Max(96, DeviceDpi);
                if (renderedFaviconBitmap is null || renderedFaviconDpi != dpi)
                {
                    RebuildRenderedFavicon();
                }
                if (renderedFaviconBitmap is not null)
                {
                    var destRect = new Rectangle(
                        (int)Math.Round(bounds.X),
                        (int)Math.Round(bounds.Y),
                        Math.Max(1, (int)Math.Round(bounds.Width)),
                        Math.Max(1, (int)Math.Round(bounds.Height)));
                    graphics.DrawImage(
                        renderedFaviconBitmap,
                        destRect,
                        new Rectangle(0, 0, renderedFaviconBitmap.Width, renderedFaviconBitmap.Height),
                        GraphicsUnit.Pixel);
                    return;
                }
            }

            if (string.IsNullOrEmpty(hostMonogram)) return;
            DrawMonogramPill(graphics, bounds, hostMonogram, scale);
        }

        private void DrawMonogramPill(Graphics graphics, RectangleF bounds, string monogram, float scale)
        {
            using var pillPath = NativeUiTheme.CreateRoundedPath(bounds, 5f * scale);
            if (HighContrast)
            {
                graphics.DrawPath(
                    NativeUiTheme.GetSharedPen(isSelected ? SystemColors.HighlightText : SystemColors.WindowText, 1f * scale),
                    pillPath);
                TextRenderer.DrawText(
                    graphics,
                    monogram,
                    TabMonogramFont,
                    Rectangle.Round(bounds),
                    isSelected ? SystemColors.HighlightText : SystemColors.WindowText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                return;
            }

            var topTint = IsPrivateMode
                ? Color.FromArgb(172, 120, 56, 174)
                : Color.FromArgb(172, 142, 54, 112);
            var bottomTint = IsPrivateMode
                ? Color.FromArgb(188, 66, 26, 98)
                : Color.FromArgb(188, 72, 24, 64);
            using (var badgeBrush = new LinearGradientBrush(bounds, topTint, bottomTint, LinearGradientMode.Vertical))
            {
                graphics.FillPath(badgeBrush, pillPath);
            }
            graphics.DrawPath(
                NativeUiTheme.GetSharedPen(
                    Color.FromArgb(145, IsPrivateMode ? NativeUiTheme.Lavender : NativeUiTheme.Focus),
                    0.9f * scale),
                pillPath);
            TextRenderer.DrawText(
                graphics,
                monogram,
                TabMonogramFont,
                Rectangle.Round(bounds),
                PageTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static void DrawAudioGlyph(
            Graphics graphics,
            RectangleF bounds,
            Color color,
            bool muted,
            float scale)
        {
            var centerY = bounds.Top + (bounds.Height / 2f);
            using var pen = new Pen(color, 1.55f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var speaker = audioSpeakerPoints ??= new PointF[6];
            speaker[0] = new PointF(bounds.Left + (1f * scale), centerY - (2f * scale));
            speaker[1] = new PointF(bounds.Left + (4f * scale), centerY - (2f * scale));
            speaker[2] = new PointF(bounds.Left + (7f * scale), centerY - (5f * scale));
            speaker[3] = new PointF(bounds.Left + (7f * scale), centerY + (5f * scale));
            speaker[4] = new PointF(bounds.Left + (4f * scale), centerY + (2f * scale));
            speaker[5] = new PointF(bounds.Left + (1f * scale), centerY + (2f * scale));
            graphics.DrawPolygon(pen, speaker);

            if (muted)
            {
                graphics.DrawLine(pen, bounds.Left + (10f * scale), centerY - (3f * scale), bounds.Right - (1f * scale), centerY + (3f * scale));
                graphics.DrawLine(pen, bounds.Right - (1f * scale), centerY - (3f * scale), bounds.Left + (10f * scale), centerY + (3f * scale));
                return;
            }

            graphics.DrawArc(
                pen,
                bounds.Left + (5.5f * scale),
                centerY - (4f * scale),
                6.5f * scale,
                8f * scale,
                -55f,
                110f);
            graphics.DrawArc(
                pen,
                bounds.Left + (6.5f * scale),
                centerY - (6f * scale),
                8.5f * scale,
                12f * scale,
                -55f,
                110f);
        }

        private void OnPointerDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || pointerTracking || isClosePressed || isAudioPressed) return;
            if (ShouldShowCloseButton() && GetCloseButtonHitBounds().Contains(e.Location))
            {
                isClosePressed = true;
                Capture = true;
                Invalidate();
                return;
            }
            if (ShouldShowAudioButton() && GetAudioButtonHitBounds().Contains(e.Location))
            {
                isAudioPressed = true;
                Capture = true;
                Invalidate();
                return;
            }

            var source = sender as Control ?? this;
            pointerDownScreenLocation = source.PointToScreen(e.Location);
            pointerTracking = true;
            dragStarted = false;
            Capture = true;
        }

        private void OnPointerMove(object? sender, MouseEventArgs e)
        {
            var nextCloseHover = ShouldShowCloseButton() && GetCloseButtonHitBounds().Contains(e.Location);
            var nextAudioHover = !nextCloseHover && ShouldShowAudioButton() && GetAudioButtonHitBounds().Contains(e.Location);
            if (isCloseHovered != nextCloseHover || isAudioHovered != nextAudioHover)
            {
                isCloseHovered = nextCloseHover;
                isAudioHovered = nextAudioHover;
                Invalidate();
            }

            if (isClosePressed || isAudioPressed || !pointerTracking) return;

            var source = sender as Control ?? this;
            var screenLocation = source.PointToScreen(e.Location);
            if (!dragStarted && IsBeyondDragThreshold(screenLocation))
            {
                dragStarted = true;
                DragStarted?.Invoke(this, new TabDragEventArgs(screenLocation));
            }
            if (dragStarted)
            {
                DragMoved?.Invoke(this, new TabDragEventArgs(screenLocation));
            }
        }

        private bool IsBeyondDragThreshold(Point screenLocation)
        {
            var dragSize = SystemInformation.DragSize;
            var dragBounds = new Rectangle(
                pointerDownScreenLocation.X - (dragSize.Width / 2),
                pointerDownScreenLocation.Y - (dragSize.Height / 2),
                dragSize.Width,
                dragSize.Height);
            return !dragBounds.Contains(screenLocation);
        }

        private void OnMouseCaptureChanged(object? sender, EventArgs e)
        {
            isClosePressed = false;
            isAudioPressed = false;
            if (!pointerTracking) return;

            var wasDragging = dragStarted;
            pointerTracking = false;
            dragStarted = false;
            if (wasDragging) DragCanceled?.Invoke(this, EventArgs.Empty);
        }

        private void OnMouseUp(object? sender, MouseEventArgs e)
        {
            if (isClosePressed)
            {
                var insideClose = e.Button == MouseButtons.Left
                    && ShouldShowCloseButton()
                    && GetCloseButtonHitBounds().Contains(e.Location);
                isClosePressed = false;
                Capture = false;
                Invalidate();
                if (insideClose) CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (isAudioPressed)
            {
                var insideAudio = e.Button == MouseButtons.Left
                    && ShouldShowAudioButton()
                    && GetAudioButtonHitBounds().Contains(e.Location);
                isAudioPressed = false;
                Capture = false;
                Invalidate();
                if (insideAudio) AudioToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (e.Button == MouseButtons.Left && pointerTracking)
            {
                var source = sender as Control ?? this;
                var screenLocation = source.PointToScreen(e.Location);
                var wasDragging = dragStarted;
                pointerTracking = false;
                dragStarted = false;
                Capture = false;
                if (wasDragging)
                {
                    DragCompleted?.Invoke(this, new TabDragEventArgs(screenLocation));
                }
                else
                {
                    Activated?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (e.Button == MouseButtons.Middle) CloseRequested?.Invoke(this, EventArgs.Empty);
            else if (e.Button == MouseButtons.Right)
            {
                var point = sender is Control control ? control.PointToScreen(e.Location) : PointToScreen(e.Location);
                ContextRequested?.Invoke(this, new TabContextEventArgs(point));
            }
        }

        private void SetHovered(bool hovered)
        {
            if (isHovered == hovered) return;
            isHovered = hovered;
            ApplyVisualState();
            NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                this,
                "hover",
                hoverProgress,
                hovered ? 1f : 0f,
                value =>
                {
                    hoverProgress = value;
                    Invalidate();
                });
        }

        private void ApplyVisualState()
        {
            var highlighted = HighContrast && (isSelected || isHovered);
            var activeBackground = IsPrivateMode ? Color.FromArgb(48, 20, 56) : TabActiveColor;
            var idleBackground = IsPrivateMode ? Color.FromArgb(24, 11, 25) : TabIdleColor;
            var hoverBackground = IsPrivateMode ? Color.FromArgb(38, 16, 44) : NativeUiTheme.Surface;
            var background = HighContrast
                ? highlighted ? SystemColors.Highlight : SystemColors.Control
                : isDragging ? NativeUiTheme.Pressed
                : isSelected ? activeBackground
                : isHovered ? hoverBackground : idleBackground;
            var hasAudioState = isAudible || isMuted;
            var hasFaviconVisual = HasFaviconVisual();
            var showSplitFaviconWithAudio = hasAudioState && hasFaviconVisual && Width >= ScalePx(115);
            var leftPadding = hasAudioState
                ? (showSplitFaviconWithAudio ? ScalePx(45) : ScalePx(25))
                : hasFaviconVisual
                    ? ScalePx(28)
                    : (isLoading || isDiscarded || isSuspended || keepAwake ? ScalePx(20) : ScalePx(10));
            var nextPadding = new Padding(leftPadding, 0, 0, 0);
            if (Padding != nextPadding) Padding = nextPadding;
            if (BackColor != background) BackColor = background;
            Invalidate();
        }

        private sealed class TabHeaderAccessibleObject(TabHeader owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleRole Role => AccessibleRole.PageTab;

            public override AccessibleStates State => base.State
                | AccessibleStates.Selectable
                | (owner.isSelected ? AccessibleStates.Selected : AccessibleStates.None);
        }
    }

    private sealed class TabContextEventArgs(Point screenLocation) : EventArgs
    {
        public Point ScreenLocation { get; } = screenLocation;
    }

    private sealed class TabDragEventArgs(Point screenLocation) : EventArgs
    {
        public Point ScreenLocation { get; } = screenLocation;
    }

    private sealed class PermissionRequest(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        CoreWebView2PermissionRequestedEventArgs args,
        CoreWebView2Deferral deferral,
        string origin,
        string permission,
        CoreWebView2PermissionKind permissionKind,
        bool isUserInitiated)
    {
        public BrowserTab Tab { get; } = tab;
        public CoreWebView2 Core { get; } = core;
        public long DocumentGeneration { get; } = documentGeneration;
        public CoreWebView2PermissionRequestedEventArgs Args { get; } = args;
        public CoreWebView2Deferral Deferral { get; } = deferral;
        public string Origin { get; } = origin;
        public string Permission { get; } = permission;
        public CoreWebView2PermissionKind PermissionKind { get; } = permissionKind;
        public bool IsUserInitiated { get; } = isUserInitiated;
        private int completionStarted;

        public bool TryBeginCompletion() => Interlocked.Exchange(ref completionStarted, 1) == 0;
    }

    private sealed class DownloadEntry(
        string fileName,
        string filePath,
        string sourceUrl,
        DateTime startedAt,
        string state)
    {
        private string state = state;
        private long bytesReceived;
        private long lastSpeedSampleBytes;
        private long lastSpeedSampleTick = Environment.TickCount64;
        private bool isTerminal;

        public string FileName { get; set; } = fileName;
        public string FilePath { get; set; } = filePath;
        public string SourceUrl { get; } = sourceUrl;
        public DateTime StartedAt { get; } = startedAt;
        public string State
        {
            get => state;
            set
            {
                state = value;
                if (!string.Equals(value, "Downloading", StringComparison.OrdinalIgnoreCase))
                {
                    BytesPerSecond = 0d;
                }
            }
        }
        public long BytesReceived
        {
            get => bytesReceived;
            set
            {
                var next = Math.Max(0L, value);
                var nowTick = Environment.TickCount64;
                if (next > bytesReceived && !isTerminal)
                {
                    var elapsedMs = nowTick - lastSpeedSampleTick;
                    if (elapsedMs >= 100)
                    {
                        var deltaBytes = next - lastSpeedSampleBytes;
                        if (deltaBytes > 0)
                        {
                            var instantRate = (deltaBytes * 1000d) / elapsedMs;
                            BytesPerSecond = BytesPerSecond <= 0d
                                ? instantRate
                                : (BytesPerSecond * 0.65d) + (instantRate * 0.35d);
                        }
                        lastSpeedSampleBytes = next;
                        lastSpeedSampleTick = nowTick;
                    }
                    else if (lastSpeedSampleBytes == 0 && bytesReceived == 0)
                    {
                        lastSpeedSampleBytes = next;
                        lastSpeedSampleTick = nowTick;
                    }
                }
                else if (next < bytesReceived)
                {
                    lastSpeedSampleBytes = next;
                    lastSpeedSampleTick = nowTick;
                    BytesPerSecond = 0d;
                }
                bytesReceived = next;
            }
        }
        public double BytesPerSecond { get; set; }
        public long TotalBytesToReceive { get; set; }
        public bool IsTerminal
        {
            get => isTerminal;
            set
            {
                isTerminal = value;
                if (value) BytesPerSecond = 0d;
            }
        }
        public Action? CancelAction { get; set; }
        public Action? PauseAction { get; set; }
        public Action? ResumeAction { get; set; }
        public bool CanResume { get; set; }
        public string EstimatedEndTimeText { get; set; } = string.Empty;
        public string InterruptReason { get; set; } = string.Empty;
        public CoreWebView2DownloadOperation? Operation { get; set; }
        public EventHandler<object>? StateChangedHandler { get; set; }
        public EventHandler<object>? BytesReceivedChangedHandler { get; set; }
        public EventHandler<object>? EstimatedEndTimeChangedHandler { get; set; }
        public bool? LastKnownFileExists { get; set; }
        public long LastFileExistsCheckedAtTick { get; set; }
    }

    private static class NativeMethods
    {
        public const int WsCaption = 0x00C00000;
        public const int WsThickFrame = 0x00040000;
        public const int WsSysMenu = 0x00080000;
        public const int WsMinimizeBox = 0x00020000;
        public const int WsMaximizeBox = 0x00010000;
        public const int WsExDlgModalFrame = 0x00000001;
        public const int WsExWindowEdge = 0x00000100;
        public const int WsExClientEdge = 0x00000200;
        public const int WmGetMinMaxInfo = 0x0024;
        public const int WmNcMouseMove = 0x00A0;
        public const int WmNcLeftButtonDown = 0x00A1;
        public const int WmNcLeftButtonUp = 0x00A2;
        public const int WmNcCalcSize = 0x0083;
        public const int WmNcHitTest = 0x0084;
        public const int WmNcActivate = 0x0086;
        public const int WmMouseMove = 0x0200;
        public const int WmLeftButtonUp = 0x0202;
        public const int WmCaptureChanged = 0x0215;
        public const int WmEnterSizeMove = 0x0231;
        public const int WmExitSizeMove = 0x0232;
        public const int WmNcMouseLeave = 0x02A2;
        public const int HtClient = 1;
        public const int HtCaption = 2;
        public const int HtLeft = 10;
        public const int HtRight = 11;
        public const int HtTop = 12;
        public const int HtTopLeft = 13;
        public const int HtTopRight = 14;
        public const int HtBottom = 15;
        public const int HtBottomLeft = 16;
        public const int HtBottomRight = 17;
        public const int HtMaxButton = 9;
        public const int MonitorDefaultToNearest = 2;

        private const int SmCxSizeFrame = 32;
        private const int SmCySizeFrame = 33;
        private const int SmCxPaddedBorder = 92;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const int TmeLeave = 0x00000002;
        private const int TmeNonClient = 0x00000010;
        private const uint GaRoot = 2;
        private const uint GaRootOwner = 3;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo plii);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(
            IntPtr window,
            int message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "TrackMouseEvent")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TrackMouseEventNative(ref TrackMouseEventInfo eventInfo);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr BeginDeferWindowPos(int nNumWindows);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr DeferWindowPos(
            IntPtr hWinPosInfo,
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

        public static void ApplyDeferredBounds(ReadOnlySpan<(Control Control, Rectangle Bounds)> placements)
        {
            if (placements.IsEmpty) return;

            var handleCount = 0;
            foreach (ref readonly var item in placements)
            {
                if (!item.Control.IsDisposed && item.Control.IsHandleCreated && item.Control.Bounds != item.Bounds)
                {
                    handleCount++;
                }
            }

            if (handleCount > 1)
            {
                var hdwp = BeginDeferWindowPos(handleCount);
                if (hdwp != IntPtr.Zero)
                {
                    const uint flags = SwpNoZOrder | SwpNoActivate;
                    var deferredAll = true;
                    foreach (ref readonly var item in placements)
                    {
                        if (item.Control.IsDisposed || item.Control.Bounds == item.Bounds) continue;
                        if (!item.Control.IsHandleCreated)
                        {
                            deferredAll = false;
                            continue;
                        }
                        hdwp = DeferWindowPos(
                            hdwp,
                            item.Control.Handle,
                            IntPtr.Zero,
                            item.Bounds.X,
                            item.Bounds.Y,
                            item.Bounds.Width,
                            item.Bounds.Height,
                            flags);
                        if (hdwp == IntPtr.Zero)
                        {
                            deferredAll = false;
                            break;
                        }
                    }

                    if (hdwp != IntPtr.Zero && EndDeferWindowPos(hdwp) && deferredAll)
                    {
                        return;
                    }
                }
            }

            foreach (ref readonly var item in placements)
            {
                if (!item.Control.IsDisposed && item.Control.Bounds != item.Bounds)
                {
                    item.Control.Bounds = item.Bounds;
                }
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static Point PointFromLParam(IntPtr value)
        {
            var packed = value.ToInt64();
            return new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
        }

        public static int GetResizeFrameThickness(int dpi, bool horizontal)
        {
            try
            {
                var frameMetric = horizontal ? SmCxSizeFrame : SmCySizeFrame;
                return Math.Max(4, GetSystemMetricsForDpi(frameMetric, (uint)dpi)
                    + GetSystemMetricsForDpi(SmCxPaddedBorder, (uint)dpi));
            }
            catch (EntryPointNotFoundException)
            {
                var frameMetric = horizontal ? SmCxSizeFrame : SmCySizeFrame;
                return Math.Max(4, GetSystemMetrics(frameMetric) + GetSystemMetrics(SmCxPaddedBorder));
            }
        }

        public static void CaptureMouse(IntPtr window)
        {
            _ = SetCapture(window);
        }

        public static void ReleaseMouseCapture()
        {
            _ = ReleaseCapture();
        }

        public static void BeginWindowMove(IntPtr window)
        {
            _ = ReleaseCapture();
            _ = SendMessage(window, WmNcLeftButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        public static void TrackNonClientMouseLeave(IntPtr window)
        {
            var eventInfo = new TrackMouseEventInfo
            {
                Size = Marshal.SizeOf<TrackMouseEventInfo>(),
                Flags = TmeLeave | TmeNonClient,
                TrackWindow = window
            };
            _ = TrackMouseEventNative(ref eventInfo);
        }

        public static void ApplyBorderlessResizableStyle(IntPtr window)
        {
            var style = GetWindowLongPtr(window, GwlStyle).ToInt64();
            style &= ~WsCaption;
            style |= WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
            _ = SetWindowLongPtr(window, GwlStyle, (IntPtr)style);

            var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            extendedStyle &= ~(WsExClientEdge | WsExWindowEdge | WsExDlgModalFrame);
            _ = SetWindowLongPtr(window, GwlExStyle, (IntPtr)extendedStyle);
            _ = SetWindowPos(
                window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        public static void TryEnableModernWindowFrame(
            IntPtr window,
            Color captionColor,
            Color borderColor,
            Color textColor)
        {
            if (SystemInformation.HighContrast) return;
            try
            {
                var darkMode = 1;
                _ = DwmSetWindowAttribute(window, 20, ref darkMode, sizeof(int));
                var roundedCorners = 2;
                _ = DwmSetWindowAttribute(window, 33, ref roundedCorners, sizeof(int));
                var border = ToColorRef(borderColor);
                _ = DwmSetWindowAttribute(window, 34, ref border, sizeof(int));
                var caption = ToColorRef(captionColor);
                _ = DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
                var text = ToColorRef(textColor);
                _ = DwmSetWindowAttribute(window, 36, ref text, sizeof(int));
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                // DWM is unavailable only on unsupported legacy Windows configurations.
            }
        }

        public static bool TryGetLastInputIdleTime(out TimeSpan idleTime)
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
            {
                idleTime = TimeSpan.Zero;
                return false;
            }
            var elapsedMs = unchecked((uint)Environment.TickCount - info.DwTime);
            idleTime = TimeSpan.FromMilliseconds(elapsedMs);
            return true;
        }

        public static bool IsForegroundWindowOwnedOrChild(IntPtr mainWindow)
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            if (foreground == mainWindow
                || IsChild(mainWindow, foreground)
                || GetAncestor(foreground, GaRoot) == mainWindow
                || GetAncestor(foreground, GaRootOwner) == mainWindow)
            {
                return true;
            }
            _ = GetWindowThreadProcessId(foreground, out var processId);
            return processId == Environment.ProcessId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint DwTime;
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrackMouseEventInfo
        {
            public int Size;
            public int Flags;
            public IntPtr TrackWindow;
            public int HoverTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MonitorInfo
        {
            public int Size;
            public NativeRect MonitorArea;
            public NativeRect WorkArea;
            public int Flags;
        }
    }

    private enum ConnectionState
    {
        Unknown,
        Loading,
        Local,
        LocalFile,
        Secure,
        Insecure,
        CertificateError,
        Failed
    }

    private sealed class IncognitoBadgeControl : Control
    {
        private static readonly Font BadgeFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8.2f);
        private readonly ToolTip toolTip;
        private bool isHovered;

        public IncognitoBadgeControl(ToolTip toolTip)
        {
            this.toolTip = toolTip;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.UserPaint,
                true);
            TabStop = true;
            Size = new Size(82, 26);
            Margin = new Padding(2, 3, 2, 3);
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Incognito browsing";
            AccessibleDescription = "No browsing history, cookies, or site data will be saved.";
            toolTip.SetToolTip(this, "Incognito mode: browsing history, cookies, and site data are deleted when all incognito windows close.");
            MouseEnter += (_, _) => { isHovered = true; Invalidate(); };
            MouseLeave += (_, _) => { isHovered = false; Invalidate(); };
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var highContrast = SystemInformation.HighContrast;
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? (highContrast ? SystemColors.Control : ChromeColor));
            if (Width <= 4 || Height <= 4) return;

            var scale = Math.Max(1f, DeviceDpi / 96f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(2f, Width - 1f), Math.Max(2f, Height - 1f));
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Min(7f * scale, bounds.Height * 0.45f));

            var highlighted = highContrast && (isHovered || Focused);
            var backColor = highContrast
                ? (highlighted ? SystemColors.Highlight : SystemColors.Control)
                : (isHovered ? Color.FromArgb(56, 24, 66) : Color.FromArgb(42, 18, 50));
            var fgColor = highContrast
                ? (highlighted ? SystemColors.HighlightText : SystemColors.ControlText)
                : NativeUiTheme.Lavender;

            if (highContrast)
            {
                var borderColor = Focused && ShowFocusCues ? SystemColors.Highlight : SystemColors.WindowText;
                g.FillPath(NativeUiTheme.GetSharedBrush(backColor), path);
                g.DrawPath(NativeUiTheme.GetSharedPen(borderColor, (Focused && ShowFocusCues ? 1.5f : 1f) * scale), path);
            }
            else
            {
                var topColor = isHovered ? Color.FromArgb(245, 76, 32, 94) : Color.FromArgb(236, 56, 22, 72);
                var bottomColor = isHovered ? Color.FromArgb(250, 44, 18, 58) : Color.FromArgb(244, 30, 12, 42);
                using (var fill = new LinearGradientBrush(bounds, topColor, bottomColor, LinearGradientMode.Vertical))
                {
                    g.FillPath(fill, path);
                }

                var clipState = g.Save();
                try
                {
                    g.SetClip(path, CombineMode.Intersect);
                    var sheenRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, Math.Max(3f * scale, bounds.Height * 0.46f));
                    using var sheen = new LinearGradientBrush(
                        sheenRect,
                        Color.FromArgb(isHovered ? 64 : 44, 246, 232, 255),
                        Color.FromArgb(0, 62, 24, 78),
                        LinearGradientMode.Vertical);
                    g.FillRectangle(sheen, sheenRect);
                }
                finally
                {
                    g.Restore(clipState);
                }

                using var rimBrush = new LinearGradientBrush(
                    bounds,
                    Focused && ShowFocusCues
                        ? NativeUiTheme.Focus
                        : Color.FromArgb(isHovered ? 236 : 196, 240, 206, 255),
                    Color.FromArgb(isHovered ? 196 : 138, NativeUiTheme.Lavender),
                    LinearGradientMode.Vertical);
                using var borderPen = new Pen(rimBrush, (Focused && ShowFocusCues ? 1.5f : 1f) * scale);
                g.DrawPath(borderPen, path);
            }

            // Draw stealth glasses icon
            var lensPen = NativeUiTheme.GetSharedPen(fgColor, Math.Max(1.1f, 1.4f * scale));
            var midY = Height / 2f;
            var lensSize = 7f * scale;
            g.DrawEllipse(lensPen, 8f * scale, midY - (4f * scale), lensSize, lensSize);
            g.DrawEllipse(lensPen, 17f * scale, midY - (4f * scale), lensSize, lensSize);
            g.DrawLine(lensPen, 14f * scale, midY - (2f * scale), 18f * scale, midY - (2f * scale));

            // Draw "Incognito" text
            var textLeft = (int)Math.Round(27f * scale);
            var textBounds = new Rectangle(textLeft, 0, Math.Max(1, Width - textLeft - (int)Math.Round(2f * scale)), Height);
            var badgeFont = NativeUiTheme.GetSharedFontForDpi("Segoe UI Semibold", 8.2f, Math.Max(96, DeviceDpi));
            TextRenderer.DrawText(
                g,
                "Incognito",
                badgeFont,
                textBounds,
                fgColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

            if (Focused && ShowFocusCues)
            {
                var focusBounds = Rectangle.Inflate(ClientRectangle, -2, -2);
                if (focusBounds.Width > 2 && focusBounds.Height > 2)
                {
                    ControlPaint.DrawFocusRectangle(g, focusBounds, fgColor, backColor);
                }
            }
        }
    }
    private sealed class FirstClickSelectTextBox : TextBox
    {
        private const int WmLeftButtonDown = 0x0201;
        private const int WmLeftButtonUp = 0x0202;
        private bool selectAllOnLeftButtonUp;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmLeftButtonDown)
            {
                selectAllOnLeftButtonUp = !Focused;
            }

            base.WndProc(ref message);

            if (message.Msg == WmLeftButtonUp)
            {
                var shouldSelectAll = selectAllOnLeftButtonUp;
                selectAllOnLeftButtonUp = false;
                if (shouldSelectAll && Focused && TextLength > 0) SelectAll();
            }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            selectAllOnLeftButtonUp = false;
            base.OnLostFocus(e);
        }
    }

    private sealed class ChromeMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly DarkMenuColorTable colorTable;

        public ChromeMenuRenderer() : this(new DarkMenuColorTable())
        {
        }

        private ChromeMenuRenderer(DarkMenuColorTable colorTable) : base(colorTable)
        {
            this.colorTable = colorTable;
            RoundedEdges = false;
        }

        public bool HighContrast
        {
            get => colorTable.HighContrast;
            set => colorTable.HighContrast = value;
        }

        protected override void Initialize(ToolStrip toolStrip)
        {
            base.Initialize(toolStrip);
            if (toolStrip is ToolStripDropDown dropDown)
            {
                NativeUiTheme.ApplyMenuWindowFrame(dropDown);
            }
        }

        private static float GetMenuScale(ToolStrip? strip) => Math.Max(1f, (strip?.DeviceDpi ?? 96) / 96f);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            if (HighContrast || e.ToolStrip is null || e.AffectedBounds.Width <= 2 || e.AffectedBounds.Height <= 2)
            {
                base.OnRenderToolStripBackground(e);
                return;
            }

            var baseColor = e.ToolStrip.BackColor.A == 255 && e.ToolStrip.BackColor != Color.Transparent
                ? e.ToolStrip.BackColor
                : ToolbarColor;
            var topColor = NativeUiTheme.BlendColor(baseColor, Color.FromArgb(255, 255, 232, 248), 0.08f);
            var bottomColor = NativeUiTheme.BlendColor(baseColor, Color.FromArgb(255, 16, 6, 18), 0.22f);
            using var surfaceBrush = new LinearGradientBrush(
                e.AffectedBounds,
                topColor,
                bottomColor,
                LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(surfaceBrush, e.AffectedBounds);
            e.Graphics.DrawLine(
                NativeUiTheme.GetSharedPen(Color.FromArgb(28, 255, 236, 248), 1f),
                e.AffectedBounds.Left + 1,
                e.AffectedBounds.Top + 1,
                e.AffectedBounds.Right - 2,
                e.AffectedBounds.Top + 1);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is null || e.ToolStrip.Width <= 2 || e.ToolStrip.Height <= 2) return;
            var borderColor = HighContrast
                ? SystemColors.WindowText
                : Color.FromArgb(156, NativeUiTheme.Border);
            e.Graphics.DrawRectangle(
                NativeUiTheme.GetSharedPen(borderColor, 1f),
                0,
                0,
                e.ToolStrip.Width - 1,
                e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is null || !e.Item.Enabled || (!e.Item.Selected && !e.Item.Pressed)) return;
            if (e.Item.Width <= 6 || e.Item.Height <= 4) return;

            var s = GetMenuScale(e.ToolStrip);
            if (HighContrast)
            {
                var padX = Math.Max(2, (int)Math.Round(2f * s));
                var padY = Math.Max(1, (int)Math.Round(1f * s));
                e.Graphics.FillRectangle(
                    NativeUiTheme.GetSharedBrush(SystemColors.Highlight),
                    new Rectangle(padX, padY, Math.Max(1, e.Item.Width - (padX * 2)), Math.Max(1, e.Item.Height - (padY * 2))));
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var insetX = 4f * s;
            var insetY = 1.5f * s;
            var pillBounds = new RectangleF(
                insetX,
                insetY,
                Math.Max(1f, e.Item.Width - (insetX * 2f)),
                Math.Max(1f, e.Item.Height - (insetY * 2f)));
            using var pillPath = NativeUiTheme.CreateRoundedPath(pillBounds, 6f * s);
            using var fillBrush = new LinearGradientBrush(
                pillBounds,
                Color.FromArgb(e.Item.Pressed ? 204 : 162, 134, 54, 112),
                Color.FromArgb(e.Item.Pressed ? 172 : 124, 72, 24, 66),
                LinearGradientMode.Vertical);
            e.Graphics.FillPath(fillBrush, pillPath);

            var state = e.Graphics.Save();
            try
            {
                e.Graphics.SetClip(pillPath, CombineMode.Intersect);
                var sheenRect = new RectangleF(
                    pillBounds.X,
                    pillBounds.Y,
                    pillBounds.Width,
                    Math.Max(2f * s, pillBounds.Height * 0.46f));
                using var sheenBrush = new LinearGradientBrush(
                    sheenRect,
                    Color.FromArgb(52, 255, 255, 255),
                    Color.FromArgb(0, 255, 232, 246),
                    LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(sheenBrush, sheenRect);
            }
            finally
            {
                e.Graphics.Restore(state);
            }

            e.Graphics.DrawPath(NativeUiTheme.GetSharedPen(Color.FromArgb(148, NativeUiTheme.Focus), 0.9f * s), pillPath);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var s = GetMenuScale(e.ToolStrip);
            var margin = 12f * s;
            if (e.Item is null || e.Item.Width <= (margin * 2f)) return;
            var lineY = e.Item.Height / 2f;
            var color = HighContrast
                ? SystemColors.WindowText
                : Color.FromArgb(96, NativeUiTheme.Border);
            e.Graphics.DrawLine(NativeUiTheme.GetSharedPen(color, Math.Max(1f, s)), margin, lineY, e.Item.Width - margin, lineY);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var rect = e.ImageRectangle;
            if (rect.Width <= 2 || rect.Height <= 2) return;

            var s = GetMenuScale(e.ToolStrip);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var badgeSize = Math.Min(16f * s, Math.Min(rect.Width, rect.Height));
            var badgeRect = new RectangleF(
                rect.X + ((rect.Width - badgeSize) / 2f),
                rect.Y + ((rect.Height - badgeSize) / 2f),
                badgeSize,
                badgeSize);
            using var badgePath = NativeUiTheme.CreateRoundedPath(badgeRect, 4.5f * s);
            if (HighContrast)
            {
                e.Graphics.FillPath(NativeUiTheme.GetSharedBrush(SystemColors.Highlight), badgePath);
            }
            else
            {
                e.Graphics.FillPath(NativeUiTheme.GetSharedBrush(Color.FromArgb(74, NativeUiTheme.Accent)), badgePath);
                e.Graphics.DrawPath(NativeUiTheme.GetSharedPen(Color.FromArgb(186, NativeUiTheme.Accent), 1f * s), badgePath);
            }

            var checkColor = HighContrast ? SystemColors.HighlightText : NativeUiTheme.Accent;
            using var checkPen = new Pen(checkColor, 1.75f * s)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var left = new PointF(badgeRect.Left + (badgeSize * 0.24f), badgeRect.Top + (badgeSize * 0.54f));
            var mid = new PointF(badgeRect.Left + (badgeSize * 0.44f), badgeRect.Top + (badgeSize * 0.74f));
            var right = new PointF(badgeRect.Left + (badgeSize * 0.78f), badgeRect.Top + (badgeSize * 0.28f));
            e.Graphics.DrawLines(checkPen, [left, mid, right]);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var rect = e.ArrowRectangle;
            if (rect.Width <= 2 || rect.Height <= 2) return;

            var s = GetMenuScale(e.Item?.Owner);
            var disabled = e.Item is { Enabled: false };
            var selected = e.Item is { Selected: true };
            var color = HighContrast
                ? disabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : SystemColors.MenuText
                : disabled ? NativeUiTheme.Muted : selected ? NativeUiTheme.Text : NativeUiTheme.SecondaryText;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.55f * s)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var cx = rect.Left + (rect.Width / 2f);
            var cy = rect.Top + (rect.Height / 2f);
            var rx = 2.6f * s;
            var ry = 4.0f * s;
            PointF[] points = e.Direction switch
            {
                ArrowDirection.Left => [new(cx + rx, cy - ry), new(cx - rx, cy), new(cx + rx, cy + ry)],
                ArrowDirection.Up => [new(cx - ry, cy + rx), new(cx, cy - rx), new(cx + ry, cy + rx)],
                ArrowDirection.Down => [new(cx - ry, cy - rx), new(cx, cy + rx), new(cx + ry, cy - rx)],
                _ => [new(cx - rx, cy - ry), new(cx + rx, cy), new(cx - rx, cy + ry)]
            };
            e.Graphics.DrawLines(pen, points);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var hasSemanticTag = e.Item.Tag is Color semanticColor && !semanticColor.IsEmpty;
            if (HighContrast)
            {
                e.TextColor = hasSemanticTag
                    ? SystemColors.MenuText
                    : !e.Item.Enabled
                        ? SystemColors.GrayText
                        : e.Item.Selected ? SystemColors.HighlightText : SystemColors.MenuText;
            }
            else if (hasSemanticTag && e.Item.Tag is Color taggedColor)
            {
                e.TextColor = taggedColor;
            }
            else
            {
                e.TextColor = !e.Item.Enabled
                    ? NativeUiTheme.Muted
                    : e.Item.Selected ? NativeUiTheme.Text : NativeUiTheme.SecondaryText;
            }
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public bool HighContrast { get; set; }

        public override Color ToolStripDropDownBackground => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color ImageMarginGradientBegin => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color ImageMarginGradientMiddle => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color ImageMarginGradientEnd => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color MenuBorder => HighContrast ? SystemColors.WindowText : NativeUiTheme.Border;
        public override Color MenuItemBorder => HighContrast ? SystemColors.Highlight : NativeUiTheme.Focus;
        public override Color MenuItemSelected => HighContrast ? SystemColors.Highlight : NativeUiTheme.Selection;
        public override Color MenuItemSelectedGradientBegin => HighContrast ? SystemColors.Highlight : NativeUiTheme.Selection;
        public override Color MenuItemSelectedGradientEnd => HighContrast ? SystemColors.Highlight : NativeUiTheme.Selection;
        public override Color SeparatorDark => HighContrast ? SystemColors.WindowText : NativeUiTheme.Border;
        public override Color SeparatorLight => HighContrast ? SystemColors.WindowText : NativeUiTheme.Border;
    }

    private enum ChromeIconKind
    {
        None,
        Back,
        Forward,
        Reload,
        Home,
        SiteInfo,
        Favorite,
        Downloads,
        Menu,
        NewTab,
        TabList,
        MatchCase,
        FindPrevious,
        FindNext,
        FindClose
    }

    private sealed class ChromeButton : Button
    {
        private static readonly Color DisabledTextColor = Color.FromArgb(139, 105, 121);
        private static readonly Color HoverSurfaceColor = NativeUiTheme.Hover;
        private static readonly Color PressedSurfaceColor = NativeUiTheme.Pressed;
        private static readonly Color FocusStrokeColor = NativeUiTheme.Focus;
        private static readonly Font MatchCaseFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9.2f);
        private static readonly Font QuestionFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9f);
        private bool isHovered;
        private float hoverProgress;
        private bool isPressed;
        private bool isActiveToggle;
        private ChromeIconKind iconKind;
        private string glyphState = string.Empty;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NativeUiTheme.UiTransitionAnimator.Cancel(this);
            }
            base.Dispose(disposing);
        }

        public ChromeButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.Opaque
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            UseMnemonic = false;
        }

        public ChromeIconKind IconKind
        {
            get => iconKind;
            set
            {
                if (iconKind == value) return;
                iconKind = value;
                if (iconKind != ChromeIconKind.None && Text.Length > 0) Text = string.Empty;
                Invalidate();
            }
        }

        public string GlyphState
        {
            get => glyphState;
            set
            {
                var next = value ?? string.Empty;
                if (glyphState.Equals(next, StringComparison.Ordinal)) return;
                glyphState = next;
                Invalidate();
            }
        }
        public int CornerRadius { get; set; } = 10;
        public bool HighContrast { get; set; }
        public bool IsOmniboxEmbedded { get; set; }

        public bool IsActiveToggle
        {
            get => isActiveToggle;
            set
            {
                if (isActiveToggle == value) return;
                isActiveToggle = value;
                Invalidate();
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            if (iconKind != ChromeIconKind.None && Text.Length > 0)
            {
                Text = string.Empty;
                return;
            }
            base.OnTextChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            isHovered = true;
            NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                this,
                "hover",
                hoverProgress,
                1f,
                value =>
                {
                    hoverProgress = value;
                    Invalidate();
                });
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            isHovered = false;
            isPressed = false;
            NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                this,
                "hover",
                hoverProgress,
                0f,
                value =>
                {
                    hoverProgress = value;
                    Invalidate();
                });
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left) isPressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            isPressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            isPressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            isPressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The hover/idle layers below are translucent. Clear to the opaque
            // owning surface first so repeated paints cannot accumulate stale
            // glyph or legacy Button.Text pixels in the backing buffer.
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var surfaceBounds = new RectangleF(
                0.5f,
                0.5f,
                Math.Max(1, Width - 1f),
                Math.Max(1, Height - 1f));
            if (IsOmniboxEmbedded)
            {
                surfaceBounds.Inflate(-2f, -2f);
            }
            using var surfacePath = NativeUiTheme.CreateRoundedPath(surfaceBounds, Scale(IsOmniboxEmbedded ? Math.Min(CornerRadius, 8) : CornerRadius));
            var hoverT = Math.Clamp(hoverProgress, 0f, 1f);
            var highlighted = HighContrast && Enabled && (isPressed || isHovered || isActiveToggle);
            if (HighContrast)
            {
                if (highlighted)
                {
                    e.Graphics.FillPath(NativeUiTheme.GetSharedBrush(SystemColors.Highlight), surfacePath);
                }
            }
            else if (IsOmniboxEmbedded)
            {
                if (Enabled && (isPressed || isActiveToggle || hoverT > 0.01f))
                {
                    var topAlpha = isPressed ? 168 : isActiveToggle ? 156 : Math.Clamp((int)MathF.Round(112f * hoverT), 1, 112);
                    var bottomAlpha = isPressed ? 132 : isActiveToggle ? 124 : Math.Clamp((int)MathF.Round(82f * hoverT), 1, 82);
                    using var pillBrush = new LinearGradientBrush(
                        surfaceBounds,
                        Color.FromArgb(topAlpha, 128, 52, 106),
                        Color.FromArgb(bottomAlpha, 74, 24, 66),
                        LinearGradientMode.Vertical);
                    e.Graphics.FillPath(pillBrush, surfacePath);
                }
            }
            else if (Enabled)
            {
                var idleTop = Color.FromArgb(42, 255, 224, 244);
                var hoverTop = Color.FromArgb(148, 122, 48, 98);
                var idleBottom = Color.FromArgb(18, 168, 72, 134);
                var hoverBottom = Color.FromArgb(114, 64, 22, 58);

                var topFill = isPressed
                    ? Color.FromArgb(196, 148, 58, 118)
                    : isActiveToggle
                        ? Color.FromArgb(186, 174, 52, 122)
                        : NativeUiTheme.BlendColor(idleTop, hoverTop, hoverT);
                var bottomFill = isPressed
                    ? Color.FromArgb(164, 76, 28, 66)
                    : isActiveToggle
                        ? Color.FromArgb(156, 104, 28, 78)
                        : NativeUiTheme.BlendColor(idleBottom, hoverBottom, hoverT);
                using var glassFill = new LinearGradientBrush(
                    surfaceBounds,
                    topFill,
                    bottomFill,
                    LinearGradientMode.Vertical);
                e.Graphics.FillPath(glassFill, surfacePath);

                var specAlpha = isPressed
                    ? 68
                    : isActiveToggle
                        ? 74
                        : (int)MathF.Round(44f + ((74f - 44f) * hoverT));
                var clipState = e.Graphics.Save();
                try
                {
                    e.Graphics.SetClip(surfacePath, CombineMode.Intersect);
                    var specRect = new RectangleF(
                        surfaceBounds.X,
                        surfaceBounds.Y,
                        Math.Max(1f, surfaceBounds.Width),
                        Math.Max(2f, surfaceBounds.Height * 0.44f));
                    using var specBrush = new LinearGradientBrush(
                        specRect,
                        Color.FromArgb(specAlpha, 255, 255, 255),
                        Color.FromArgb(0, 255, 232, 246),
                        LinearGradientMode.Vertical);
                    e.Graphics.FillRectangle(specBrush, specRect);
                }
                finally
                {
                    e.Graphics.Restore(clipState);
                }
            }
            else
            {
                // Recessed capsule silhouette for disabled non-embedded toolbar buttons
                e.Graphics.FillPath(NativeUiTheme.GetSharedBrush(Color.FromArgb(26, 18, 8, 18)), surfacePath);
                e.Graphics.DrawPath(NativeUiTheme.GetSharedPen(Color.FromArgb(42, NativeUiTheme.Border), Scale(0.9f)), surfacePath);
            }

            if (Enabled && (HighContrast ? (isHovered || isPressed || isActiveToggle || (Focused && ShowFocusCues)) : (!IsOmniboxEmbedded || hoverT > 0.01f || isPressed || isActiveToggle || (Focused && ShowFocusCues))))
            {
                if (HighContrast)
                {
                    e.Graphics.DrawPath(
                        NativeUiTheme.GetSharedPen(
                            Focused && ShowFocusCues ? SystemColors.Highlight : SystemColors.WindowText,
                            Focused && ShowFocusCues ? Scale(2f) : Scale(0.8f)),
                        surfacePath);
                }
                else
                {
                    var rimTopAlpha = Focused && ShowFocusCues
                        ? 230
                        : isActiveToggle
                            ? 218
                            : isPressed
                                ? 195
                                : (int)MathF.Round((IsOmniboxEmbedded ? 0f : 78f) + ((195f - (IsOmniboxEmbedded ? 0f : 78f)) * hoverT));
                    var rimBottomAlpha = Focused && ShowFocusCues
                        ? 175
                        : isActiveToggle
                            ? 196
                            : isPressed
                                ? 108
                                : (int)MathF.Round((IsOmniboxEmbedded ? 0f : 26f) + ((108f - (IsOmniboxEmbedded ? 0f : 26f)) * hoverT));
                    using var rimBrush = new LinearGradientBrush(
                        surfaceBounds,
                        Color.FromArgb(Math.Clamp(rimTopAlpha, 0, 255), 255, 228, 246),
                        Color.FromArgb(Math.Clamp(rimBottomAlpha, 0, 255), NativeUiTheme.Accent),
                        LinearGradientMode.Vertical);
                    using var border = new Pen(rimBrush, (Focused && ShowFocusCues) || isActiveToggle ? Scale(1.35f) : Scale(0.9f));
                    e.Graphics.DrawPath(border, surfacePath);
                }
            }

            var scale = DeviceDpi / 96f;
            var glyphColor = HighContrast
                ? !Enabled ? SystemColors.GrayText : highlighted ? SystemColors.HighlightText : ForeColor
                : Enabled ? ForeColor : DisabledTextColor;
            var glyphSize = Math.Min(21f * scale, Math.Max(12f, Math.Min(Width, Height) - (8f * scale)));
            var glyphBounds = new RectangleF(
                (Width - glyphSize) / 2f,
                (Height - glyphSize) / 2f,
                glyphSize,
                glyphSize);
            DrawIcon(e.Graphics, IconKind, GlyphState, glyphBounds, glyphColor, scale);
        }

        private void DrawIcon(
            Graphics graphics,
            ChromeIconKind kind,
            string text,
            RectangleF bounds,
            Color color,
            float scale)
        {
            if (kind == ChromeIconKind.None)
            {
                TextRenderer.DrawText(
                    graphics,
                    text,
                    Font,
                    ClientRectangle,
                    color,
                    TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding
                    | TextFormatFlags.NoPrefix
                    | TextFormatFlags.SingleLine);
                return;
            }

            var penWidth = Math.Max(1.35f, 1.7f * scale);
            using var pen = new Pen(color, penWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var center = new PointF(
                bounds.Left + (bounds.Width / 2f),
                bounds.Top + (bounds.Height / 2f));

            switch (kind)
            {
                case ChromeIconKind.Back:
                    DrawNavigationArrow(graphics, pen, bounds, forward: false);
                    break;
                case ChromeIconKind.Forward:
                    DrawNavigationArrow(graphics, pen, bounds, forward: true);
                    break;
                case ChromeIconKind.Reload:
                    {
                        if (text.Equals("stop", StringComparison.Ordinal))
                        {
                            using var stopPath = NativeUiTheme.CreateRoundedPath(
                                new RectangleF(
                                    center.X - (bounds.Width * 0.27f),
                                    center.Y - (bounds.Height * 0.27f),
                                    bounds.Width * 0.54f,
                                    bounds.Height * 0.54f),
                                2.5f * scale);
                            using var stopFill = new SolidBrush(color);
                            graphics.FillPath(stopFill, stopPath);
                        }
                        else
                        {
                            var arcBounds = RectangleF.Inflate(bounds, -bounds.Width * 0.17f, -bounds.Height * 0.17f);
                            graphics.DrawArc(pen, arcBounds, -48, 292);
                            var tip = new PointF(
                                center.X + (bounds.Width * 0.34f),
                                center.Y - (bounds.Height * 0.28f));
                            graphics.DrawLines(
                                pen,
                                [
                                    new PointF(tip.X - (bounds.Width * 0.2f), tip.Y - (bounds.Height * 0.02f)),
                                    tip,
                                    new PointF(tip.X - (bounds.Width * 0.02f), tip.Y + (bounds.Height * 0.2f))
                                ]);
                        }
                    }
                    break;
                case ChromeIconKind.Home:
                    var roof = new PointF(
                        center.X,
                        bounds.Top + (bounds.Height * 0.1f));
                    graphics.DrawLines(
                        pen,
                        [
                            new PointF(bounds.Left + (bounds.Width * 0.13f), center.Y - (bounds.Height * 0.02f)),
                            roof,
                            new PointF(bounds.Right - (bounds.Width * 0.13f), center.Y - (bounds.Height * 0.02f))
                        ]);
                    using (var house = NativeUiTheme.CreateRoundedPath(
                        new RectangleF(
                            bounds.Left + (bounds.Width * 0.22f),
                            center.Y - (bounds.Height * 0.02f),
                            bounds.Width * 0.56f,
                            bounds.Height * 0.5f),
                        2f * scale))
                    {
                        graphics.DrawPath(pen, house);
                    }
                    graphics.DrawLine(
                        pen,
                        center.X,
                        bounds.Bottom - (bounds.Height * 0.02f),
                        center.X,
                        bounds.Bottom - (bounds.Height * 0.25f));
                    break;
                case ChromeIconKind.SiteInfo:
                    DrawSiteInfo(graphics, pen, text, bounds, color, scale);
                    break;
                case ChromeIconKind.Favorite:
                    {
                        var star = CreateStarPoints(center, bounds.Width * 0.44f, bounds.Width * 0.2f);
                        if (text.Equals("filled", StringComparison.Ordinal))
                        {
                            using var starFill = new SolidBrush(color);
                            graphics.FillPolygon(starFill, star);
                        }
                        else
                        {
                            graphics.DrawPolygon(pen, star);
                        }
                    }
                    break;
                case ChromeIconKind.Downloads:
                    graphics.DrawLine(pen, center.X, bounds.Top + (bounds.Height * 0.12f), center.X, bounds.Bottom - (bounds.Height * 0.2f));
                    graphics.DrawLines(
                        pen,
                        [
                            new PointF(center.X - (bounds.Width * 0.2f), center.Y + (bounds.Height * 0.12f)),
                            new PointF(center.X, bounds.Bottom - (bounds.Height * 0.2f)),
                            new PointF(center.X + (bounds.Width * 0.2f), center.Y + (bounds.Height * 0.12f))
                        ]);
                    graphics.DrawLine(pen, bounds.Left + (bounds.Width * 0.15f), bounds.Bottom - (bounds.Height * 0.08f), bounds.Right - (bounds.Width * 0.15f), bounds.Bottom - (bounds.Height * 0.08f));
                    if (text.Equals("active", StringComparison.Ordinal))
                    {
                        var dotSize = Math.Max(4f, 5f * scale);
                        using var badgeBrush = new SolidBrush(HighContrast ? SystemColors.Highlight : NativeUiTheme.Accent);
                        graphics.FillEllipse(
                            badgeBrush,
                            bounds.Right - dotSize,
                            bounds.Top,
                            dotSize,
                            dotSize);
                    }
                    break;
                    case ChromeIconKind.Menu:
                    {
                        using var dot = new SolidBrush(color);
                        var dotSize = Math.Max(2f, 2.6f * scale);
                        for (var index = -1; index <= 1; index++)
                        {
                            graphics.FillEllipse(
                                dot,
                                center.X - (dotSize / 2f),
                                center.Y + (index * bounds.Height * 0.25f) - (dotSize / 2f),
                                dotSize,
                                dotSize);
                        }
                    }
                    break;
                case ChromeIconKind.NewTab:
                    {
                        var arm = bounds.Width * 0.30f;
                        graphics.DrawLine(pen, center.X - arm, center.Y, center.X + arm, center.Y);
                        graphics.DrawLine(pen, center.X, center.Y - arm, center.X, center.Y + arm);
                    }
                    break;
                case ChromeIconKind.TabList:
                    {
                        // Vector replacement for the legacy "\u2304" downward chevron glyph.
                        var halfWidth = bounds.Width * 0.28f;
                        var halfHeight = bounds.Height * 0.15f;
                        graphics.DrawLines(
                            pen,
                            [
                                new PointF(center.X - halfWidth, center.Y - halfHeight),
                                new PointF(center.X, center.Y + halfHeight),
                                new PointF(center.X + halfWidth, center.Y - halfHeight)
                            ]);
                    }
                    break;
                case ChromeIconKind.MatchCase:
                    {
                        TextRenderer.DrawText(
                            graphics,
                            "Aa",
                            MatchCaseFont,
                            ClientRectangle,
                            color,
                            TextFormatFlags.HorizontalCenter
                            | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.NoPadding
                            | TextFormatFlags.NoPrefix
                            | TextFormatFlags.SingleLine);
                    }
                    break;
                case ChromeIconKind.FindPrevious:
                    {
                        var halfWidth = bounds.Width * 0.28f;
                        var halfHeight = bounds.Height * 0.16f;
                        graphics.DrawLines(
                            pen,
                            [
                                new PointF(center.X - halfWidth, center.Y + halfHeight),
                                new PointF(center.X, center.Y - halfHeight),
                                new PointF(center.X + halfWidth, center.Y + halfHeight)
                            ]);
                    }
                    break;
                case ChromeIconKind.FindNext:
                    {
                        var halfWidth = bounds.Width * 0.28f;
                        var halfHeight = bounds.Height * 0.16f;
                        graphics.DrawLines(
                            pen,
                            [
                                new PointF(center.X - halfWidth, center.Y - halfHeight),
                                new PointF(center.X, center.Y + halfHeight),
                                new PointF(center.X + halfWidth, center.Y - halfHeight)
                            ]);
                    }
                    break;
                case ChromeIconKind.FindClose:
                    {
                        var arm = bounds.Width * 0.26f;
                        graphics.DrawLine(pen, center.X - arm, center.Y - arm, center.X + arm, center.Y + arm);
                        graphics.DrawLine(pen, center.X + arm, center.Y - arm, center.X - arm, center.Y + arm);
                    }
                    break;
            }
        }

        private static void DrawNavigationArrow(Graphics graphics, Pen pen, RectangleF bounds, bool forward)
        {
            var centerY = bounds.Top + (bounds.Height / 2f);
            var left = bounds.Left + (bounds.Width * 0.16f);
            var right = bounds.Right - (bounds.Width * 0.16f);
            var headX = forward ? right : left;
            var lineStart = forward ? left : right;
            var lineEnd = forward ? right - (bounds.Width * 0.02f) : left + (bounds.Width * 0.02f);
            graphics.DrawLine(pen, lineStart, centerY, lineEnd, centerY);
            var wing = bounds.Width * 0.24f;
            graphics.DrawLines(
                pen,
                [
                    new PointF(headX, centerY),
                    new PointF(headX + (forward ? -wing : wing), centerY - (bounds.Height * 0.24f)),
                    new PointF(headX, centerY),
                    new PointF(headX + (forward ? -wing : wing), centerY + (bounds.Height * 0.24f))
                ]);
        }

        private static void DrawSiteInfo(
            Graphics graphics,
            Pen pen,
            string text,
            RectangleF bounds,
            Color color,
            float scale)
        {
            var center = new PointF(
                bounds.Left + (bounds.Width / 2f),
                bounds.Top + (bounds.Height / 2f));
            if (text == "warning")
            {
                graphics.DrawLines(
                    pen,
                    [
                        new PointF(center.X, bounds.Top + (bounds.Height * 0.08f)),
                        new PointF(bounds.Right - (bounds.Width * 0.08f), bounds.Bottom - (bounds.Height * 0.1f)),
                        new PointF(bounds.Left + (bounds.Width * 0.08f), bounds.Bottom - (bounds.Height * 0.1f)),
                        new PointF(center.X, bounds.Top + (bounds.Height * 0.08f))
                    ]);
                graphics.DrawLine(pen, center.X, center.Y - (bounds.Height * 0.18f), center.X, center.Y + (bounds.Height * 0.12f));
                using var dot = new SolidBrush(color);
                graphics.FillEllipse(dot, center.X - (1.2f * scale), center.Y + (bounds.Height * 0.22f), 2.4f * scale, 2.4f * scale);
            }
            else if (text == "loading")
            {
                using var dot = new SolidBrush(color);
                var dotSize = Math.Max(2f, 2.5f * scale);
                for (var index = -1; index <= 1; index++)
                {
                    graphics.FillEllipse(dot, center.X + (index * bounds.Width * 0.24f) - (dotSize / 2f), center.Y - (dotSize / 2f), dotSize, dotSize);
                }
            }
            else if (text == "unknown")
            {
                graphics.DrawEllipse(pen, bounds);
                TextRenderer.DrawText(
                    graphics,
                    "?",
                    QuestionFont,
                    Rectangle.Round(bounds),
                    color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                using var ring = new Pen(color, Math.Max(1f, 1.3f * scale));
                graphics.DrawEllipse(ring, bounds);
                using var fill = new SolidBrush(color);
                graphics.FillEllipse(fill, center.X - (2.2f * scale), center.Y - (2.2f * scale), 4.4f * scale, 4.4f * scale);
            }
        }

        private static PointF[] CreateStarPoints(PointF center, float outerRadius, float innerRadius)
        {
            var points = new PointF[10];
            for (var index = 0; index < points.Length; index++)
            {
                var radius = index % 2 == 0 ? outerRadius : innerRadius;
                var angle = (-MathF.PI / 2f) + (index * MathF.PI / 5f);
                points[index] = new PointF(
                    center.X + (MathF.Cos(angle) * radius),
                    center.Y + (MathF.Sin(angle) * radius));
            }
            return points;
        }

        private new float Scale(float value)
        {
            return Math.Max(0.5f, value * DeviceDpi / 96f);
        }
    }

    private sealed class PillActionButton : Button
    {
        private bool isHovered;
        private bool isPressed;

        public PillActionButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.Opaque
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            UseMnemonic = false;
        }

        public bool PrivateMode { get; set; }
        public bool HighContrast { get; set; }

        protected override void OnMouseEnter(EventArgs e)
        {
            isHovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            isHovered = false;
            isPressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left) isPressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            isPressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            isPressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            isPressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var bounds = new RectangleF(1f, 1f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 2f));
            var radius = Math.Max(6f, bounds.Height / 2f);
            using var pillPath = NativeUiTheme.CreateRoundedPath(bounds, radius);
            var useHighContrast = HighContrast || SystemInformation.HighContrast;

            if (useHighContrast)
            {
                var highlighted = Enabled && (isHovered || isPressed);
                using var hcFill = new SolidBrush(highlighted ? SystemColors.Highlight : SystemColors.Control);
                e.Graphics.FillPath(hcFill, pillPath);
                using var hcBorder = new Pen(
                    Focused && ShowFocusCues ? SystemColors.Highlight : SystemColors.WindowText,
                    Focused && ShowFocusCues ? 2f : 1f);
                e.Graphics.DrawPath(hcBorder, pillPath);
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    ClientRectangle,
                    !Enabled ? SystemColors.GrayText : highlighted ? SystemColors.HighlightText : SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return;
            }

            var topColor = !Enabled
                ? NativeUiTheme.Surface
                : isPressed
                    ? (PrivateMode ? Color.FromArgb(182, 118, 224) : NativeUiTheme.AccentPressed)
                    : isHovered
                        ? (PrivateMode ? Color.FromArgb(232, 168, 255) : NativeUiTheme.AccentHover)
                        : (PrivateMode ? NativeUiTheme.Lavender : NativeUiTheme.Accent);
            var bottomColor = !Enabled
                ? NativeUiTheme.Chrome
                : isPressed
                    ? (PrivateMode ? Color.FromArgb(148, 86, 194) : Color.FromArgb(204, 72, 130))
                    : isHovered
                        ? (PrivateMode ? Color.FromArgb(208, 134, 248) : NativeUiTheme.BrandCoral)
                        : (PrivateMode ? Color.FromArgb(186, 112, 232) : Color.FromArgb(240, 102, 152));

            using (var fill = new LinearGradientBrush(bounds, topColor, bottomColor, LinearGradientMode.Vertical))
            {
                e.Graphics.FillPath(fill, pillPath);
            }

            if (Enabled)
            {
                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SetClip(pillPath, CombineMode.Intersect);
                    var gloss = new RectangleF(bounds.X, bounds.Y, bounds.Width, Math.Max(4f, bounds.Height * 0.45f));
                    using var glossBrush = new LinearGradientBrush(
                        gloss,
                        Color.FromArgb(isPressed ? 48 : 88, 255, 255, 255),
                        Color.FromArgb(0, 255, 240, 248),
                        LinearGradientMode.Vertical);
                    e.Graphics.FillRectangle(glossBrush, gloss);
                }
                finally
                {
                    e.Graphics.Restore(state);
                }
            }

            var borderColor = Focused && ShowFocusCues
                ? Color.White
                : Color.FromArgb(Enabled ? 196 : 80, 255, 236, 248);
            using var rim = new Pen(borderColor, Focused && ShowFocusCues ? 1.6f : 1f);
            e.Graphics.DrawPath(rim, pillPath);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                Enabled ? ForeColor : NativeUiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    private enum CaptionGlyph
    {
        Minimize,
        Maximize,
        Restore,
        Close
    }

    private sealed class WindowCaptionButton : Button
    {
        private bool isHovered;
        private bool isPressed;
        private float hoverProgress;

        public CaptionGlyph Glyph { get; set; }
        public bool HighContrast { get; set; }

        public void SetNonClientState(bool hovered, bool pressed)
        {
            if (isHovered == hovered && isPressed == pressed) return;
            var hoverChanged = isHovered != hovered;
            isHovered = hovered;
            isPressed = pressed;
            if (hoverChanged)
            {
                NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                    this,
                    "hover",
                    hoverProgress,
                    hovered ? 1f : 0f,
                    value =>
                    {
                        hoverProgress = value;
                        Invalidate();
                    });
            }
            Invalidate();
        }

        public WindowCaptionButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NativeUiTheme.UiTransitionAnimator.Cancel(this);
            }
            base.Dispose(disposing);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            isHovered = true;
            NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                this,
                "hover",
                hoverProgress,
                1f,
                value =>
                {
                    hoverProgress = value;
                    Invalidate();
                });
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            isHovered = false;
            isPressed = false;
            NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                this,
                "hover",
                hoverProgress,
                0f,
                value =>
                {
                    hoverProgress = value;
                    Invalidate();
                });
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) isPressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            isPressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            var scale = Math.Max(1f, DeviceDpi / 96f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var highlighted = HighContrast && (isPressed || isHovered);
            var hoverT = isPressed ? 1f : Math.Clamp(hoverProgress, 0f, 1f);
            var focusBackColor = BackColor;

            if (highlighted || (!HighContrast && (isPressed || hoverT > 0.01f)))
            {
                var padX = 3f * scale;
                var padY = 2.5f * scale;
                var pillRect = new RectangleF(
                    padX,
                    padY,
                    Math.Max(2f, ClientRectangle.Width - (padX * 2f)),
                    Math.Max(2f, ClientRectangle.Height - (padY * 2f)));
                var radius = Math.Min(6f * scale, Math.Min(pillRect.Width, pillRect.Height) / 2f);
                using var pillPath = NativeUiTheme.CreateRoundedPath(pillRect, radius);

                if (HighContrast)
                {
                    g.FillPath(NativeUiTheme.GetSharedBrush(SystemColors.Highlight), pillPath);
                    focusBackColor = SystemColors.Highlight;
                }
                else
                {
                    var isClose = Glyph == CaptionGlyph.Close;
                    Color topColor;
                    Color bottomColor;
                    Color rimTop;
                    Color rimBottom;
                    if (isClose)
                    {
                        var topAlpha = isPressed ? 238 : Math.Clamp((int)MathF.Round(226f * hoverT), 1, 226);
                        var bottomAlpha = isPressed ? 244 : Math.Clamp((int)MathF.Round(234f * hoverT), 1, 234);
                        topColor = isPressed
                            ? Color.FromArgb(topAlpha, 152, 34, 72)
                            : Color.FromArgb(topAlpha, 198, 52, 98);
                        bottomColor = isPressed
                            ? Color.FromArgb(bottomAlpha, 112, 22, 54)
                            : Color.FromArgb(bottomAlpha, 146, 32, 70);
                        rimTop = Color.FromArgb(Math.Clamp((int)MathF.Round(188f * hoverT), 1, 188), 255, 206, 224);
                        rimBottom = Color.FromArgb(Math.Clamp((int)MathF.Round(144f * hoverT), 1, 144), 232, 92, 134);
                    }
                    else
                    {
                        var topAlpha = isPressed ? 204 : Math.Clamp((int)MathF.Round(164f * hoverT), 1, 164);
                        var bottomAlpha = isPressed ? 192 : Math.Clamp((int)MathF.Round(148f * hoverT), 1, 148);
                        topColor = isPressed
                            ? Color.FromArgb(topAlpha, 124, 52, 102)
                            : Color.FromArgb(topAlpha, 102, 44, 88);
                        bottomColor = isPressed
                            ? Color.FromArgb(bottomAlpha, 74, 28, 66)
                            : Color.FromArgb(bottomAlpha, 56, 22, 52);
                        rimTop = Color.FromArgb(Math.Clamp((int)MathF.Round(152f * hoverT), 1, 152), 255, 228, 246);
                        rimBottom = Color.FromArgb(Math.Clamp((int)MathF.Round(96f * hoverT), 1, 96), NativeUiTheme.Accent);
                    }

                    using (var fill = new LinearGradientBrush(pillRect, topColor, bottomColor, LinearGradientMode.Vertical))
                    {
                        g.FillPath(fill, pillPath);
                    }

                    var clipState = g.Save();
                    try
                    {
                        g.SetClip(pillPath, CombineMode.Intersect);
                        var sheenRect = new RectangleF(
                            pillRect.X,
                            pillRect.Y,
                            pillRect.Width,
                            Math.Max(2f * scale, pillRect.Height * 0.45f));
                        var sheenAlpha = Math.Clamp((int)MathF.Round((isClose ? 62f : 50f) * hoverT), 1, 62);
                        using var sheenBrush = new LinearGradientBrush(
                            sheenRect,
                            Color.FromArgb(sheenAlpha, 255, 255, 255),
                            Color.FromArgb(0, 255, 232, 246),
                            LinearGradientMode.Vertical);
                        g.FillRectangle(sheenBrush, sheenRect);
                    }
                    finally
                    {
                        g.Restore(clipState);
                    }

                    using var rimBrush = new LinearGradientBrush(pillRect, rimTop, rimBottom, LinearGradientMode.Vertical);
                    using var rimPen = new Pen(rimBrush, 0.9f * scale);
                    g.DrawPath(rimPen, pillPath);
                    focusBackColor = isPressed ? FlatAppearance.MouseDownBackColor : FlatAppearance.MouseOverBackColor;
                }
            }

            var glyphColor = highlighted
                ? SystemColors.HighlightText
                : (!HighContrast && Glyph == CaptionGlyph.Close && (isHovered || isPressed))
                    ? Color.White
                    : ForeColor;

            var glyphSize = Math.Max(9f, 10f * scale);
            var centerX = ClientRectangle.Left + (ClientRectangle.Width / 2f);
            var centerY = ClientRectangle.Top + (ClientRectangle.Height / 2f);
            var left = centerX - (glyphSize / 2f);
            var top = centerY - (glyphSize / 2f);
            using var pen = new Pen(glyphColor, Math.Max(1f, 1.2f * scale))
            {
                StartCap = LineCap.Square,
                EndCap = LineCap.Square
            };

            switch (Glyph)
            {
                case CaptionGlyph.Minimize:
                    g.DrawLine(pen, left, centerY + (glyphSize * 0.32f), left + glyphSize, centerY + (glyphSize * 0.32f));
                    break;
                case CaptionGlyph.Maximize:
                    g.DrawRectangle(pen, left, top, glyphSize, glyphSize * 0.82f);
                    break;
                case CaptionGlyph.Restore:
                    var offset = Math.Max(2f, 2f * scale);
                    g.DrawRectangle(pen, left + offset, top, glyphSize - offset, glyphSize - offset);
                    g.DrawRectangle(pen, left, top + offset, glyphSize - offset, glyphSize - offset);
                    break;
                case CaptionGlyph.Close:
                    g.DrawLine(pen, left, top, left + glyphSize, top + glyphSize);
                    g.DrawLine(pen, left + glyphSize, top, left, top + glyphSize);
                    break;
            }

            if (Focused && ShowFocusCues)
            {
                var focusBounds = Rectangle.Inflate(ClientRectangle, -4, -4);
                ControlPaint.DrawFocusRectangle(
                    g,
                    focusBounds,
                    HighContrast ? SystemColors.Highlight : glyphColor,
                    focusBackColor);
            }
        }
    }

    private sealed class RoundedPanel : Panel
    {
        private readonly record struct ChromeCacheKey(
            int Width,
            int Height,
            int Dpi,
            bool IsFocused,
            bool IsHovered,
            bool HighContrast,
            int FillArgb,
            int BorderArgb,
            int FocusBorderArgb,
            int ParentBackArgb,
            int CornerRadius);

        private bool isFocused;
        private bool isHovered;
        private float focusProgress;
        private float hoverProgress;
        private Color fillColor = OmniboxColor;
        private Color borderColor = NativeUiTheme.Border;
        private Color focusBorderColor = OmniboxFocusColor;
        private int cornerRadius = 10;
        private bool highContrast;
        private Bitmap? cachedChromeBitmap;
        private ChromeCacheKey cachedChromeKey;
        private bool hasCachedChromeKey;

        public RoundedPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            BackColor = OmniboxColor;
        }

        public Color FillColor
        {
            get => fillColor;
            set
            {
                if (fillColor == value) return;
                fillColor = value;
                Invalidate();
            }
        }

        public Color BorderColor
        {
            get => borderColor;
            set
            {
                if (borderColor == value) return;
                borderColor = value;
                Invalidate();
            }
        }

        public Color FocusBorderColor
        {
            get => focusBorderColor;
            set
            {
                if (focusBorderColor == value) return;
                focusBorderColor = value;
                Invalidate();
            }
        }

        public int CornerRadius
        {
            get => cornerRadius;
            set
            {
                if (cornerRadius == value) return;
                cornerRadius = value;
                Invalidate();
            }
        }

        public bool HighContrast
        {
            get => highContrast;
            set
            {
                if (highContrast == value) return;
                highContrast = value;
                Invalidate();
            }
        }

        public bool IsFocused
        {
            get => isFocused;
            set
            {
                if (isFocused == value) return;
                isFocused = value;
                NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                    this,
                    "focus",
                    focusProgress,
                    value ? 1f : 0f,
                    progress =>
                    {
                        focusProgress = progress;
                        Invalidate();
                    });
            }
        }

        public bool IsHovered
        {
            get => isHovered;
            set
            {
                if (isHovered == value) return;
                isHovered = value;
                NativeUiTheme.UiTransitionAnimator.AnimateFloat(
                    this,
                    "hover",
                    hoverProgress,
                    value ? 1f : 0f,
                    progress =>
                    {
                        hoverProgress = progress;
                        Invalidate();
                    });
            }
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control is not null)
            {
                WireChildHoverTracking(e.Control);
            }
        }

        private void WireChildHoverTracking(Control child)
        {
            child.MouseEnter += (_, _) => IsHovered = true;
            child.MouseLeave += (_, _) => UpdateHoverFromCursor();
            child.ControlAdded += (_, args) =>
            {
                if (args.Control is not null) WireChildHoverTracking(args.Control);
            };
            foreach (Control grandChild in child.Controls)
            {
                WireChildHoverTracking(grandChild);
            }
        }

        private void UpdateHoverFromCursor()
        {
            if (IsDisposed || !IsHandleCreated || !Visible)
            {
                IsHovered = false;
                return;
            }
            var clientPoint = PointToClient(Cursor.Position);
            IsHovered = ClientRectangle.Contains(clientPoint);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            IsHovered = true;
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            UpdateHoverFromCursor();
            base.OnMouseLeave(e);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NativeUiTheme.UiTransitionAnimator.Cancel(this);
                cachedChromeBitmap?.Dispose();
                cachedChromeBitmap = null;
                hasCachedChromeKey = false;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var parentBack = Parent?.BackColor ?? (highContrast ? SystemColors.Window : ToolbarColor);
            if (Width <= 4 || Height <= 4)
            {
                e.Graphics.Clear(parentBack);
                return;
            }

            var dpi = Math.Max(96, DeviceDpi);
            var isAnimating = !highContrast
                && ((focusProgress > 0.001f && focusProgress < 0.999f)
                    || (hoverProgress > 0.001f && hoverProgress < 0.999f));
            if (isAnimating)
            {
                RenderChrome(e.Graphics, Width, Height, dpi, parentBack, focusProgress, hoverProgress);
                return;
            }

            var key = new ChromeCacheKey(
                Width,
                Height,
                dpi,
                isFocused,
                isHovered,
                highContrast,
                fillColor.ToArgb(),
                borderColor.ToArgb(),
                focusBorderColor.ToArgb(),
                parentBack.ToArgb(),
                cornerRadius);
            if (!hasCachedChromeKey || cachedChromeBitmap is null || cachedChromeKey != key)
            {
                cachedChromeBitmap?.Dispose();
                cachedChromeBitmap = null;
                hasCachedChromeKey = false;

                var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                try
                {
                    bitmap.SetResolution(Math.Max(48f, e.Graphics.DpiX), Math.Max(48f, e.Graphics.DpiY));
                    using var renderGraphics = Graphics.FromImage(bitmap);
                    RenderChrome(
                        renderGraphics,
                        Width,
                        Height,
                        dpi,
                        parentBack,
                        isFocused ? 1f : 0f,
                        isHovered ? 1f : 0f);
                    cachedChromeBitmap = bitmap;
                    cachedChromeKey = key;
                    hasCachedChromeKey = true;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }

            e.Graphics.DrawImage(
                cachedChromeBitmap,
                new Rectangle(0, 0, Width, Height),
                new Rectangle(0, 0, cachedChromeBitmap.Width, cachedChromeBitmap.Height),
                GraphicsUnit.Pixel);
        }

        private void RenderChrome(
            Graphics graphics,
            int width,
            int height,
            int dpi,
            Color parentBack,
            float focusT,
            float hoverT)
        {
            graphics.Clear(parentBack);
            var scale = Math.Max(1f, dpi / 96f);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(2f, width - 1f), Math.Max(2f, height - 1f));
            var radius = Math.Min(cornerRadius * scale, Math.Min(bounds.Width, bounds.Height) / 2f);
            using var path = NativeUiTheme.CreateRoundedPath(bounds, radius);
            if (highContrast)
            {
                graphics.FillPath(NativeUiTheme.GetSharedBrush(SystemColors.Window), path);
                graphics.DrawPath(
                    NativeUiTheme.GetSharedPen(
                        isFocused ? SystemColors.Highlight : SystemColors.WindowText,
                        (isFocused ? 2f : 1f) * scale),
                    path);
                return;
            }

            var effectiveFill = hoverT > 0.01f && focusT < 0.99f
                ? NativeUiTheme.BlendColor(fillColor, Color.FromArgb(255, 34, 16, 33), hoverT * (1f - focusT) * 0.45f)
                : fillColor;
            using (var fill = new SolidBrush(effectiveFill))
            {
                graphics.FillPath(fill, path);
            }

            var glowT = MathF.Max(focusT, hoverT * 0.5f);
            var state = graphics.Save();
            try
            {
                graphics.SetClip(path, CombineMode.Intersect);
                var topRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, 3.5f * scale);
                var topAlpha = (int)MathF.Round(44f + ((68f - 44f) * glowT));
                using var topSheen = new LinearGradientBrush(
                    topRect,
                    Color.FromArgb(topAlpha, 255, 240, 250),
                    Color.FromArgb(0, effectiveFill),
                    LinearGradientMode.Vertical);
                graphics.FillRectangle(topSheen, topRect);

                var bottomHeight = 4f * scale;
                var bottomRect = new RectangleF(bounds.X, bounds.Bottom - (3.5f * scale), bounds.Width, bottomHeight);
                var bottomAlpha = (int)MathF.Round(32f + ((54f - 32f) * glowT));
                var bottomTint = focusT > 0.01f
                    ? NativeUiTheme.BlendColor(NativeUiTheme.Accent, focusBorderColor, focusT)
                    : NativeUiTheme.Accent;
                using var bottomCaustic = new LinearGradientBrush(
                    bottomRect,
                    Color.FromArgb(0, effectiveFill),
                    Color.FromArgb(bottomAlpha, bottomTint),
                    LinearGradientMode.Vertical);
                graphics.FillRectangle(bottomCaustic, bottomRect);
            }
            finally
            {
                graphics.Restore(state);
            }

            var outerTopAlpha = (int)MathF.Round(172f + ((235f - 172f) * glowT));
            var outerBottomAlpha = (int)MathF.Round(96f + ((195f - 96f) * glowT));
            var outerBottomColor = NativeUiTheme.BlendColor(NativeUiTheme.Accent, focusBorderColor, focusT);
            using var outerRimBrush = new LinearGradientBrush(
                bounds,
                Color.FromArgb(outerTopAlpha, 255, 228, 246),
                Color.FromArgb(outerBottomAlpha, outerBottomColor),
                LinearGradientMode.Vertical);
            var outerWidth = (1.1f + (0.5f * focusT) + (0.15f * hoverT * (1f - focusT))) * scale;
            using var outerBorder = new Pen(outerRimBrush, outerWidth);
            graphics.DrawPath(outerBorder, path);

            var innerInset = 1.1f * scale;
            var innerBounds = bounds;
            innerBounds.Inflate(-innerInset, -innerInset);
            if (innerBounds.Width > 6f * scale && innerBounds.Height > 6f * scale)
            {
                using var innerPath = NativeUiTheme.CreateRoundedPath(innerBounds, Math.Max(2f, radius - innerInset));
                var innerTopAlpha = (int)MathF.Round(58f + ((95f - 58f) * glowT));
                using var innerRimBrush = new LinearGradientBrush(
                    innerBounds,
                    Color.FromArgb(innerTopAlpha, 255, 255, 255),
                    Color.FromArgb(18, focusBorderColor),
                    LinearGradientMode.Vertical);
                using var innerBorder = new Pen(innerRimBrush, 1f * scale);
                graphics.DrawPath(innerBorder, innerPath);
            }
        }
    }

    private sealed class TransparentPanelHost : Panel
    {
        public TransparentPanelHost()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            TrySetBackColor(this, Color.Transparent, SystemColors.Control);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }
    }

    private sealed class TransparentTableLayoutPanel : TableLayoutPanel
    {
        public TransparentTableLayoutPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            TrySetBackColor(this, Color.Transparent, SystemColors.Control);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }
    }

    /// <summary>
    /// Toolbar status text. Draws itself with GDI so the text is always a single
    /// line, vertically centred in the toolbar row, and truncated with an
    /// ellipsis. A plain autosizing <see cref="Label"/> cannot do this: capping
    /// MaximumSize.Height caps the control and pushes the text to the top of the
    /// row, while removing the cap lets a long status wrap onto a second line.
    /// AutoEllipsis does not prevent that wrap.
    /// </summary>
    private sealed class AnnouncingStatusLabel : Label
    {
        public AnnouncingStatusLabel()
        {
            AutoSize = false;
        }

        public void Announce(string message)
        {
            AccessibleName = $"Browser status: {message}";
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            AccessibilityObject.RaiseLiveRegionChanged();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(Text))
            {
                base.OnPaint(e);
                return;
            }

            // Clear rather than filling with a brush, so a repaint allocates
            // nothing. This control repaints on every status change.
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.EndEllipsis
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.Right
                    | TextFormatFlags.NoPrefix
                    | TextFormatFlags.NoPadding);
        }
    }

    private sealed class FrameSetupWork(
        BrowserTab tab,
        CoreWebView2Frame frame,
        Func<bool> isCurrent,
        Func<Task> execute)
    {
        public BrowserTab Tab { get; } = tab;
        public CoreWebView2Frame Frame { get; } = frame;
        public Func<bool> IsCurrent { get; private set; } = isCurrent;
        public Func<Task> Execute { get; private set; } = execute;

        public void Replace(Func<bool> replacementIsCurrent, Func<Task> replacementExecute)
        {
            IsCurrent = replacementIsCurrent;
            Execute = replacementExecute;
        }
    }

    internal sealed class FrameEventSubscription : IDisposable
    {
        private readonly CoreWebView2Frame frame;
        private readonly EventHandler<object> destroyedHandler;
        private readonly EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStartingHandler;
        private readonly EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoadedHandler;
        private readonly EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreatedHandler;
        private int attachedCount;

        public FrameEventSubscription(
            CoreWebView2Frame frame,
            EventHandler<object> destroyedHandler,
            EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStartingHandler,
            EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoadedHandler,
            EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreatedHandler)
        {
            this.frame = frame;
            this.destroyedHandler = destroyedHandler;
            this.navigationStartingHandler = navigationStartingHandler;
            this.domContentLoadedHandler = domContentLoadedHandler;
            this.frameCreatedHandler = frameCreatedHandler;
        }

        public void Attach()
        {
            frame.Destroyed += destroyedHandler;
            attachedCount = 1;
            frame.NavigationStarting += navigationStartingHandler;
            attachedCount = 2;
            frame.DOMContentLoaded += domContentLoadedHandler;
            attachedCount = 3;
            frame.FrameCreated += frameCreatedHandler;
            attachedCount = 4;
        }

        public void Dispose()
        {
            var count = Interlocked.Exchange(ref attachedCount, 0);
            if (count >= 4) TryDetach(() => frame.FrameCreated -= frameCreatedHandler);
            if (count >= 3) TryDetach(() => frame.DOMContentLoaded -= domContentLoadedHandler);
            if (count >= 2) TryDetach(() => frame.NavigationStarting -= navigationStartingHandler);
            if (count >= 1) TryDetach(() => frame.Destroyed -= destroyedHandler);
        }

        public void AbandonDestroyedFrame()
        {
            // Never call a CoreWebView2Frame member from its Destroyed event.
            // Dropping this subscription's managed owner is sufficient because
            // the native frame is already tearing down all registrations.
            Interlocked.Exchange(ref attachedCount, 0);
        }

        private static void TryDetach(Action detach)
        {
            try { detach(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
        }
    }

}

internal sealed class IncognitoShortcutMessageFilter : IMessageFilter
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var key = (Keys)(int)m.WParam;
            var modifiers = Control.ModifierKeys;
            if (key == Keys.N && (modifiers & (Keys.Control | Keys.Shift)) == (Keys.Control | Keys.Shift))
            {
                if (Form.ActiveForm is MainForm activeWindow && !activeWindow.IsDisposed)
                {
                    activeWindow.OpenPrivateWindow();
                    return true;
                }
                if (Application.OpenForms.OfType<MainForm>().FirstOrDefault(w => !w.IsDisposed) is { } anyWindow)
                {
                    anyWindow.OpenPrivateWindow();
                    return true;
                }
            }
            if (key == Keys.Tab
                && (modifiers & Keys.Control) == Keys.Control
                && (modifiers & Keys.Alt) == 0)
            {
                var direction = (modifiers & Keys.Shift) == Keys.Shift ? -1 : 1;
                if (Form.ActiveForm is MruSwitcherForm activeSwitcher && activeSwitcher.Visible && !activeSwitcher.IsDisposed)
                {
                    activeSwitcher.MoveSelection(direction);
                    return true;
                }
                if (Form.ActiveForm is MainForm activeWindow && !activeWindow.IsDisposed)
                {
                    activeWindow.BeginMruSwitch(direction);
                    return true;
                }
            }
        }
        else if (m.Msg is WM_KEYUP or WM_SYSKEYUP)
        {
            var key = (Keys)(int)m.WParam;
            if (key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey)
            {
                if (Form.ActiveForm is MruSwitcherForm activeSwitcher && activeSwitcher.Visible && !activeSwitcher.IsDisposed)
                {
                    activeSwitcher.CommitSelection();
                    return true;
                }
                if (Form.ActiveForm is MainForm activeWindow && !activeWindow.IsDisposed && activeWindow.TryCommitActiveMruSwitch())
                {
                    return true;
                }
            }
        }
        return false;
    }
}
