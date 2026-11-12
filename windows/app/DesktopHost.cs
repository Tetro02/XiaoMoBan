using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PenNoteBridge
{
    internal sealed class DesktopHost : IDisposable
    {
        readonly object gate = new object();
        readonly object logGate = new object();
        readonly Receiver receiver;
        readonly UsbBridge usb;
        readonly System.Threading.Timer wiredPoll;
        readonly string settings;
        readonly Queue<string> messages = new Queue<string>();
        Dictionary<string, object> options;
        string lastArea;
        string onlineCode = "";
        DateTime onlineUntil;
        bool automaticWired;
        bool winnerEmptyLastPoll;
        bool usbEnabled;
        bool disposed;
        volatile bool injectInput;

        internal static string ProfileDirectory()
        {
            string configured = Environment.GetEnvironmentVariable("PENNOTE_BRIDGE_DATA");
            return String.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "io.pennotebridge.desktop")
                : Path.GetFullPath(configured);
        }

        public DesktopHost(string profile)
        {
            Directory.CreateDirectory(profile);
            settings = Path.Combine(profile, "tablet-settings.json");
            options = LoadOptions();
            receiver = new Receiver(profile);
            usb = new UsbBridge(Log, Text(options, "adbPath", ""));
            UpdateOutput(options);
            wiredPoll = new System.Threading.Timer(PollWired, null, Timeout.Infinite, Timeout.Infinite);
        }

        // These UI-thread operations use independent native state.
        public void SetAnnotationCapture(IntPtr[] windows) { receiver.SetAnnotationCapture(windows); }
        public uint TakeBarrelGesture() { return receiver.TakeBarrelGesture(); }
        public bool TakeNavigation(out CanvasNavigation motion) { return receiver.TakeNavigation(out motion); }
        public bool TakeEraserIntent(IntPtr window, int x, int y, uint messageTime)
        {
            return receiver.TakeEraserIntent(window, x, y, messageTime);
        }
        public bool CanInjectInput { get { return injectInput; } }

        public IDictionary<string, object> TrySnapshot()
        {
            if (!Monitor.TryEnter(gate)) return null;
            try
            {
                if (disposed) return null;
                var displays = Displays();
                string area;
                IDictionary<string, object> map = Mapping(options, displays, out area);
                if (area != lastArea)
                {
                    receiver.Configure(map);
                    lastArea = area;
                }
                var state = receiver.Snapshot();
                var diagnostics = Dictionary(Read(state, "diagnostics"));
                var clients = Items(Read(state, "clients")).ToArray();
                bool running = Flag(state, "running");
                string status = !running ? "接收器已停止" : clients.Length == 0 ? "等待平板连接"
                    : clients.Length == 1 ? "已连接 · " + Text(Dictionary(clients[0]), "displayName", "Android")
                    : clients.Length + " 台平板已连接";
                string[] logs;
                lock (logGate)
                {
                    var lines = Items(Read(state, "logs")).Select(item => Convert.ToString(item))
                        .Concat(messages).ToArray();
                    logs = lines.Skip(Math.Max(0, lines.Length - 80)).ToArray();
                }
                return new Dictionary<string, object>
                {
                    {"config", new Dictionary<string, object>(options)},
                    {"running", Read(state, "transports")}, {"status", status},
                    {"wiredWinner", Read(state, "wiredWinner")},
                    {"addresses", Read(state, "addresses")}, {"fingerprint", Read(state, "fingerprint")},
                    {"code", Read(state, "pairingCode")}, {"monitors", displays},
                    {"bluetoothCode", Read(state, "pairingCode")},
                    {"shortPairingCode", Read(state, "shortPairingCode")},
                    {"pairingSecondsRemaining", Read(state, "pairingSecondsRemaining")},
                    {"pairingAvailable", Read(state, "pairingAddress") != null},
                    {"discoveryEnabled", Flag(state, "discoveryEnabled")},
                    {"hidStatus", "Windows 虚拟 HID 驱动未发布 · 当前使用 Windows Ink"},
                    {"online", new Dictionary<string, object> {
                        {"endpoint", options["onlineEndpoint"]},
                        {"code", onlineUntil > DateTime.UtcNow ? onlineCode : ""},
                        {"status", onlineUntil > DateTime.UtcNow ? "已生成 · 五分钟内可使用一次" : "使用两端共用的 HTTPS 配对服务"},
                    }},
                    {"usbDevices", usb.Devices}, {"usbScanStatus", usb.ScanStatus}, {"clients", clients}, {"logs", logs},
                    {"sample", Read(diagnostics, "sample")}, {"rate", Read(diagnostics, "rate") ?? 0},
                    {"received", Read(diagnostics, "received") ?? 0},
                    {"detected", Read(diagnostics, "detected")},
                };
            }
            finally { Monitor.Exit(gate); }
        }

        public IDictionary<string, object> Execute(string command, IDictionary<string, object> arguments)
        {
            lock (gate)
            {
                if (disposed) return null;
                switch (command)
                {
                    case "tablet_auto_start":
                        StartAutomatic();
                        break;
                    case "tablet_start":
                        var desired = Normalize(Dictionary(Read(arguments, "config")), options);
                        Start(desired, false);
                        SetAutomatic(false);
                        break;
                    case "tablet_stop":
                        SetAutomatic(false);
                        Stop();
                        break;
                    case "tablet_output":
                        UpdateOutput(Normalize(arguments, options));
                        SaveOptions();
                        break;
                    case "tablet_usb_scan":
                        usb.Scan();
                        break;
                    case "tablet_usb_adb":
                        options["adbPath"] = usb.ConfigureAdb(Text(arguments, "path", ""));
                        SaveOptions();
                        usb.Scan();
                        break;
                    case "tablet_usb_attach":
                        usb.Attach(Text(arguments, "serial", ""));
                        break;
                    case "tablet_disconnect":
                        receiver.Disconnect(Text(arguments, "connectionId", ""));
                        break;
                    case "tablet_reset_test":
                        receiver.ResetTest();
                        break;
                    case "tablet_pairing":
                        return receiver.Pairing(Text(arguments, "address", ""));
                    case "tablet_new_code":
                        receiver.RenewPairing();
                        break;
                    case "tablet_online_settings":
                        string endpoint = Text(arguments, "endpoint", "").Trim();
                        options["onlineEndpoint"] = endpoint.Length == 0 ? "" : OnlineRendezvous.ValidateEndpoint(endpoint);
                        onlineCode = "";
                        onlineUntil = DateTime.MinValue;
                        SaveOptions();
                        break;
                    case "tablet_online_create":
                        if (!Flag(Dictionary(Read(receiver.Snapshot(), "transports")), "lan"))
                            throw new InvalidOperationException("请先启动局域网接收");
                        var pair = receiver.Pairing(Text(arguments, "address", ""));
                        onlineCode = OnlineRendezvous.Create((string)options["onlineEndpoint"], Text(pair, "payload", ""));
                        onlineUntil = DateTime.UtcNow.AddMinutes(5);
                        break;
                    default:
                        throw new InvalidOperationException("未知操作 " + command);
                }
                return null;
            }
        }

        void StartAutomatic()
        {
            var desired = new Dictionary<string, object>(options);
            desired["lan"] = true;
            desired["usb"] = true;
            desired["bluetooth"] = false;
            Start(desired, true);
            SetAutomatic(true);
        }

        void SetAutomatic(bool enabled)
        {
            automaticWired = enabled;
            winnerEmptyLastPoll = false;
            wiredPoll.Change(enabled ? 300 : Timeout.Infinite, enabled ? 300 : Timeout.Infinite);
        }

        void PollWired(object unused)
        {
            if (!Monitor.TryEnter(gate)) return;
            try
            {
                if (disposed || !automaticWired) return;
                var state = receiver.Snapshot();
                SyncUsb(state);
                bool winnerEmpty = Read(state, "wiredWinner") != null && !Items(Read(state, "clients")).Any();
                if (winnerEmpty && winnerEmptyLastPoll) StartAutomatic();
                else winnerEmptyLastPoll = winnerEmpty;
            }
            catch (Exception error)
            {
                SetAutomatic(false);
                Log("自动接收恢复失败 · " + error.Message);
            }
            finally { Monitor.Exit(gate); }
        }

        void SyncUsb(IDictionary<string, object> state)
        {
            SetUsbEnabled(Flag(Dictionary(Read(state, "transports")), "usb"));
        }

        void SetUsbEnabled(bool enable)
        {
            if (usbEnabled == enable) return;
            usb.Enable(enable);
            usbEnabled = enable;
        }

        void Start(Dictionary<string, object> desired, bool firstWins)
        {
            if (!Flag(desired, "lan") && !Flag(desired, "usb") && !Flag(desired, "bluetooth"))
                throw new InvalidOperationException("请至少选择一种连接方式");
            string area;
            var config = Mapping(desired, Displays(), out area);
            config["lanEnabled"] = Flag(desired, "lan");
            config["usbEnabled"] = Flag(desired, "usb");
            config["bluetoothEnabled"] = Flag(desired, "bluetooth");
            config["firstWiredTransportWins"] = firstWins;
            if (!firstWins)
            {
                options = desired;
                injectInput = Flag(desired, "inject");
                SaveOptions();
            }
            lastArea = area;
            try
            {
                if (Flag(receiver.Snapshot(), "running")) receiver.Stop();
                receiver.Start(config);
            }
            finally { SyncUsb(receiver.Snapshot()); }
        }

        void Stop()
        {
            try { receiver.Stop(); }
            finally { SetUsbEnabled(false); }
        }

        void UpdateOutput(Dictionary<string, object> desired)
        {
            string area;
            var map = Mapping(desired, Displays(), out area);
            receiver.Configure(map);
            options = desired;
            injectInput = Flag(desired, "inject");
            lastArea = area;
        }

        static Dictionary<string, object>[] Displays()
        {
            return Screen.AllScreens.Select((screen, index) =>
            {
                Rectangle area = screen.Bounds;
                return new Dictionary<string, object>
                {
                    {"id", String.Format("{0},{1}:{2}x{3}", area.X, area.Y, area.Width, area.Height)},
                    {"label", "显示器 " + (index + 1) + (screen.Primary ? "（主）" : "")},
                    {"index", index}, {"left", area.X}, {"top", area.Y}, {"width", area.Width}, {"height", area.Height},
                };
            }).ToArray();
        }

        static Dictionary<string, object> Mapping(Dictionary<string, object> config,
            Dictionary<string, object>[] displays, out string key)
        {
            string target = Text(config, "target", "all");
            var selected = displays.FirstOrDefault(display => Text(display, "id", "") == target);
            Rectangle area = selected == null ? SystemInformation.VirtualScreen :
                new Rectangle((int)selected["left"], (int)selected["top"], (int)selected["width"], (int)selected["height"]);
            key = (selected == null ? "all" : target) + area.ToString();
            return new Dictionary<string, object>
            {
                {"injectionEnabled", Flag(config, "inject")}, {"pressureGamma", config["pressure"]},
                {"sideKeyAction", config["sideKeyAction"]}, {"sideKeyVirtualKey", config["sideKeyVirtualKey"]},
                {"sideKeyModifiers", config["sideKeyModifiers"]},
                {"mapAll", selected == null}, {"displayName", Text(selected, "label", "所有显示器")},
                {"displayIndex", selected == null ? 0 : selected["index"]},
                {"left", area.X}, {"top", area.Y},
                {"width", Math.Max(1, area.Width)}, {"height", Math.Max(1, area.Height)},
            };
        }

        Dictionary<string, object> LoadOptions()
        {
            var defaults = new Dictionary<string, object>
            {
                {"lan", true}, {"usb", true}, {"bluetooth", false},
                {"inject", true}, {"target", "all"}, {"pressure", 1.0},
                {"sideKeyAction", "native"}, {"sideKeyVirtualKey", 0}, {"sideKeyModifiers", 0},
                {"onlineEndpoint", ""}, {"adbPath", ""},
            };
            if (!File.Exists(settings)) return defaults;
            try { return Normalize(Dictionary(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(settings))), defaults); }
            catch (Exception error)
            {
                Log("读取接收设置失败 · " + error.Message);
                return defaults;
            }
        }

        static Dictionary<string, object> Normalize(IDictionary<string, object> values, Dictionary<string, object> defaults)
        {
            var result = new Dictionary<string, object>(defaults);
            foreach (string name in new[] { "lan", "usb", "bluetooth", "inject" })
            {
                object value = Read(values, name);
                if (value is bool) result[name] = value;
            }
            if (Read(values, "target") is string) result["target"] = values["target"];
            string sideAction = Text(values, "sideKeyAction", "");
            if (sideAction == "native" || sideAction == "disabled" || sideAction == "shortcut")
                result["sideKeyAction"] = sideAction;
            foreach (string key in new[] { "sideKeyVirtualKey", "sideKeyModifiers" })
            {
                object value = Read(values, key);
                if (value != null) result[key] = Convert.ToInt32(value);
            }
            if ((string)result["sideKeyAction"] == "shortcut" && (int)result["sideKeyVirtualKey"] == 0)
                result["sideKeyAction"] = "native";
            if (Read(values, "onlineEndpoint") is string) result["onlineEndpoint"] = values["onlineEndpoint"];
            if (Read(values, "adbPath") is string) result["adbPath"] = values["adbPath"];
            object pressure = Read(values, "pressure");
            if (pressure != null)
            {
                double numeric = Convert.ToDouble(pressure);
                if (!Double.IsNaN(numeric) && !Double.IsInfinity(numeric))
                    result["pressure"] = Math.Max(.25, Math.Min(2.0, numeric));
            }
            return result;
        }

        void SaveOptions()
        {
            string temporary = settings + ".native.tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(options));
                if (File.Exists(settings)) File.Replace(temporary, settings, null);
                else File.Move(temporary, settings);
            }
            catch (IOException error) { Log("保存设置失败 · " + error.Message); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        void Log(string message)
        {
            lock (logGate)
            {
                if (messages.Count >= 40) messages.Dequeue();
                messages.Enqueue(message);
            }
        }

        internal static IDictionary<string, object> Dictionary(object value) { return value as IDictionary<string, object>; }
        internal static object Read(IDictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result : null;
        }
        internal static bool Flag(IDictionary<string, object> value, string key) { return Object.Equals(Read(value, key), true); }
        internal static string Text(IDictionary<string, object> value, string key, string fallback)
        {
            return Read(value, key) as string ?? fallback;
        }
        internal static IEnumerable<object> Items(object value)
        {
            var entries = value as IEnumerable;
            return entries != null && !(value is string) ? entries.Cast<object>() : Enumerable.Empty<object>();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                wiredPoll.Dispose();
                try { usb.Dispose(); }
                finally { receiver.Dispose(); }
            }
        }
    }

}
