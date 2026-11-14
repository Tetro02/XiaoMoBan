using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PenNoteBridge;

internal sealed class AnnotationController : IDisposable
{
    private const int FirstHotkey = 0x5D01;
    private const int BoardFormat = 2;
    private const int MaxBoards = 50;
    private const int MaxMarks = 3000;
    private const int MaxStoredMarks = MaxMarks * 2;
    private const int MaxPoints = 20000;
    private const int MaxEraserDocumentWidth = 480000;
    private static readonly int[] EraserSizes = { 8, 14, 20, 30, 44, 64, 96 };
    private static readonly string[] HotkeyNames = { "屏幕批注", "白板", "撤销批注" };
    private static readonly int[] HotkeyIds = { FirstHotkey, FirstHotkey + 1, FirstHotkey + 5 };
    private static readonly Color LightBoardPaper = Color.FromArgb(248, 249, 252);
    private static readonly double DarkInkMinLuminance = 4.5 * (Luminance(
        LinearChannel(DesktopTheme.Canvas.R), LinearChannel(DesktopTheme.Canvas.G),
        LinearChannel(DesktopTheme.Canvas.B)) + 0.05) - 0.05;
    private static Color BoardBackground { get { return DesktopTheme.IsDark ? DesktopTheme.Canvas : LightBoardPaper; } }

    private enum Mode { None, Screen, Board }
    internal enum Tool { Ink, Line, Arrow, Rectangle, Ellipse, Text, Eraser, Pan }

    private struct Sample
    {
        internal PointF Position;
        internal float Pressure;
        internal Sample(PointF position, float pressure) { Position = position; Pressure = pressure; }
    }

    private sealed class Mark
    {
        internal readonly Tool Tool;
        internal readonly Color Color;
        internal readonly float Width;
        internal readonly string Text;
        internal readonly List<Sample> Points = new List<Sample>();

        internal Mark(Tool tool, Color color, float width, string text)
        {
            Tool = tool;
            Color = color;
            Width = width;
            Text = text;
        }
    }

    private sealed class Document
    {
        internal string Name;
        internal readonly List<Mark> Marks = new List<Mark>();
        internal readonly List<Mark[]> History = new List<Mark[]>();
        internal int Revision;
        internal int InkCount;

        internal Document(string name) { Name = name; }

        internal void Remember(Mark[] before)
        {
            if (History.Count == 30) History.RemoveAt(0);
            History.Add(before);
        }

        internal bool Undo()
        {
            if (History.Count == 0) return false;
            int last = History.Count - 1;
            Marks.Clear();
            Marks.AddRange(History[last]);
            InkCount = 0;
            foreach (Mark mark in Marks)
                if (mark.Tool != Tool.Eraser) InkCount++;
            History.RemoveAt(last);
            Revision++;
            return true;
        }
    }

    private readonly List<Surface> surfaces = new List<Surface>();
    private readonly Document screen = new Document("屏幕");
    private readonly List<Document> boards = new List<Document>();
    private readonly Timer save = new Timer { Interval = 600 };
    private readonly string directory;
    private readonly Keys[] shortcuts = {
        Keys.Control | Keys.Alt | Keys.F7, Keys.Control | Keys.Alt | Keys.F8,
        Keys.Control | Keys.Alt | Keys.Z
    };
    private readonly bool[] registered = new bool[HotkeyNames.Length];
    private IntPtr hotkeyWindow;
    private Mode mode;
    private int boardIndex;
    private Tool tool;
    private Tool toolBeforeEraser;
    private Color ink = Color.FromArgb(227, 44, 65);
    private float width = 4;
    private int eraserSize = 20;
    private float zoom = 1;
    private PointF pan;
    private float screenZoom = 1;
    private PointF screenPan;
    private Mark live;
    private StrokeInput strokeInput;
    private StrokeInput previewInput;
    private PointF eraserPosition;
    private float eraserPreviewDiameter;
    private bool drawing, erasing, panning, clickThrough;
    private bool repaintAll, frameQueued, boardsDirty, disposed, smallBoard;
    private int inkRevision;
    private RectangleF inkDamage;
    private Rectangle boardReference;
    private PointF lastPan;
    private string storageError;
    private Palette palette;
    private BoardSidebarWindow sidebar;
    private BoardWindow boardWindow;
    private EscapeKey escapeKey;
    private bool displayRefreshPending;
    private int modalDepth;

    internal bool Annotating { get { return mode == Mode.Screen; } }
    internal bool Whiteboard { get { return mode == Mode.Board; } }
    internal Func<IntPtr, int, int, uint, bool> ServerEraserIntent { get; set; }
    internal event EventHandler StateChanged;

    internal IntPtr[] PenCaptureWindows()
    {
        if (disposed || !CapturesInput) return new IntPtr[0];
        if (Whiteboard && smallBoard)
        {
            if (boardWindow == null || !boardWindow.Visible || !boardWindow.Canvas.IsHandleCreated) return new IntPtr[0];
            return new[] { boardWindow.Canvas.Handle };
        }

        List<IntPtr> windows = new List<IntPtr>(surfaces.Count);
        foreach (Surface surface in surfaces)
            if (surface.Visible && surface.IsHandleCreated) windows.Add(surface.Handle);
        return windows.ToArray();
    }

    internal AnnotationController()
    {
        directory = PenNoteBridge.DesktopHost.ProfileDirectory();
        LoadBoards();
        LoadShortcuts();
        LoadEraserSize();
        save.Tick += delegate { save.Stop(); SaveBoards(); };
        DesktopTheme.Changed += ApplyTheme;
    }

    private void ApplyTheme(object sender, EventArgs e)
    {
        if (palette != null && !palette.IsDisposed) palette.ApplyTheme();
        if (sidebar != null && !sidebar.IsDisposed) sidebar.ApplyTheme();
        if (boardWindow != null && !boardWindow.IsDisposed) boardWindow.ApplyTheme();
        if (Whiteboard) InvalidateLayers();
    }

    private Document Current { get { return mode == Mode.Board ? boards[boardIndex] : screen; } }
    private bool DarkBoard { get { return Whiteboard && DesktopTheme.IsDark; } }
    private bool CapturesInput { get { return mode != Mode.None && !clickThrough; } }
    private float ViewZoom { get { return mode == Mode.Board ? zoom : screenZoom; } }
    private PointF ViewPan { get { return mode == Mode.Board ? pan : screenPan; } }
    private float RenderScale { get { return mode == Mode.Board && smallBoard && boardWindow != null
        ? boardWindow.Canvas.Viewport.Scale : 1; } }

    internal void ToggleAnnotation() { SwitchMode(mode == Mode.Screen ? Mode.None : Mode.Screen); }
    internal void ToggleWhiteboard() { SwitchMode(mode == Mode.Board ? Mode.None : Mode.Board); }

    internal void ToggleMiniWhiteboard()
    {
        if (Whiteboard)
        {
            if (smallBoard) SwitchMode(Mode.None);
            else ToggleBoardWindow();
            return;
        }

        if (!smallBoard) boardReference = Screen.FromPoint(Cursor.Position).Bounds;
        smallBoard = true;
        SwitchMode(Mode.Board);
    }

    internal void HandlePenBodyGesture(uint action)
    {
        if (disposed || modalDepth != 0) return;
        switch (action)
        {
            case 3:
                if (mode == Mode.None) return;
                Tool previous = toolBeforeEraser == Tool.Pan && !Whiteboard ? Tool.Ink : toolBeforeEraser;
                SelectTool(tool == Tool.Eraser ? previous : Tool.Eraser);
                break;
            case 4:
                if (!Whiteboard || !smallBoard) ToggleMiniWhiteboard();
                break;
            case 5: SwitchMode(Mode.Screen); break;
        }
    }

    private void SelectTool(Tool selected)
    {
        FinishGesture();
        ClearErasePreview();
        if (selected == Tool.Eraser && tool != Tool.Eraser) toolBeforeEraser = tool;
        tool = selected;
        if (clickThrough)
        {
            clickThrough = false;
            UpdateVisibility();
        }
        else palette.UpdateState();
    }

    internal void Undo()
    {
        if (mode == Mode.None) return;
        FinishGesture();
        if (Current.Undo()) Changed();
    }

    internal void Clear()
    {
        if (mode == Mode.None) return;
        FinishGesture();
        if (Current.Marks.Count == 0) return;
        Current.Remember(Current.Marks.ToArray());
        Current.Marks.Clear();
        Current.InkCount = 0;
        Current.Revision++;
        Changed();
    }

    internal bool NavigateCanvas(float panX, float panY, float scale, float focusX, float focusY)
    {
        if (disposed || mode == Mode.None || !Finite(panX) || !Finite(panY) || !Finite(scale) || scale <= 0 ||
            !Finite(focusX) || !Finite(focusY)) return false;
        focusX = Math.Max(0, Math.Min(1, focusX));
        focusY = Math.Max(0, Math.Min(1, focusY));
        panX = Math.Max(-1, Math.Min(1, panX));
        panY = Math.Max(-1, Math.Min(1, panY));

        PointF focus;
        SizeF source;
        if (mode == Mode.Board && smallBoard)
        {
            if (boardWindow == null || !boardWindow.Canvas.Visible) return false;
            Size size = boardWindow.Canvas.ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return false;
            BoardViewport viewport = boardWindow.Canvas.Viewport;
            focus = viewport.ToSource(new PointF(focusX * size.Width, focusY * size.Height));
            source = new SizeF(size.Width / viewport.Scale, size.Height / viewport.Scale);
        }
        else
        {
            Rectangle screenBounds = SystemInformation.VirtualScreen;
            source = screenBounds.Size;
            focus = new PointF(screenBounds.Left + focusX * source.Width, screenBounds.Top + focusY * source.Height);
        }

        PointF offset = new PointF(panX * source.Width, panY * source.Height);
        // Pinch scale about the previous centroid, then move it to the current one.
        ChangeView(scale, new PointF(focus.X - offset.X, focus.Y - offset.Y), offset);
        return true;
    }

    internal void PanCanvasByPixels(float dx, float dy)
    {
        if (disposed || !CapturesInput || drawing || !Finite(dx) || !Finite(dy) || (dx == 0 && dy == 0)) return;
        float scale = RenderScale;
        PointF origin = ViewPan;
        SetView(ViewZoom, new PointF(origin.X + dx / scale, origin.Y + dy / scale));
    }

    private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }

    private void SwitchMode(Mode next)
    {
        if (mode == next) return;
        if (mode == Mode.Screen && escapeKey != null) escapeKey.Stop();
        FinishGesture();
        if (previewInput != null) previewInput.ClearHover();
        mode = next;
        clickThrough = false;
        if (mode == Mode.Board && boardReference.IsEmpty) boardReference = Screen.FromPoint(Cursor.Position).Bounds;
        if (tool == Tool.Pan && mode != Mode.Board) tool = Tool.Ink;
        StartEscapeKey();
        UpdateVisibility();
    }

    private void StartEscapeKey()
    {
        if (disposed || mode != Mode.Screen || modalDepth != 0) return;
        if (escapeKey == null) escapeKey = new EscapeKey(delegate { if (mode == Mode.Screen) SwitchMode(Mode.None); });
        escapeKey.Start();
    }

    private DialogResult ShowAnnotationDialog(Func<IWin32Window, DialogResult> show)
    {
        Form owner = boardWindow != null && boardWindow.Visible ? boardWindow
            : palette != null && palette.Visible ? (Form)palette : Form.ActiveForm;
        modalDepth++;
        if (escapeKey != null) escapeKey.Stop();
        try { return show(owner); }
        finally { modalDepth--; StartEscapeKey(); }
    }

    private DialogResult ShowAnnotationDialog(Form dialog)
    {
        EventHandler applyTheme = delegate
        {
            bool dark = DesktopTheme.IsDark;
            dialog.BackColor = dark ? DesktopTheme.Paper : SystemColors.Control;
            dialog.ForeColor = dark ? DesktopTheme.Ink : SystemColors.ControlText;
            foreach (Control control in dialog.Controls)
            {
                var input = control as TextBox;
                if (input != null)
                {
                    input.BackColor = dark ? DesktopTheme.Canvas : input.ReadOnly ? SystemColors.Control : SystemColors.Window;
                    input.ForeColor = dark ? DesktopTheme.Ink : SystemColors.WindowText;
                }

                var button = control as Button;
                if (button == null) continue;
                button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                button.BackColor = dark ? DesktopTheme.Surface : SystemColors.Control;
                button.UseVisualStyleBackColor = !dark;
                button.FlatAppearance.BorderColor = dark ? DesktopTheme.Outline : Color.Empty;
                button.FlatAppearance.MouseOverBackColor = dark ? DesktopTheme.Hover : Color.Empty;
                button.FlatAppearance.MouseDownBackColor = dark ? DesktopTheme.Pressed : Color.Empty;
            }
            DesktopTheme.ApplyTitleBar(dialog);
        };
        dialog.HandleCreated += applyTheme;
        DesktopTheme.Changed += applyTheme;
        try
        {
            applyTheme(dialog, EventArgs.Empty);
            return ShowAnnotationDialog(dialog.ShowDialog);
        }
        finally
        {
            DesktopTheme.Changed -= applyTheme;
            dialog.HandleCreated -= applyTheme;
        }
    }

    private void UpdateVisibility()
    {
        bool windowed = mode == Mode.Board && smallBoard;
        bool activateBoardWindow = false;
        if (!windowed && boardWindow != null) boardWindow.Hide();
        if (mode != Mode.None && !windowed)
        {
            EnsureSurfaces();
            foreach (Surface surface in surfaces)
            {
                surface.SetInput(CapturesInput);
                if (!surface.Visible) surface.Show();
            }
        }
        else foreach (Surface surface in surfaces) surface.HideAndRelease();

        if (mode == Mode.None)
        {
            if (palette != null) palette.Hide();
            if (sidebar != null) sidebar.Hide();
        }
        else
        {
            if (palette == null || palette.IsDisposed) palette = new Palette(this);
            if (windowed)
            {
                palette.Hide();
                if (sidebar != null) sidebar.Hide();
                if (boardWindow == null)
                {
                    boardWindow = new BoardWindow(this);
                    boardWindow.Owner = CanvasStack;
                }
                palette.AttachTools(boardWindow.Tools);
                palette.UpdateState();
                boardWindow.UpdateState();
                if (!boardWindow.Visible)
                {
                    boardWindow.Show();
                    if (boardWindow.WindowState == FormWindowState.Minimized)
                        boardWindow.WindowState = FormWindowState.Normal;
                    activateBoardWindow = true;
                }
            }
            else
            {
                palette.Owner = CanvasStack;
                palette.AttachTools(null);
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                int left = area.Left + 18;
                bool showBoardSidebar = mode == Mode.Board;
                if (showBoardSidebar)
                {
                    if (sidebar == null) sidebar = new BoardSidebarWindow(this);
                    sidebar.Owner = CanvasStack;
                    bool showSidebar = !sidebar.Visible;
                    sidebar.Place(area);
                    left = sidebar.Right + 10;
                    UpdateBoardList();
                    if (showSidebar) { sidebar.Show(); sidebar.Activate(); }
                }
                else if (sidebar != null) sidebar.Hide();
                palette.UpdateState();
                if (!palette.Visible || showBoardSidebar)
                {
                    palette.Location = new Point(left, area.Top + 18);
                    if (!palette.Visible) palette.Show();
                }
                palette.KeepOnScreen(Screen.FromControl(palette).WorkingArea);
            }
        }

        inkRevision++;
        repaintAll = mode != Mode.None;
        inkDamage = RectangleF.Empty;
        RefreshLayers();
        // Layered canvases have been shown or hidden before raising the visible UI.
        if (windowed)
        {
            Native.BringTop(boardWindow.Handle);
            if (activateBoardWindow) boardWindow.Activate();
        }
        else if (mode != Mode.None)
        {
            foreach (Surface surface in surfaces)
                if (surface.Visible) Native.BringTop(surface.Handle);
            if (mode == Mode.Board && sidebar != null && sidebar.Visible) Native.BringTop(sidebar.Handle);
            Native.BringTop(palette.Handle);
        }
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (disposed) return;
        EventHandler changed = StateChanged;
        if (changed != null) changed(this, EventArgs.Empty);
    }

    private void ToggleBoardWindow()
    {
        if (mode != Mode.Board) return;
        FinishGesture();
        if (previewInput != null) previewInput.ClearHover();
        if (!smallBoard) boardReference = Screen.FromPoint(Cursor.Position).Bounds;
        smallBoard = !smallBoard;
        UpdateVisibility();
    }

    private WhiteboardSidebar NewBoardSidebar()
    {
        WhiteboardSidebar panel = new WhiteboardSidebar { Dock = DockStyle.Fill };
        panel.ExitRequested += ToggleWhiteboard;
        panel.ToggleWindowRequested += ToggleBoardWindow;
        panel.NewRequested += AddBoard;
        panel.RenameRequested += RenameBoard;
        panel.BoardSelected += SelectBoard;
        return panel;
    }

    private void UpdateBoardList()
    {
        if (mode != Mode.Board) return;
        string[] names = boards.ConvertAll(delegate(Document board) { return board.Name; }).ToArray();
        if (sidebar != null) sidebar.Panel.UpdateBoards(names, boardIndex, false);
        if (boardWindow != null) boardWindow.Panel.UpdateBoards(names, boardIndex, true);
    }

    private void EnsureSurfaces()
    {
        Screen[] displays = Screen.AllScreens;
        bool same = surfaces.Count == displays.Length;
        for (int index = 0; same && index < displays.Length; index++)
            same = surfaces[index].DisplayBounds == displays[index].Bounds;
        if (same) return;
        if (surfaces.Count != 0) FinishGesture();
        if (palette != null) palette.Owner = null;
        if (sidebar != null) sidebar.Owner = null;
        if (boardWindow != null) boardWindow.Owner = null;
        // Controls owned by the last canvas stay above the entire display stack.
        foreach (Surface surface in surfaces) surface.Owner = null;
        foreach (Surface surface in surfaces) surface.Dispose();
        surfaces.Clear();
        foreach (Screen display in displays)
        {
            Surface surface = new Surface(this, display.Bounds) { Owner = CanvasStack };
            surfaces.Add(surface);
        }
        if (boardWindow != null) boardWindow.Owner = CanvasStack;
    }

    private Surface CanvasStack { get { return surfaces.Count == 0 ? null : surfaces[surfaces.Count - 1]; } }

    private void RefreshLayers()
    {
        if (disposed || mode == Mode.None || (!repaintAll && inkDamage.IsEmpty)) return;
        bool entireFrame = repaintAll;
        RectangleF region = inkDamage;
        repaintAll = false;
        inkDamage = RectangleF.Empty;
        if (mode == Mode.Board && smallBoard)
        {
            if (boardWindow != null && boardWindow.Visible) boardWindow.Canvas.Invalidate();
            return;
        }
        foreach (Surface surface in surfaces)
        {
            if (!surface.Visible) continue;
            Rectangle display = surface.DisplayBounds;
            RectangleF covered = RectangleF.Intersect(display, region);
            Rectangle dirty = entireFrame ? display : Rectangle.FromLTRB((int)Math.Floor(covered.Left),
                (int)Math.Floor(covered.Top), (int)Math.Ceiling(covered.Right), (int)Math.Ceiling(covered.Bottom));
            if (dirty.Width <= 0 || dirty.Height <= 0) continue;
            dirty.Offset(-display.X, -display.Y);
            surface.Present(dirty);
        }
    }

    private void QueueFrame()
    {
        if (frameQueued || disposed || mode == Mode.None) return;
        Control host = mode == Mode.Board && smallBoard ? (Control)boardWindow : palette;
        if (host == null || !host.IsHandleCreated) return;
        frameQueued = true;
        host.BeginInvoke((MethodInvoker)delegate { frameQueued = false; RefreshLayers(); });
    }

    private void InvalidateLayers()
    {
        inkRevision++;
        repaintAll = true;
        QueueFrame();
    }

    private void InvalidateMark(Mark mark)
    {
        inkRevision++;
        PointF end = mark.Points[mark.Points.Count - 1].Position;
        PointF start = (mark.Tool == Tool.Ink || mark.Tool == Tool.Eraser) && mark.Points.Count > 1
            ? mark.Points[mark.Points.Count - 2].Position : mark.Points[0].Position;
        RectangleF bounds = RectangleBetween(start, end);
        if (mark.Tool == Tool.Text)
            bounds = new RectangleF(start.X, start.Y, FontSize(mark) * (mark.Text.Length + 1), FontSize(mark) * 1.8f);
        float scale = ViewZoom;
        PointF origin = ViewPan;
        bounds = new RectangleF(bounds.X * scale + origin.X, bounds.Y * scale + origin.Y, bounds.Width * scale, bounds.Height * scale);
        float padding = mark.Width * scale * (mark.Tool == Tool.Arrow ? 6 : 1.25f) + 3;
        bounds.Inflate(padding, padding);
        Damage(bounds);
    }

    private void Damage(RectangleF bounds)
    {
        inkDamage = inkDamage.IsEmpty ? bounds : RectangleF.Union(inkDamage, bounds);
        QueueFrame();
    }

    private void SetErasePreview(StrokeInput input, PointF position, bool visible)
    {
        if (!visible || !CapturesInput)
        {
            if (previewInput == input) ClearErasePreview();
            return;
        }
        float diameter = eraserSize / RenderScale;
        if (previewInput == input && eraserPosition == position && eraserPreviewDiameter == diameter) return;
        if (previewInput != null) Damage(ErasePreviewBounds());
        previewInput = input;
        eraserPosition = position;
        eraserPreviewDiameter = diameter;
        Damage(ErasePreviewBounds());
    }

    private RectangleF ErasePreviewBounds()
    {
        float radius = eraserPreviewDiameter / 2 + 3 / RenderScale;
        return new RectangleF(eraserPosition.X - radius, eraserPosition.Y - radius, radius * 2, radius * 2);
    }

    private void ClearErasePreview()
    {
        if (previewInput == null) return;
        previewInput = null;
        Damage(ErasePreviewBounds());
    }

    private PointF ToDocument(PointF absolute)
    {
        PointF origin = ViewPan;
        return new PointF((absolute.X - origin.X) / ViewZoom, (absolute.Y - origin.Y) / ViewZoom);
    }

    private void BeginGesture(PointF absolute, float pressure, bool useEraser)
    {
        if (!CapturesInput || drawing) return;
        drawing = true;
        PointF point = ToDocument(absolute);
        if (useEraser || tool == Tool.Eraser)
        {
            erasing = true;
            if (Current.InkCount > 0 && Current.Marks.Count < MaxStoredMarks)
            {
                live = new Mark(Tool.Eraser, Color.Transparent, eraserSize / RenderScale / ViewZoom, "");
                live.Points.Add(new Sample(point, 1));
                InvalidateMark(live);
            }
        }
        else if (tool == Tool.Pan)
        {
            panning = true;
            lastPan = absolute;
        }
        else if (tool == Tool.Text)
        {
            drawing = false;
            if (Current.InkCount >= MaxMarks || Current.Marks.Count >= MaxStoredMarks) return;
            Document document = Current;
            string text = ReadText("插入文字", "文字", "", 300);
            if (mode == Mode.None || Current != document || String.IsNullOrWhiteSpace(text)) return;
            Mark mark = new Mark(Tool.Text, ink, width, text);
            mark.Points.Add(new Sample(point, 1));
            Commit(mark);
        }
        else if (Current.InkCount < MaxMarks && Current.Marks.Count < MaxStoredMarks)
        {
            live = new Mark(tool, ink, width, "");
            live.Points.Add(new Sample(point, pressure));
            InvalidateMark(live);
        }
    }

    private void MoveGesture(PointF absolute, float pressure)
    {
        if (!drawing) return;
        PointF point = ToDocument(absolute);
        if (panning)
        {
            pan.X += absolute.X - lastPan.X;
            pan.Y += absolute.Y - lastPan.Y;
            lastPan = absolute;
            InvalidateLayers();
        }
        else if (live != null)
        {
            if (erasing) pressure = 1;
            int last = live.Points.Count - 1;
            if (live.Points[last].Position == point && live.Points[last].Pressure == pressure) return;
            Sample sample = new Sample(point, pressure);
            if ((live.Tool != Tool.Ink && live.Tool != Tool.Eraser && last > 0) || live.Points.Count == MaxPoints)
            {
                InvalidateMark(live);
                live.Points[last] = sample;
            }
            else live.Points.Add(sample);
            InvalidateMark(live);
        }
    }

    private void FinishGesture()
    {
        StrokeInput previous = strokeInput;
        strokeInput = null;
        drawing = false;
        erasing = false;
        panning = false;
        if (previous != null) previous.Release();
        if (live != null)
        {
            Mark mark = live;
            live = null;
            if (mark.Tool == Tool.Ink || mark.Tool == Tool.Eraser || mark.Points.Count > 1) Commit(mark);
            else InvalidateMark(mark);
        }
    }

    private void Commit(Mark mark)
    {
        Current.Remember(Current.Marks.ToArray());
        Current.Marks.Add(mark);
        if (mark.Tool != Tool.Eraser) Current.InkCount++;
        InvalidateMark(mark);
        if (mode == Mode.Board) ScheduleSave();
    }

    private void Changed()
    {
        InvalidateLayers();
        if (mode == Mode.Board) ScheduleSave();
    }

    private void ZoomBoard(float amount, PointF anchor)
    {
        if (mode != Mode.Board) return;
        ChangeView(amount, anchor, PointF.Empty);
    }

    private void ChangeView(float amount, PointF anchor, PointF offset)
    {
        float scale = Math.Max(0.2f, Math.Min(5, ViewZoom * amount));
        if (scale == ViewZoom && offset.IsEmpty) return;
        FinishGesture();
        PointF point = ToDocument(anchor);
        SetView(scale, new PointF(anchor.X + offset.X - point.X * scale, anchor.Y + offset.Y - point.Y * scale));
    }

    private void ResetView()
    {
        FinishGesture();
        SetView(1, PointF.Empty);
    }

    private void SetView(float scale, PointF origin)
    {
        if (mode == Mode.Board) { zoom = scale; pan = origin; }
        else { screenZoom = scale; screenPan = origin; }
        if (palette != null) palette.UpdateViewLabel();
        InvalidateLayers();
    }

    private void SetDocumentTransform(Graphics graphics)
    {
        PointF origin = ViewPan;
        graphics.TranslateTransform(origin.X, origin.Y);
        graphics.ScaleTransform(ViewZoom, ViewZoom);
    }

    private void Paint(Graphics graphics, Rectangle display, Bitmap committed, Rectangle dirty)
    {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        Color background = mode == Mode.Board ? BoardBackground
            : CapturesInput ? Color.FromArgb(1, 0, 0, 0) : Color.Transparent;
        using (SolidBrush fill = new SolidBrush(background)) graphics.FillRectangle(fill, dirty);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SetClip(dirty);
        bool erasingLive = live != null && live.Tool == Tool.Eraser;
        if (committed != null)
            DrawInkRegion(graphics, committed, dirty, erasingLive ? new Rectangle(Point.Empty, dirty.Size) : dirty);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(-display.X, -display.Y);
        if (live != null && !erasingLive)
        {
            GraphicsState view = graphics.Save();
            SetDocumentTransform(graphics);
            PaintMark(graphics, live, darkBoard: DarkBoard);
            graphics.Restore(view);
        }
        PaintErasePreview(graphics);
    }

    private static void DrawInkRegion(Graphics graphics, Bitmap ink, Rectangle destination, Rectangle source)
    {
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(ink, destination, source, GraphicsUnit.Pixel);
        graphics.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private void PaintBoardWindowInk(Graphics graphics, BoardViewport viewport)
    {
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(viewport.Origin.X, viewport.Origin.Y);
        graphics.ScaleTransform(viewport.Scale, viewport.Scale);
        SetDocumentTransform(graphics);
        // Fit the board's coordinates to the window without changing its pen width.
        float strokeWidthScale = 1 / viewport.Scale;
        bool darkBoard = DarkBoard;
        foreach (Mark mark in Current.Marks) PaintMark(graphics, mark, strokeWidthScale, darkBoard);
        if (live != null) PaintMark(graphics, live, strokeWidthScale, darkBoard);
    }

    private void PaintBoardWindowPreview(Graphics graphics, BoardViewport viewport)
    {
        if (previewInput == null) return;
        GraphicsState before = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(viewport.Origin.X, viewport.Origin.Y);
        graphics.ScaleTransform(viewport.Scale, viewport.Scale);
        PaintErasePreview(graphics);
        graphics.Restore(before);
    }

    private void PaintErasePreview(Graphics graphics)
    {
        if (previewInput == null) return;
        float radius = eraserPreviewDiameter / 2;
        float scale = RenderScale;
        var circle = new RectangleF(eraserPosition.X - radius, eraserPosition.Y - radius,
            eraserPreviewDiameter, eraserPreviewDiameter);
        using (var outline = new Pen(Color.FromArgb(220, 22, 22, 28), 3 / scale))
        using (var center = new Pen(Color.White, 1 / scale))
        {
            graphics.DrawEllipse(outline, circle);
            graphics.DrawEllipse(center, circle);
        }
    }

    private static RectangleF RectangleBetween(PointF start, PointF end)
    {
        return RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
    }

    private static float FontSize(Mark mark) { return Math.Max(16, mark.Width * 4); }

    private static float LineWidth(float width, float pressure) { return width * (0.22f + 1.25f * pressure); }

    private static Color DarkBoardInk(Color color)
    {
        double red = LinearChannel(color.R);
        double green = LinearChannel(color.G);
        double blue = LinearChannel(color.B);
        double luminance = Luminance(red, green, blue);
        if (luminance >= DarkInkMinLuminance) return color;

        // Mix toward white in linear RGB for display only. Saved marks keep their original colors.
        double lift = (DarkInkMinLuminance - luminance) / (1 - luminance);
        return Color.FromArgb(color.A, SrgbChannel(red + (1 - red) * lift),
            SrgbChannel(green + (1 - green) * lift), SrgbChannel(blue + (1 - blue) * lift));
    }

    private static double Luminance(double red, double green, double blue)
    {
        return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    }

    private static double LinearChannel(int component)
    {
        double value = component / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static int SrgbChannel(double value)
    {
        double encoded = value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
        return Math.Min(255, (int)Math.Ceiling(255 * encoded));
    }

    private static void PaintMark(Graphics graphics, Mark mark, float strokeWidthScale = 1, bool darkBoard = false)
    {
        Sample first = mark.Points[0];
        PointF start = first.Position;
        PointF end = mark.Points[mark.Points.Count - 1].Position;
        bool erase = mark.Tool == Tool.Eraser;
        Color color = erase ? Color.Transparent : darkBoard ? DarkBoardInk(mark.Color) : mark.Color;
        if (mark.Tool == Tool.Text)
        {
            using (Font font = new Font(SystemFonts.MessageBoxFont.FontFamily, FontSize(mark), FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color)) graphics.DrawString(mark.Text, font, brush, start);
            return;
        }

        // Erasers always modify a transparent ink buffer. Drawing their
        // antialiased caps onto the opaque canvas can create visible fringes.
        float strokeWidth = erase ? mark.Width : mark.Width * strokeWidthScale;
        if (erase) graphics.CompositingMode = CompositingMode.SourceCopy;
        using (Pen pen = new Pen(color, strokeWidth))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            if (mark.Tool == Tool.Ink || erase)
            {
                RectangleF visible = graphics.VisibleClipBounds;
                visible.Inflate(strokeWidth + 2, strokeWidth + 2);
                if (mark.Points.Count == 1)
                {
                    float size = erase ? strokeWidth : LineWidth(strokeWidth, first.Pressure);
                    using (SolidBrush brush = new SolidBrush(color))
                        graphics.FillEllipse(brush, start.X - size / 2, start.Y - size / 2, size, size);
                }
                for (int index = 1; index < mark.Points.Count; index++)
                {
                    Sample before = mark.Points[index - 1];
                    Sample current = mark.Points[index];
                    if (Math.Max(before.Position.X, current.Position.X) < visible.Left ||
                        Math.Min(before.Position.X, current.Position.X) > visible.Right ||
                        Math.Max(before.Position.Y, current.Position.Y) < visible.Top ||
                        Math.Min(before.Position.Y, current.Position.Y) > visible.Bottom) continue;
                    if (!erase) pen.Width = LineWidth(strokeWidth, (before.Pressure + current.Pressure) / 2);
                    graphics.DrawLine(pen, before.Position, current.Position);
                }
            }
            else if (mark.Tool == Tool.Line) graphics.DrawLine(pen, start, end);
            else if (mark.Tool == Tool.Arrow)
            {
                using (AdjustableArrowCap arrow = new AdjustableArrowCap(4, 5, true))
                {
                    pen.CustomEndCap = arrow;
                    graphics.DrawLine(pen, start, end);
                }
            }
            else
            {
                RectangleF bounds = RectangleBetween(start, end);
                if (mark.Tool == Tool.Rectangle) graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                else graphics.DrawEllipse(pen, bounds);
            }
        }
        if (erase) graphics.CompositingMode = CompositingMode.SourceOver;
    }

    private void SelectBoard(int index)
    {
        if (mode != Mode.Board || index < 0 || index >= boards.Count || index == boardIndex) return;
        FinishGesture();
        boardIndex = index;
        pan = PointF.Empty;
        zoom = 1;
        ScheduleSave();
        palette.UpdateState();
        UpdateBoardList();
        if (boardWindow != null) boardWindow.UpdateState();
        InvalidateLayers();
    }

    private void AddBoard()
    {
        if (boards.Count >= MaxBoards) return;
        string name = ReadText("新建白板", "白板名称", "白板 " + (boards.Count + 1), 80);
        if (String.IsNullOrWhiteSpace(name)) return;
        boards.Add(new Document(name.Trim()));
        SelectBoard(boards.Count - 1);
    }

    private void RenameBoard()
    {
        if (mode != Mode.Board) return;
        Document document = Current;
        string name = ReadText("重命名白板", "白板名称", document.Name, 80);
        if (String.IsNullOrWhiteSpace(name)) return;
        document.Name = name.Trim();
        ScheduleSave();
        palette.UpdateState();
        UpdateBoardList();
        if (boardWindow != null) boardWindow.UpdateState();
    }

    private string ReadText(string title, string label, string initial, int maxLength)
    {
        using (Form dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(370, 107), FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false, TopMost = true, MaximizeBox = false, MinimizeBox = false })
        using (Label hint = new Label { Text = label, Location = new Point(12, 12), AutoSize = true })
        using (TextBox input = new TextBox { Text = initial, Location = new Point(12, 34), Width = 346, MaxLength = maxLength })
        using (Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(195, 68) })
        using (Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(283, 68) })
        {
            dialog.Controls.AddRange(new Control[] { hint, input, ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            dialog.ActiveControl = input;
            input.SelectAll();
            return ShowAnnotationDialog(dialog) == DialogResult.OK ? input.Text : null;
        }
    }

    private void ScheduleSave() { boardsDirty = true; save.Stop(); save.Start(); }

    private void LoadBoards()
    {
        string path = Path.Combine(directory, "annotation-boards.bin");
        try
        {
            if (File.Exists(path)) using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (stream.Length > 128 * 1024 * 1024)
                    throw new InvalidDataException("白板文件超出大小限制");
                int version = reader.ReadInt32();
                if (version != 1 && version != BoardFormat)
                    throw new InvalidDataException("白板文件格式不受支持");
                int count = ReadCount(reader, MaxBoards, 1);
                boardIndex = ReadCount(reader, count - 1, 0);
                for (int index = 0; index < count; index++)
                {
                    string name = reader.ReadString();
                    if (name.Length > 80) throw new InvalidDataException("白板名称超出范围");
                    Document board = new Document(name);
                    int marks = ReadCount(reader, version == 1 ? MaxMarks : MaxStoredMarks, 0);
                    for (int item = 0; item < marks; item++)
                    {
                        Tool kind = (Tool)ReadCount(reader, (int)(version == 1 ? Tool.Text : Tool.Eraser), 0);
                        Color color = Color.FromArgb(reader.ReadInt32());
                        float size = kind == Tool.Eraser ? ReadFloat(reader, 0.001f, MaxEraserDocumentWidth) : ReadFloat(reader, 1, 48);
                        string title = reader.ReadString();
                        if (title.Length > 300) throw new InvalidDataException("白板文字超出范围");
                        Mark mark = new Mark(kind, color, size, title);
                        int points = ReadCount(reader, MaxPoints, 1);
                        for (int point = 0; point < points; point++)
                            mark.Points.Add(new Sample(new PointF(ReadFloat(reader, -10000000, 10000000),
                                ReadFloat(reader, -10000000, 10000000)), ReadFloat(reader, 0, 1)));
                        board.Marks.Add(mark);
                        if (kind != Tool.Eraser) board.InkCount++;
                    }
                    boards.Add(board);
                }
            }
        }
        catch (IOException error) { ResetBoards(error); }
        catch (InvalidDataException error) { ResetBoards(error); }
        catch (FormatException error) { ResetBoards(error); }
        catch (UnauthorizedAccessException error) { ResetBoards(error); }
        if (boards.Count == 0) boards.Add(new Document("默认白板"));
    }

    private void ResetBoards(Exception error)
    {
        boards.Clear();
        boardIndex = 0;
        storageError = "无法读取白板文件。" + error.Message;
    }

    private static int ReadCount(BinaryReader reader, int maximum, int minimum)
    {
        int value = reader.ReadInt32();
        if (value < minimum || value > maximum) throw new InvalidDataException("白板记录数量超出范围");
        return value;
    }

    private static float ReadFloat(BinaryReader reader, float minimum, float maximum)
    {
        float value = reader.ReadSingle();
        if (Single.IsNaN(value) || value < minimum || value > maximum) throw new InvalidDataException("白板坐标超出范围");
        return value;
    }

    private void SaveBoards()
    {
        if (!boardsDirty) return;
        try
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "annotation-boards.bin");
            string temporary = target + ".new";
            using (FileStream stream = File.Create(temporary))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                bool legacy = boards.TrueForAll(document => document.Marks.TrueForAll(mark => mark.Tool != Tool.Eraser));
                writer.Write(legacy ? 1 : BoardFormat);
                writer.Write(boards.Count);
                writer.Write(boardIndex);
                foreach (Document board in boards)
                {
                    writer.Write(board.Name);
                    writer.Write(board.Marks.Count);
                    foreach (Mark mark in board.Marks)
                    {
                        writer.Write((int)mark.Tool);
                        writer.Write(mark.Color.ToArgb());
                        writer.Write(mark.Width);
                        writer.Write(mark.Text);
                        writer.Write(mark.Points.Count);
                        foreach (Sample point in mark.Points)
                        {
                            writer.Write(point.Position.X);
                            writer.Write(point.Position.Y);
                            writer.Write(point.Pressure);
                        }
                    }
                }
            }
            if (File.Exists(target)) File.Replace(temporary, target, target + ".bak");
            else File.Move(temporary, target);
            boardsDirty = false;
            storageError = null;
        }
        catch (IOException error) { storageError = "无法保存白板。" + error.Message; }
        catch (UnauthorizedAccessException error) { storageError = "无法保存白板。" + error.Message; }
        if (palette != null && !palette.IsDisposed) palette.UpdateState();
    }

    private void LoadShortcuts()
    {
        string path = Path.Combine(directory, "annotation-hotkeys.txt");
        try
        {
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < shortcuts.Length; index++)
            {
                int line = lines.Length == 6 && index == 2 ? 5 : index;
                int key;
                if (line < lines.Length && Int32.TryParse(lines[line], out key) && ValidShortcut((Keys)key))
                    shortcuts[index] = (Keys)key;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void LoadEraserSize()
    {
        string path = Path.Combine(directory, "annotation-eraser-size.txt");
        try
        {
            int size;
            if (File.Exists(path) && Int32.TryParse(File.ReadAllText(path), out size) && Array.IndexOf(EraserSizes, size) >= 0)
                eraserSize = size;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SetEraserSize(int size)
    {
        if (eraserSize == size) return;
        FinishGesture();
        eraserSize = size;
        if (previewInput != null) SetErasePreview(previewInput, eraserPosition, true);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "annotation-eraser-size.txt"), size.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool ValidShortcut(Keys shortcut)
    {
        if (shortcut == Keys.None) return true;
        Keys key = shortcut & Keys.KeyCode;
        return (shortcut & (Keys.Control | Keys.Alt)) != Keys.None && ((key >= Keys.D0 && key <= Keys.D9) ||
            (key >= Keys.A && key <= Keys.Z) || (key >= Keys.NumPad0 && key <= Keys.NumPad9) ||
            (key >= Keys.F1 && key <= Keys.F24));
    }

    internal void RegisterHotkeys(IntPtr mainWindow)
    {
        if (mainWindow == hotkeyWindow) return;
        UnregisterHotkeys();
        hotkeyWindow = mainWindow;
        RegisterCurrentHotkeys();
    }

    private void RegisterCurrentHotkeys()
    {
        if (hotkeyWindow == IntPtr.Zero) return;
        for (int index = 0; index < shortcuts.Length; index++)
        {
            Keys shortcut = shortcuts[index];
            if (shortcut == Keys.None) continue;
            uint modifiers = 0x4000;
            if ((shortcut & Keys.Alt) != 0) modifiers |= 1;
            if ((shortcut & Keys.Control) != 0) modifiers |= 2;
            if ((shortcut & Keys.Shift) != 0) modifiers |= 4;
            registered[index] = Native.RegisterHotKey(hotkeyWindow, HotkeyIds[index], modifiers, (uint)(shortcut & Keys.KeyCode));
        }
    }

    private void UnregisterHotkeys()
    {
        for (int index = 0; index < registered.Length; index++)
        {
            if (registered[index]) Native.UnregisterHotKey(hotkeyWindow, HotkeyIds[index]);
            registered[index] = false;
        }
    }

    internal bool HandleHotkey(int id)
    {
        switch (id - FirstHotkey)
        {
            case 0: ToggleAnnotation(); return true;
            case 1: ToggleWhiteboard(); return true;
            case 5: Undo(); return true;
            default: return false;
        }
    }

    internal void ShowSettings()
    {
        Keys[] values = (Keys[])shortcuts.Clone();
        bool[] previouslyRegistered = (bool[])registered.Clone();
        UnregisterHotkeys();
        try { EditShortcuts(values, previouslyRegistered); }
        finally { UnregisterHotkeys(); RegisterCurrentHotkeys(); }
    }

    private void EditShortcuts(Keys[] values, bool[] previouslyRegistered)
    {
        using (Form dialog = new Form { Text = "批注快捷键", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(465, 200), FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false, TopMost = true, MaximizeBox = false, MinimizeBox = false })
        {
            dialog.Controls.Add(new Label { Text = "按 Ctrl 或 Alt 加一个字母、数字或功能键。用清除停用快捷键。",
                Location = new Point(12, 14), AutoSize = true });
            for (int index = 0; index < shortcuts.Length; index++)
            {
                int item = index;
                int y = 42 + index * 33;
                string display = FormatShortcut(values[index]);
                if (values[index] != Keys.None && hotkeyWindow != IntPtr.Zero && !previouslyRegistered[index]) display += "（被占用）";
                Label name = new Label { Text = HotkeyNames[index], Location = new Point(12, y + 4), AutoSize = true };
                TextBox input = new TextBox { ReadOnly = true, Text = display, Location = new Point(125, y), Width = 220 };
                input.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    e.SuppressKeyPress = true;
                    if (!ValidShortcut(e.KeyData) || e.KeyData == Keys.None) return;
                    values[item] = e.KeyData;
                    input.Text = FormatShortcut(e.KeyData);
                };
                Button reset = new Button { Text = "清除", Location = new Point(365, y - 1), Width = 78 };
                reset.Click += delegate { values[item] = Keys.None; input.Text = FormatShortcut(Keys.None); };
                dialog.Controls.AddRange(new Control[] { name, input, reset });
            }
            Button ok = new Button { Text = "保存", Location = new Point(274, 156) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(366, 156) };
            ok.Click += delegate { if (SaveShortcuts(values, dialog)) dialog.DialogResult = DialogResult.OK; };
            dialog.Controls.AddRange(new Control[] { ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            ShowAnnotationDialog(dialog);
        }
    }

    private static string FormatShortcut(Keys keys)
    {
        if (keys == Keys.None) return "停用";
        Keys key = keys & Keys.KeyCode;
        string label = key >= Keys.D0 && key <= Keys.D9 ? ((int)key - (int)Keys.D0).ToString() : key.ToString();
        return ((keys & Keys.Control) != 0 ? "Ctrl + " : "") + ((keys & Keys.Alt) != 0 ? "Alt + " : "")
            + ((keys & Keys.Shift) != 0 ? "Shift + " : "") + label;
    }

    private bool SaveShortcuts(Keys[] values, IWin32Window owner)
    {
        HashSet<Keys> unique = new HashSet<Keys>();
        foreach (Keys value in values)
        {
            if (value == Keys.None || unique.Add(value)) continue;
            MessageBox.Show(owner, "有两项功能使用了同一个快捷键。", "批注快捷键");
            return false;
        }

        Keys[] previous = (Keys[])shortcuts.Clone();
        UnregisterHotkeys();
        Array.Copy(values, shortcuts, values.Length);
        RegisterCurrentHotkeys();
        string error = null;
        if (hotkeyWindow != IntPtr.Zero)
        {
            for (int index = 0; index < values.Length; index++)
                if (values[index] != Keys.None && !registered[index]) { error = HotkeyNames[index] + "的快捷键无法注册。"; break; }
        }
        if (error == null)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string[] lines = Array.ConvertAll(values, delegate(Keys key) { return ((int)key).ToString(); });
                File.WriteAllLines(Path.Combine(directory, "annotation-hotkeys.txt"), lines);
                return true;
            }
            catch (IOException failure) { error = failure.Message; }
            catch (UnauthorizedAccessException failure) { error = failure.Message; }
        }
        UnregisterHotkeys();
        Array.Copy(previous, shortcuts, previous.Length);
        MessageBox.Show(owner, error, "批注快捷键");
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DesktopTheme.Changed -= ApplyTheme;
        if (escapeKey != null) escapeKey.Dispose();
        FinishGesture();
        SaveBoards();
        UnregisterHotkeys();
        save.Dispose();
        if (sidebar != null) sidebar.Dispose();
        if (palette != null) palette.AttachTools(null);
        if (boardWindow != null) boardWindow.Dispose();
        if (palette != null) palette.Dispose();
        foreach (Surface surface in surfaces) surface.Dispose();
    }

    private sealed class Palette : Form
    {
        internal static Color Paper { get { return DesktopTheme.IsDark ? DesktopTheme.Paper : Color.FromArgb(255, 251, 245); } }
        private static Color TextInk { get { return DesktopTheme.IsDark ? DesktopTheme.Ink : Color.FromArgb(37, 39, 53); } }
        private static Color Muted { get { return DesktopTheme.IsDark ? DesktopTheme.Muted : Color.FromArgb(108, 93, 84); } }
        private static Color Outline { get { return DesktopTheme.IsDark ? DesktopTheme.Outline : Color.FromArgb(227, 214, 201); } }
        private static Color Hover { get { return DesktopTheme.IsDark ? DesktopTheme.Hover : Color.FromArgb(245, 234, 222); } }
        private static Color Accent { get { return DesktopTheme.IsDark ? DesktopTheme.Accent : Color.FromArgb(62, 80, 163); } }
        private static Color AccentSurface { get { return DesktopTheme.IsDark ? DesktopTheme.SoftAccent : Color.FromArgb(230, 235, 253); } }
        private static Color AccentLine { get { return DesktopTheme.IsDark ? DesktopTheme.AccentLine : Color.FromArgb(194, 203, 242); } }
        private static readonly PaletteRenderer Renderer = new PaletteRenderer();

        private readonly AnnotationController controller;
        private readonly Font toolbarFont;
        private readonly Font titleFont;
        private readonly TableLayoutPanel rows;
        private readonly TableLayoutPanel header;
        private readonly Label title;
        private readonly Label modeLabel;
        private readonly Button close;
        private readonly ToolStrip tools;
        private readonly ToolStrip actions;
        private readonly ToolStrip boardTools;
        private readonly ToolStripButton pass;
        private readonly ToolStripButton color;
        private readonly ToolStripButton pan;
        private readonly ToolStripButton sidebarToggle;
        private readonly ToolStripButton screenReset;
        private readonly ToolStripDropDownButton eraserWidth;
        private readonly ToolStripLabel status;
        private readonly Dictionary<Tool, ToolStripButton> toolButtons = new Dictionary<Tool, ToolStripButton>();
        private Color swatchColor;

        internal Palette(AnnotationController controller)
        {
            this.controller = controller;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            DoubleBuffered = true;
            KeyPreview = true;
            Padding = new Padding(12, 7, 12, 9);
            toolbarFont = new Font("Microsoft YaHei UI", 9f);
            titleFont = new Font(toolbarFont.FontFamily, 10.5f, FontStyle.Bold);
            Font = toolbarFont;

            rows = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
            header = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 4) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            title = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Font = titleFont, Margin = new Padding(0, 0, 10, 0) };
            modeLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(8, 0, 10, 0) };
            close = new Button { Text = "×", Width = 29, Height = 28, FlatStyle = FlatStyle.Flat,
                Margin = Padding.Empty, AccessibleName = "关闭工具栏", UseVisualStyleBackColor = false };
            close.FlatAppearance.BorderSize = 0;
            close.Click += delegate { controller.SwitchMode(Mode.None); };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(modeLabel, 1, 0);
            header.Controls.Add(close, 2, 0);
            header.MouseDown += Drag;
            title.MouseDown += Drag;
            modeLabel.MouseDown += Drag;
            tools = MakeRow();
            actions = MakeRow();
            boardTools = MakeRow();
            Controls.Add(rows);
            rows.Controls.Add(header, 0, 0);
            rows.Controls.Add(tools, 0, 1);
            rows.Controls.Add(actions, 0, 2);
            rows.Controls.Add(boardTools, 0, 3);

            AddTool(tools, Tool.Ink, "画笔");
            AddTool(tools, Tool.Line, "直线");
            AddTool(tools, Tool.Arrow, "箭头");
            AddTool(tools, Tool.Rectangle, "矩形");
            AddTool(tools, Tool.Ellipse, "椭圆");
            AddTool(tools, Tool.Text, "文字");
            AddTool(tools, Tool.Eraser, "橡皮");
            tools.Items.Add(new ToolStripSeparator());
            color = AddButton(tools, "颜色", PickColor);
            color.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            color.ImageScaling = ToolStripItemImageScaling.None;
            ToolStripDropDownButton width = new ToolStripDropDownButton(controller.width + " px") { Padding = ItemPadding() };
            width.ToolTipText = "调整笔画粗细";
            foreach (int size in new[] { 1, 2, 4, 6, 9, 14, 22, 32, 48 })
            {
                int value = size;
                width.DropDownItems.Add(size + " px", null, delegate { controller.width = value; width.Text = value + " px"; });
            }
            tools.Items.Add(width);
            AddButton(actions, "撤销", controller.Undo);
            AddButton(actions, "清空", controller.Clear);
            actions.Items.Add(new ToolStripSeparator());
            eraserWidth = new ToolStripDropDownButton { Padding = ItemPadding(), ToolTipText = "调整橡皮直径" };
            foreach (int size in EraserSizes)
            {
                int selected = size;
                var item = new ToolStripMenuItem(size + " px") { Tag = size };
                item.Click += delegate { controller.SetEraserSize(selected); UpdateEraserSize(); };
                eraserWidth.DropDownItems.Add(item);
            }
            UpdateEraserSize();
            actions.Items.Add(eraserWidth);
            pass = AddButton(actions, "点击穿透", TogglePass);
            screenReset = AddButton(actions, "复位视图", controller.ResetView);
            AddButton(actions, "快捷键", controller.ShowSettings);

            sidebarToggle = AddButton(boardTools, "收起侧栏", delegate
            {
                controller.FinishGesture();
                controller.boardWindow.ToggleSidebar();
                UpdateState();
            });
            pan = AddTool(boardTools, Tool.Pan, "平移");
            AddButton(boardTools, "放大", delegate { ZoomFromCenter(1.25f); });
            AddButton(boardTools, "缩小", delegate { ZoomFromCenter(0.8f); });
            AddButton(boardTools, "复位视图", controller.ResetView);
            status = new ToolStripLabel();
            boardTools.Items.Add(status);
            ApplyTheme();
        }

        internal void ApplyTheme()
        {
            BackColor = rows.BackColor = header.BackColor = close.BackColor = Paper;
            ForeColor = title.ForeColor = TextInk;
            modeLabel.ForeColor = close.ForeColor = Muted;
            close.FlatAppearance.MouseOverBackColor = Hover;
            close.FlatAppearance.MouseDownBackColor = DesktopTheme.IsDark ? DesktopTheme.Pressed : Color.Empty;
            foreach (ToolStrip row in new[] { tools, actions, boardTools })
            {
                row.BackColor = Paper;
                row.ForeColor = TextInk;
                foreach (ToolStripItem item in row.Items)
                {
                    var button = item as ToolStripButton;
                    item.ForeColor = button != null && button.Checked ? Accent : TextInk;
                    var menu = item as ToolStripDropDownButton;
                    if (menu == null) continue;
                    menu.DropDown.Renderer = Renderer;
                    menu.DropDown.BackColor = DesktopTheme.IsDark ? Paper : Color.Empty;
                    foreach (ToolStripItem option in menu.DropDownItems)
                        option.ForeColor = DesktopTheme.IsDark ? TextInk : Color.Empty;
                    menu.DropDown.Invalidate();
                }
                row.Invalidate();
            }
            status.ForeColor = DesktopTheme.IsDark ? DesktopTheme.Error : Color.DarkRed;
            UpdateSwatch(true);
            Invalidate();
        }

        internal void AttachTools(Control container)
        {
            Control parent = container ?? this;
            if (rows.Parent == parent) return;
            parent.Controls.Add(rows);
            rows.Dock = container == null ? DockStyle.Fill : DockStyle.Top;
            header.Visible = container == null;
        }

        private static ToolStrip MakeRow()
        {
            return new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
                Dock = DockStyle.None, AutoSize = true, CanOverflow = false, Margin = new Padding(0, 2, 0, 0),
                Padding = Padding.Empty, Renderer = Renderer };
        }

        private static Padding ItemPadding() { return new Padding(7, 4, 7, 4); }

        private static ToolStripButton AddButton(ToolStrip strip, string title, Action action)
        {
            ToolStripButton button = new ToolStripButton(title) { Padding = ItemPadding() };
            button.Click += delegate { action(); };
            strip.Items.Add(button);
            return button;
        }

        private ToolStripButton AddTool(ToolStrip strip, Tool tool, string title)
        {
            ToolStripButton button = AddButton(strip, title, delegate { controller.SelectTool(tool); });
            toolButtons.Add(tool, button);
            return button;
        }

        private void TogglePass()
        {
            controller.FinishGesture();
            if (controller.previewInput != null) controller.previewInput.ClearHover();
            controller.clickThrough = !controller.clickThrough;
            controller.UpdateVisibility();
        }

        private void PickColor()
        {
            using (ColorDialog dialog = new ColorDialog { Color = controller.ink, FullOpen = true })
            {
                if (controller.ShowAnnotationDialog(dialog.ShowDialog) != DialogResult.OK) return;
                controller.ink = dialog.Color;
                UpdateState();
            }
        }

        private void ZoomFromCenter(float factor)
        {
            Rectangle display = controller.smallBoard ? controller.boardReference : Screen.FromPoint(Location).Bounds;
            controller.ZoomBoard(factor, new Point(display.Left + display.Width / 2, display.Top + display.Height / 2));
        }

        internal void UpdateState()
        {
            bool board = controller.mode == Mode.Board;
            Text = board ? "白板 · " + controller.Current.Name : "屏幕批注";
            title.Text = Text;
            UpdateViewLabel();
            boardTools.Visible = board;
            sidebarToggle.Available = board && controller.smallBoard;
            sidebarToggle.Text = controller.boardWindow != null && controller.boardWindow.SidebarCollapsed
                ? "展开侧栏" : "收起侧栏";
            pan.Enabled = board;
            pass.Visible = !board;
            pass.Checked = controller.clickThrough;
            pass.Text = controller.clickThrough ? "恢复书写" : "点击穿透";
            pass.ForeColor = controller.clickThrough ? Accent : TextInk;
            UpdateSwatch();
            status.Text = controller.storageError == null ? "" : "白板保存/读取错误";
            status.Visible = controller.storageError != null;
            status.ToolTipText = controller.storageError;
            foreach (KeyValuePair<Tool, ToolStripButton> pair in toolButtons)
            {
                pair.Value.Checked = pair.Key == controller.tool;
                pair.Value.ForeColor = pair.Value.Checked ? Accent : TextInk;
            }
        }

        internal void UpdateViewLabel()
        {
            screenReset.Available = controller.Annotating && (controller.screenZoom != 1 || !controller.screenPan.IsEmpty);
            if (controller.Whiteboard) modeLabel.Text = Math.Round(controller.zoom * 100) + "%";
            else modeLabel.Text = controller.escapeKey != null && controller.escapeKey.Available ? "Esc 退出" : "";
        }

        private void UpdateEraserSize()
        {
            eraserWidth.Text = "橡皮 " + controller.eraserSize + " px";
            foreach (ToolStripMenuItem item in eraserWidth.DropDownItems)
                item.Checked = (int)item.Tag == controller.eraserSize;
        }

        private void UpdateSwatch(bool force = false)
        {
            if (!force && color.Image != null && swatchColor == controller.ink) return;
            Image previous = color.Image;
            Bitmap image = new Bitmap(14, 14, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(image))
            using (SolidBrush fill = new SolidBrush(controller.ink))
            using (Pen border = new Pen(Outline))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(fill, 1, 1, 12, 12);
                graphics.DrawEllipse(border, 1, 1, 12, 12);
            }
            color.Image = image;
            swatchColor = controller.ink;
            if (previous != null) previous.Dispose();
        }

        private void Drag(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Native.DragWindow(Handle);
        }

        internal void KeepOnScreen(Rectangle area)
        {
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
                using (GraphicsPath shape = Rounded(ClientRectangle, 13)) Region = new Region(shape);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle inside = Rectangle.Inflate(ClientRectangle, -1, -1);
            if (inside.Width < 1 || inside.Height < 1) return;
            using (GraphicsPath shape = Rounded(inside, 12))
            using (Pen border = new Pen(Outline)) e.Graphics.DrawPath(border, shape);
        }

        private static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && color != null && color.Image != null)
            {
                Image image = color.Image;
                color.Image = null;
                image.Dispose();
            }
            base.Dispose(disposing);
            if (disposing && titleFont != null) titleFont.Dispose();
            if (disposing && toolbarFont != null) toolbarFont.Dispose();
        }

        private sealed class PaletteRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown && !DesktopTheme.IsDark) base.OnRenderToolStripBackground(e);
                else e.Graphics.Clear(Paper);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (!(e.ToolStrip is ToolStripDropDown)) return;
                if (!DesktopTheme.IsDark) { base.OnRenderToolStripBorder(e); return; }
                using (var border = new Pen(Outline))
                    e.Graphics.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                if (!DesktopTheme.IsDark) { base.OnRenderImageMargin(e); return; }
                using (var fill = new SolidBrush(Paper)) e.Graphics.FillRectangle(fill, e.AffectedBounds);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!DesktopTheme.IsDark) { base.OnRenderMenuItemBackground(e); return; }
                PaintButton(e.Graphics, e.Item, false);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                if (DesktopTheme.IsDark) e.ArrowColor = e.Item.Enabled ? TextInk : Muted;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (DesktopTheme.IsDark && !e.Item.Enabled) e.TextColor = Muted;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                if (!DesktopTheme.IsDark) { base.OnRenderItemCheck(e); return; }
                Rectangle mark = e.ImageRectangle;
                using (var pen = new Pen(Accent, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new PointF(mark.Left + mark.Width * 0.22f, mark.Top + mark.Height * 0.53f),
                        new PointF(mark.Left + mark.Width * 0.43f, mark.Top + mark.Height * 0.73f),
                        new PointF(mark.Left + mark.Width * 0.79f, mark.Top + mark.Height * 0.26f)
                    });
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                ToolStripButton button = (ToolStripButton)e.Item;
                PaintButton(e.Graphics, button, button.Checked);
            }

            protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
            {
                PaintButton(e.Graphics, e.Item, false);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                if (!e.Vertical) { base.OnRenderSeparator(e); return; }
                using (Pen divider = new Pen(Outline))
                    e.Graphics.DrawLine(divider, 3, 5, 3, Math.Max(5, e.Item.Height - 5));
            }

            private static void PaintButton(Graphics graphics, ToolStripItem item, bool active)
            {
                if ((!active && !item.Selected && !item.Pressed) || item.Width <= 4 || item.Height <= 4) return;
                GraphicsState before = graphics.Save();
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath shape = Rounded(new Rectangle(1, 1, item.Width - 3, item.Height - 3), 7))
                using (SolidBrush fill = new SolidBrush(active ? AccentSurface : Hover))
                {
                    graphics.FillPath(fill, shape);
                    if (active) using (Pen border = new Pen(AccentLine)) graphics.DrawPath(border, shape);
                }
                graphics.Restore(before);
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= 0x20000;
                return parameters;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                controller.SwitchMode(Mode.None);
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) controller.SwitchMode(Mode.None);
            else if (e.Control && e.KeyCode == Keys.Z) controller.Undo();
            else { base.OnKeyDown(e); return; }
            e.SuppressKeyPress = true;
        }
    }

    private struct BoardViewport
    {
        internal readonly float Scale;
        internal readonly PointF Origin;

        internal BoardViewport(Rectangle source, Size target)
        {
            Scale = Math.Max(0.001f, Math.Min(target.Width / (float)Math.Max(1, source.Width),
                target.Height / (float)Math.Max(1, source.Height)));
            Origin = new PointF((target.Width - source.Width * Scale) / 2 - source.Left * Scale,
                (target.Height - source.Height * Scale) / 2 - source.Top * Scale);
        }

        internal PointF ToSource(PointF point)
        {
            return new PointF((point.X - Origin.X) / Scale, (point.Y - Origin.Y) / Scale);
        }
    }

    private sealed class BoardSidebarWindow : Form
    {
        private readonly AnnotationController controller;
        internal readonly WhiteboardSidebar Panel;

        internal BoardSidebarWindow(AnnotationController controller)
        {
            this.controller = controller;
            Panel = controller.NewBoardSidebar();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Text = "白板侧栏";
            ClientSize = Panel.Size;
            Controls.Add(Panel);
            ApplyTheme();
        }

        internal void ApplyTheme()
        {
            Panel.ApplyTheme();
            BackColor = Panel.BackColor;
        }

        internal void Place(Rectangle area)
        {
            int width = ClientSize.Width;
            Bounds = new Rectangle(area.Left + 18, area.Top + 18, width, Math.Max(246, area.Height - 36));
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys != Keys.Escape) return base.ProcessCmdKey(ref message, keys);
            controller.ToggleWhiteboard();
            return true;
        }
    }

    private sealed class BoardWindow : Form
    {
        private readonly AnnotationController controller;
        internal readonly WhiteboardSidebar Panel;
        internal readonly Panel Tools;
        internal readonly BoardCanvas Canvas;
        internal bool SidebarCollapsed { get; private set; }

        internal BoardWindow(AnnotationController controller)
        {
            this.controller = controller;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(710, 330);
            ClientSize = new Size(820, 520);
            FormBorderStyle = FormBorderStyle.Sizable;
            TopMost = true;
            KeyPreview = true;
            Panel = controller.NewBoardSidebar();
            Panel.Dock = DockStyle.Left;
            Tools = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 4, 8, 4) };
            Canvas = new BoardCanvas(controller) { Dock = DockStyle.Fill };
            Panel body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(Canvas);
            body.Controls.Add(Tools);
            Controls.Add(body);
            Controls.Add(Panel);
            ApplyTheme();
        }

        internal void ApplyTheme()
        {
            BackColor = DesktopTheme.IsDark ? DesktopTheme.Surface : SystemColors.Control;
            Canvas.BackColor = BoardBackground;
            Tools.BackColor = Palette.Paper;
            Panel.ApplyTheme();
            DesktopTheme.ApplyTitleBar(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DesktopTheme.ApplyTitleBar(this);
        }

        internal void UpdateState()
        {
            if (!controller.Whiteboard) return;
            Text = "白板 · " + controller.Current.Name;
            controller.UpdateBoardList();
        }

        internal void ToggleSidebar()
        {
            SidebarCollapsed = !SidebarCollapsed;
            Panel.Visible = !SidebarCollapsed;
            Canvas.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && controller.Whiteboard && controller.smallBoard)
            {
                e.Cancel = true;
                controller.ToggleWhiteboard();
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape) { controller.ToggleWhiteboard(); return true; }
            if (keys == (Keys.Control | Keys.Z)) { controller.Undo(); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }
    }

    private sealed class BoardCanvas : Control
    {
        private readonly AnnotationController controller;
        private readonly StrokeInput input;
        private Bitmap inkLayer;
        private int renderedRevision;

        internal BoardCanvas(AnnotationController controller)
        {
            this.controller = controller;
            BackColor = BoardBackground;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            input = new StrokeInput(controller, this, delegate { return controller.Whiteboard && controller.smallBoard; },
                delegate(Point absolute) { return Viewport.ToSource(PointToClient(absolute)); });
        }

        internal BoardViewport Viewport { get { return new BoardViewport(controller.boardReference, ClientSize); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (controller.Whiteboard && controller.smallBoard && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                bool render = inkLayer == null || renderedRevision != controller.inkRevision;
                if (inkLayer == null)
                    inkLayer = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
                BoardViewport viewport = Viewport;
                if (render)
                {
                    using (Graphics ink = Graphics.FromImage(inkLayer))
                        controller.PaintBoardWindowInk(ink, viewport);
                    renderedRevision = controller.inkRevision;
                }
                e.Graphics.DrawImageUnscaled(inkLayer, Point.Empty);
                controller.PaintBoardWindowPreview(e.Graphics, viewport);
            }
            base.OnPaint(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, e.ClipRectangle);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible)
            {
                if (input != null) input.Reset();
                ReleaseInkLayer();
            }
            base.OnVisibleChanged(e);
        }

        protected override void OnResize(EventArgs e)
        {
            if (input != null) input.ClearHover();
            ReleaseInkLayer();
            base.OnResize(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseInkLayer();
            base.Dispose(disposing);
        }

        private void ReleaseInkLayer()
        {
            if (inkLayer == null) return;
            inkLayer.Dispose();
            inkLayer = null;
        }

        protected override void WndProc(ref Message message)
        {
            if (input == null || !input.HandleMessage(ref message)) base.WndProc(ref message);
        }
    }

    private sealed class Surface : Form
    {
        private readonly AnnotationController controller;
        internal readonly Rectangle DisplayBounds;
        private readonly StrokeInput input;
        private bool takesInput;
        private LayerBuffer buffer;
        private Bitmap committed;
        private Bitmap liveEraseInk;
        private Document cachedDocument;
        private PointF cachedPan;
        private float cachedZoom;
        private int cachedRevision;
        private bool cachedDarkBoard;
        private int renderedMarks;

        internal Surface(AnnotationController controller, Rectangle display)
        {
            this.controller = controller;
            DisplayBounds = display;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            Bounds = display;
            input = new StrokeInput(controller, this, delegate
            {
                return takesInput && controller.CapturesInput && (!controller.Whiteboard || !controller.smallBoard);
            }, delegate(Point absolute) { return absolute; });
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= Native.Layered | Native.NoActivate | Native.ToolWindow;
                if (!takesInput) parameters.ExStyle |= Native.Transparent;
                return parameters;
            }
        }

        internal void SetInput(bool enabled)
        {
            if (takesInput == enabled) return;
            takesInput = enabled;
            if (!enabled) input.Reset();
            if (IsHandleCreated)
            {
                int style = Native.GetWindowLong(Handle, Native.ExtendedStyle);
                Native.SetWindowLong(Handle, Native.ExtendedStyle, enabled ? style & ~Native.Transparent : style | Native.Transparent);
                Native.RefreshWindowStyle(Handle);
            }
        }

        internal void Present(Rectangle dirty)
        {
            if (buffer == null)
            {
                buffer = new LayerBuffer(DisplayBounds.Size);
                dirty = new Rectangle(Point.Empty, DisplayBounds.Size);
            }
            if (buffer.Image == null) return;
            RefreshCommittedMarks();
            Bitmap ink = InkForFrame(dirty);
            using (Graphics graphics = Graphics.FromImage(buffer.Image))
            {
                controller.Paint(graphics, DisplayBounds, ink, dirty);
                graphics.Flush(FlushIntention.Sync);
            }
            buffer.Present(Handle, DisplayBounds.Location, dirty);
        }

        private Bitmap InkForFrame(Rectangle dirty)
        {
            Mark eraser = controller.live;
            if (eraser == null || eraser.Tool != Tool.Eraser)
            {
                ReleaseLiveEraseInk();
                return committed;
            }
            if (liveEraseInk == null || liveEraseInk.Width < dirty.Width || liveEraseInk.Height < dirty.Height)
            {
                int width = liveEraseInk == null ? dirty.Width : Math.Max(liveEraseInk.Width, dirty.Width);
                int height = liveEraseInk == null ? dirty.Height : Math.Max(liveEraseInk.Height, dirty.Height);
                ReleaseLiveEraseInk();
                liveEraseInk = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            }
            Rectangle scratch = new Rectangle(Point.Empty, dirty.Size);
            using (Graphics ink = Graphics.FromImage(liveEraseInk))
            {
                ink.CompositingMode = CompositingMode.SourceCopy;
                if (committed != null) DrawInkRegion(ink, committed, scratch, dirty);
                else using (var clear = new SolidBrush(Color.Transparent)) ink.FillRectangle(clear, scratch);
                ink.SetClip(scratch);
                ink.SmoothingMode = SmoothingMode.AntiAlias;
                ink.TranslateTransform(-DisplayBounds.X - dirty.X, -DisplayBounds.Y - dirty.Y);
                controller.SetDocumentTransform(ink);
                PaintMark(ink, eraser);
            }
            return liveEraseInk;
        }

        internal void HideAndRelease()
        {
            input.Reset();
            Hide();
            ReleaseLayers();
        }

        private void RefreshCommittedMarks()
        {
            if (controller.mode == Mode.None || controller.Current.Marks.Count == 0)
            {
                if (committed != null) { committed.Dispose(); committed = null; }
                cachedDocument = null;
                return;
            }

            Document document = controller.Current;
            float zoom = controller.ViewZoom;
            PointF pan = controller.ViewPan;
            bool darkBoard = controller.DarkBoard;
            bool reset = cachedDocument != document || cachedRevision != document.Revision || cachedZoom != zoom
                || cachedPan != pan || cachedDarkBoard != darkBoard;
            if (!reset && renderedMarks == document.Marks.Count) return;
            if (committed == null)
                committed = new Bitmap(DisplayBounds.Width, DisplayBounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(committed))
            {
                if (reset)
                {
                    graphics.Clear(Color.Transparent);
                    renderedMarks = 0;
                }
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TranslateTransform(-DisplayBounds.X, -DisplayBounds.Y);
                controller.SetDocumentTransform(graphics);
                while (renderedMarks < document.Marks.Count)
                {
                    PaintMark(graphics, document.Marks[renderedMarks], darkBoard: darkBoard);
                    renderedMarks++;
                }
            }
            cachedDocument = document;
            cachedRevision = document.Revision;
            cachedZoom = zoom;
            cachedPan = pan;
            cachedDarkBoard = darkBoard;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.DisplayChange && IsHandleCreated && !controller.displayRefreshPending)
            {
                controller.displayRefreshPending = true;
                BeginInvoke((MethodInvoker)delegate
                {
                    controller.displayRefreshPending = false;
                    if (!controller.disposed) controller.UpdateVisibility();
                });
            }
            if (input == null || !input.HandleMessage(ref message)) base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseLayers();
            base.Dispose(disposing);
        }

        private void ReleaseLayers()
        {
            if (buffer != null) { buffer.Dispose(); buffer = null; }
            if (committed != null) { committed.Dispose(); committed = null; }
            ReleaseLiveEraseInk();
            cachedDocument = null;
        }

        private void ReleaseLiveEraseInk()
        {
            if (liveEraseInk == null) return;
            liveEraseInk.Dispose();
            liveEraseInk = null;
        }
    }

    private sealed class StrokeInput
    {
        private readonly AnnotationController controller;
        private readonly Control view;
        private readonly Func<bool> enabled;
        private readonly Func<Point, PointF> toCanvas;
        private readonly Native.PenInfo[] history = new Native.PenInfo[64];
        private uint pointer;
        private uint hoveringPen;
        private ulong lastPenSample;
        private int lastPenTick, hoverTick;
        private Point hoverPosition;
        private bool sawPen, mouseBarrelAtDown, hoverBarrel;

        internal StrokeInput(AnnotationController controller, Control view, Func<bool> enabled, Func<Point, PointF> toCanvas)
        {
            this.controller = controller;
            this.view = view;
            this.enabled = enabled;
            this.toCanvas = toCanvas;
            view.MouseDown += MouseDown;
            view.MouseMove += MouseMove;
            view.MouseUp += MouseUp;
            view.MouseLeave += delegate { if (hoveringPen == 0 && !OwnsMouse) ClearHover(); };
            view.MouseWheel += delegate(object sender, MouseEventArgs e)
            {
                if (enabled()) controller.ZoomBoard((float)Math.Pow(1.16, e.Delta / 120.0), Position(e.Location));
            };
            view.MouseCaptureChanged += delegate { if (OwnsMouse && !view.Capture) controller.FinishGesture(); };
            view.HandleDestroyed += delegate { Reset(); };
            view.HandleCreated += delegate
            {
                if (enabled()) view.BeginInvoke((MethodInvoker)controller.NotifyStateChanged);
            };
        }

        internal void Reset()
        {
            if (controller.strokeInput == this) controller.FinishGesture();
            else Release();
        }

        internal void Release()
        {
            pointer = 0;
            lastPenSample = 0;
            mouseBarrelAtDown = false;
            ClearHover();
            if (view.IsHandleCreated && view.Capture) view.Capture = false;
        }

        internal void ClearHover()
        {
            hoveringPen = 0;
            hoverBarrel = false;
            controller.SetErasePreview(this, PointF.Empty, false);
        }

        private PointF Position(Point local) { return toCanvas(view.PointToScreen(local)); }
        private bool UseMouse { get { return enabled() &&
            !(sawPen && unchecked(Environment.TickCount - lastPenTick) < 300 && Native.IsPromotedPen); } }
        private bool OwnsMouse { get { return pointer == 0 && controller.strokeInput == this; } }

        private void MouseDown(object sender, MouseEventArgs e)
        {
            if (!UseMouse || e.Button != MouseButtons.Left) return;
            bool useEraser = mouseBarrelAtDown;
            controller.FinishGesture();
            view.Capture = true;
            Point physical = view.PointToScreen(e.Location);
            BeginInput(0, physical, 0.62f, useEraser);
            controller.SetErasePreview(this, toCanvas(physical), controller.erasing);
        }

        private void MouseMove(object sender, MouseEventArgs e)
        {
            if (!UseMouse) return;
            PointF position = Position(e.Location);
            if (OwnsMouse && view.Capture)
            {
                controller.MoveGesture(position, 0.62f);
                controller.SetErasePreview(this, position, controller.erasing);
            }
            else if (!controller.drawing && hoveringPen == 0)
                controller.SetErasePreview(this, position, controller.tool == Tool.Eraser ||
                    (e.Button & MouseButtons.Right) != 0);
        }

        private void MouseUp(object sender, MouseEventArgs e)
        {
            if (!UseMouse || !OwnsMouse || e.Button != MouseButtons.Left) return;
            PointF position = Position(e.Location);
            controller.MoveGesture(position, 0.62f);
            controller.FinishGesture();
            controller.SetErasePreview(this, position, controller.tool == Tool.Eraser);
        }

        internal bool HandleMessage(ref Message message)
        {
            if (message.Msg == Native.CancelMode) Reset();
            if (!enabled()) return false;
            if ((message.Msg == Native.MouseActivate || message.Msg == Native.PointerActivate) && view is Surface)
            {
                message.Result = new IntPtr(Native.NoActivateInput);
                return true;
            }
            if (message.Msg == Native.LeftDown || message.Msg == Native.LeftDoubleClick)
                mouseBarrelAtDown = (message.WParam.ToInt64() & Native.MouseRightButton) != 0;
            if (message.Msg == Native.TabletGestureStatus)
            {
                message.Result = new IntPtr(Native.TabletDisablePressAndHold | Native.TabletDisablePenBarrelFeedback);
                return true;
            }
            if (message.Msg == Native.ContextMenu || (message.Msg >= Native.RightDown && message.Msg <= Native.RightDoubleClick))
            {
                if (message.Msg != Native.ContextMenu && !controller.drawing && hoveringPen == 0 && UseMouse)
                    controller.SetErasePreview(this, Position(MessagePoint(message.LParam)),
                        controller.tool == Tool.Eraser || message.Msg != Native.RightUp);
                message.Result = IntPtr.Zero;
                return true;
            }
            uint packed = unchecked((uint)message.WParam.ToInt64());
            uint id = packed & 0xFFFF;
            uint flags = packed >> 16;
            if (id == 0) return false;
            if (message.Msg == Native.PointerLeave && id == hoveringPen)
            {
                ClearHover();
                message.Result = IntPtr.Zero;
                return true;
            }
            if (pointer != 0 && id == pointer && (message.Msg == Native.PointerCaptureChanged ||
                (message.Msg == Native.PointerLeave && (flags & Native.PointerInContact) == 0)))
            {
                controller.FinishGesture();
                message.Result = IntPtr.Zero;
                return true;
            }
            bool entering = message.Msg == Native.PointerEnter;
            if (!entering && (message.Msg < Native.PointerUpdate || message.Msg > Native.PointerUp)) return false;
            Native.PenInfo pen;
            if (!Native.GetPointerPenInfo(id, out pen))
            {
                uint kind;
                bool knownHover = hoveringPen == id && unchecked(Environment.TickCount - hoverTick) <= 250;
                if (pointer != id && !knownHover &&
                    (!Native.GetPointerType(id, out kind) || kind != Native.PointerTypePen)) return false;
                // Windows may release detailed pointer data before a queued message is dispatched.
                pen = new Native.PenInfo();
                pen.Info.Flags = flags;
                pen.Info.PixelLocation = MessagePoint(message.LParam);
            }
            if (message.Msg == Native.PointerDown) pen.Info.Flags |= Native.PointerDownFlag | Native.PointerInContact;
            uint count = pen.Info.HistoryCount;
            if (message.Msg == Native.PointerUpdate && count > 1 && count <= history.Length &&
                (pointer == id || (pen.Info.Flags & Native.PointerInContact) != 0) &&
                Native.GetPointerPenInfoHistory(id, ref count, history))
            {
                for (int index = (int)count - 1; index >= 0; index--)
                {
                    Native.PenInfo entry = history[index];
                    entry.Info.Flags &= ~Native.PointerDownFlag;
                    ApplyPenSample(id, entry, false);
                }
            }
            else if (!entering || (pen.Info.Flags & Native.PointerInContact) == 0)
                ApplyPenSample(id, pen, message.Msg == Native.PointerUp);
            sawPen = true;
            lastPenTick = Environment.TickCount;
            message.Result = IntPtr.Zero;
            return true;
        }

        private void ApplyPenSample(uint id, Native.PenInfo pen, bool lifted)
        {
            bool contact = (pen.Info.Flags & Native.PointerInContact) != 0;
            bool canceled = (pen.Info.Flags & Native.PointerCanceled) != 0;
            bool down = (pen.Info.Flags & Native.PointerDownFlag) != 0 && contact && !canceled && !lifted;
            ulong sample = pen.Info.PerformanceCount;
            if (pointer == id && sample != 0 && sample <= lastPenSample && !lifted && !canceled) return;
            if (down && controller.drawing) controller.FinishGesture();
            if (pointer != 0 && id != pointer) return;
            PointF position = toCanvas(pen.Info.PixelLocation);
            if (pointer == 0)
            {
                if (controller.drawing) return;
                if (!contact || canceled || lifted) { UpdatePenHover(id, pen); return; }
                bool held = hoveringPen == id && hoverBarrel && unchecked(Environment.TickCount - hoverTick) <= 250;
                ClearHover();
                BeginInput(id, pen.Info.PixelLocation, Pressure(pen), IsEraserPen(pen) || held);
                if (pointer == id) lastPenSample = sample;
                controller.SetErasePreview(this, position, controller.erasing);
                return;
            }

            if (sample != 0) lastPenSample = sample;
            if (!canceled && (contact || lifted))
                controller.MoveGesture(position, Pressure(pen));
            if (canceled || lifted || !contact)
            {
                controller.FinishGesture();
                UpdatePenHover(id, pen);
            }
            else controller.SetErasePreview(this, position, controller.erasing);
        }

        private void UpdatePenHover(uint id, Native.PenInfo pen)
        {
            if ((pen.Info.Flags & (Native.PointerInRange | Native.PointerCanceled)) != Native.PointerInRange)
            {
                ClearHover();
                return;
            }
            Point physical = pen.Info.PixelLocation;
            int now = Environment.TickCount;
            bool barrel = (pen.Flags & Native.PenBarrel) != 0 ||
                (pen.Info.Flags & Native.PointerSecondButton) != 0;
            if (barrel && hoverBarrel && hoveringPen == id)
                controller.PanCanvasByPixels(physical.X - hoverPosition.X, physical.Y - hoverPosition.Y);
            hoveringPen = id;
            hoverBarrel = barrel;
            hoverPosition = physical;
            hoverTick = now;
            controller.SetErasePreview(this, toCanvas(physical), IsEraserPen(pen) || controller.tool == Tool.Eraser);
        }

        private static bool IsEraserPen(Native.PenInfo pen)
        {
            return (pen.Flags & (Native.PenBarrel | Native.PenInverted | Native.PenEraser)) != 0 ||
                (pen.Info.Flags & Native.PointerSecondButton) != 0;
        }

        private static Point MessagePoint(IntPtr value)
        {
            long location = value.ToInt64();
            return new Point(unchecked((short)location), unchecked((short)(location >> 16)));
        }

        private void BeginInput(uint id, Point physical, float pressure, bool useEraser)
        {
            var request = controller.ServerEraserIntent;
            bool serverErase = request != null && request(view.Handle, physical.X, physical.Y,
                unchecked((uint)Native.GetMessageTime()));
            controller.strokeInput = this;
            pointer = id;
            controller.BeginGesture(toCanvas(physical), pressure, useEraser || serverErase);
            if (!controller.drawing && controller.strokeInput == this) controller.FinishGesture();
        }

        private static float Pressure(Native.PenInfo pen)
        {
            return (pen.Mask & 1) == 0 ? 0.62f : Math.Min(1, pen.Pressure / 1024f);
        }
    }

    private sealed class LayerBuffer : IDisposable
    {
        internal readonly Bitmap Image;
        private readonly IntPtr dc;
        private readonly IntPtr dib;
        private readonly IntPtr previous;
        private readonly IntPtr arguments;
        private readonly Size size;

        internal LayerBuffer(Size size)
        {
            this.size = size;
            Native.BitmapHeader header = new Native.BitmapHeader { Size = 40, Width = size.Width, Height = -size.Height,
                Planes = 1, Bits = 32 };
            dc = Native.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return;
            IntPtr pixels;
            dib = Native.CreateDIBSection(dc, ref header, 0, out pixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || pixels == IntPtr.Zero) return;
            previous = Native.SelectObject(dc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return;
            Image = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels);
            arguments = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.LayerArguments)));
        }

        internal void Present(IntPtr window, Point location, Rectangle dirty)
        {
            Point zero = Point.Empty;
            Size dimension = size;
            Native.Blend blend = new Native.Blend { Alpha = 255, Format = 1 };
            if (dirty.Size != size)
            {
                Native.LayerArguments details = new Native.LayerArguments { Size = size, Blend = blend,
                    Left = dirty.Left, Top = dirty.Top, Right = dirty.Right, Bottom = dirty.Bottom };
                Marshal.StructureToPtr(details, arguments, false);
                Native.LayerUpdate update = new Native.LayerUpdate
                {
                    Size = Native.LayerUpdate.StructSize, Source = dc, Flags = 2,
                    Dimensions = IntPtr.Add(arguments, Native.LayerArguments.SizeOffset),
                    SourcePoint = IntPtr.Add(arguments, Native.LayerArguments.PointOffset),
                    Blend = IntPtr.Add(arguments, Native.LayerArguments.BlendOffset),
                    Dirty = IntPtr.Add(arguments, Native.LayerArguments.DirtyOffset)
                };
                if (Native.UpdateLayeredWindowIndirect(window, ref update)) return;
            }
            Native.UpdateLayeredWindow(window, IntPtr.Zero, ref location, ref dimension, dc, ref zero, 0, ref blend, 2);
        }

        public void Dispose()
        {
            if (Image != null) Image.Dispose();
            if (arguments != IntPtr.Zero) Marshal.FreeHGlobal(arguments);
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(dc, previous);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (dc != IntPtr.Zero) Native.DeleteDC(dc);
        }
    }

    private sealed class EscapeKey : NativeWindow, IDisposable
    {
        private const int HotkeyId = 1;
        private const int HotkeyMessage = 0x0312;
        private const int HookMessage = 0x8073;
        private readonly Action exit;
        private readonly Native.KeyboardHook callback;
        private IntPtr hook;
        private bool registered;

        internal EscapeKey(Action exit)
        {
            this.exit = exit;
            callback = OnKey;
        }

        internal bool Available { get { return registered || hook != IntPtr.Zero; } }

        internal void Start()
        {
            if (Handle != IntPtr.Zero) return;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            registered = Native.RegisterHotKey(Handle, HotkeyId, 0x4000, (uint)Keys.Escape);
            if (!registered) hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
        }

        internal void Stop()
        {
            if (registered)
            {
                registered = false;
                Native.UnregisterHotKey(Handle, HotkeyId);
            }
            if (hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        private IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            if (code == 0 && hook != IntPtr.Zero && message.ToInt32() == 0x0100 &&
                Marshal.ReadInt32(data) == (int)Keys.Escape && !Native.ShortcutModifierDown &&
                Native.PostMessage(Handle, HookMessage, IntPtr.Zero, IntPtr.Zero))
                return new IntPtr(1);
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        protected override void WndProc(ref Message message)
        {
            if ((registered && message.Msg == HotkeyMessage && message.WParam.ToInt32() == HotkeyId) ||
                (hook != IntPtr.Zero && message.Msg == HookMessage))
            {
                exit();
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose() { Stop(); }
    }

    private static class Native
    {
        internal const int ExtendedStyle = -20, Layered = 0x80000, Transparent = 0x20, NoActivate = 0x08000000,
            ToolWindow = 0x80, DisplayChange = 0x007E, PointerUpdate = 0x0245, PointerDown = 0x0246,
            PointerUp = 0x0247, PointerEnter = 0x0249, PointerLeave = 0x024A, PointerActivate = 0x024B, PointerCaptureChanged = 0x024C,
            CancelMode = 0x001F, MouseActivate = 0x0021, NoActivateInput = 3,
            ContextMenu = 0x007B, LeftDown = 0x0201, LeftDoubleClick = 0x0203, RightDown = 0x0204, RightUp = 0x0205,
            RightDoubleClick = 0x0206, MouseRightButton = 0x0002;
        internal const int TabletGestureStatus = 0x02CC, TabletDisablePressAndHold = 0x01, TabletDisablePenBarrelFeedback = 0x10;
        internal const uint PointerInRange = 0x02, PointerInContact = 0x04, PointerSecondButton = 0x20, PointerCanceled = 0x8000;
        internal const uint PointerDownFlag = 0x10000, PointerTypePen = 3;
        internal const uint PenBarrel = 0x01, PenInverted = 0x02, PenEraser = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        internal struct PointerInfo
        {
            internal uint Type, Id, Frame, Flags;
            internal IntPtr Device, Window;
            internal Point PixelLocation, HimetricLocation, PixelRaw, HimetricRaw;
            internal uint Time, HistoryCount;
            internal int InputData;
            internal uint KeyStates;
            internal ulong PerformanceCount;
            internal uint ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PenInfo
        {
            internal PointerInfo Info;
            internal uint Flags, Mask, Pressure, Rotation;
            internal int TiltX, TiltY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, Bits;
            internal uint Compression, ImageSize;
            internal int XResolution, YResolution;
            internal uint Colors, Important, ColorTableEntry;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Blend
        {
            internal byte Operation, Flags, Alpha, Format;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LayerArguments
        {
            internal static readonly int SizeOffset = Marshal.OffsetOf(typeof(LayerArguments), "Size").ToInt32();
            internal static readonly int PointOffset = Marshal.OffsetOf(typeof(LayerArguments), "Point").ToInt32();
            internal static readonly int BlendOffset = Marshal.OffsetOf(typeof(LayerArguments), "Blend").ToInt32();
            internal static readonly int DirtyOffset = Marshal.OffsetOf(typeof(LayerArguments), "Left").ToInt32();
            internal Size Size;
            internal Point Point;
            internal Blend Blend;
            internal int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LayerUpdate
        {
            internal static readonly uint StructSize = (uint)Marshal.SizeOf(typeof(LayerUpdate));
            internal uint Size;
            internal IntPtr Destination, DestinationPoint, Dimensions, Source, SourcePoint;
            internal uint Color;
            internal IntPtr Blend;
            internal uint Flags;
            internal IntPtr Dirty;
        }

        internal static bool ShortcutModifierDown
        {
            get { return KeyDown(0x10) || KeyDown(0x11) || KeyDown(0x12) || KeyDown(0x5B) || KeyDown(0x5C); }
        }

        private static bool KeyDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        internal static bool IsPromotedPen
        {
            get
            {
                uint extra = unchecked((uint)GetMessageExtraInfo().ToInt64());
                return (extra & 0xFFFFFF80u) == 0xFF515700u;
            }
        }

        internal static void BringTop(IntPtr window) { SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x13); }
        internal static void RefreshWindowStyle(IntPtr window)
        {
            // Refresh the input style without moving the canvas above the toolbar.
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        }

        internal static void DragWindow(IntPtr window)
        {
            ReleaseCapture();
            Point cursor = Cursor.Position;
            int location = ((cursor.Y & 0xFFFF) << 16) | (cursor.X & 0xFFFF);
            SendMessage(window, 0x00A1, new IntPtr(2), new IntPtr(location));
        }

        internal delegate IntPtr KeyboardHook(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SetWindowsHookEx(int type, KeyboardHook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool GetPointerPenInfo(uint id, out PenInfo info);
        [DllImport("user32.dll")] internal static extern bool GetPointerPenInfoHistory(uint id, ref uint count, [Out] PenInfo[] history);
        [DllImport("user32.dll")] internal static extern bool GetPointerType(uint id, out uint type);
        [DllImport("user32.dll")] internal static extern int GetMessageTime();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc,
            ref Point destination, ref Size dimension, IntPtr sourceDc, ref Point source, uint color, ref Blend blend, uint flags);
        [DllImport("user32.dll")] internal static extern bool UpdateLayeredWindowIndirect(IntPtr window, ref LayerUpdate update);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage,
            out IntPtr bits, IntPtr mapping, uint offset);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    }
}
