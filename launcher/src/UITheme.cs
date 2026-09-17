using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeepSeekHarnessLauncher
{
    // =====================================================================
    //  主题色板 —— 深色现代风格（蓝紫品牌主色 + 青绿强调）
    // =====================================================================
    public static class Theme
    {
        // 背景层级
        public static readonly Color WindowBg   = Color.FromArgb(18, 20, 30);   // 主窗口背景
        public static readonly Color SidebarBg  = Color.FromArgb(13, 15, 23);   // 侧栏
        public static readonly Color CardBg     = Color.FromArgb(31, 35, 52);   // 卡片
        public static readonly Color CardBorder = Color.FromArgb(58, 66, 94);   // 卡片描边
        public static readonly Color InputBg    = Color.FromArgb(38, 43, 62);   // 输入框
        public static readonly Color HoverBg    = Color.FromArgb(48, 55, 82);   // 悬停

        // 文字
        public static readonly Color TextMain   = Color.FromArgb(232, 236, 244);
        public static readonly Color TextSub    = Color.FromArgb(150, 158, 180);
        public static readonly Color TextFaint  = Color.FromArgb(105, 113, 138);

        // 品牌与状态
        public static readonly Color Accent     = Color.FromArgb(104, 122, 255);   // 蓝紫
        public static readonly Color Accent2    = Color.FromArgb(45, 212, 191);    // 青绿
        public static readonly Color AccentGrad1= Color.FromArgb(104, 122, 255);
        public static readonly Color AccentGrad2= Color.FromArgb(45, 212, 191);
        public static readonly Color Green      = Color.FromArgb(52, 211, 153);
        public static readonly Color Yellow     = Color.FromArgb(251, 191, 36);
        public static readonly Color Red        = Color.FromArgb(248, 113, 113);
        public static readonly Color Blue       = Color.FromArgb(96, 165, 250);

        public static Font TitleFont   = new Font("Segoe UI", 16F, FontStyle.Bold);
        public static Font SubFont     = new Font("Segoe UI", 9.5F);
        public static Font BodyFont    = new Font("Segoe UI", 9F);
        public static Font MonoFont    = new Font("Consolas", 9F);
        public static Font SemiBold    = new Font("Segoe UI Semibold", 10F);

        // 绘制圆角矩形
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 渐变填充圆角矩形
        public static void FillRound(Graphics g, Rectangle r, int radius, Color c1, Color c2, float angle)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new LinearGradientBrush(r, c1, c2, angle))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, path);
            }
        }

        public static void FillSolidRound(Graphics g, Rectangle r, int radius, Color c)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, path);
            }
        }

        public static void DrawRoundBorder(Graphics g, Rectangle r, int radius, Color c, float width)
        {
            using (var path = RoundRect(r, radius))
            using (var p = new Pen(c, width))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPath(p, path);
            }
        }

        public static GraphicsPath ShadowPath(Rectangle r, int radius)
        {
            return RoundRect(r, radius);
        }
    }

    // =====================================================================
    //  圆角卡片 Panel（自绘，带描边）
    // =====================================================================
    public class CardPanel : Panel
    {
        public int Radius = 14;
        public Color BorderColor = Theme.CardBorder;
        public bool FillGradient = false;
        public Color Grad1 = Theme.CardBg;
        public Color Grad2 = Theme.CardBg;

        public CardPanel()
        {
            this.BackColor = Theme.CardBg;
            this.DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (FillGradient)
                Theme.FillRound(e.Graphics, r, Radius, Grad1, Grad2, 45f);
            else
                Theme.FillSolidRound(e.Graphics, r, Radius, BackColor);
            Theme.DrawRoundBorder(e.Graphics, r, Radius, BorderColor, 1f);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* 不绘制，由 OnPaint 接管 */ }
    }

    // =====================================================================
    //  自定义圆角按钮（支持主按钮 / 次按钮 / 危险按钮 / 幽灵按钮）
    // =====================================================================
    public class ThemedButton : Control
    {
        public enum BtnKind { Primary, Secondary, Danger, Ghost, Success }

        private BtnKind _kind = BtnKind.Secondary;
        public BtnKind Kind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        private bool _hover;
        private bool _down;
        public int Radius = 8;

        public ThemedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            this.Size = new Size(120, 36);
            this.Cursor = Cursors.Hand;
            this.Font = new Font("Segoe UI Semibold", 9F);
        }

        private Color BaseColor()
        {
            switch (_kind)
            {
                case BtnKind.Primary: return Theme.Accent;
                case BtnKind.Success: return Theme.Green;
                case BtnKind.Danger: return Theme.Red;
                case BtnKind.Ghost: return Color.Transparent;
                default: return Color.FromArgb(38, 43, 62);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color baseC = BaseColor();
            if (_kind == BtnKind.Ghost)
            {
                if (_hover) Theme.FillSolidRound(e.Graphics, r, Radius, Color.FromArgb(50, 56, 84));
                if (_down) Theme.FillSolidRound(e.Graphics, r, Radius, Color.FromArgb(62, 70, 102));
            }
            else if (_kind == BtnKind.Primary)
            {
                Color c1 = _hover ? ControlPaint.Light(baseC, 0.12f) : baseC;
                Color c2 = _down ? ControlPaint.Dark(baseC, 0.12f) : baseC;
                Theme.FillRound(e.Graphics, r, Radius, c1, c2, 90f);
            }
            else
            {
                Color c = _hover ? ControlPaint.Light(baseC, 0.18f) : baseC;
                if (_down) c = ControlPaint.Dark(baseC, 0.15f);
                Theme.FillSolidRound(e.Graphics, r, Radius, c);
            }

            Color fg = Color.White;
            if (_kind == BtnKind.Ghost) fg = _hover ? Theme.TextMain : Theme.TextSub;
            if (!Enabled) { fg = Theme.TextFaint; }

            TextRenderer.DrawText(e.Graphics, Text, Font,
                new Rectangle(0, 0, Width, Height), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    }

    // =====================================================================
    //  侧栏导航项（自绘，选中高亮 + 图标；支持紧凑模式）
    // =====================================================================
    public class NavItem : Control
    {
        public string Icon { get; set; }       // 使用字形字符（Segoe MDL2 Assets）
        public bool Selected { get; set; }
        public bool Compact { get; set; }      // 紧凑模式：仅显示图标
        private bool _hover;

        public NavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            this.Height = 44;
            this.Cursor = Cursors.Hand;
            this.Font = new Font("Segoe UI", 10F);
        }

        public void SetSelected(bool s) { Selected = s; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            if (Selected)
            {
                // 选中背景：圆角高亮
                Rectangle hl = new Rectangle(6, 4, Width - 12, Height - 8);
                Theme.FillRound(e.Graphics, hl, 9, Color.FromArgb(38, 45, 72), Color.FromArgb(34, 40, 64), 90f);
                // 左侧指示条
                using (var b = new SolidBrush(Theme.Accent))
                using (var path = Theme.RoundRect(new Rectangle(6, 10, 3, Height - 20), 2))
                    e.Graphics.FillPath(b, path);
            }
            else if (_hover)
            {
                Rectangle hl = new Rectangle(6, 4, Width - 12, Height - 8);
                Theme.FillSolidRound(e.Graphics, hl, 9, Color.FromArgb(30, 35, 54));
            }

            Color fg = Selected ? Color.White : (Enabled ? Theme.TextSub : Theme.TextFaint);

            if (!string.IsNullOrEmpty(Icon))
            {
                if (Compact)
                {
                    // 紧凑：图标居中
                    Font iconFont = new Font("Segoe MDL2 Assets", 11F);
                    TextRenderer.DrawText(e.Graphics, Icon, iconFont,
                        new Rectangle(0, 0, Width, Height), Selected ? Theme.Accent2 : Theme.TextSub,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
                else
                {
                    Font iconFont = new Font("Segoe MDL2 Assets", 11F);
                    TextRenderer.DrawText(e.Graphics, Icon, iconFont,
                        new Rectangle(16, 0, 32, Height), Selected ? Theme.Accent2 : Theme.TextFaint,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(e.Graphics, Text, Font,
                        new Rectangle(52, 0, Width - 60, Height), fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    new Rectangle(52, 0, Width - 60, Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    // =====================================================================
    //  状态指示灯
    // =====================================================================
    public class StatusDot : Control
    {
        private Color _color = Theme.TextFaint;
        public Color DotColor
        {
            get { return _color; }
            set { _color = value; Invalidate(); }
        }
        public bool Pulsing { get; set; }

        private Timer _pulseTimer;
        private float _pulse = 0f;
        private bool _grow = true;

        public StatusDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
            this.Size = new Size(16, 16);
            _pulseTimer = new Timer();
            _pulseTimer.Interval = 30;
            _pulseTimer.Tick += (s, e) =>
            {
                if (_grow) { _pulse += 0.06f; if (_pulse >= 1f) _grow = false; }
                else { _pulse -= 0.06f; if (_pulse <= 0f) _grow = true; }
                Invalidate();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Point c = new Point(Width / 2, Height / 2);
            int rad = Math.Min(Width, Height) / 2 - 2;
            if (Pulsing)
            {
                // 外圈光晕
                int halo = rad + (int)(_pulse * 5);
                using (var b = new SolidBrush(Color.FromArgb((int)(80 * (1 - _pulse)), _color)))
                    e.Graphics.FillEllipse(b, c.X - halo, c.Y - halo, halo * 2, halo * 2);
            }
            using (var b = new SolidBrush(_color))
                e.Graphics.FillEllipse(b, c.X - rad, c.Y - rad, rad * 2, rad * 2);
            // 高光
            using (var b = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                e.Graphics.FillEllipse(b, c.X - rad / 2 - 1, c.Y - rad / 2 - 1, rad, rad);
        }
    }

    // =====================================================================
    //  深色输入框
    // =====================================================================
    public class DarkTextBox : TextBox
    {
        public DarkTextBox()
        {
            this.BackColor = Theme.InputBg;
            this.ForeColor = Theme.TextMain;
            this.BorderStyle = BorderStyle.FixedSingle;
            this.Font = Theme.BodyFont;
        }
    }

    public class DarkComboBox : ComboBox
    {
        public DarkComboBox()
        {
            this.DropDownStyle = ComboBoxStyle.DropDownList;
            this.BackColor = Theme.InputBg;
            this.ForeColor = Theme.TextMain;
            this.FlatStyle = FlatStyle.Flat;
            this.Font = Theme.BodyFont;
        }
    }

    // =====================================================================
    //  分段分隔标题
    // =====================================================================
    public class SectionLabel : Control
    {
        public SectionLabel(string text)
        {
            this.Text = text;
            this.Height = 30;
            this.Font = new Font("Segoe UI Semibold", 10.5F);
            this.ForeColor = Theme.Accent2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(ForeColor))
                e.Graphics.DrawString(Text, Font, b, 2, 6);
            using (var p = new Pen(Color.FromArgb(60, Theme.Accent2)))
            {
                int y = Height / 2 + 2;
                p.Width = 1;
                e.Graphics.DrawLine(p, 8 + (int)e.Graphics.MeasureString(Text, Font).Width, y, Width - 6, y);
            }
        }
    }
}
