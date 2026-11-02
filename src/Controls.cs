using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace JellyScale
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(15, 20, 40);
        public static readonly Color Card = Color.FromArgb(24, 31, 61);
        public static readonly Color CardBorder = Color.FromArgb(40, 49, 96);
        public static readonly Color Input = Color.FromArgb(19, 25, 52);
        public static readonly Color Text = Color.FromArgb(243, 244, 255);
        public static readonly Color Muted = Color.FromArgb(166, 173, 214);
        public static readonly Color Faint = Color.FromArgb(118, 126, 176);
        public static readonly Color Violet = Color.FromArgb(124, 92, 255);
        public static readonly Color Cyan = Color.FromArgb(34, 190, 240);
        public static readonly Color Green = Color.FromArgb(52, 211, 153);
        public static readonly Color Amber = Color.FromArgb(251, 191, 36);
        public static readonly Color Red = Color.FromArgb(248, 113, 113);

        /// <summary>Layout scale (1.0 at 96 DPI). Fonts scale on their own because they are in points.</summary>
        public static float Scale = 1f;

        public static int S(float v) { return (int)Math.Round(v * Scale); }
        public static Rectangle R(float x, float y, float w, float h) { return new Rectangle(S(x), S(y), S(w), S(h)); }

        static bool? hasSemibold;

        public static Font Regular(float size) { return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point); }

        public static Font Semibold(float size)
        {
            if (hasSemibold == null)
            {
                try { using (new FontFamily("Segoe UI Semibold")) hasSemibold = true; }
                catch (ArgumentException) { hasSemibold = false; }
            }
            return hasSemibold.Value ? new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point)
                                     : new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Label Label(string text, Font font, Color fore, Color back, ContentAlignment align)
        {
            return new Label
            {
                Text = text, Font = font, ForeColor = fore, BackColor = back, TextAlign = align,
                AutoSize = false, UseMnemonic = false
            };
        }
    }

    /// <summary>Rounded card. Children should use the same BackColor as Fill.</summary>
    class RoundPanel : Panel
    {
        Color fill = Theme.Card, border = Theme.CardBorder;
        public float Radius = 14;

        public RoundPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = fill;
        }

        public Color Fill { get { return fill; } set { fill = value; BackColor = value; foreach (Control c in Controls) if (!(c is FlatButton)) c.BackColor = value; Invalidate(true); } }
        public Color Border { get { return border; } set { border = value; Invalidate(); } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius * Theme.Scale))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(border, Math.Max(1f, Theme.Scale)))
            {
                g.FillPath(b, path);
                if (border.A > 0) g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Flat, rounded, keyboard-accessible button.</summary>
    class FlatButton : Control
    {
        public enum Kind { Primary, Secondary, Ghost, Dots }
        public Kind Style = Kind.Secondary;
        bool hover, down;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = Theme.Semibold(9.5f);
            ForeColor = Theme.Text;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && Enabled) { e.Handled = true; OnClick(EventArgs.Empty); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Style == Kind.Dots ? rect.Height / 2 : 10 * Theme.Scale;
            Color text = ForeColor;

            using (var path = Theme.RoundRect(rect, radius))
            {
                if (Style == Kind.Primary)
                {
                    if (!Enabled)
                    {
                        using (var b = new SolidBrush(Color.FromArgb(44, 52, 96))) g.FillPath(b, path);
                        text = Theme.Faint;
                    }
                    else
                    {
                        float k = down ? -0.12f : hover ? 0.12f : 0f;
                        Color a = k >= 0 ? Theme.Mix(Theme.Violet, Color.White, k) : Theme.Mix(Theme.Violet, Color.Black, -k);
                        Color c = k >= 0 ? Theme.Mix(Theme.Cyan, Color.White, k) : Theme.Mix(Theme.Cyan, Color.Black, -k);
                        using (var b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), a, c, LinearGradientMode.Horizontal)) g.FillPath(b, path);
                        text = Color.White;
                    }
                }
                else if (Style == Kind.Secondary)
                {
                    Color fill = !Enabled ? Color.FromArgb(32, 39, 76) : down ? Color.FromArgb(30, 37, 74) : hover ? Color.FromArgb(52, 62, 120) : Color.FromArgb(40, 48, 94);
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    if (!Enabled) text = Theme.Faint;
                }
                else
                {
                    if (hover || down || Focused)
                        using (var b = new SolidBrush(down ? Color.FromArgb(36, 44, 86) : Color.FromArgb(32, 40, 80))) g.FillPath(b, path);
                    if (!Enabled) text = Theme.Faint;
                }
            }

            if (Style == Kind.Dots)
            {
                float r = Math.Max(1.6f, 2.2f * Theme.Scale), gap = 6.5f * Theme.Scale, cx = Width / 2f, cy = Height / 2f;
                using (var b = new SolidBrush(hover ? Theme.Text : Theme.Muted))
                    for (int i = -1; i <= 1; i++) g.FillEllipse(b, cx + i * gap - r, cy - r, 2 * r, 2 * r);
            }
            else
            {
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                using (var b = new SolidBrush(text))
                    g.DrawString(Text, Font, b, new RectangleF(0, 0, Width, Height), sf);
            }

            if (Focused && ShowFocusCues)
            {
                var fr = new RectangleF(2.5f * Theme.Scale, 2.5f * Theme.Scale, Width - 5f * Theme.Scale - 1, Height - 5f * Theme.Scale - 1);
                using (var path = Theme.RoundRect(fr, Math.Max(2, radius - 3 * Theme.Scale)))
                using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), 1) { DashStyle = DashStyle.Dot })
                    g.DrawPath(p, path);
            }
        }
    }

    /// <summary>The big round ON/OFF button with a subtle breathing glow while ON.</summary>
    class PowerButton : Control
    {
        public enum Mode { Off, On, Busy, Attention }
        Mode mode = Mode.Off;
        bool hover, down;
        float phase;
        public event EventHandler Activated;

        public PowerButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Turn JellyScale on or off";
        }

        public Mode State
        {
            get { return mode; }
            set { if (mode == value) return; mode = value; Cursor = mode == Mode.Busy ? Cursors.AppStarting : (hover ? Cursors.Hand : Cursors.Default); Invalidate(); }
        }

        public bool Animating { get { return mode == Mode.Busy || mode == Mode.On; } }

        public void Tick(float seconds) { phase += seconds; Invalidate(); }

        RectangleF Circle()
        {
            float d = Math.Min(Width, Height) * 0.80f;
            return new RectangleF((Width - d) / 2f, (Height - d) / 2f, d, d);
        }

        bool Inside(Point p)
        {
            var c = Circle();
            float dx = p.X - (c.X + c.Width / 2), dy = p.Y - (c.Y + c.Height / 2);
            return dx * dx + dy * dy <= (c.Width / 2) * (c.Width / 2);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = Inside(e.Location);
            if (h != hover) { hover = h; Cursor = mode == Mode.Busy ? Cursors.AppStarting : (h ? Cursors.Hand : Cursors.Default); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Inside(e.Location)) { down = true; Focus(); Invalidate(); } base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = down && Inside(e.Location) && e.Button == MouseButtons.Left;
            down = false; Invalidate();
            base.OnMouseUp(e);
            if (fire) Fire();
        }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.Handled = true; Fire(); } base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        void Fire() { if (Enabled && mode != Mode.Busy && Activated != null) Activated(this, EventArgs.Empty); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var c = Circle();
            float cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2, D = c.Width;
            float ring = Math.Max(3f, D * 0.045f);

            // Glow
            if (mode == Mode.On || (mode == Mode.Off && hover) || mode == Mode.Attention)
            {
                float breathe = mode == Mode.On ? 0.75f + 0.25f * (float)Math.Sin(phase * 2.1) : 0.45f;
                Color glow = mode == Mode.Attention ? Theme.Amber : mode == Mode.On ? Theme.Mix(Theme.Violet, Theme.Cyan, 0.5f + 0.5f * (float)Math.Sin(phase * 0.9)) : Theme.Violet;
                float gr = Math.Min(Width, Height) / 2f;
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
                    using (var pb = new PathGradientBrush(path))
                    {
                        pb.CenterPoint = new PointF(cx, cy);
                        pb.CenterColor = Color.FromArgb((int)(150 * breathe), glow);
                        pb.SurroundColors = new[] { Color.FromArgb(0, glow) };
                        var blend = new ColorBlend(3);
                        blend.Colors = new[] { Color.FromArgb(0, glow), Color.FromArgb((int)(70 * breathe), glow), Color.FromArgb((int)(150 * breathe), glow) };
                        blend.Positions = new[] { 0f, 0.35f, 1f };
                        pb.InterpolationColors = blend;
                        g.FillEllipse(pb, cx - gr, cy - gr, gr * 2, gr * 2);
                    }
                }
            }

            // Body
            Color body = mode == Mode.On ? Color.FromArgb(31, 39, 86) : hover && mode != Mode.Busy ? Color.FromArgb(32, 40, 82) : Color.FromArgb(25, 32, 66);
            if (down) body = Theme.Mix(body, Color.Black, 0.2f);
            using (var b = new SolidBrush(body)) g.FillEllipse(b, c);

            var ringRect = RectangleF.Inflate(c, -ring / 2, -ring / 2);
            if (mode == Mode.On)
            {
                using (var lb = new LinearGradientBrush(c, Theme.Violet, Theme.Cyan, 45f))
                using (var p = new Pen(lb, ring)) g.DrawEllipse(p, ringRect);
            }
            else
            {
                Color rc = mode == Mode.Attention ? Theme.Amber : hover && mode == Mode.Off ? Color.FromArgb(78, 90, 160) : Color.FromArgb(46, 56, 108);
                using (var p = new Pen(rc, ring)) g.DrawEllipse(p, ringRect);
                if (mode == Mode.Busy)
                {
                    using (var lb = new LinearGradientBrush(c, Theme.Violet, Theme.Cyan, 45f + phase * 200f))
                    using (var p = new Pen(lb, ring) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(p, ringRect, (phase * 300f) % 360f, 110f);
                }
            }

            // Power glyph
            Color glyph = mode == Mode.On ? Color.White : mode == Mode.Busy ? Theme.Faint : mode == Mode.Attention ? Theme.Amber : hover ? Theme.Text : Theme.Muted;
            float gr2 = D * 0.17f, gw = Math.Max(2.5f, D * 0.05f);
            using (var p = new Pen(glyph, gw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(p, cx - gr2, cy - gr2 + D * 0.02f, gr2 * 2, gr2 * 2, -58, 296);
                g.DrawLine(p, cx, cy - gr2 - D * 0.045f, cx, cy - D * 0.02f);
            }

            if (Focused && ShowFocusCues)
            {
                using (var p = new Pen(Color.FromArgb(180, 255, 255, 255), 1) { DashStyle = DashStyle.Dot })
                    g.DrawEllipse(p, RectangleF.Inflate(c, 4 * Theme.Scale, 4 * Theme.Scale));
            }
        }
    }

    /// <summary>One line of the server check list.</summary>
    class CheckRow : Control
    {
        CheckStatus status = CheckStatus.Pending;
        float phase;

        public CheckRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Regular(9.5f);
        }

        public CheckStatus Status { get { return status; } set { status = value; Invalidate(); } }
        public void Tick(float s) { phase += s; if (status == CheckStatus.Running) Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = Height * 0.68f, x = 1, y = (Height - d) / 2f;
            var icon = new RectangleF(x, y, d, d);
            float w = Math.Max(1.6f, d * 0.12f);
            Color color = status == CheckStatus.Ok ? Theme.Green : status == CheckStatus.Warn ? Theme.Amber : status == CheckStatus.Fail ? Theme.Red : Theme.Faint;

            if (status == CheckStatus.Pending)
            {
                using (var p = new Pen(Color.FromArgb(70, 80, 140), w)) g.DrawEllipse(p, RectangleF.Inflate(icon, -w / 2, -w / 2));
            }
            else if (status == CheckStatus.Running)
            {
                using (var p = new Pen(Color.FromArgb(55, 64, 120), w)) g.DrawEllipse(p, RectangleF.Inflate(icon, -w / 2, -w / 2));
                using (var p = new Pen(Theme.Cyan, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, RectangleF.Inflate(icon, -w / 2, -w / 2), (phase * 360f) % 360f, 100);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(48, color))) g.FillEllipse(b, icon);
                using (var p = new Pen(color, w * 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float cx = icon.X + d / 2, cy = icon.Y + d / 2, k = d * 0.2f;
                    if (status == CheckStatus.Ok) g.DrawLines(p, new[] { new PointF(cx - k * 1.1f, cy + k * 0.05f), new PointF(cx - k * 0.25f, cy + k * 0.9f), new PointF(cx + k * 1.2f, cy - k * 0.85f) });
                    else if (status == CheckStatus.Fail) { g.DrawLine(p, cx - k, cy - k, cx + k, cy + k); g.DrawLine(p, cx + k, cy - k, cx - k, cy + k); }
                    else { g.DrawLine(p, cx, cy - k * 1.1f, cx, cy + k * 0.25f); g.DrawLine(p, cx, cy + k * 1.05f, cx, cy + k * 1.1f); }
                }
            }

            Color tc = status == CheckStatus.Pending ? Theme.Muted : Theme.Text;
            var tr = new Rectangle((int)(d + 10 * Theme.Scale), 0, Width - (int)(d + 10 * Theme.Scale), Height);
            TextRenderer.DrawText(g, Text, Font, tr, tc, Parent != null ? Parent.BackColor : Theme.Card,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Hi = Color.FromArgb(48, 58, 112);
        public override Color MenuItemSelected { get { return Hi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
        public override Color MenuItemPressedGradientBegin { get { return Hi; } }
        public override Color MenuItemPressedGradientEnd { get { return Hi; } }
        public override Color MenuItemBorder { get { return Hi; } }
        public override Color MenuBorder { get { return Theme.CardBorder; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.CardBorder; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }

    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
            base.OnRenderItemText(e);
        }
    }
}
