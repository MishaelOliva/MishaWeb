using System.Diagnostics;
using System.Drawing.Drawing2D;
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

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var shadowBounds = new RectangleF(3, 3, Width - 6, Height - 5);
            using (var shadowPath = CreateRoundedPath(shadowBounds, 9f))
            using (var shadowBrush = new SolidBrush(Color.FromArgb(76, Color.Black)))
            {
                e.Graphics.FillPath(shadowBrush, shadowPath);
            }

            var tabBounds = new RectangleF(1, 1, Width - 5, Height - 5);
            using (var tabPath = CreateRoundedPath(tabBounds, 9f))
            using (var fillBrush = new SolidBrush(highContrast
                ? Color.FromArgb(232, SystemColors.Highlight)
                : Color.FromArgb(224, NativeUiTheme.SurfaceRaised)))
            using (var borderPen = new Pen(
                highContrast ? SystemColors.HighlightText : AccentColor,
                highContrast ? 2f : 1.5f))
            {
                e.Graphics.FillPath(fillBrush, tabPath);
                e.Graphics.DrawPath(borderPen, tabPath);
            }

            var foreground = highContrast ? SystemColors.HighlightText : PageTextColor;
            var textBounds = new Rectangle(12, 1, Math.Max(1, Width - 42), Math.Max(1, Height - 6));
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

            using var closePen = new Pen(foreground, 1.4f);
            var closeCenterX = Width - 20;
            var closeCenterY = (Height - 3) / 2;
            e.Graphics.DrawLine(closePen, closeCenterX - 3, closeCenterY - 3, closeCenterX + 3, closeCenterY + 3);
            e.Graphics.DrawLine(closePen, closeCenterX + 3, closeCenterY - 3, closeCenterX - 3, closeCenterY + 3);

            using var accentPen = new Pen(highContrast ? SystemColors.HighlightText : AccentColor, 2f);
            e.Graphics.DrawLine(accentPen, 10, Height - 4, Math.Max(10, Width - 12), Height - 4);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class TabHeader : UserControl
    {
        private static readonly Font TabMonogramFont = new("Segoe UI Semibold", 8f, FontStyle.Bold);
        private readonly ToolTip toolTip;
        private bool isSelected;
        private bool isHovered;
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
        private string? hostMonogram;
        private bool isWebTab;

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

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Region = null;
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

        private bool ShouldShowCloseButton() => !isDragging && !isPinned && (isSelected || isHovered || Width >= 170);

        private RectangleF GetCloseButtonBounds() => new(Math.Max(4f, Width - 25f), Math.Max(1f, (Height - 20f) / 2f), 20f, 20f);

        private RectangleF GetCloseButtonHitBounds()
        {
            var bounds = GetCloseButtonBounds();
            bounds.Inflate(3f, 3f);
            return bounds;
        }

        private bool ShouldShowAudioButton() => (isAudible || isMuted) && !isDragging;

        private RectangleF GetAudioButtonBounds() => new(6f, Math.Max(1f, (Height - 16f) / 2f), 16f, 16f);

        private RectangleF GetAudioButtonHitBounds()
        {
            var bounds = GetAudioButtonBounds();
            bounds.Inflate(2f, 4f);
            return bounds;
        }

        private bool HasFaviconVisual() => faviconBitmap is not null || (isWebTab && !string.IsNullOrEmpty(hostMonogram));

        private static GraphicsPath CreateTabRoundedPath(RectangleF bounds, float radius)
        {
            var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            var diameter = safeRadius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }

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

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var effectiveAccent = HighContrast
                ? SystemColors.HighlightText
                : IsPrivateMode ? NativeUiTheme.Lavender : AccentColor;
            var topInset = isMaximized ? 1f : 1.5f;
            var pillBounds = new RectangleF(
                1f,
                topInset,
                Math.Max(1f, Width - 2f),
                Math.Max(1f, Height - topInset - 1.5f));
            var pillRadius = Math.Min(8f, pillBounds.Height / 2f);
            using var pillPath = CreateTabRoundedPath(pillBounds, pillRadius);

            if (HighContrast)
            {
                var highlighted = isSelected || isHovered;
                using var hcFill = new SolidBrush(highlighted ? SystemColors.Highlight : SystemColors.Control);
                e.Graphics.FillPath(hcFill, pillPath);
                using var hcBorder = new Pen(highlighted ? SystemColors.HighlightText : SystemColors.WindowText, isSelected ? 1.5f : 1f);
                e.Graphics.DrawPath(hcBorder, pillPath);
            }
            else
            {
                var topColor = isSelected
                    ? Color.FromArgb(228, IsPrivateMode ? 68 : 94, IsPrivateMode ? 30 : 40, IsPrivateMode ? 88 : 82)
                    : isHovered
                        ? Color.FromArgb(164, 74, 34, 68)
                        : Color.FromArgb(52, 62, 28, 58);
                var bottomColor = isSelected
                    ? Color.FromArgb(242, IsPrivateMode ? 42 : 56, IsPrivateMode ? 18 : 22, IsPrivateMode ? 54 : 52)
                    : isHovered
                        ? Color.FromArgb(182, 42, 18, 42)
                        : Color.FromArgb(28, 36, 16, 38);

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

                    if (isSelected || isHovered)
                    {
                        var specHeight = Math.Max(4f, pillBounds.Height * 0.48f);
                        var specRect = new RectangleF(pillBounds.X, pillBounds.Y, pillBounds.Width, specHeight + 1f);
                        using var specBrush = new LinearGradientBrush(
                            specRect,
                            Color.FromArgb(isSelected ? 74 : 42, 255, 255, 255),
                            Color.FromArgb(0, 255, 232, 246),
                            LinearGradientMode.Vertical);
                        e.Graphics.FillRectangle(specBrush, specRect);

                        if (isSelected)
                        {
                            var causticHeight = Math.Max(4f, pillBounds.Height * 0.28f);
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
                }
                finally
                {
                    e.Graphics.Restore(state);
                }

                using var rimBrush = new LinearGradientBrush(
                    pillBounds,
                    Color.FromArgb(isSelected ? 228 : isHovered ? 138 : 44, 255, 230, 246),
                    Color.FromArgb(isSelected ? 152 : isHovered ? 62 : 16, effectiveAccent),
                    LinearGradientMode.Vertical);
                using var rimPen = new Pen(rimBrush, isSelected ? 1.25f : 0.9f);
                e.Graphics.DrawPath(rimPen, pillPath);

                if (isSelected && pillBounds.Width > 26f)
                {
                    var barWidth = Math.Min(pillBounds.Width - 20f, Math.Max(18f, pillBounds.Width * 0.42f));
                    var barLeft = pillBounds.Left + ((pillBounds.Width - barWidth) / 2f);
                    using var glowBar = new Pen(effectiveAccent, 2f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    e.Graphics.DrawLine(glowBar, barLeft, pillBounds.Bottom - 0.5f, barLeft + barWidth, pillBounds.Bottom - 0.5f);
                }
            }

            if (isDragging)
            {
                using var dragOutline = new Pen(effectiveAccent, 1f)
                {
                    DashStyle = DashStyle.Dash
                };
                e.Graphics.DrawRectangle(dragOutline, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            }

            var hasAudioState = isAudible || isMuted;
            var hasFaviconVisual = HasFaviconVisual();
            if (hasAudioState)
            {
                var audioBounds = GetAudioButtonBounds();
                if (isAudioHovered || isAudioPressed)
                {
                    var pillRect = RectangleF.Inflate(audioBounds, 2f, 2f);
                    using var hoverPath = CreateTabRoundedPath(pillRect, 6f);
                    using var hoverFill = new SolidBrush(
                        HighContrast
                            ? SystemColors.Highlight
                            : Color.FromArgb(isAudioPressed ? 196 : 132, 138, 52, 108));
                    e.Graphics.FillPath(hoverFill, hoverPath);
                }

                var audioColor = HighContrast
                    ? (isAudioHovered || isAudioPressed || isSelected) ? SystemColors.HighlightText : SystemColors.WindowText
                    : isMuted ? Color.FromArgb(244, 198, 132) : PageTextColor;
                DrawAudioGlyph(
                    e.Graphics,
                    audioBounds,
                    audioColor,
                    isMuted);

                if (hasFaviconVisual && Width >= 115)
                {
                    DrawFaviconOrMonogram(
                        e.Graphics,
                        new RectangleF(26f, Math.Max(1f, (Height - 16f) / 2f), 16f, 16f));
                }
            }
            else if (hasFaviconVisual)
            {
                var iconBounds = new RectangleF(8f, Math.Max(1f, (Height - 16f) / 2f), 16f, 16f);
                DrawFaviconOrMonogram(e.Graphics, iconBounds);
                if (isLoading || isDiscarded || isSuspended || keepAwake)
                {
                    var stateColor = HighContrast
                        ? isSelected ? SystemColors.HighlightText : SystemColors.WindowText
                        : isLoading
                            ? AccentColor
                            : isDiscarded ? NegativeColor : keepAwake ? PositiveColor : MutedTextColor;
                    using var stateBrush = new SolidBrush(stateColor);
                    e.Graphics.FillEllipse(stateBrush, iconBounds.Right - 4f, iconBounds.Bottom - 4f, 5f, 5f);
                }
            }
            else if (isLoading || isDiscarded || isSuspended || keepAwake)
            {
                var stateColor = HighContrast
                    ? isSelected ? SystemColors.HighlightText : SystemColors.WindowText
                    : isLoading
                        ? AccentColor
                        : isDiscarded ? NegativeColor : keepAwake ? PositiveColor : MutedTextColor;
                using var stateBrush = new SolidBrush(stateColor);
                e.Graphics.FillEllipse(stateBrush, 9f, Math.Max(1f, (Height - 6f) / 2f), 6f, 6f);
            }

            var showClose = ShouldShowCloseButton();
            var titleLeft = Math.Max(10, Padding.Left);
            var titleRightReserve = showClose ? 29 : 10;
            var titleBounds = new Rectangle(
                titleLeft,
                0,
                Math.Max(1, Width - titleLeft - titleRightReserve),
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
                        using var closeRim = new Pen(Color.FromArgb(172, 255, 232, 246), 1f);
                        e.Graphics.DrawEllipse(closeRim, closeBounds);
                    }
                }

                var crossColor = HighContrast
                    ? (isCloseHovered || isSelected ? SystemColors.HighlightText : SystemColors.ControlText)
                    : (isCloseHovered || isClosePressed) ? Color.White : isSelected ? PageTextColor : MutedTextColor;
                var inset = 5.6f;
                using var crossPen = new Pen(crossColor, 1.6f)
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

        private void DrawFaviconOrMonogram(Graphics graphics, RectangleF bounds)
        {
            if (faviconBitmap is not null)
            {
                using var clipPath = CreateTabRoundedPath(bounds, 4f);
                var state = graphics.Save();
                try
                {
                    graphics.SetClip(clipPath, CombineMode.Intersect);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(faviconBitmap, bounds);
                }
                finally
                {
                    graphics.Restore(state);
                }
                return;
            }

            if (string.IsNullOrEmpty(hostMonogram)) return;
            using var pillPath = CreateTabRoundedPath(bounds, 5f);
            if (HighContrast)
            {
                using var hcBorder = new Pen(isSelected ? SystemColors.HighlightText : SystemColors.WindowText, 1f);
                graphics.DrawPath(hcBorder, pillPath);
                TextRenderer.DrawText(
                    graphics,
                    hostMonogram,
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
            using var badgeRim = new Pen(Color.FromArgb(145, IsPrivateMode ? NativeUiTheme.Lavender : NativeUiTheme.Focus), 0.9f);
            graphics.DrawPath(badgeRim, pillPath);
            TextRenderer.DrawText(
                graphics,
                hostMonogram,
                TabMonogramFont,
                Rectangle.Round(bounds),
                PageTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static void DrawAudioGlyph(
            Graphics graphics,
            RectangleF bounds,
            Color color,
            bool muted)
        {
            var centerY = bounds.Top + (bounds.Height / 2f);
            using var pen = new Pen(color, 1.55f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var speaker = new[]
            {
                new PointF(bounds.Left + 1f, centerY - 2f),
                new PointF(bounds.Left + 4f, centerY - 2f),
                new PointF(bounds.Left + 7f, centerY - 5f),
                new PointF(bounds.Left + 7f, centerY + 5f),
                new PointF(bounds.Left + 4f, centerY + 2f),
                new PointF(bounds.Left + 1f, centerY + 2f)
            };
            graphics.DrawPolygon(pen, speaker);

            if (muted)
            {
                graphics.DrawLine(pen, bounds.Left + 10f, centerY - 3f, bounds.Right - 1f, centerY + 3f);
                graphics.DrawLine(pen, bounds.Right - 1f, centerY - 3f, bounds.Left + 10f, centerY + 3f);
                return;
            }

            graphics.DrawArc(
                pen,
                bounds.Left + 5.5f,
                centerY - 4f,
                6.5f,
                8f,
                -55f,
                110f);
            graphics.DrawArc(
                pen,
                bounds.Left + 6.5f,
                centerY - 6f,
                8.5f,
                12f,
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
            var showSplitFaviconWithAudio = hasAudioState && hasFaviconVisual && Width >= 115;
            var leftPadding = hasAudioState
                ? (showSplitFaviconWithAudio ? 45 : 25)
                : hasFaviconVisual
                    ? 28
                    : (isLoading || isDiscarded || isSuspended || keepAwake ? 20 : 10);
            Padding = new Padding(leftPadding, 0, 0, 0);
            BackColor = background;
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
        public string FileName { get; set; } = fileName;
        public string FilePath { get; set; } = filePath;
        public string SourceUrl { get; } = sourceUrl;
        public DateTime StartedAt { get; } = startedAt;
        public string State { get; set; } = state;
        public long BytesReceived { get; set; }
        public long TotalBytesToReceive { get; set; }
        public bool IsTerminal { get; set; }
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
        private static readonly Font BadgeFont = new("Segoe UI Semibold", 8.2f, FontStyle.Regular);
        private readonly ToolTip toolTip;
        private bool isHovered;

        public IncognitoBadgeControl(ToolTip toolTip)
        {
            this.toolTip = toolTip;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            TabStop = false;
            Size = new Size(82, 26);
            Margin = new Padding(2, 3, 2, 3);
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = "Incognito browsing";
            AccessibleDescription = "No browsing history, cookies, or site data will be saved.";
            toolTip.SetToolTip(this, "Incognito mode: browsing history, cookies, and site data are deleted when all incognito windows close.");
            MouseEnter += (_, _) => { isHovered = true; Invalidate(); };
            MouseLeave += (_, _) => { isHovered = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = CreateRoundedRectanglePath(bounds, 7);
            var backColor = isHovered ? Color.FromArgb(56, 24, 66) : Color.FromArgb(42, 18, 50);
            using var brush = new SolidBrush(backColor);
            using var borderPen = new Pen(isHovered ? NativeUiTheme.Lavender : Color.FromArgb(120, 60, 148), 1f);
            g.FillPath(brush, path);
            g.DrawPath(borderPen, path);

            // Draw stealth glasses icon
            using var lensPen = new Pen(NativeUiTheme.Lavender, 1.4f);
            var midY = Height / 2;
            g.DrawEllipse(lensPen, 8, midY - 4, 7, 7);
            g.DrawEllipse(lensPen, 17, midY - 4, 7, 7);
            g.DrawLine(lensPen, 14, midY - 2, 18, midY - 2);

            // Draw "Incognito" text
            var textBounds = new Rectangle(27, 0, Width - 29, Height);
            TextRenderer.DrawText(
                g,
                "Incognito",
                BadgeFont,
                textBounds,
                NativeUiTheme.Lavender,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
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

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is null || e.ToolStrip.Width <= 2 || e.ToolStrip.Height <= 2) return;
            var borderColor = HighContrast
                ? SystemColors.WindowText
                : Color.FromArgb(156, NativeUiTheme.Border);
            using var pen = new Pen(borderColor, 1f);
            e.Graphics.DrawRectangle(
                pen,
                0,
                0,
                e.ToolStrip.Width - 1,
                e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is null || !e.Item.Enabled || (!e.Item.Selected && !e.Item.Pressed)) return;
            if (e.Item.Width <= 6 || e.Item.Height <= 4) return;

            if (HighContrast)
            {
                using var hcBrush = new SolidBrush(SystemColors.Highlight);
                e.Graphics.FillRectangle(
                    hcBrush,
                    new Rectangle(2, 1, Math.Max(1, e.Item.Width - 4), Math.Max(1, e.Item.Height - 2)));
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var pillBounds = new RectangleF(
                4f,
                1.5f,
                Math.Max(1f, e.Item.Width - 8f),
                Math.Max(1f, e.Item.Height - 3f));
            using var pillPath = NativeUiTheme.CreateRoundedPath(pillBounds, 6f);
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
                    Math.Max(2f, pillBounds.Height * 0.46f));
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

            using var rimPen = new Pen(Color.FromArgb(148, NativeUiTheme.Focus), 0.9f);
            e.Graphics.DrawPath(rimPen, pillPath);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            if (e.Item is null || e.Item.Width <= 24) return;
            var lineY = e.Item.Height / 2f;
            var color = HighContrast
                ? SystemColors.WindowText
                : Color.FromArgb(96, NativeUiTheme.Border);
            using var pen = new Pen(color, 1f);
            e.Graphics.DrawLine(pen, 12f, lineY, e.Item.Width - 12f, lineY);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var rect = e.ImageRectangle;
            if (rect.Width <= 2 || rect.Height <= 2) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var badgeSize = Math.Min(16f, Math.Min(rect.Width, rect.Height));
            var badgeRect = new RectangleF(
                rect.X + ((rect.Width - badgeSize) / 2f),
                rect.Y + ((rect.Height - badgeSize) / 2f),
                badgeSize,
                badgeSize);
            using var badgePath = NativeUiTheme.CreateRoundedPath(badgeRect, 4.5f);
            if (HighContrast)
            {
                using var hcFill = new SolidBrush(SystemColors.Highlight);
                e.Graphics.FillPath(hcFill, badgePath);
            }
            else
            {
                using var fill = new SolidBrush(Color.FromArgb(74, NativeUiTheme.Accent));
                e.Graphics.FillPath(fill, badgePath);
                using var rim = new Pen(Color.FromArgb(186, NativeUiTheme.Accent), 1f);
                e.Graphics.DrawPath(rim, badgePath);
            }

            var checkColor = HighContrast ? SystemColors.HighlightText : NativeUiTheme.Accent;
            using var checkPen = new Pen(checkColor, 1.75f)
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

            var disabled = e.Item is { Enabled: false };
            var selected = e.Item is { Selected: true };
            var color = HighContrast
                ? disabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : SystemColors.MenuText
                : disabled ? NativeUiTheme.Muted : selected ? NativeUiTheme.Text : NativeUiTheme.SecondaryText;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.55f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var cx = rect.Left + (rect.Width / 2f);
            var cy = rect.Top + (rect.Height / 2f);
            var rx = 2.6f;
            var ry = 4.0f;
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
            if (HighContrast)
            {
                e.TextColor = !e.Item.Enabled
                    ? SystemColors.GrayText
                    : e.Item.Selected ? SystemColors.HighlightText : SystemColors.MenuText;
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
        private bool isHovered;
        private bool isPressed;
        private bool isActiveToggle;
        private ChromeIconKind iconKind;
        private string glyphState = string.Empty;

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
            using var surfacePath = CreateRoundedPath(surfaceBounds, Scale(IsOmniboxEmbedded ? Math.Min(CornerRadius, 8) : CornerRadius));
            var highlighted = HighContrast && Enabled && (isPressed || isHovered || isActiveToggle);
            if (HighContrast)
            {
                if (highlighted)
                {
                    using var hcFill = new SolidBrush(SystemColors.Highlight);
                    e.Graphics.FillPath(hcFill, surfacePath);
                }
            }
            else if (IsOmniboxEmbedded)
            {
                if (Enabled && (isPressed || isHovered || isActiveToggle))
                {
                    using var pillBrush = new LinearGradientBrush(
                        surfaceBounds,
                        Color.FromArgb(isPressed ? 168 : isActiveToggle ? 156 : 112, 128, 52, 106),
                        Color.FromArgb(isPressed ? 132 : isActiveToggle ? 124 : 82, 74, 24, 66),
                        LinearGradientMode.Vertical);
                    e.Graphics.FillPath(pillBrush, surfacePath);
                }
            }
            else if (Enabled)
            {
                var topAlpha = isPressed ? 196 : isActiveToggle ? 186 : isHovered ? 148 : 42;
                var bottomAlpha = isPressed ? 164 : isActiveToggle ? 156 : isHovered ? 114 : 18;
                var topFill = isPressed
                    ? Color.FromArgb(topAlpha, 148, 58, 118)
                    : isActiveToggle
                        ? Color.FromArgb(topAlpha, 174, 52, 122)
                        : isHovered
                            ? Color.FromArgb(topAlpha, 122, 48, 98)
                            : Color.FromArgb(topAlpha, 255, 224, 244);
                var bottomFill = isPressed
                    ? Color.FromArgb(bottomAlpha, 76, 28, 66)
                    : isActiveToggle
                        ? Color.FromArgb(bottomAlpha, 104, 28, 78)
                        : isHovered
                            ? Color.FromArgb(bottomAlpha, 64, 22, 58)
                            : Color.FromArgb(bottomAlpha, 168, 72, 134);
                using var glassFill = new LinearGradientBrush(
                    surfaceBounds,
                    topFill,
                    bottomFill,
                    LinearGradientMode.Vertical);
                e.Graphics.FillPath(glassFill, surfacePath);

                var specRect = new RectangleF(
                    surfaceBounds.X + 2f,
                    surfaceBounds.Y + 0.5f,
                    Math.Max(1f, surfaceBounds.Width - 4f),
                    Math.Max(2f, surfaceBounds.Height * 0.44f));
                using var specBrush = new LinearGradientBrush(
                    specRect,
                    Color.FromArgb(isPressed ? 68 : isHovered || isActiveToggle ? 74 : 44, 255, 255, 255),
                    Color.FromArgb(0, 255, 232, 246),
                    LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(specBrush, specRect);
            }

            if (Enabled && (HighContrast ? (isHovered || isPressed || isActiveToggle || (Focused && ShowFocusCues)) : (!IsOmniboxEmbedded || isHovered || isPressed || isActiveToggle || (Focused && ShowFocusCues))))
            {
                if (HighContrast)
                {
                    using var hcBorder = new Pen(
                        Focused && ShowFocusCues ? SystemColors.Highlight : SystemColors.WindowText,
                        Focused && ShowFocusCues ? Scale(2f) : Scale(0.8f));
                    e.Graphics.DrawPath(hcBorder, surfacePath);
                }
                else
                {
                    using var rimBrush = new LinearGradientBrush(
                        surfaceBounds,
                        Color.FromArgb(Focused && ShowFocusCues ? 230 : isActiveToggle ? 218 : isHovered || isPressed ? 195 : 78, 255, 228, 246),
                        Color.FromArgb(Focused && ShowFocusCues ? 175 : isActiveToggle ? 196 : isHovered || isPressed ? 108 : 26, NativeUiTheme.Accent),
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
                            using var stopPath = CreateRoundedPath(
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
                    using (var house = CreateRoundedPath(
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
                        using var caseFont = new Font(
                            "Segoe UI Semibold",
                            Math.Max(7.5f, 9.2f * scale),
                            FontStyle.Regular);
                        TextRenderer.DrawText(
                            graphics,
                            "Aa",
                            caseFont,
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
                    GetQuestionFont(scale),
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

        private static readonly object QuestionFontSync = new();
        private static float cachedQuestionFontScale = -1f;
        private static Font? cachedQuestionFont;

        private static Font GetQuestionFont(float scale)
        {
            lock (QuestionFontSync)
            {
                if (cachedQuestionFont is null || Math.Abs(cachedQuestionFontScale - scale) > 0.001f)
                {
                    var nextFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9f * scale);
                    cachedQuestionFont = nextFont;
                    cachedQuestionFontScale = scale;
                }
                return cachedQuestionFont;
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

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
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

        public CaptionGlyph Glyph { get; set; }
        public bool HighContrast { get; set; }

        public void SetNonClientState(bool hovered, bool pressed)
        {
            if (isHovered == hovered && isPressed == pressed) return;
            isHovered = hovered;
            isPressed = pressed;
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
            var highlighted = HighContrast && (isPressed || isHovered);
            var background = highlighted
                ? SystemColors.Highlight
                : isPressed ? FlatAppearance.MouseDownBackColor
                : isHovered ? FlatAppearance.MouseOverBackColor
                : BackColor;
            var glyphColor = highlighted ? SystemColors.HighlightText : ForeColor;
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, ClientRectangle);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
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
                    e.Graphics.DrawLine(pen, left, centerY + (glyphSize * 0.32f), left + glyphSize, centerY + (glyphSize * 0.32f));
                    break;
                case CaptionGlyph.Maximize:
                    e.Graphics.DrawRectangle(pen, left, top, glyphSize, glyphSize * 0.82f);
                    break;
                case CaptionGlyph.Restore:
                    var offset = Math.Max(2f, 2f * scale);
                    e.Graphics.DrawRectangle(pen, left + offset, top, glyphSize - offset, glyphSize - offset);
                    e.Graphics.DrawRectangle(pen, left, top + offset, glyphSize - offset, glyphSize - offset);
                    break;
                case CaptionGlyph.Close:
                    e.Graphics.DrawLine(pen, left, top, left + glyphSize, top + glyphSize);
                    e.Graphics.DrawLine(pen, left + glyphSize, top, left, top + glyphSize);
                    break;
            }

            if (Focused && ShowFocusCues)
            {
                var focusBounds = Rectangle.Inflate(ClientRectangle, -4, -4);
                ControlPaint.DrawFocusRectangle(
                    e.Graphics,
                    focusBounds,
                    HighContrast ? SystemColors.Highlight : glyphColor,
                    background);
            }
        }
    }

    private sealed class RoundedPanel : Panel
    {
        private bool isFocused;

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

        public Color FillColor { get; set; } = OmniboxColor;
        public Color BorderColor { get; set; } = NativeUiTheme.Border;
        public Color FocusBorderColor { get; set; } = OmniboxFocusColor;
        public int CornerRadius { get; set; } = 10;
        public bool HighContrast { get; set; }

        public bool IsFocused
        {
            get => isFocused;
            set
            {
                if (isFocused == value) return;
                isFocused = value;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Region = null;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? (HighContrast ? SystemColors.Window : ToolbarColor));
            if (Width <= 2 || Height <= 2) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));
            using var path = CreateRoundedPath(bounds, CornerRadius);
            if (HighContrast)
            {
                using var fill = new SolidBrush(SystemColors.Window);
                using var border = new Pen(IsFocused ? SystemColors.Highlight : SystemColors.WindowText, IsFocused ? 2f : 1f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
                return;
            }

            using (var fill = new SolidBrush(FillColor))
            {
                e.Graphics.FillPath(fill, path);
            }

            var state = e.Graphics.Save();
            try
            {
                e.Graphics.SetClip(path, CombineMode.Intersect);
                var topRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, 3.5f);
                using var topSheen = new LinearGradientBrush(
                    topRect,
                    Color.FromArgb(IsFocused ? 68 : 44, 255, 240, 250),
                    Color.FromArgb(0, FillColor),
                    LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(topSheen, topRect);

                var bottomRect = new RectangleF(bounds.X, bounds.Bottom - 3.5f, bounds.Width, 4f);
                using var bottomCaustic = new LinearGradientBrush(
                    bottomRect,
                    Color.FromArgb(0, FillColor),
                    Color.FromArgb(IsFocused ? 54 : 32, NativeUiTheme.Accent),
                    LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(bottomCaustic, bottomRect);
            }
            finally
            {
                e.Graphics.Restore(state);
            }

            using var outerRimBrush = new LinearGradientBrush(
                bounds,
                Color.FromArgb(IsFocused ? 235 : 172, 255, 228, 246),
                Color.FromArgb(IsFocused ? 195 : 96, IsFocused ? FocusBorderColor : NativeUiTheme.Accent),
                LinearGradientMode.Vertical);
            using var outerBorder = new Pen(outerRimBrush, IsFocused ? 1.6f : 1.1f);
            e.Graphics.DrawPath(outerBorder, path);

            var innerBounds = bounds;
            innerBounds.Inflate(-1.1f, -1.1f);
            if (innerBounds.Width > 6f && innerBounds.Height > 6f)
            {
                using var innerPath = CreateRoundedPath(innerBounds, Math.Max(2f, CornerRadius - 1.1f));
                using var innerRimBrush = new LinearGradientBrush(
                    innerBounds,
                    Color.FromArgb(IsFocused ? 95 : 58, 255, 255, 255),
                    Color.FromArgb(18, FocusBorderColor),
                    LinearGradientMode.Vertical);
                using var innerBorder = new Pen(innerRimBrush, 1f);
                e.Graphics.DrawPath(innerBorder, innerPath);
            }
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            var diameter = safeRadius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
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
    private const int WM_SYSKEYDOWN = 0x0104;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var key = (Keys)(int)m.WParam;
            if (key == Keys.N && (Control.ModifierKeys & (Keys.Control | Keys.Shift)) == (Keys.Control | Keys.Shift))
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
        }
        return false;
    }
}
