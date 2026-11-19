using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

internal sealed partial class TrainerForm
{
    private static readonly Color ButtonInk = Color.FromArgb(25, 9, 69);
    private static readonly Lazy<Font[]> GameButtonFonts = new Lazy<Font[]>(() => new[] {
        GameArt.RecipeTitleFont(16), GameArt.RecipeTitleFont(14), GameArt.RecipeTitleFont(13),
        GameArt.RecipeTitleFont(12), GameArt.RecipeTitleFont(11)
    });

    private static void PaintGameControlBackground(Graphics canvas, Control control)
    {
        using (var brush = new SolidBrush(control.Parent == null ? Background : control.Parent.BackColor))
            canvas.FillRectangle(brush, control.ClientRectangle);
    }

    private static void PaintGameLabel(Graphics canvas, Rectangle bounds, string text, Color color,
        StringAlignment alignment = StringAlignment.Center, Image icon = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        string label = text.ToUpper(CultureInfo.CurrentCulture);
        canvas.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        using (StringFormat format = StringFormat.GenericTypographic)
        using (var ink = new SolidBrush(color))
        {
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags = StringFormatFlags.NoWrap;
            format.Trimming = StringTrimming.EllipsisCharacter;

            int iconSize = icon == null ? 0 : Math.Min(20, bounds.Height);
            int gutter = iconSize == 0 ? 0 : iconSize + 5;
            Font font = FitGameButtonFont(canvas, label, bounds.Width - gutter, format);

            if (icon != null && bounds.Width > gutter)
            {
                int labelWidth = (int)Math.Ceiling(canvas.MeasureString(label, font, int.MaxValue, format).Width);
                int start = bounds.Left + Math.Max(0, (bounds.Width - labelWidth - gutter) / 2);
                GameArt.DrawFit(canvas, icon, new RectangleF(start,
                    bounds.Top + (bounds.Height - iconSize) / 2f, iconSize, iconSize));
                bounds = Rectangle.FromLTRB(start + gutter, bounds.Top, bounds.Right, bounds.Bottom);
                format.Alignment = StringAlignment.Near;
            }
            canvas.DrawString(label, font, ink, bounds, format);
        }
    }

    private static Font FitGameButtonFont(Graphics canvas, string label, int width, StringFormat format)
    {
        Font[] fonts = GameButtonFonts.Value;
        foreach (Font candidate in fonts)
            if (canvas.MeasureString(label, candidate, int.MaxValue, format).Width + 3 <= width)
                return candidate;
        return fonts[fonts.Length - 1];
    }

    private sealed class GameButton : Button
    {
        private readonly bool primary;
        private bool hovered;
        private bool mouseDown;
        private bool spaceDown;

        internal GameButton(bool primary)
        {
            this.primary = primary;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            Graphics canvas = args.Graphics;
            PaintGameControlBackground(canvas, this);
            bool pressed = Enabled && (mouseDown && hovered || spaceDown);
            bool highlighted = Enabled && (primary || hovered || Focused && ShowFocusCues || pressed);
            GameArt.DrawButtonSkin(canvas, ClientRectangle, highlighted ? "Orange" : "Blue", pressed,
                Enabled, primary && hovered && !pressed);
            Rectangle content = Rectangle.Inflate(ClientRectangle, -7, -4);
            content.Offset(0, pressed ? 1 : -1);
            PaintGameLabel(canvas, content, Text, !Enabled ? Muted : highlighted ? ButtonInk : TextColor, icon: Image);
            if (Enabled && Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(canvas, Rectangle.Inflate(ClientRectangle, -4, -4), ButtonInk, Accent);
        }

        protected override void OnMouseEnter(EventArgs args)
        {
            base.OnMouseEnter(args);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs args)
        {
            base.OnMouseLeave(args);
            hovered = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button == MouseButtons.Left)
                mouseDown = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            mouseDown = false;
            base.OnMouseUp(args);
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs args)
        {
            if (!Capture)
                mouseDown = false;
            base.OnMouseCaptureChanged(args);
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs args)
        {
            if (args.KeyCode == Keys.Space)
                spaceDown = true;
            base.OnKeyDown(args);
            Invalidate();
        }

        protected override void OnKeyUp(KeyEventArgs args)
        {
            if (args.KeyCode == Keys.Space)
                spaceDown = false;
            base.OnKeyUp(args);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs args)
        {
            spaceDown = false;
            base.OnLostFocus(args);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs args)
        {
            mouseDown = false;
            spaceDown = false;
            base.OnEnabledChanged(args);
            Invalidate();
        }
    }

    private sealed class ToggleSwitch : CheckBox
    {
        private readonly Image art;
        private readonly bool pixelArt;

        public ToggleSwitch(Image art, bool pixelArt)
        {
            this.art = art;
            this.pixelArt = pixelArt;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.CheckButton;
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            Graphics canvas = args.Graphics;
            PaintGameControlBackground(canvas, this);
            bool pressed = Enabled && Capture && (MouseButtons & MouseButtons.Left) != 0 &&
                ClientRectangle.Contains(PointToClient(Cursor.Position));
            GameArt.DrawButtonSkin(canvas, ClientRectangle, "Blue", pressed, Enabled);

            int badgeWidth = Math.Max(47, Height * 7 / 6);
            var badge = new Rectangle(Width - badgeWidth - 10, 7, badgeWidth, Math.Max(1, Height - 16));
            var badgeLabel = Rectangle.Inflate(badge, -4, -2);
            GameArt.DrawButtonSkin(canvas, badge, Checked ? "Green" : "Blue", pressed, Enabled);
            PaintGameLabel(canvas, badgeLabel, Checked ? "On" : "Off",
                !Enabled ? Muted : Checked ? ButtonInk : TextColor);

            int glyph = Math.Max(1, Height * 5 / 11);
            canvas.InterpolationMode = pixelArt ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            GameArt.DrawFit(canvas, art, new RectangleF(12, (Height - glyph) / 2f - 1, glyph, glyph));
            Rectangle label = Rectangle.FromLTRB(12 + glyph + 9, 0, badge.Left - 7, Height - 3);
            PaintGameLabel(canvas, label, AccessibleName, Enabled ? TextColor : Muted, StringAlignment.Near);
            if (Enabled && Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(canvas, Rectangle.Inflate(ClientRectangle, -4, -4), TextColor, Surface);
        }
    }
}
