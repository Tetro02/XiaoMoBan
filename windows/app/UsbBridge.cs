using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PenNoteBridge
{
    internal sealed class UsbBridge : IDisposable
    {
        const string Port = "tcp:57212";
        readonly object operations = new object();
        readonly object state = new object();
        readonly HashSet<string> added = new HashSet<string>(StringComparer.Ordinal);
        readonly Timer poll;
        readonly Action<string> log;
        Dictionary<string, object>[] devices = new Dictionary<string, object>[0];
        string adbPath;
        string scanStatus = "点击「刷新设备」检测 USB。";
        string selected;
        string active;
        string lastError;
        bool enabled;
        bool disposed;

        public UsbBridge(Action<string> log, string adbPath)
        {
            this.log = log;
            this.adbPath = adbPath;
            poll = new Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
        }

        public Dictionary<string, object>[] Devices
        {
            get { lock (state) return devices; }
        }

        public string ScanStatus { get { lock (state) return scanStatus; } }

        public string ConfigureAdb(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请选择 Platform-Tools 的 adb.exe");
            path = Path.GetFullPath(path);
            if (!String.Equals(Path.GetFileName(path), "adb.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException("请选择 Platform-Tools 的 adb.exe");
            lock (operations)
            {
                if (String.Equals(adbPath, path, StringComparison.OrdinalIgnoreCase)) return path;
                ClearOwnedRules();
                adbPath = path;
                return path;
            }
        }

        public void Enable(bool enable)
        {
            lock (operations)
            {
                if (disposed || enabled == enable) return;
                enabled = enable;
                poll.Change(enable ? 0 : Timeout.Infinite, enable ? 1600 : Timeout.Infinite);
                if (!enable) ClearOwnedRules();
            }
        }

        public void Scan()
        {
            lock (operations)
            {
                try { Refresh(); }
                catch (Exception error) { ScanFailed(error, true); throw; }
            }
        }

        public void Attach(string serial)
        {
            if (!Regex.IsMatch(serial ?? "", "^[A-Za-z0-9._-]{1,128}$"))
                throw new InvalidOperationException("无效的 USB 设备");
            lock (operations)
            {
                if (!enabled) throw new InvalidOperationException("请先启动 USB 接收");
                Refresh();
                var device = Devices.FirstOrDefault(d => (string)d["serial"] == serial && (string)d["state"] == "device");
                if (device == null) throw new InvalidOperationException("请在平板上授权 USB 调试");
                SetReverse(serial);
                selected = serial;
                log("USB 已就绪 · " + device["model"]);
            }
        }

        void Poll(object unused)
        {
            if (!Monitor.TryEnter(operations)) return;
            bool refreshed = false;
            try
            {
                if (!enabled || disposed) return;
                Refresh();
                refreshed = true;
                var authorized = Devices.Where(d => (string)d["state"] == "device").ToArray();
                if (!authorized.Any(d => (string)d["serial"] == active)) active = null;
                var target = authorized.FirstOrDefault(d => (string)d["serial"] == selected);
                if (target == null && authorized.Length == 1) target = authorized[0];
                if (target != null && (string)target["serial"] != active)
                {
                    SetReverse((string)target["serial"]);
                    log("USB 已就绪 · " + target["model"]);
                }
                lastError = null;
            }
            catch (Exception error)
            {
                ScanFailed(error, !refreshed);
                if (lastError != error.Message)
                {
                    lastError = error.Message;
                    log(lastError);
                }
            }
            finally { Monitor.Exit(operations); }
        }

        void Refresh()
        {
            var found = new List<Dictionary<string, object>>();
            foreach (string line in Run("devices", "-l").Split('\n'))
            {
                string[] tokens = Regex.Split(line.Trim(), "\\s+");
                if (tokens.Length < 2 || !Regex.IsMatch(tokens[0], "^[A-Za-z0-9._-]{1,128}$")
                    || tokens[0].StartsWith("emulator-", StringComparison.Ordinal)
                    || tokens[0].IndexOf("._adb-tls-", StringComparison.Ordinal) >= 0) continue;
                string status = tokens[1];
                if (status != "device" && status != "unauthorized" && status != "offline" && status != "recovery") continue;
                string model = tokens.FirstOrDefault(t => t.StartsWith("model:", StringComparison.Ordinal));
                found.Add(new Dictionary<string, object> {
                    {"serial", tokens[0]}, {"state", status},
                    {"model", model == null ? "Android" : model.Substring(6).Replace('_', ' ')}
                });
            }
            lock (state)
            {
                devices = found.ToArray();
                scanStatus = found.Count == 0 ? "ADB 未检测到 USB 设备。请用数据线直连电脑并解锁平板。"
                    : found.Any(device => (string)device["state"] == "unauthorized")
                        ? "请在解锁的平板上确认「允许 USB 调试」。"
                    : found.Any(device => (string)device["state"] == "device")
                        ? "已检测到可用的 USB 调试设备。"
                        : "检测到 USB 设备，调试连接尚未就绪。请重新插拔并解锁平板。";
            }
        }

        void ScanFailed(Exception error, bool clearDevices)
        {
            lock (state)
            {
                if (clearDevices) devices = new Dictionary<string, object>[0];
                scanStatus = error.Message;
            }
        }

        void SetReverse(string serial)
        {
            if (!HasReverse(Run("-s", serial, "reverse", "--list")))
            {
                Run("-s", serial, "reverse", "--no-rebind", Port, Port);
                added.Add(serial);
                if (!HasReverse(Run("-s", serial, "reverse", "--list")))
                    throw new InvalidOperationException("ADB 未确认 USB 转发");
            }
            active = serial;
        }

        static bool HasReverse(string output)
        {
            return output.Split('\n').Any(line =>
            {
                string[] tokens = Regex.Split(line.Trim(), "\\s+");
                return tokens.Length >= 2 && tokens[tokens.Length - 2] == Port && tokens[tokens.Length - 1] == Port;
            });
        }

        void ClearOwnedRules()
        {
            foreach (string serial in added.ToArray())
            {
                try
                {
                    if (HasReverse(Run("-s", serial, "reverse", "--list")))
                        Run("-s", serial, "reverse", "--remove", Port);
                    added.Remove(serial);
                }
                catch (Exception error) { log("USB 转发清理 · " + error.Message); }
            }
            active = null;
        }

        string FindAdb()
        {
            if (!String.IsNullOrWhiteSpace(adbPath) && File.Exists(adbPath)) return adbPath;
            string configured = Environment.GetEnvironmentVariable("PENNOTE_ADB");
            if (!String.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim('"'))) return configured.Trim('"');
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string nearby = Path.Combine(directory, "platform-tools", "adb.exe");
            if (File.Exists(nearby)) return nearby;
            nearby = Path.Combine(directory, "adb.exe");
            if (File.Exists(nearby)) return nearby;
            string localSdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
            foreach (string sdk in new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"),
                                           Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"), localSdk })
            {
                if (String.IsNullOrWhiteSpace(sdk)) continue;
                string executable = Path.Combine(sdk.Trim('"'), "platform-tools", "adb.exe");
                if (File.Exists(executable)) return executable;
            }
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(entry)) continue;
                try
                {
                    string executable = Path.Combine(entry.Trim('"'), "adb.exe");
                    if (File.Exists(executable)) return executable;
                }
                catch (ArgumentException) { }
            }
            throw new InvalidOperationException("未找到 ADB。请下载 Android Platform-Tools 并选择解压后的 adb.exe。");
        }

        string Run(params string[] args)
        {
            try
            {
                // Every argument is a fixed flag, a port, or an ASCII device serial.
                string executable = FindAdb();
                var start = new ProcessStartInfo(executable, String.Join(" ", args)) {
                    UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (Process process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        throw new InvalidOperationException("ADB 响应超时");
                    }
                    // An adb daemon started on this call can inherit the pipes.
                    if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { output, errors }, 1500))
                        throw new InvalidOperationException("无法读取 ADB 输出");
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("ADB · " + errors.Result.Trim());
                    return output.Result;
                }
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException("无法启动 ADB · " + error.Message);
            }
        }

        public void Dispose()
        {
            lock (operations)
            {
                if (disposed) return;
                disposed = true;
                enabled = false;
                poll.Dispose();
                ClearOwnedRules();
            }
        }
    }
}
