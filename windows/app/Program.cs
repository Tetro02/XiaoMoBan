using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("小墨板")]
[assembly: AssemblyDescription("Windows 数位板接收与屏幕批注")]
[assembly: AssemblyProduct("小墨板")]
[assembly: AssemblyVersion("0.6.8.0")]
[assembly: AssemblyFileVersion("0.6.8.0")]

namespace PenNoteBridge
{
    internal static class Program
    {
        [STAThread]
        public static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var instance = new Mutex(false, @"Local\PenNoteBridge.Native.0f419626"))
            {
                bool acquired;
                try { acquired = instance.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    MessageBox.Show("小墨板已运行，可从系统托盘打开。", "小墨板", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                try
                {
                    string profile = DesktopHost.ProfileDirectory();
                    DesktopTheme.Initialize(profile);
                    using (var receiver = new DesktopHost(profile))
                    using (var desktop = new DesktopSession(receiver))
                        Application.Run(desktop);
                    return 0;
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, "小墨板启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { instance.ReleaseMutex(); }
            }
        }
    }

    internal sealed class DesktopSession : ApplicationContext, IMessageFilter
    {
        readonly DesktopHost receiver;
        readonly Dashboard dashboard;
        readonly AnnotationController annotation;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Icon icon;
        readonly System.Windows.Forms.Timer timer;
        readonly System.Windows.Forms.Timer gestures;
        readonly TouchpadInput touchpad = new TouchpadInput();
        bool busy;
        bool exiting;
        string snapshotError;

        public DesktopSession(DesktopHost receiver)
        {
            this.receiver = receiver;
            annotation = new AnnotationController();
            annotation.ServerEraserIntent = receiver.TakeEraserIntent;
            dashboard = new Dashboard();
            icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
            dashboard.Icon = icon;
            dashboard.Command += OnCommand;
            dashboard.FormClosing += OnClosing;
            annotation.StateChanged += delegate { SyncAnnotation(); };
            dashboard.HandleCreated += delegate { annotation.RegisterHotkeys(dashboard.Handle); };
            dashboard.Shown += delegate
            {
                Refresh();
                SyncAnnotation();
                OnCommand("tablet_auto_start", null);
            };

            menu = new ContextMenuStrip();
            menu.Items.Add("打开小墨板", null, delegate { ShowDashboard(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("屏幕批注", null, delegate { annotation.ToggleAnnotation(); });
            menu.Items.Add("白板", null, delegate { annotation.ToggleWhiteboard(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出小墨板", null, delegate { Exit(); });
            ApplyTheme(null, EventArgs.Empty);
            DesktopTheme.Changed += ApplyTheme;
            tray = new NotifyIcon { Icon = icon, Text = "小墨板 · 数位板与批注", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowDashboard(); };

            timer = new System.Windows.Forms.Timer { Interval = 160 };
            timer.Tick += delegate
            {
                DesktopTheme.RefreshSystemTheme();
                if (dashboard.Visible) Refresh();
            };
            timer.Start();
            gestures = new System.Windows.Forms.Timer { Interval = 25 };
            gestures.Tick += delegate
            {
                switch (receiver.TakeBarrelGesture())
                {
                    case 2: annotation.ToggleMiniWhiteboard(); break;
                    case 3: annotation.ToggleAnnotation(); break;
                }
                CanvasNavigation motion;
                for (int i = 0; i < 12 && receiver.TakeNavigation(out motion); i++)
                {
                    if (motion.Action >= 3)
                    {
                        if (receiver.CanInjectInput) annotation.HandlePenBodyGesture(motion.Action);
                        continue;
                    }
                    if (motion.Action == 2)
                    {
                        annotation.PanCanvasByPixels(motion.PanX, motion.PanY);
                        continue;
                    }
                    if (motion.Action == 0 && annotation.NavigateCanvas(
                        motion.PanX, motion.PanY, motion.Scale, motion.FocusX, motion.FocusY))
                        touchpad.Reset();
                    else if (receiver.CanInjectInput) touchpad.Send(motion);
                }
            };
            gestures.Start();
            Application.AddMessageFilter(this);
            MainForm = dashboard;
        }

        void ShowDashboard()
        {
            if (exiting) return;
            if (!dashboard.Visible) dashboard.Show();
            if (dashboard.WindowState == FormWindowState.Minimized) dashboard.WindowState = FormWindowState.Normal;
            dashboard.Activate();
            Refresh();
        }

        void ApplyTheme(object sender, EventArgs args)
        {
            DesktopTheme.ApplyMenu(menu);
        }

        public bool PreFilterMessage(ref Message message)
        {
            return message.Msg == 0x0312 && message.HWnd == dashboard.Handle
                && annotation.HandleHotkey(message.WParam.ToInt32());
        }

        async void OnCommand(string command, IDictionary<string, object> args)
        {
            if (exiting) return;
            try
            {
                switch (command)
                {
                    case "annotate_toggle": annotation.ToggleAnnotation(); return;
                    case "whiteboard_toggle": annotation.ToggleWhiteboard(); return;
                    case "open_settings": annotation.ShowSettings(); return;
                }
                if (busy)
                {
                    dashboard.SetStatus("正在处理上一项操作", false);
                    return;
                }
                busy = true;
                try
                {
                    var reply = await Task.Run(() => receiver.Execute(command, args));
                    if (exiting) return;
                    if (command == "tablet_pairing" && reply != null)
                        dashboard.RenderPairing(DesktopHost.Text(reply, "payload", ""), DesktopHost.Text(reply, "svg", ""));
                    else
                        Refresh();
                }
                finally { busy = false; }
            }
            catch (Exception error)
            {
                if (!exiting)
                {
                    Refresh();
                    dashboard.SetStatus(error.Message, true);
                }
            }
        }

        void SyncAnnotation()
        {
            if (exiting) return;
            receiver.SetAnnotationCapture(annotation.PenCaptureWindows());
            dashboard.RenderAnnotationState(new Dictionary<string, object>
            {
                {"annotating", annotation.Annotating}, {"whiteboard", annotation.Whiteboard},
            });
        }

        void Refresh()
        {
            try
            {
                var snapshot = receiver.TrySnapshot();
                if (snapshot != null)
                {
                    dashboard.RenderSnapshot(snapshot);
                    snapshotError = null;
                }
            }
            catch (Exception error)
            {
                if (snapshotError == error.Message) return;
                snapshotError = error.Message;
                dashboard.SetStatus(snapshotError, true);
            }
        }

        void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (args.CloseReason != CloseReason.UserClosing || exiting) return;
            args.Cancel = true;
            dashboard.Hide();
        }

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            tray.Visible = false;
            dashboard.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                exiting = true;
                timer.Stop();
                timer.Dispose();
                gestures.Stop();
                gestures.Dispose();
                Application.RemoveMessageFilter(this);
                DesktopTheme.Changed -= ApplyTheme;
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                annotation.Dispose();
                dashboard.Dispose();
                icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
