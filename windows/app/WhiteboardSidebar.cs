using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PenNoteBridge
{
    internal sealed class WhiteboardSidebar : UserControl
    {
        private static Color Surface { get { return DesktopTheme.IsDark ? DesktopTheme.Surface : Color.FromArgb(250, 245, 244); } }
        private static Color Paper { get { return DesktopTheme.IsDark ? DesktopTheme.Paper : Color.FromArgb(255, 253, 253); } }
        private static Color Ink { get { return DesktopTheme.IsDark ? DesktopTheme.Ink : Color.FromArgb(53, 39, 45); } }
        private static Color Outline { get { return DesktopTheme.IsDark ? DesktopTheme.Outline : Color.FromArgb(236, 219, 224); } }
        private static Color Accent { get { return DesktopTheme.IsDark ? DesktopTheme.Accent : Color.FromArgb(150, 53, 83); } }
        private static Color Selected { get { return DesktopTheme.IsDark ? DesktopTheme.SoftAccent : Color.FromArgb(250, 229, 236); } }
        private static Color Muted { get { return DesktopTheme.IsDark ? DesktopTheme.Muted : Color.FromArgb(108, 85, 93); } }

        private readonly Font bodyFont = new Font("Microsoft YaHei UI", 9F);
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        private readonly Label title;
        private readonly Panel library;
        private readonly ListBox boards;
        private readonly Label empty;
        private readonly Button add;
        private readonly Button toggleWindow;
        private readonly Button rename;
        private readonly Button exit;
        private bool updating;

        internal event Action ExitRequested;
        internal event Action ToggleWindowRequested;
        internal event Action NewRequested;
        internal event Action RenameRequested;
        internal event Action<int> BoardSelected;

        internal WhiteboardSidebar()
        {
            Font = bodyFont;
            Size = new Size(174, 352);
            MinimumSize = new Size(148, 0);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            AccessibleName = "白板侧栏";
            TabStop = false;

            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(9, 8, 10, 9),
                Margin = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 9));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

            title = new Label {
                Text = "画板", AccessibleName = "画板", AutoSize = true, Anchor = AnchorStyles.Left,
                Font = titleFont, Margin = new Padding(3, 0, 0, 0), UseMnemonic = false
            };
            layout.Controls.Add(title, 0, 0);
            layout.SetColumnSpan(title, 2);

            library = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            boards = new ListBox {
                Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30, IntegralHeight = false,
                BorderStyle = BorderStyle.None, AccessibleName = "画板列表",
                AccessibleDescription = "使用方向键切换画板"
            };
            boards.DrawItem += PaintBoard;
            boards.SelectedIndexChanged += delegate {
                rename.Enabled = boards.SelectedIndex >= 0;
                var select = BoardSelected;
                if (!updating && select != null && boards.SelectedIndex >= 0) select(boards.SelectedIndex);
            };
            empty = new Label {
                Text = "暂无画板", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                AccessibleName = "暂无画板"
            };
            library.Controls.Add(boards);
            library.Controls.Add(empty);
            layout.Controls.Add(library, 0, 1);
            layout.SetColumnSpan(library, 2);

            add = MakeButton("新建");
            add.AccessibleName = "新建画板";
            add.Margin = new Padding(0, 4, 3, 0);
            add.Click += delegate { var action = NewRequested; if (action != null) action(); };
            rename = MakeButton("重命名");
            rename.AccessibleName = "重命名当前画板";
            rename.Margin = new Padding(3, 4, 0, 0);
            rename.Click += delegate { var action = RenameRequested; if (action != null) action(); };
            layout.Controls.Add(add, 0, 2);
            layout.Controls.Add(rename, 1, 2);

            toggleWindow = MakeButton("切换小窗");
            toggleWindow.Click += delegate { var action = ToggleWindowRequested; if (action != null) action(); };
            layout.Controls.Add(toggleWindow, 0, 4);
            layout.SetColumnSpan(toggleWindow, 2);

            exit = MakeButton("退出白板");
            exit.Click += delegate { var action = ExitRequested; if (action != null) action(); };
            layout.Controls.Add(exit, 0, 5);
            layout.SetColumnSpan(exit, 2);

            Controls.Add(layout);
            ApplyTheme();
            UpdateBoards(null, -1, false);
        }

        internal void ApplyTheme()
        {
            bool dark = DesktopTheme.IsDark;
            BackColor = Surface;
            ForeColor = title.ForeColor = boards.ForeColor = Ink;
            library.BackColor = boards.BackColor = Paper;
            empty.ForeColor = Muted;
            foreach (Button button in new[] { add, rename, toggleWindow })
            {
                button.BackColor = Paper;
                button.ForeColor = Accent;
                button.FlatAppearance.BorderColor = Outline;
                button.FlatAppearance.MouseOverBackColor = dark ? DesktopTheme.Hover : Selected;
                button.FlatAppearance.MouseDownBackColor = dark ? DesktopTheme.Pressed : Color.FromArgb(244, 211, 224);
            }
            exit.BackColor = dark ? DesktopTheme.AccentFill : Accent;
            exit.ForeColor = dark ? DesktopTheme.AccentText : Color.White;
            exit.FlatAppearance.BorderColor = exit.BackColor;
            exit.FlatAppearance.MouseOverBackColor = dark ? ControlPaint.Light(exit.BackColor, 0.15f) : Color.FromArgb(126, 43, 70);
            exit.FlatAppearance.MouseDownBackColor = dark ? ControlPaint.Dark(exit.BackColor, 0.1f) : Color.FromArgb(112, 36, 60);
            boards.Invalidate();
            Invalidate();
        }

        internal void UpdateBoards(string[] names, int selected, bool smallWindow)
        {
            if (names == null) names = new string[0];
            selected = selected >= 0 && selected < names.Length ? selected : -1;
            bool changed = names.Length != boards.Items.Count;
            for (int i = 0; !changed && i < names.Length; ++i)
                changed = (string)boards.Items[i] != BoardName(names[i]);

            int previousSelection = boards.SelectedIndex;
            int top = boards.Items.Count == 0 ? 0 : boards.TopIndex;
            updating = true;
            boards.BeginUpdate();
            try
            {
                if (changed)
                {
                    boards.Items.Clear();
                    foreach (string name in names) boards.Items.Add(BoardName(name));
                }
                if (boards.SelectedIndex != selected) boards.SelectedIndex = selected;
                if (names.Length > 0 && changed && previousSelection == selected)
                    boards.TopIndex = Math.Min(top, names.Length - 1);
                else if (selected >= 0 && previousSelection != selected)
                {
                    int visibleCount = Math.Max(1, boards.ClientSize.Height / Math.Max(1, boards.ItemHeight));
                    if (selected < boards.TopIndex) boards.TopIndex = selected;
                    else if (selected >= boards.TopIndex + visibleCount)
                        boards.TopIndex = Math.Max(0, selected - visibleCount + 1);
                }
            }
            finally
            {
                boards.EndUpdate();
                updating = false;
            }
            boards.Visible = names.Length > 0;
            empty.Visible = names.Length == 0;
            rename.Enabled = selected >= 0;
            toggleWindow.Text = smallWindow ? "返回全屏" : "切换小窗";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Outline))
                e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            base.ScaleControl(factor, specified);
            if (boards != null && (specified & BoundsSpecified.Height) != 0)
                boards.ItemHeight = Math.Max(26, (int)Math.Round(boards.ItemHeight * factor.Height));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                titleFont.Dispose();
                bodyFont.Dispose();
            }
        }

        private static Button MakeButton(string text)
        {
            return new Button {
                Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                UseMnemonic = false, Margin = new Padding(0, 3, 0, 0)
            };
        }

        private static string BoardName(string name) { return String.IsNullOrWhiteSpace(name) ? "未命名画板" : name; }

        private void PaintBoard(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool active = (e.State & DrawItemState.Selected) != 0;
            var bounds = e.Bounds;
            using (var fill = new SolidBrush(active ? Selected : Paper)) e.Graphics.FillRectangle(fill, bounds);

            int scale = Math.Max(1, bounds.Height / 28);
            int x = bounds.Left + 9 * scale, y = bounds.Top + (bounds.Height - 12 * scale) / 2;
            var old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(active ? Accent : Muted, scale))
            {
                e.Graphics.DrawRectangle(pen, x, y, 10 * scale, 12 * scale);
                e.Graphics.DrawLine(pen, x + 2 * scale, y + 4 * scale, x + 8 * scale, y + 4 * scale);
                e.Graphics.DrawLine(pen, x + 2 * scale, y + 7 * scale, x + 7 * scale, y + 7 * scale);
            }
            e.Graphics.SmoothingMode = old;
            int textLeft = x + 17 * scale;
            var text = new Rectangle(textLeft, bounds.Top, Math.Max(0, bounds.Right - textLeft - 4), bounds.Height);
            TextRenderer.DrawText(e.Graphics, (string)boards.Items[e.Index], e.Font, text, active ? Accent : Ink,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }
    }
}

