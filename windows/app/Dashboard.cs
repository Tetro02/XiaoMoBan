using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PenNoteBridge
{
    public sealed class Dashboard : Form
    {
        static readonly Color LightPaper = Color.FromArgb(255, 253, 253);
        static readonly Color LightQuiet = Color.FromArgb(108, 85, 93);
        static readonly Color LightLine = Color.FromArgb(236, 219, 224);
        static readonly Color LightAccent = Color.FromArgb(150, 53, 83);
        static readonly Color LightSoftAccent = Color.FromArgb(250, 229, 236);
        static Color Canvas { get { return DesktopTheme.IsDark ? DesktopTheme.Canvas : Color.FromArgb(250, 245, 244); } }
        static Color Paper { get { return DesktopTheme.IsDark ? DesktopTheme.Paper : LightPaper; } }
        static Color Ink { get { return DesktopTheme.IsDark ? DesktopTheme.Ink : Color.FromArgb(53, 39, 45); } }
        static Color Quiet { get { return DesktopTheme.IsDark ? DesktopTheme.Muted : LightQuiet; } }
        static Color Line { get { return DesktopTheme.IsDark ? DesktopTheme.Outline : LightLine; } }
        static Color Accent { get { return DesktopTheme.IsDark ? DesktopTheme.Accent : LightAccent; } }
        static Color SoftAccent { get { return DesktopTheme.IsDark ? DesktopTheme.SoftAccent : LightSoftAccent; } }
        static Color AccentLine { get { return DesktopTheme.IsDark ? DesktopTheme.AccentLine : Color.FromArgb(193, 115, 143); } }
        static Color Green { get { return DesktopTheme.IsDark ? DesktopTheme.Green : Color.FromArgb(25, 108, 70); } }
        static Color Error { get { return DesktopTheme.IsDark ? DesktopTheme.Error : Color.FromArgb(164, 39, 60); } }
        static readonly object PaperBackground = new object();
        static readonly string[] Routes = { "lan", "usb", "bluetooth" };
        static readonly string[] RouteNames = { "局域网", "USB", "蓝牙" };
        static readonly DesktopThemeMode[] ThemeModes = { DesktopThemeMode.System, DesktopThemeMode.Light, DesktopThemeMode.Dark };

        readonly ThemeLabel status = LabelFor("准备连接");
        readonly ThemeLabel lanState = LabelFor("");
        readonly Label lanHint = Paragraph("");
        readonly ThemeLabel usbState = LabelFor("");
        readonly Label usbScanStatus = Paragraph("");
        readonly Label hidState = LabelFor("");
        readonly Label bluetoothHidState = LabelFor("");
        readonly ThemeLabel pin = LabelFor("— — —");
        readonly ThemeLabel expiry = LabelFor("");
        readonly Label deviceSummary = LabelFor("连接设备", true);
        readonly Label pressureValue = LabelFor("×1.00");
        readonly Button annotate = ButtonFor("屏幕批注");
        readonly Button board = ButtonFor("白板");
        readonly Button lanStart = ButtonFor("开启局域网", true);
        readonly Button usbStart = ButtonFor("开启 USB", true);
        readonly Button copyCode = ButtonFor("复制");
        readonly Button newCode = ButtonFor("刷新连接码");
        readonly Button showPair = ButtonFor("连接二维码");
        readonly Button attach = ButtonFor("连接", true);
        readonly Button disconnect = ButtonFor("断开");
        readonly ComboBox address = ComboFor("电脑 IPv4 地址");
        readonly ComboBox usb = ComboFor("USB 设备");
        readonly ComboBox target = ComboFor("显示器映射");
        readonly ComboBox sideAction = ComboFor("侧键操作");
        readonly ComboBox clients = ComboFor("已连接平板");
        readonly CheckBox inject = CheckFor("将笔事件输出到 Windows");
        readonly TrackBar pressure = new TrackBar { Minimum = 25, Maximum = 200, Value = 100, SmallChange = 5, LargeChange = 25,
            TickStyle = TickStyle.None, AutoSize = false, Dock = DockStyle.Fill, AccessibleName = "压感倍率" };
        readonly Timer sendPressure = new Timer { Interval = 220 };
        readonly ToolTip connectionDetails = new ToolTip();
        readonly TextBox shortcut = new TextBox { ReadOnly = true, AccessibleName = "侧键快捷键" };
        readonly TextBox onlineEndpoint = new TextBox { AccessibleName = "在线配对服务 HTTPS 地址" };
        readonly Label onlineState = LabelFor("");
        readonly Label onlineCode = LabelFor("", true);
        readonly Button copyOnline = ButtonFor("复制在线码");
        readonly Button createOnline = ButtonFor("生成连接码", true);
        readonly Button copyBluetooth = ButtonFor("复制兼容蓝牙密钥");
        readonly Button sppStart = ButtonFor("启用兼容蓝牙");
        readonly TextBox logs = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Tag = PaperBackground, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "接收日志" };
        readonly Panel page = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Tag = PaperBackground };
        readonly Button[] tabButtons = new Button[3];
        readonly Control[] tabPages = new Control[3];

        IDictionary<string, object> current;
        bool rendering;
        bool onlineEdited;
        bool awaitingShortcut;
        bool canRenew;
        int sideKey;
        int sideModifiers;
        string code = "", bluetoothCode = "", fingerprint = "", relayCode = "", lastError = "";
        string feedback = "";
        DateTime feedbackUntil;
        Form settings;
        TabPage connectionSettings;
        TabPage onlineSettings;
        ComboBox appearance;

        public event Action<string, IDictionary<string, object>> Command;

        public Dashboard()
        {
            Text = "小墨板";
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Canvas;
            ForeColor = Ink;
            ClientSize = new Size(827, 553);
            MinimumSize = new Size(698, 548);
            StartPosition = FormStartPosition.CenterScreen;

            var frame = Rows(1, 77, -1);
            frame.Dock = DockStyle.Fill;
            frame.Controls.Add(BuildHeader(), 0, 0);
            var main = Rows(1, 54, -1, 12, 65);
            main.Dock = DockStyle.Fill;
            main.Padding = new Padding(22, 14, 22, 19);
            main.Controls.Add(BuildTransportTabs(), 0, 0);
            main.Controls.Add(page, 0, 1);
            tabPages[0] = BuildLan();
            tabPages[1] = BuildUsb();
            tabPages[2] = BuildBluetooth();
            foreach (var tab in tabPages) page.Controls.Add(tab);
            main.Controls.Add(BuildDeviceTray(), 0, 3);
            frame.Controls.Add(main, 0, 1);
            Controls.Add(frame);

            inject.Checked = true;
            SetChoices(target, new[] { new Choice("all", "整个桌面") }, "all");
            SetChoices(sideAction, new[] { new Choice("native", "随笔上报"), new Choice("disabled", "禁用"), new Choice("shortcut", "Windows 快捷键") }, "native");
            WireActions();
            SelectTab(0);
            RenderSnapshot(Args());
            ApplyTheme(this);
            DesktopTheme.Changed += ThemeChanged;
        }

        Control BuildHeader()
        {
            var header = Rows(3, -1);
            header.Dock = DockStyle.Fill;
            header.Tag = PaperBackground;
            header.Padding = new Padding(22, 9, 16, 9);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new BrandMark { Size = new Size(43, 43), Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, 0);
            var title = Rows(1, 34, -1);
            title.Dock = DockStyle.Fill;
            title.Controls.Add(Title("小墨板", 20F), 0, 0);
            status.Dock = DockStyle.Fill;
            status.AutoSize = false;
            status.AutoEllipsis = true;
            title.Controls.Add(status, 0, 1);
            header.Controls.Add(title, 1, 0);
            var open = ButtonFor("设置");
            open.Click += delegate { OpenSettings(null); };
            header.Controls.Add(Group(false, annotate, board, open), 2, 0);
            return header;
        }

        Control BuildTransportTabs()
        {
            var tabs = Rows(3, -1);
            tabs.Dock = DockStyle.Fill;
            tabs.Margin = new Padding(0, 0, 0, 10);
            tabs.AccessibleName = "连接方式";
            for (int i = 0; i < tabButtons.Length; ++i)
            {
                int route = i;
                tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / tabButtons.Length));
                var tab = ButtonFor(RouteNames[i]);
                tab.Dock = DockStyle.Fill;
                tab.Margin = new Padding(i == 0 ? 0 : 4, 0, i == tabButtons.Length - 1 ? 0 : 4, 0);
                tab.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
                tab.AccessibleName = RouteNames[i] + "连接";
                tab.Click += delegate { SelectTab(route); };
                tabButtons[i] = tab;
                tabs.Controls.Add(tab, i, 0);
            }
            return tabs;
        }

        void SelectTab(int index)
        {
            for (int i = 0; i < tabPages.Length; ++i)
            {
                tabPages[i].Visible = index == i;
                Mark(tabButtons[i], index == i);
            }
            tabPages[index].BringToFront();
        }

        Control BuildLan()
        {
            TableLayoutPanel body;
            var view = ConnectionCard(0, out body, 36, 37, 25, 44, 23, 38, -1);
            body.Controls.Add(Title("在同一网络连接", 17F), 0, 0);
            body.Controls.Add(lanHint, 0, 1);
            body.Controls.Add(LabelFor("本地连接码", true), 0, 2);
            pin.Font = new Font("Consolas", 23F, FontStyle.Bold);
            pin.SetTone(LabelTone.Accent);
            pin.AccessibleName = "本地连接码";
            body.Controls.Add(Group(true, pin, copyCode, newCode), 0, 3);
            body.Controls.Add(FillStatus(expiry), 0, 4);
            var destination = Grid(3, 1);
            destination.Dock = DockStyle.Fill;
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            destination.Controls.Add(LabelFor("电脑 IPv4"), 0, 0);
            address.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            destination.Controls.Add(address, 1, 0);
            showPair.AccessibleName = "连接二维码";
            showPair.Enabled = false;
            destination.Controls.Add(showPair, 2, 0);
            body.Controls.Add(destination, 0, 5);
            var state = Rows(2, -1);
            state.Dock = DockStyle.Fill;
            state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            state.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            state.Controls.Add(FillStatus(lanState), 0, 0);
            state.Controls.Add(lanStart, 1, 0);
            body.Controls.Add(state, 0, 6);
            return view;
        }

        Control BuildUsb()
        {
            TableLayoutPanel body;
            var view = ConnectionCard(1, out body, 33, 44, 37, 42, 39, -1);
            body.Controls.Add(Title("用数据线连接", 17F), 0, 0);
            body.Controls.Add(Paragraph("在平板上开启 USB 调试，连接电脑后，在平板上允许 USB 调试。"), 0, 1);
            usb.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            body.Controls.Add(usb, 0, 2);
            var refresh = ButtonFor("刷新设备");
            refresh.Click += delegate { Send("tablet_usb_scan"); };
            body.Controls.Add(Group(true, attach, refresh, usbStart), 0, 3);
            var details = Rows(1, 22, -1);
            details.Dock = DockStyle.Fill;
            details.Controls.Add(FillStatus(usbState), 0, 0);
            details.Controls.Add(usbScanStatus, 0, 1);
            body.Controls.Add(details, 0, 5);
            var locateAdb = ButtonFor("选择 adb.exe");
            locateAdb.Click += delegate {
                using (var dialog = new OpenFileDialog { Title = "选择解压后的 Android Platform-Tools",
                    Filter = "ADB|adb.exe", CheckFileExists = true, RestoreDirectory = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        Send("tablet_usb_adb", Args("path", dialog.FileName));
            };
            var getAdb = ButtonFor("获取 ADB");
            getAdb.Click += delegate {
                try { Process.Start(new ProcessStartInfo("https://developer.android.google.cn/tools/releases/platform-tools?hl=zh-cn") { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception error) { SetStatus(error.Message, true); }
            };
            body.Controls.Add(Group(true, locateAdb, getAdb), 0, 4);
            return view;
        }

        Control BuildBluetooth()
        {
            TableLayoutPanel body;
            var view = ConnectionCard(2, out body, 36, 54, 53, 40, 33, -1);
            body.Controls.Add(Title("选择蓝牙连接方式", 17F), 0, 0);
            body.Controls.Add(Paragraph("平板需继续连接蓝牙键鼠时，选「保留外设」并开启兼容蓝牙。"), 0, 1);
            body.Controls.Add(Paragraph("使用免 Server 直连时，在平板选「HID 直连」，再从 Windows 添加设备。"), 0, 2);
            var open = ButtonFor("打开蓝牙设置");
            open.Click += delegate {
                try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception error) { SetStatus(error.Message, true); }
            };
            var compat = ButtonFor("保留外设", true);
            compat.Click += delegate { OpenSettings("connection"); };
            body.Controls.Add(Group(true, compat, open), 0, 3);
            body.Controls.Add(bluetoothHidState, 0, 4);
            return view;
        }

        Control ConnectionCard(int route, out TableLayoutPanel body, params int[] rows)
        {
            var card = Rows(2, -1);
            card.Tag = PaperBackground;
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(21, 18, 19, 13);
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            card.Paint += delegate (object sender, PaintEventArgs e) { DrawOutline(card, e); };
            body = Rows(1, rows);
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(0, 0, 7, 0);
            card.Controls.Add(body, 0, 0);
            card.Controls.Add(new ConnectionArt(route) { Dock = DockStyle.Fill, Margin = Padding.Empty }, 1, 0);
            return card;
        }

        Control BuildDeviceTray()
        {
            var tray = Rows(3, -1);
            tray.Dock = DockStyle.Fill;
            tray.Tag = PaperBackground;
            tray.Padding = new Padding(14, 0, 11, 0);
            tray.Paint += delegate (object sender, PaintEventArgs e) { DrawOutline(tray, e); };
            tray.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            tray.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tray.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tray.Controls.Add(deviceSummary, 0, 0);
            clients.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            tray.Controls.Add(clients, 1, 0);
            tray.Controls.Add(disconnect, 2, 0);
            return tray;
        }

        void WireActions()
        {
            annotate.Click += delegate { Send("annotate_toggle", Args("withToolbar", true)); };
            board.Click += delegate { Send("whiteboard_toggle"); };
            lanStart.Click += delegate { StartRoute("lan"); };
            usbStart.Click += delegate { StartRoute("usb"); };
            sppStart.Click += delegate { StartRoute("bluetooth"); };
            copyCode.Click += delegate { Copy(code, "已复制本地连接码"); };
            newCode.Click += delegate { Send("tablet_new_code"); };
            onlineEndpoint.TextChanged += delegate { if (!rendering) onlineEdited = true; };
            copyBluetooth.Click += delegate { Copy(bluetoothCode, "已复制兼容蓝牙密钥"); };
            copyOnline.Click += delegate { Copy(relayCode, "已复制在线连接码"); };
            showPair.Click += delegate { Send("tablet_pairing", Args("address", SelectedKey(address))); };
            address.SelectedIndexChanged += delegate { UpdateButtons(); };
            usb.SelectedIndexChanged += delegate { UpdateButtons(); };
            clients.SelectedIndexChanged += delegate { UpdateButtons(); };
            attach.Click += delegate {
                var choice = usb.SelectedItem as Choice;
                if (choice != null && choice.Value != null) Send("tablet_usb_attach", Args("serial", choice.Key));
            };
            disconnect.Click += delegate {
                var choice = clients.SelectedItem as Choice;
                if (choice != null && choice.Value != null)
                    Send("tablet_disconnect", Args("clientId", StringAt(choice.Value, "clientId"), "connectionId", choice.Key));
            };
            inject.CheckedChanged += delegate { if (!rendering) SaveOutput(); };
            target.SelectionChangeCommitted += delegate { if (!rendering) SaveOutput(); };
            sideAction.SelectionChangeCommitted += delegate {
                if (rendering) return;
                awaitingShortcut = SelectedKey(sideAction) == "shortcut" && sideKey == 0;
                UpdateShortcut();
                if (awaitingShortcut) shortcut.Focus();
                else SaveOutput();
            };
            shortcut.KeyDown += delegate (object sender, KeyEventArgs e) {
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu ||
                    e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
                sideKey = (int)e.KeyCode;
                bool win = (GetKeyState(0x5b) & 0x8000) != 0 || (GetKeyState(0x5c) & 0x8000) != 0;
                sideModifiers = (e.Control ? 1 : 0) | (e.Shift ? 2 : 0) | (e.Alt ? 4 : 0) | (win ? 8 : 0);
                e.SuppressKeyPress = true;
                awaitingShortcut = false;
                UpdateShortcut();
                SaveOutput();
            };
            pressure.ValueChanged += delegate {
                pressureValue.Text = "×" + (pressure.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
                if (!rendering) { sendPressure.Stop(); sendPressure.Start(); }
            };
            sendPressure.Tick += delegate { sendPressure.Stop(); SaveOutput(); };
        }

        void StartRoute(string route)
        {
            lastError = "";
            var active = Map(At(current, "running"));
            var desired = OutputArgs();
            foreach (string key in Routes)
                desired[key] = route == key || BoolAt(active, key);
            Send("tablet_start", Args("config", desired));
        }

        IDictionary<string, object> OutputArgs()
        {
            string display = SelectedKey(target);
            var args = Args("inject", inject.Checked, "target", display.Length == 0 ? "all" : display,
                "pressure", pressure.Value / 100.0);
            if (!awaitingShortcut)
            {
                args["sideKeyAction"] = SelectedKey(sideAction);
                args["sideKeyVirtualKey"] = sideKey;
                args["sideKeyModifiers"] = sideModifiers;
            }
            return args;
        }

        void SaveOutput() { Send("tablet_output", OutputArgs()); }

        void UpdateShortcut()
        {
            shortcut.Enabled = SelectedKey(sideAction) == "shortcut";
            shortcut.Text = sideKey == 0 ? "单击这里，再按下快捷键" :
                ((sideModifiers & 1) != 0 ? "Ctrl + " : "") + ((sideModifiers & 2) != 0 ? "Shift + " : "") +
                ((sideModifiers & 4) != 0 ? "Alt + " : "") + ((sideModifiers & 8) != 0 ? "Win + " : "") + (Keys)sideKey;
        }

        public void RenderSnapshot(IDictionary<string, object> snapshot)
        {
            if (snapshot == null || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<IDictionary<string, object>>(RenderSnapshot), snapshot); return; }
            current = snapshot;
            rendering = true;
            try
            {
                var config = Map(At(snapshot, "config"));
                var active = Map(At(snapshot, "running"));
                bool lanRunning = BoolAt(active, "lan"), usbRunning = BoolAt(active, "usb"), sppRunning = BoolAt(active, "bluetooth");
                int lanPort = (int)NumberAt(snapshot, "lanPort", 0);
                bool alternateLan = lanRunning && lanPort > 0 && lanPort != DesktopHost.DefaultLanPort;
                string wiredWinner = StringAt(snapshot, "wiredWinner");
                string lanError = !lanRunning && wiredWinner != "usb" ? StringAt(snapshot, "lanError") : "";
                string usbError = !usbRunning && wiredWinner != "lan" ? StringAt(snapshot, "usbError") : "";
                SetConnectionState(lanState, lanRunning, wiredWinner == "usb", lanError,
                    alternateLan ? "●  局域网端口 " + lanPort : "●  局域网已开启",
                    "○  USB 已连接，局域网暂停", "○  局域网已停止");
                SetConnectionState(usbState, usbRunning, wiredWinner == "lan", usbError,
                    "●  USB 接收已开启", "○  局域网已连接，USB 暂停", "○  USB 接收已停止");
                lanStart.Visible = !lanRunning && wiredWinner != "usb";
                usbStart.Visible = !usbRunning && wiredWinner != "lan";
                sppStart.Visible = !sppRunning;
                hidState.Text = StringAt(snapshot, "hidStatus");
                bluetoothHidState.Text = StringAt(snapshot, "bluetoothHidStatus");

                bool pairingAvailable = At(snapshot, "pairingAvailable") == null || BoolAt(snapshot, "pairingAvailable");
                lanHint.Text = alternateLan
                    ? pairingAvailable ? "请用新版平板端扫码或输入六位连接码。" : "请用新版平板端扫描连接二维码。"
                    : "首次连接可输入六位连接码，或用平板扫描连接二维码。";
                string pairingError = lanRunning && !pairingAvailable ? StringAt(snapshot, "pairingError") : "";
                canRenew = lanRunning && pairingAvailable;
                code = canRenew ? StringAt(snapshot, "shortPairingCode") : "";
                pin.Text = code.Length == 6 ? code.Insert(3, " ") : "— — —";
                pin.AccessibleDescription = code;
                int seconds = (int)NumberAt(snapshot, "pairingSecondsRemaining", 0);
                expiry.Text = code.Length == 6 && seconds > 0 ? "剩余 " + seconds + " 秒" : canRenew ?
                    "点击刷新生成连接码" : lanRunning ? "六位码不可用 · 请使用二维码" : "开启局域网后生成";
                expiry.SetTone(pairingError.Length > 0 ? LabelTone.Error : LabelTone.Quiet);
                SetConnectionDetails(expiry, pairingError);
                if (!alternateLan && canRenew && At(snapshot, "discoveryEnabled") is bool && !BoolAt(snapshot, "discoveryEnabled"))
                    lanState.Text = "●  局域网已开启，可手动输入电脑 IPv4";
                bluetoothCode = StringAt(snapshot, "bluetoothCode");
                if (bluetoothCode.Length == 0) bluetoothCode = StringAt(snapshot, "code");
                fingerprint = StringAt(snapshot, "fingerprint");
                var addresses = new List<Choice>();
                foreach (var item in Items(At(snapshot, "addresses")))
                {
                    string ip = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!String.IsNullOrEmpty(ip)) addresses.Add(new Choice(ip, ip));
                }
                if (addresses.Count == 0) addresses.Add(new Choice("", "暂未获取"));
                SetChoices(address, addresses, SelectedKey(address));

                var devices = new List<Choice>();
                foreach (var item in Items(At(snapshot, "usbDevices")))
                {
                    var device = Map(item);
                    string serial = StringAt(device, "serial"), name = StringAt(device, "model"), state = StringAt(device, "state");
                    string detail = state == "device" ? "已授权" : state == "unauthorized" ? "等待授权" : state == "offline" ? "离线" : state;
                    devices.Add(new Choice(serial, (name.Length == 0 ? serial : name) + "  /  " + detail, device));
                }
                if (devices.Count == 0) devices.Add(new Choice("", "未发现 USB 设备"));
                SetChoices(usb, devices, SelectedKey(usb));
                usbScanStatus.Text = StringAt(snapshot, "usbScanStatus");

                var connected = new List<Choice>();
                foreach (var item in Items(At(snapshot, "clients")))
                {
                    var client = Map(item);
                    string name = StringAt(client, "displayName");
                    connected.Add(new Choice(StringAt(client, "connectionId"), (name.Length == 0 ? "平板" : name) +
                        "  /  " + TransportLabel(StringAt(client, "transport")), client));
                }
                int count = connected.Count;
                deviceSummary.Text = count == 0 ? "连接设备" : "已连接 " + count + " 台";
                if (count == 0) connected.Add(new Choice("", "等待小墨板平板连接"));
                SetChoices(clients, connected, SelectedKey(clients));

                string text = StringAt(snapshot, "status");
                status.Text = lastError.Length > 0 ? lastError : DateTime.UtcNow < feedbackUntil ? feedback :
                    count > 0 ? "平板已连接" : text.Length > 0 ? text : "准备连接";
                status.SetTone(lastError.Length > 0 ? LabelTone.Error : count > 0 ? LabelTone.Success : LabelTone.Quiet);
                if (At(config, "inject") != null) inject.Checked = BoolAt(config, "inject");
                if (At(config, "pressure") != null && !sendPressure.Enabled && !pressure.Capture)
                    pressure.Value = Math.Max(25, Math.Min(200, (int)Math.Round(NumberAt(config, "pressure", 1) * 100)));
                if (!awaitingShortcut)
                {
                    SetChoices(sideAction, new[] { new Choice("native", "原生侧键"), new Choice("disabled", "关闭侧键右键菜单"),
                        new Choice("shortcut", "Windows 快捷键") }, StringAt(config, "sideKeyAction"));
                    sideKey = (int)NumberAt(config, "sideKeyVirtualKey", sideKey);
                    sideModifiers = (int)NumberAt(config, "sideKeyModifiers", sideModifiers);
                }
                UpdateShortcut();
                var monitors = new List<Choice> { new Choice("all", "整个桌面") };
                foreach (var item in Items(At(snapshot, "monitors")))
                {
                    var display = Map(item);
                    monitors.Add(new Choice(StringAt(display, "id"), StringAt(display, "label") + "  " +
                        StringAt(display, "width") + " × " + StringAt(display, "height")));
                }
                SetChoices(target, monitors, StringAt(config, "target"));
                var online = Map(At(snapshot, "online"));
                if (!onlineEdited && !onlineEndpoint.Focused) onlineEndpoint.Text = StringAt(online, "endpoint");
                relayCode = StringAt(online, "code");
                onlineState.Text = StringAt(online, "status");
                onlineCode.Text = relayCode.Length == 0 ? "尚未生成在线连接码" : relayCode;
                UpdateLogs(At(snapshot, "logs"));
            }
            finally { rendering = false; }
            UpdateButtons();
        }

        void SetConnectionState(ThemeLabel label, bool active, bool paused, string error, string started, string suspended, string stopped)
        {
            label.Text = active ? started : paused ? suspended : error.Length > 0 ? error : stopped;
            label.SetTone(active ? LabelTone.Success : error.Length > 0 ? LabelTone.Error : LabelTone.Quiet);
            SetConnectionDetails(label, error);
        }

        void SetConnectionDetails(ThemeLabel label, string details)
        {
            if (connectionDetails.GetToolTip(label) == details) return;
            label.AccessibleDescription = details;
            connectionDetails.SetToolTip(label, details);
        }

        void UpdateButtons()
        {
            if (rendering) return;
            copyCode.Enabled = code.Length > 0;
            newCode.Enabled = canRenew;
            System.Net.IPAddress ip;
            showPair.Enabled = BoolAt(Map(At(current, "running")), "lan") &&
                System.Net.IPAddress.TryParse(SelectedKey(address), out ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !System.Net.IPAddress.IsLoopback(ip) && !ip.Equals(System.Net.IPAddress.Any);
            copyBluetooth.Enabled = bluetoothCode.Length > 0;
            copyOnline.Enabled = relayCode.Length > 0;
            createOnline.Enabled = SelectedKey(address).Length > 0 &&
                BoolAt(Map(At(current, "running")), "lan") && StringAt(Map(At(current, "online")), "endpoint").Length > 0;
            var device = usb.SelectedItem as Choice;
            attach.Enabled = BoolAt(Map(At(current, "running")), "usb") && device != null && StringAt(device.Value, "state") == "device";
            var client = clients.SelectedItem as Choice;
            disconnect.Enabled = client != null && client.Value != null;
        }

        void UpdateLogs(object value)
        {
            var entries = new List<string>();
            foreach (var item in Items(value)) entries.Add(Convert.ToString(item, CultureInfo.CurrentCulture));
            string text = String.Join(Environment.NewLine, entries.ToArray());
            if (logs.Text == text) return;
            bool atEnd = logs.SelectionStart + logs.SelectionLength >= logs.TextLength;
            int selection = logs.SelectionStart;
            logs.Text = text;
            logs.SelectionStart = atEnd ? logs.TextLength : Math.Min(selection, logs.TextLength);
            logs.ScrollToCaret();
        }

        void OpenSettings(string section)
        {
            if (settings == null || settings.IsDisposed)
            {
                settings = MakeDialog("小墨板设置", new Size(543, 360));
                var tabs = new ThemeTabs { Dock = DockStyle.Fill };
                var output = new TabPage("笔与显示") { Padding = new Padding(15) };
                var grid = Rows(1, 39, 37, 43, 39, 39, 48, 31, -1);
                grid.Dock = DockStyle.Fill;
                grid.Controls.Add(inject, 0, 0);
                grid.Controls.Add(Field("映射到", target), 0, 1);
                var slider = Rows(3, -1);
                slider.Dock = DockStyle.Fill;
                slider.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
                slider.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                slider.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
                slider.Controls.Add(LabelFor("压感倍率"), 0, 0);
                slider.Controls.Add(pressure, 1, 0);
                slider.Controls.Add(pressureValue, 2, 0);
                grid.Controls.Add(slider, 0, 2);
                grid.Controls.Add(Field("侧键操作", sideAction), 0, 3);
                grid.Controls.Add(Field("快捷键", shortcut), 0, 4);
                var test = ButtonFor("打开试笔");
                test.Click += delegate { OpenPenTest(); };
                var options = ButtonFor("批注设置");
                options.Click += delegate { Send("open_settings"); };
                grid.Controls.Add(Group(true, test, options), 0, 5);
                grid.Controls.Add(hidState, 0, 6);
                output.Controls.Add(grid);
                tabs.TabPages.Add(output);

                connectionSettings = new TabPage("连接与日志") { Padding = new Padding(15) };
                var details = Rows(1, 45, 45, 30, -1);
                details.Dock = DockStyle.Fill;
                var copyIp = ButtonFor("复制 IPv4");
                copyIp.Click += delegate { Copy(SelectedKey(address), "已复制电脑 IPv4"); };
                var copyFingerprint = ButtonFor("复制证书指纹");
                copyFingerprint.Click += delegate { Copy(fingerprint, "已复制证书指纹"); };
                details.Controls.Add(Group(true, copyIp, copyFingerprint), 0, 0);
                details.Controls.Add(Group(true, sppStart, copyBluetooth), 0, 1);
                details.Controls.Add(LabelFor("接收日志", true), 0, 2);
                details.Controls.Add(logs, 0, 3);
                connectionSettings.Controls.Add(details);
                tabs.TabPages.Add(connectionSettings);

                tabs.TabPages.Add(BuildAppearanceSettings());

                onlineSettings = new TabPage("在线连接码") { Padding = new Padding(15) };
                var relay = Rows(1, 58, 41, 44, 47, -1);
                relay.Dock = DockStyle.Fill;
                relay.Controls.Add(Paragraph("可用自己的 HTTPS 配对服务交换连接信息。笔输入仍在本地网络传输。"), 0, 0);
                relay.Controls.Add(Field("配对服务", onlineEndpoint), 0, 1);
                var save = ButtonFor("保存地址");
                save.Click += delegate {
                    string endpoint = onlineEndpoint.Text.Trim();
                    onlineEdited = false;
                    Send("tablet_online_settings", Args("endpoint", endpoint));
                };
                createOnline.Click += delegate { Send("tablet_online_create", Args("address", SelectedKey(address))); };
                relay.Controls.Add(Group(true, save, createOnline), 0, 2);
                relay.Controls.Add(Group(true, onlineCode, copyOnline), 0, 3);
                relay.Controls.Add(onlineState, 0, 4);
                onlineSettings.Controls.Add(relay);
                if (At(current, "online") != null) tabs.TabPages.Add(onlineSettings);
                settings.Controls.Add(tabs);
                settings.FormClosing += delegate (object sender, FormClosingEventArgs e) {
                    if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; settings.Hide(); }
                };
            }
            var settingsTabs = (TabControl)settings.Controls[0];
            if (onlineSettings != null && At(current, "online") != null && !settingsTabs.TabPages.Contains(onlineSettings))
                settingsTabs.TabPages.Add(onlineSettings);
            ApplyTheme(settings);
            if (section == "connection") settingsTabs.SelectedTab = connectionSettings;
            if (!settings.Visible) settings.Show(this);
            settings.Activate();
        }

        TabPage BuildAppearanceSettings()
        {
            var tab = new TabPage("外观") { Padding = new Padding(15) };
            var fields = Rows(1, 43, -1);
            fields.Dock = DockStyle.Fill;
            appearance = ComboFor("主题");
            appearance.Items.AddRange(new object[] { "跟随系统", "浅色", "深色" });
            appearance.SelectedIndex = Array.IndexOf(ThemeModes, DesktopTheme.Mode);
            appearance.SelectionChangeCommitted += delegate {
                int selection = appearance.SelectedIndex;
                if (selection < 0) return;
                try { DesktopTheme.SetMode(ThemeModes[selection]); }
                catch (Exception error)
                {
                    if (!(error is IOException || error is UnauthorizedAccessException)) throw;
                    appearance.SelectedIndex = Array.IndexOf(ThemeModes, DesktopTheme.Mode);
                    MessageBox.Show(settings, "无法保存主题设置，请检查小墨板本地数据目录的写入权限。",
                        "小墨板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            fields.Controls.Add(Field("主题", appearance), 0, 0);
            tab.Controls.Add(fields);
            return tab;
        }

        void OpenPenTest()
        {
            using (var dialog = MakeDialog("Windows 试笔", new Size(555, 396)))
            {
                var grid = Rows(2, -1, 43);
                grid.Dock = DockStyle.Fill;
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var pad = new PenPad { Dock = DockStyle.Fill };
                var clear = ButtonFor("清除");
                clear.Click += delegate { pad.Reset(); };
                grid.Controls.Add(pad, 0, 0);
                grid.SetColumnSpan(pad, 2);
                grid.Controls.Add(LabelFor("用平板上的笔轻重落笔，比较笔画粗细。"), 0, 1);
                grid.Controls.Add(clear, 1, 1);
                dialog.Controls.Add(grid);
                ApplyTheme(dialog);
                dialog.ShowDialog(settings ?? this);
            }
        }

        public void RenderAnnotationState(IDictionary<string, object> snapshot)
        {
            if (snapshot == null || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<IDictionary<string, object>>(RenderAnnotationState), snapshot); return; }
            bool whiteboard = BoolAt(snapshot, "whiteboard");
            Mark(annotate, BoolAt(snapshot, "annotating") && !whiteboard);
            Mark(board, whiteboard);
        }

        public void RenderPairing(string payload, string svg)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string, string>(RenderPairing), payload, svg); return; }
            var matrix = QrSurface.Read(svg);
            if (matrix == null) SetStatus("无法显示二维码，请复制连接码", true);
            using (var dialog = MakeDialog("局域网配对", new Size(359, matrix == null ? 145 : 388)))
            {
                bool alternateLan = payload != null && payload.StartsWith("PNB2|", StringComparison.Ordinal);
                var tip = Paragraph(alternateLan ? "请用新版小墨板扫描二维码，二维码包含备用端口。"
                    : "在平板的局域网连接页面扫描二维码。");
                tip.Dock = DockStyle.Top;
                tip.Height = 43;
                var copy = ButtonFor("复制完整连接码", true);
                copy.Dock = DockStyle.Bottom;
                copy.Click += delegate { Copy(payload, "已复制完整连接码"); };
                if (matrix != null) dialog.Controls.Add(new QrSurface(matrix) { Dock = DockStyle.Fill, AccessibleName = "局域网配对二维码" });
                dialog.Controls.Add(tip);
                dialog.Controls.Add(copy);
                ApplyTheme(dialog);
                dialog.ShowDialog(settings != null && settings.Visible ? settings : this);
            }
        }

        public void SetStatus(string message, bool error)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string, bool>(SetStatus), message, error); return; }
            status.Text = message ?? "";
            status.SetTone(error ? LabelTone.Error : LabelTone.Quiet);
            lastError = error ? status.Text : "";
            feedback = status.Text;
            feedbackUntil = DateTime.UtcNow.AddSeconds(3);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DesktopTheme.ApplyTitleBar(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DesktopTheme.Changed -= ThemeChanged;
                sendPressure.Dispose();
                connectionDetails.Dispose();
                if (settings != null) settings.Dispose();
            }
            base.Dispose(disposing);
        }

        void ThemeChanged(object sender, EventArgs e)
        {
            if (appearance != null && !appearance.IsDisposed)
                appearance.SelectedIndex = Array.IndexOf(ThemeModes, DesktopTheme.Mode);
            ApplyTheme(this);
            if (settings != null && !settings.IsDisposed && settings.Owner != this) ApplyTheme(settings);
        }

        static void ApplyTheme(Control control)
        {
            var form = control as Form;
            var button = control as ThemeButton;
            var label = control as ThemeLabel;
            var tabs = control as ThemeTabs;
            if (form != null)
            {
                form.BackColor = form is Dashboard ? Canvas : Paper;
                form.ForeColor = Ink;
                DesktopTheme.ApplyTitleBar(form);
                foreach (Form owned in form.OwnedForms) ApplyTheme(owned);
            }
            else if (button != null) button.ApplyColors();
            else if (label != null) label.ApplyColors();
            else if (tabs != null) tabs.ApplyColors();
            else if (control is PenPad) ((PenPad)control).ApplyColors();
            else if (control is TabPage)
            {
                ((TabPage)control).UseVisualStyleBackColor = false;
                control.BackColor = Paper;
            }
            else if (control is TextBox || control is ComboBox)
            {
                control.ForeColor = Ink;
                control.BackColor = ReferenceEquals(control.Tag, PaperBackground) ? Paper :
                    DesktopTheme.IsDark ? DesktopTheme.Surface : SystemColors.Window;
            }
            else if (control is TrackBar || ReferenceEquals(control.Tag, PaperBackground))
                control.BackColor = Paper;
            else if (control is CheckBox)
                control.ForeColor = Ink;

            foreach (Control child in control.Controls) ApplyTheme(child);
            control.Invalidate();
        }

        void Send(string name, IDictionary<string, object> args = null)
        {
            var listener = Command;
            if (listener != null) listener(name, args ?? Args());
        }

        void Copy(string text, string feedbackText)
        {
            if (String.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); SetStatus(feedbackText, false); }
            catch (ExternalException) { SetStatus("剪贴板正忙，请重试", true); }
        }

        static TableLayoutPanel Rows(int columns, params int[] heights)
        {
            var grid = Grid(columns, heights.Length);
            foreach (int height in heights)
                grid.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            return grid;
        }

        static FlowLayoutPanel Group(bool left, params Control[] controls)
        {
            var group = Actions();
            if (left) group.Anchor = AnchorStyles.Left;
            group.Controls.AddRange(controls);
            return group;
        }

        Label Title(string text, float size)
        {
            var label = LabelFor(text, true);
            label.Font = new Font(Font.FontFamily, size, FontStyle.Bold);
            return label;
        }

        static Label Paragraph(string text)
        {
            var label = LabelFor(text);
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.TopLeft;
            label.Padding = new Padding(0, 6, 5, 0);
            return label;
        }

        static void DrawOutline(Control control, PaintEventArgs e)
        {
            if (control.Width < 2 || control.Height < 2) return;
            using (var pen = new Pen(Line)) e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
        }

        [DllImport("user32.dll")]
        static extern short GetKeyState(int key);

        sealed class ConnectionArt : Control
        {
            readonly int route;
            public ConnectionArt(int route) { this.route = route; DoubleBuffered = true; ResizeRedraw = true; TabStop = false; }

            protected override void OnPaint(PaintEventArgs e)
            {
                float scale = Math.Min(Width / 244F, Height / 207F);
                if (scale <= 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform((Width - 244 * scale) / 2, (Height - 207 * scale) / 2);
                g.ScaleTransform(scale, scale);
                using (var background = new SolidBrush(SoftAccent))
                using (var paper = new SolidBrush(Paper))
                using (var stroke = new Pen(Accent, 2.2F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                using (var soft = new Pen(AccentLine, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.FillEllipse(background, 31, 13, 181, 181);
                    using (var monitor = Round(19, 76, 96, 62))
                    using (var tablet = Round(154, 68, 61, 84))
                    {
                        g.FillPath(paper, monitor); g.DrawPath(stroke, monitor);
                        g.FillPath(paper, tablet); g.DrawPath(stroke, tablet);
                    }
                    g.DrawLine(stroke, 67, 138, 67, 151);
                    g.DrawLine(stroke, 49, 151, 85, 151);
                    g.DrawLine(soft, 174, 141, 195, 141);
                    g.DrawLine(soft, 175, 117, 196, 94);
                    g.DrawLine(soft, 175, 117, 178, 109);
                    g.DrawLine(soft, 175, 117, 183, 114);
                    if (route == 0)
                    {
                        g.DrawArc(soft, 106, 54, 52, 48, 227, 86);
                        g.DrawArc(soft, 115, 64, 34, 30, 224, 92);
                        g.DrawArc(stroke, 125, 75, 14, 12, 220, 100);
                    }
                    else if (route == 1)
                    {
                        g.DrawBezier(soft, 115, 109, 126, 109, 139, 127, 154, 127);
                        g.DrawLine(stroke, 120, 105, 120, 113);
                        g.DrawLine(stroke, 147, 123, 147, 131);
                    }
                    else
                    {
                        g.DrawLines(stroke, new[] { new Point(123, 75), new Point(142, 94), new Point(132, 103),
                            new Point(132, 66), new Point(142, 76), new Point(123, 94) });
                    }
                }
            }

            static GraphicsPath Round(int x, int y, int width, int height)
            {
                const int d = 14;
                var path = new GraphicsPath();
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + width - d, y, d, d, 270, 90);
                path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
                path.AddArc(x, y + height - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }



        static string TransportLabel(string route)
        {
            int index = Array.IndexOf(Routes, route);
            return index < 0 ? route : RouteNames[index];
        }

        static TableLayoutPanel Grid(int columns, int rows)
        {
            return new TableLayoutPanel { ColumnCount = columns, RowCount = rows, Margin = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
        }

        static Control Field(string text, Control field)
        {
            var row = Grid(2, 1);
            row.Dock = DockStyle.Fill;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(LabelFor(text), 0, 0);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            row.Controls.Add(field, 1, 0);
            return row;
        }

        static FlowLayoutPanel Actions()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Anchor = AnchorStyles.Right, Margin = Padding.Empty, Padding = Padding.Empty };
        }

        static ThemeLabel LabelFor(string text, bool important = false)
        {
            return new ThemeLabel(important ? LabelTone.Ink : LabelTone.Quiet) { AutoSize = true, Text = text,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 4, 0), TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        }

        static ThemeLabel FillStatus(ThemeLabel label)
        {
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            return label;
        }

        static Button ButtonFor(string text, bool primary = false)
        {
            return new ThemeButton(primary) { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left,
                MinimumSize = new Size(0, 32), Margin = new Padding(4, 0, 0, 0), Padding = new Padding(9, 0, 9, 0),
                UseMnemonic = false };
        }

        static void Mark(Button button, bool active)
        {
            ((ThemeButton)button).SetActive(active);
        }

        static CheckBox CheckFor(string text)
        {
            return new CheckBox { AutoSize = true, Anchor = AnchorStyles.Left, Text = text, Margin = Padding.Empty, ForeColor = Ink };
        }

        static ComboBox ComboFor(string name)
        {
            return new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name, ForeColor = Ink };
        }

        Form MakeDialog(string caption, Size size)
        {
            var dialog = new Form { Text = caption, ClientSize = size, Padding = new Padding(15), StartPosition = FormStartPosition.CenterParent,
                Font = Font, BackColor = Paper, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi };
            dialog.HandleCreated += delegate { DesktopTheme.ApplyTitleBar(dialog); };
            return dialog;
        }

        enum LabelTone { Quiet, Ink, Accent, Success, Error }

        sealed class ThemeLabel : Label
        {
            LabelTone tone;

            public ThemeLabel(LabelTone tone) { SetTone(tone); }

            public void SetTone(LabelTone value)
            {
                tone = value;
                ApplyColors();
            }

            public void ApplyColors()
            {
                switch (tone)
                {
                    case LabelTone.Ink: ForeColor = Ink; break;
                    case LabelTone.Accent: ForeColor = Accent; break;
                    case LabelTone.Success: ForeColor = Green; break;
                    case LabelTone.Error: ForeColor = Error; break;
                    default: ForeColor = Quiet; break;
                }
            }
        }

        sealed class ThemeButton : Button
        {
            readonly bool primary;
            bool active;

            public ThemeButton(bool primary)
            {
                this.primary = primary;
                FlatStyle = FlatStyle.Flat;
                UseVisualStyleBackColor = false;
                ApplyColors();
            }

            public void SetActive(bool value)
            {
                active = value;
                AccessibleDescription = value ? "已启用" : "未启用";
                ApplyColors();
            }

            public void ApplyColors()
            {
                bool dark = DesktopTheme.IsDark;
                Color fill = dark ? DesktopTheme.AccentFill : LightAccent;
                ForeColor = primary ? dark ? DesktopTheme.AccentText : LightPaper : Accent;
                BackColor = primary ? fill : active ? SoftAccent : Paper;
                FlatAppearance.BorderColor = primary ? fill : active ? Accent : Line;
                FlatAppearance.MouseOverBackColor = primary ? dark ? DesktopTheme.Hover : Color.FromArgb(125, 43, 69) : SoftAccent;
                FlatAppearance.MouseDownBackColor = dark ? DesktopTheme.Pressed : Color.Empty;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled || !DesktopTheme.IsDark)
                {
                    base.OnPaint(e);
                    return;
                }

                e.Graphics.Clear(DesktopTheme.Surface);
                DrawOutline(this, e);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Quiet,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        sealed class ThemeComboBox : ComboBox
        {
            public ThemeComboBox()
            {
                DrawMode = DrawMode.OwnerDrawFixed;
                ItemHeight = Font.Height + 2;
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                ItemHeight = Font.Height + 2;
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                bool highlighted = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                Color background = highlighted ? DesktopTheme.IsDark ? SoftAccent : SystemColors.Highlight : BackColor;
                Color foreground = !Enabled ? Quiet : highlighted && !DesktopTheme.IsDark ? SystemColors.HighlightText : Ink;
                using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0 && e.Index < Items.Count)
                    TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -2, 0), foreground,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if ((e.State & DrawItemState.Focus) != 0) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, foreground, background);
            }
        }

        sealed class ThemeTabs : TabControl
        {
            public void ApplyColors()
            {
                bool dark = DesktopTheme.IsDark;
                if (GetStyle(ControlStyles.UserPaint) == dark) return;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, dark);
                UpdateStyles();
            }

            protected override void OnSelectedIndexChanged(EventArgs e)
            {
                base.OnSelectedIndexChanged(e);
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Canvas);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                for (int index = 0; index < TabCount; ++index)
                {
                    Rectangle bounds = Rectangle.Inflate(GetTabRect(index), -1, -1);
                    if (bounds.Width < 1 || bounds.Height < 1) continue;
                    bool active = index == SelectedIndex;
                    Color fill = active ? SoftAccent : Paper;
                    Color text = active ? Ink : Quiet;
                    using (var brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, bounds);
                    using (var pen = new Pen(active ? AccentLine : Line))
                        e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
                    TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds, text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    if (Focused && active) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3), text, fill);
                }
            }
        }

        sealed class Choice
        {
            public readonly string Key;
            public readonly string Caption;
            public readonly IDictionary<string, object> Value;
            public Choice(string key, string caption, IDictionary<string, object> value = null) { Key = key; Caption = caption; Value = value; }
            public override string ToString() { return Caption; }
        }

        static string SelectedKey(ListControl control)
        {
            var combo = control as ComboBox;
            var choice = (combo == null ? ((ListBox)control).SelectedItem : combo.SelectedItem) as Choice;
            return choice == null ? "" : choice.Key;
        }

        static bool SameChoices(IList items, IList<Choice> expected)
        {
            if (items.Count != expected.Count) return false;
            for (int index = 0; index < items.Count; ++index)
            {
                var old = items[index] as Choice;
                var next = expected[index];
                if (old == null || old.Key != next.Key || old.Caption != next.Caption ||
                    StringAt(old.Value, "state") != StringAt(next.Value, "state") ||
                    StringAt(old.Value, "clientId") != StringAt(next.Value, "clientId")) return false;
            }
            return true;
        }

        static void SetChoices(ComboBox combo, IList<Choice> next, string selected)
        {
            if (!SameChoices(combo.Items, next))
            {
                if (combo.DroppedDown) return;
                combo.BeginUpdate();
                try { combo.Items.Clear(); foreach (var choice in next) combo.Items.Add(choice); }
                finally { combo.EndUpdate(); }
            }
            if (!combo.DroppedDown) combo.SelectedIndex = FindChoice(next, selected);
        }

        static int FindChoice(IList<Choice> choices, string key)
        {
            for (int index = 0; index < choices.Count; ++index)
                if (choices[index].Key == key) return index;
            return choices.Count == 0 ? -1 : 0;
        }

        static IDictionary<string, object> Args(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int index = 0; index < pairs.Length; index += 2) result.Add((string)pairs[index], pairs[index + 1]);
            return result;
        }

        static object At(IDictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        static IDictionary<string, object> Map(object value) { return value as IDictionary<string, object>; }
        static IEnumerable Items(object value) { return (value as IEnumerable) ?? new object[0]; }
        static string StringAt(IDictionary<string, object> map, string key) { return Convert.ToString(At(map, key), CultureInfo.InvariantCulture) ?? ""; }
        static bool BoolAt(IDictionary<string, object> map, string key) { var value = At(map, key); return value is bool && (bool)value; }
        static double NumberAt(IDictionary<string, object> map, string key, double fallback)
        {
            double value;
            return Double.TryParse(StringAt(map, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !Double.IsNaN(value) && !Double.IsInfinity(value) ? value : fallback;
        }

        sealed class BrandMark : Control
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.ScaleTransform(Width / 43F, Height / 43F);
                using (var fill = new SolidBrush(SoftAccent)) e.Graphics.FillEllipse(fill, 1, 1, 41, 41);
                using (var pen = new Pen(Accent, 1.9F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    e.Graphics.DrawLines(pen, new[] { new Point(12, 29), new Point(14, 22), new Point(25, 11), new Point(31, 17), new Point(20, 28), new Point(12, 29) });
                    e.Graphics.DrawLine(pen, 23, 14, 28, 19);
                    e.Graphics.DrawLine(pen, 11, 33, 31, 33);
                }
            }
        }

        sealed class QrSurface : Control
        {
            static readonly Regex Box = new Regex(@"\bviewBox\s*=\s*['"" ]0\s+0\s+(\d+)\s+\1['""]", RegexOptions.CultureInvariant);
            static readonly Regex Pixel = new Regex(@"M(\d+) (\d+)h1v1h-1z", RegexOptions.CultureInvariant);
            readonly bool[,] matrix;
            public QrSurface(bool[,] matrix) { this.matrix = matrix; BackColor = Color.White; DoubleBuffered = true; }

            public static bool[,] Read(string svg)
            {
                if (String.IsNullOrEmpty(svg) || svg.Length > 300000) return null;
                var box = Box.Match(svg);
                int size;
                if (!box.Success || !Int32.TryParse(box.Groups[1].Value, out size) || size < 29 || size > 185) return null;
                var result = new bool[size, size];
                int count = 0;
                foreach (Match pixel in Pixel.Matches(svg))
                {
                    int x, y;
                    if (!Int32.TryParse(pixel.Groups[1].Value, out x) || !Int32.TryParse(pixel.Groups[2].Value, out y) ||
                        x < 4 || y < 4 || x >= size - 4 || y >= size - 4) return null;
                    result[x, y] = true;
                    count++;
                }
                return count > 0 ? result : null;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                int size = matrix.GetLength(0);
                int unit = Math.Min(ClientSize.Width, ClientSize.Height) / size;
                if (unit < 1) return;
                int left = (ClientSize.Width - unit * size) / 2;
                int top = (ClientSize.Height - unit * size) / 2;
                using (var fill = new SolidBrush(Color.FromArgb(24, 33, 49)))
                {
                    for (int y = 0; y < size; ++y)
                        for (int x = 0; x < size; ++x)
                            if (matrix[x, y]) e.Graphics.FillRectangle(fill, left + x * unit, top + y * unit, unit, unit);
                }
            }
        }

        sealed class PenPad : Control
        {
            const int PointerUpdate = 0x0245, PointerDown = 0x0246, PointerUp = 0x0247,
                PointerEnter = 0x0249, PointerLeave = 0x024A, PointerCaptureChanged = 0x024C;
            const uint InRange = 0x2, InContact = 0x4, Canceled = 0x8000;
            const uint InvertedOrEraser = 0x6, MaskPressure = 0x1;
            static readonly Color LightStage = Color.FromArgb(253, 246, 249);
            static readonly Color LightDots = Color.FromArgb(233, 209, 221);
            static readonly Color StrokeColor = Color.FromArgb(188, 87, 126);
            Bitmap inkLayer;
            bool mouseStroke;
            uint penId;
            Point last;
            bool hasLast;
            Point penTip;
            bool penHover;

            public PenPad()
            {
                ApplyColors();
                DoubleBuffered = true;
                ResizeRedraw = true;
                TabStop = false;
                AccessibleName = "Windows 笔迹测试区域";
                AccessibleRole = AccessibleRole.Graphic;
            }

            public void ApplyColors() { BackColor = DesktopTheme.IsDark ? DesktopTheme.Surface : LightStage; }

            public void Reset()
            {
                if (inkLayer != null) { inkLayer.Dispose(); inkLayer = null; }
                mouseStroke = false;
                penId = 0;
                hasLast = false;
                penHover = false;
                Invalidate();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && inkLayer != null) inkLayer.Dispose();
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Rectangle bounds = ClientRectangle;
                if (bounds.Width < 2 || bounds.Height < 2) return;
                using (var dot = new SolidBrush(DesktopTheme.IsDark ? Line : LightDots))
                {
                    for (int y = 13; y < bounds.Height - 5; y += 16)
                        for (int x = 13; x < bounds.Width - 5; x += 16)
                            e.Graphics.FillEllipse(dot, x, y, 2, 2);
                }
                using (var border = new Pen(Line)) e.Graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
                using (var guide = new Pen(DesktopTheme.IsDark ? AccentLine : Color.FromArgb(200, 130, 156), 1.6F))
                {
                    for (int corner = 0; corner < 4; ++corner)
                    {
                        int x = (corner & 1) == 0 ? 6 : bounds.Width - 7;
                        int y = (corner & 2) == 0 ? 6 : bounds.Height - 7;
                        int dx = (corner & 1) == 0 ? 14 : -14;
                        int dy = (corner & 2) == 0 ? 14 : -14;
                        e.Graphics.DrawLines(guide, new[] { new Point(x + dx, y), new Point(x, y), new Point(x, y + dy) });
                    }
                }
                if (inkLayer != null) e.Graphics.DrawImageUnscaled(inkLayer, 0, 0);
                else if (!penHover) DrawHint(e.Graphics, "在这里落笔，检查 Windows 压感");
                if (penHover) DrawTip(e.Graphics, penTip);
            }

            void DrawHint(Graphics graphics, string hint)
            {
                TextRenderer.DrawText(graphics, hint, Font, ClientRectangle, Quiet,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            static void DrawTip(Graphics graphics, Point point)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(SoftAccent)) graphics.FillEllipse(fill, point.X - 6, point.Y - 6, 12, 12);
                using (var line = new Pen(Accent, 1.4F)) graphics.DrawEllipse(line, point.X - 7, point.Y - 7, 14, 14);
            }

            void Stroke(Point point, double pressure, bool erase)
            {
                if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
                if (inkLayer == null || inkLayer.Width != Width || inkLayer.Height != Height)
                {
                    var next = new Bitmap(Width, Height);
                    if (inkLayer != null)
                    {
                        using (var graphics = Graphics.FromImage(next)) graphics.DrawImageUnscaled(inkLayer, 0, 0);
                        inkLayer.Dispose();
                    }
                    inkLayer = next;
                }
                float size = erase ? 19F : 1.5F + (float)Clamp(pressure) * 9F;
                using (var graphics = Graphics.FromImage(inkLayer))
                using (var line = new Pen(erase ? Color.Transparent : StrokeColor, size) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var brush = new SolidBrush(line.Color))
                {
                    graphics.SmoothingMode = erase ? SmoothingMode.None : SmoothingMode.AntiAlias;
                    if (erase) graphics.CompositingMode = CompositingMode.SourceCopy;
                    if (hasLast && last != point) graphics.DrawLine(line, last, point);
                    else graphics.FillEllipse(brush, point.X - size / 2, point.Y - size / 2, size, size);
                }
                last = point;
                hasLast = true;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || PromotedMouse()) return;
                mouseStroke = true;
                penHover = false;
                hasLast = false;
                Stroke(e.Location, 0.28, false);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (mouseStroke && !PromotedMouse()) Stroke(e.Location, 0.28, false);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!mouseStroke || PromotedMouse()) return;
                mouseStroke = false;
                hasLast = false;
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (mouseStroke && !Capture) { mouseStroke = false; hasLast = false; }
            }

            protected override void WndProc(ref Message message)
            {
                int kind = message.Msg;
                if (kind == PointerUpdate || kind == PointerDown || kind == PointerUp ||
                    kind == PointerEnter || kind == PointerLeave || kind == PointerCaptureChanged)
                {
                    uint id = (uint)(message.WParam.ToInt64() & 0xffff);
                    PointerPenInfo pen;
                    if ((kind == PointerLeave || kind == PointerCaptureChanged) && id == penId)
                    {
                        penId = 0;
                        hasLast = false;
                        penHover = false;
                        Invalidate();
                        return;
                    }
                    if (GetPointerPenInfo(id, out pen))
                    {
                        if (kind != PointerLeave && kind != PointerCaptureChanged) ProcessPen(id, kind, pen);
                        return;
                    }
                }
                base.WndProc(ref message);
            }

            void ProcessPen(uint id, int kind, PointerPenInfo pen)
            {
                if (penId != 0 && penId != id) return;
                penId = id;
                Point point = PointToClient(new Point(pen.Pointer.Pixel.X, pen.Pointer.Pixel.Y));
                uint flags = pen.Pointer.Flags;
                bool contact = kind != PointerUp && (flags & (InContact | Canceled)) == InContact;
                bool hover = !contact && (flags & (InRange | Canceled)) == InRange;
                bool erase = (pen.Flags & InvertedOrEraser) != 0;
                double force = (pen.Mask & MaskPressure) != 0 ? pen.Pressure / 1024.0 : 0.28;
                if (kind == PointerDown) hasLast = false;
                if (contact) Stroke(point, force, erase);
                else hasLast = false;
                penHover = hover;
                penTip = point;
                Invalidate();
            }

            static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }
            static bool PromotedMouse() { return (GetMessageExtraInfo().ToInt64() & 0xffffff00L) == 0xff515700L; }

            [DllImport("user32.dll")]
            static extern IntPtr GetMessageExtraInfo();

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool GetPointerPenInfo(uint id, out PointerPenInfo pen);

            [StructLayout(LayoutKind.Sequential)]
            struct NativePoint { public int X, Y; }

            [StructLayout(LayoutKind.Sequential)]
            struct PointerInfo
            {
                public uint Type, Id, Frame, Flags;
                public IntPtr Source, Target;
                public NativePoint Pixel, Himetric, PixelRaw, HimetricRaw;
                public uint Time, History;
                public int InputData;
                public uint KeyStates;
                public ulong Performance;
                public uint ButtonChange;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct PointerPenInfo
            {
                public PointerInfo Pointer;
                public uint Flags, Mask, Pressure, Rotation;
                public int TiltX, TiltY;
            }
        }
    }
}
