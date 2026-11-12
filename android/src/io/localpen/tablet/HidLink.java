package io.localpen.tablet;

import android.annotation.SuppressLint;
import android.annotation.TargetApi;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothHidDevice;
import android.bluetooth.BluetoothHidDeviceAppSdpSettings;
import android.bluetooth.BluetoothProfile;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

import java.util.function.BooleanSupplier;

@TargetApi(28)
@SuppressLint("MissingPermission")
final class HidLink implements AutoCloseable {
    private static final String TAG = "PenNoteHID";
    private static final float POINTER_UNITS_X = 1600f, POINTER_UNITS_Y = 1000f;

    enum Failure {
        FOREGROUND("请让小墨板保持在前台再启动直连", true),
        BLUETOOTH_OFF("平板蓝牙未开启", false),
        SESSION_SERVICE("Android 未启动蓝牙 HID 前台服务", true),
        PROFILE_REQUEST("Android 未接受 HID 服务绑定请求", true),
        PROFILE_TIMEOUT("Android 未在时限内连接 HID 服务", true),
        PROFILE_LOST("注册前 Android HID 服务已断开", true),
        REGISTER_REQUEST("HID 服务已连接，Android 未接受注册请求", true),
        REGISTER_STATUS("HID 注册请求已发送，Android 返回未注册", true),
        REGISTER_TIMEOUT("Android 未在时限内返回 HID 注册状态", true),
        PERMISSION("HID 直连需要「附近的设备」权限", false),
        HOST_LE_ONLY("所选记录只有低功耗蓝牙，请搜索电脑的经典蓝牙", false),
        PAIR_REQUEST("经典蓝牙配对未完成，请在电脑和平板上确认配对", false),
        PAIR_TIMEOUT("等待经典蓝牙配对超时，请在电脑和平板上确认配对", false),
        HOST_REQUEST("HID 已注册，但所选设备没有建立经典蓝牙 HID 通道", false),
        HOST_TIMEOUT("HID 已注册，等待经典蓝牙 HID 通道超时", false),
        HOST_DISCONNECTED("电脑已断开蓝牙 HID", false),
        SERVICE_LOST("注册后 Android HID 服务已断开", false),
        UNREGISTERED("Android 已注销 HID 直连", false),
        BACKLOG("蓝牙 HID 输入积压，请重新连接", false),
        SHORTCUT_OUTPUT("蓝牙 HID 快捷键发送失败", false),
        OUTPUT("蓝牙 HID 报告发送失败", false);

        final String message;
        final boolean retryable;

        Failure(String message, boolean retryable) {
            this.message = message;
            this.retryable = retryable;
        }
    }

    interface Listener {
        void state(HidLink source, PenLink.State state, String message);
        void failed(HidLink source, Failure failure);
        default void readyToPair(HidLink source) {}
    }

    private final Context context;
    private final BluetoothAdapter adapter;
    private final BluetoothDevice selected;
    private final Listener listener;
    private final BooleanSupplier foreground;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final PenSampleQueue pending = new PenSampleQueue();
    private final Object outputLock = new Object();
    private volatile BluetoothHidDevice profile;
    private volatile BluetoothDevice host;
    private volatile byte[] shortcut;
    private volatile boolean closed;
    private volatile PenLink.Sample lastSent;
    private boolean registered;
    private boolean registrationRequested;
    private boolean registrationAccepted;
    private boolean sessionStarted;
    private boolean sessionBound;
    private BluetoothDevice pendingHost;
    private boolean pairingSelected, sawBonding;
    private boolean hostRequested;
    private Failure hostTimeoutReason = Failure.HOST_TIMEOUT;
    private final Runnable sessionTimeout = () -> fail(Failure.SESSION_SERVICE);
    private final Runnable profileTimeout = () -> fail(Failure.PROFILE_TIMEOUT);
    private final Runnable registrationTimeout =
            () -> fail(registrationAccepted ? Failure.REGISTER_TIMEOUT : Failure.REGISTER_REQUEST);
    private final Runnable hostTimeout = () -> fail(hostTimeoutReason);
    private final Runnable checkHostBond = this::checkHostBond;
    private final Runnable checkSelectedHost = this::checkSelectedHost;

    HidLink(Context context, BluetoothAdapter adapter, BluetoothDevice selected, byte[] shortcut,
            BooleanSupplier foreground, Listener listener) {
        this.context = context;
        this.adapter = adapter;
        this.selected = selected;
        this.shortcut = shortcut;
        this.foreground = foreground;
        this.listener = listener;
    }

    void start() {
        if (!foreground.getAsBoolean()) { fail(Failure.FOREGROUND); return; }
        try {
            if (!adapter.isEnabled()) { fail(Failure.BLUETOOTH_OFF); return; }
            if (selected != null && selected.getType() == BluetoothDevice.DEVICE_TYPE_LE) {
                fail(Failure.HOST_LE_ONLY);
                return;
            }
            listener.state(this, PenLink.State.WAITING, "正在启动蓝牙 HID 服务…");
            Intent intent = new Intent(context, HidSessionService.class);
            sessionStarted = context.startService(intent) != null;
            sessionBound = sessionStarted && context.bindService(intent, session, Context.BIND_AUTO_CREATE);
            if (sessionBound) main.postDelayed(sessionTimeout, 7000);
            else fail(Failure.SESSION_SERVICE);
        } catch (SecurityException error) {
            fail(Failure.PERMISSION);
        } catch (IllegalStateException error) {
            fail(Failure.SESSION_SERVICE);
        }
    }

    private final ServiceConnection session = new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name, IBinder binder) {
            if (closed) return;
            main.removeCallbacks(sessionTimeout);
            connectProfile();
        }

        @Override public void onServiceDisconnected(ComponentName name) { fail(Failure.SESSION_SERVICE); }
        @Override public void onBindingDied(ComponentName name) { fail(Failure.SESSION_SERVICE); }
        @Override public void onNullBinding(ComponentName name) { fail(Failure.SESSION_SERVICE); }
    };

    private void connectProfile() {
        listener.state(this, PenLink.State.WAITING, "正在连接 Android HID 服务…");
        try {
            Log.i(TAG, "getProfileProxy: HID_DEVICE");
            main.postDelayed(profileTimeout, 7000);
            if (!adapter.getProfileProxy(context, service, BluetoothProfile.HID_DEVICE)) {
                fail(adapter.isEnabled() ? Failure.PROFILE_REQUEST : Failure.BLUETOOTH_OFF);
                return;
            }
            Thread worker = new Thread(this::output, "XiaoMoBan HID");
            worker.setDaemon(true);
            worker.start();
        } catch (SecurityException error) {
            fail(Failure.PERMISSION);
        }
    }

    private final BluetoothProfile.ServiceListener service = new BluetoothProfile.ServiceListener() {
        @Override public void onServiceConnected(int kind, BluetoothProfile proxy) {
            if (closed || kind != BluetoothProfile.HID_DEVICE || !(proxy instanceof BluetoothHidDevice)) {
                closeProxy(kind, proxy);
                if (!closed) fail(Failure.PROFILE_REQUEST);
                return;
            }
            BluetoothHidDevice device = (BluetoothHidDevice) proxy;
            if (profile == device) return;
            main.removeCallbacks(profileTimeout);
            profile = device;
            BluetoothHidDeviceAppSdpSettings settings = new BluetoothHidDeviceAppSdpSettings(
                    "XiaoMoBan", "Pen tablet", "XiaoMoBan",
                    BluetoothHidDevice.SUBCLASS1_COMBO, HidReports.DESCRIPTOR);
            try {
                listener.state(HidLink.this, PenLink.State.WAITING, "正在向 Android 注册 HID…");
                Log.i(TAG, "onServiceConnected; registerApp");
                registrationRequested = true;
                registrationAccepted = device.registerApp(settings, null, null, main::post, callbacks);
                Log.i(TAG, "registerApp request: " + registrationAccepted);
                // A device may still deliver the authoritative callback after returning false.
                main.postDelayed(registrationTimeout, 7000);
            } catch (SecurityException error) { fail(Failure.PERMISSION); }
        }

        @Override public void onServiceDisconnected(int kind) {
            if (kind != BluetoothProfile.HID_DEVICE) return;
            host = null;
            fail(registered ? Failure.SERVICE_LOST : Failure.PROFILE_LOST);
        }
    };

    private final BluetoothHidDevice.Callback callbacks = new BluetoothHidDevice.Callback() {
        @Override public void onAppStatusChanged(BluetoothDevice pluggedDevice, boolean active) {
            if (closed) return;
            main.removeCallbacks(registrationTimeout);
            registrationRequested = false;
            boolean wasRegistered = registered;
            registered = active;
            Log.i(TAG, "onAppStatusChanged: " + active);
            if (!active) {
                host = null;
                fail(wasRegistered ? Failure.UNREGISTERED : Failure.REGISTER_STATUS);
                return;
            }
            if (wasRegistered || host != null) return;
            try {
                BluetoothHidDevice device = profile;
                if (selected == null) {
                    BluetoothDevice waiting = pendingHost != null ? pendingHost : pluggedDevice;
                    pendingHost = null;
                    if (waiting != null && device.getConnectionState(waiting) == BluetoothProfile.STATE_CONNECTED)
                        onConnectionStateChanged(waiting, BluetoothProfile.STATE_CONNECTED);
                    if (host == null) {
                        listener.state(HidLink.this, PenLink.State.WAITING, "HID 已注册，请在 Windows 选择「添加设备 → 蓝牙」");
                        main.postDelayed(hostTimeout, 120000);
                        if (pendingHost == null) listener.readyToPair(HidLink.this);
                    }
                } else {
                    beginSelectedHost();
                }
            } catch (SecurityException error) {
                fail(Failure.PERMISSION);
            }
        }

        @Override public void onConnectionStateChanged(BluetoothDevice device, int state) {
            if (closed || device == null) return;
            if (!registered) {
                if (state == BluetoothProfile.STATE_CONNECTED) pendingHost = device;
                else if (state == BluetoothProfile.STATE_DISCONNECTED && device.equals(pendingHost)) pendingHost = null;
                return;
            }
            try {
                if (state == BluetoothProfile.STATE_CONNECTED) {
                    if (device.getType() == BluetoothDevice.DEVICE_TYPE_LE
                            || (selected != null && !selected.equals(device)) || (host != null && !host.equals(device))) return;
                    int bond = device.getBondState();
                    if (bond == BluetoothDevice.BOND_BONDING) {
                        pendingHost = device;
                        main.removeCallbacks(checkHostBond);
                        main.postDelayed(checkHostBond, 300);
                        return;
                    }
                    if (bond != BluetoothDevice.BOND_BONDED) {
                        if (device.equals(pendingHost)) pendingHost = null;
                        return;
                    }
                    if (device.equals(host)) return;
                    pendingHost = null;
                    main.removeCallbacks(checkHostBond);
                    host = device;
                    main.removeCallbacks(hostTimeout);
                    main.removeCallbacks(checkSelectedHost);
                    Log.i(TAG, "onConnectionStateChanged: connected");
                    listener.state(HidLink.this, PenLink.State.CONNECTED, "蓝牙 HID 已连接");
                } else if (state == BluetoothProfile.STATE_DISCONNECTED) {
                    if (device.equals(pendingHost)) {
                        pendingHost = null;
                        main.removeCallbacks(checkHostBond);
                    }
                    if (device.equals(host)) {
                        host = null;
                        fail(Failure.HOST_DISCONNECTED);
                    } else if (device.equals(selected) && !pairingSelected) {
                        hostTimeoutReason = Failure.HOST_REQUEST;
                        listener.state(HidLink.this, PenLink.State.WAITING, "等待电脑建立经典蓝牙 HID 通道…");
                    }
                }
            } catch (SecurityException error) {
                fail(Failure.PERMISSION);
            }
        }

        @Override public void onGetReport(BluetoothDevice device, byte type, byte id, int size) {
            BluetoothHidDevice current = profile;
            if (closed || current == null) return;
            try {
                if (type == BluetoothHidDevice.REPORT_TYPE_INPUT) {
                    byte[] report = id == HidReports.PEN ? new byte[11]
                            : id == HidReports.MOUSE || id == HidReports.RELATIVE_MOUSE ? new byte[5]
                            : id == HidReports.NAVIGATION ? new byte[2]
                            : id == HidReports.KEYBOARD ? new byte[8] : null;
                    if (report != null && (size == 0 || size >= report.length)) {
                        current.replyReport(device, type, id, report);
                        return;
                    }
                }
                current.reportError(device, BluetoothHidDevice.ERROR_RSP_UNSUPPORTED_REQ);
            } catch (SecurityException error) {
                fail(Failure.PERMISSION);
            }
        }
    };

    private void beginSelectedHost() {
        int bond = selected.getBondState();
        if (bond == BluetoothDevice.BOND_BONDED) {
            waitForSelectedConnection();
            return;
        }
        pairingSelected = true;
        sawBonding = bond == BluetoothDevice.BOND_BONDING;
        listener.state(this, PenLink.State.WAITING, "HID 已注册，请在电脑和平板上确认经典蓝牙配对");
        boolean requested = sawBonding || selected.createBond();
        hostTimeoutReason = requested ? Failure.PAIR_TIMEOUT : Failure.PAIR_REQUEST;
        main.postDelayed(hostTimeout, requested ? 90000 : 12000);
        main.postDelayed(checkSelectedHost, 500);
    }

    private void waitForSelectedConnection() {
        pairingSelected = false;
        hostTimeoutReason = Failure.HOST_TIMEOUT;
        main.removeCallbacks(hostTimeout);
        main.postDelayed(hostTimeout, 30000);
        listener.state(this, PenLink.State.WAITING, "已配对，正在建立经典蓝牙 HID 通道…");
        checkSelectedHost();
    }

    private void checkSelectedHost() {
        BluetoothHidDevice current = profile;
        if (closed || current == null || selected == null || host != null) return;
        try {
            int bond = selected.getBondState();
            if (pairingSelected) {
                if (bond == BluetoothDevice.BOND_BONDED) {
                    waitForSelectedConnection();
                    return;
                }
                if (bond == BluetoothDevice.BOND_BONDING) sawBonding = true;
                else if (sawBonding) { fail(Failure.PAIR_REQUEST); return; }
            } else if (bond == BluetoothDevice.BOND_BONDED) {
                int state = current.getConnectionState(selected);
                if (state == BluetoothProfile.STATE_CONNECTED) {
                    callbacks.onConnectionStateChanged(selected, state);
                    return;
                }
                if (state == BluetoothProfile.STATE_DISCONNECTED && !hostRequested) {
                    hostRequested = true;
                    boolean accepted = current.connect(selected);
                    Log.i(TAG, "Classic HID connect request: " + accepted);
                    if (!accepted) hostTimeoutReason = Failure.HOST_REQUEST;
                }
            } else {
                fail(Failure.PAIR_REQUEST);
                return;
            }
            main.postDelayed(checkSelectedHost, 500);
        } catch (SecurityException error) {
            fail(Failure.PERMISSION);
        }
    }

    private void checkHostBond() {
        BluetoothDevice waiting = pendingHost;
        BluetoothHidDevice current = profile;
        if (closed || current == null || waiting == null) return;
        try {
            int state = current.getConnectionState(waiting);
            if (state == BluetoothProfile.STATE_CONNECTED || state == BluetoothProfile.STATE_DISCONNECTED)
                callbacks.onConnectionStateChanged(waiting, state);
            else main.postDelayed(checkHostBond, 300);
        } catch (SecurityException error) {
            fail(Failure.PERMISSION);
        }
    }

    void setShortcut(byte[] shortcut) { this.shortcut = shortcut; }

    boolean sendShortcut(byte[] report) {
        return !closed && host != null && pending.offerShortcut(report);
    }

    boolean sendTouchpadNavigation(float[] navigation) {
        return !closed && host != null && pending.offerNavigation(navigation);
    }

    void send(PenLink.Sample sample) {
        if (closed || host == null) return;
        if (!pending.offer(sample)) fail(Failure.BACKLOG);
    }

    private void output() {
        boolean barrel = false;
        boolean shortcutTap = false;
        boolean barrelAnchor = false, barrelMoved = false;
        boolean penStrokeEraser = false;
        long lastSampleAt = 0;
        float barrelX = 0, barrelY = 0;
        float carryX = 0, carryY = 0;
        TouchpadOutput touchpad = new TouchpadOutput();
        try {
            for (PenSampleQueue.Pending next; !closed && (next = pending.take()) != null;) {
                BluetoothHidDevice device = profile;
                BluetoothDevice target = host;
                if (device == null || target == null) continue;
                PenLink.Sample sample = next.sample;
                synchronized (outputLock) {
                    if (closed) return;
                    if (next.shortcut != null) {
                        if (!sendKeyboardStroke(device, target, next.shortcut)) {
                            main.post(() -> fail(Failure.SHORTCUT_OUTPUT));
                            return;
                        }
                        continue;
                    }
                    if (next.navigation != null) {
                        if (!touchpad.send(device, target, next.navigation, next.time)) {
                            main.post(() -> fail(Failure.OUTPUT));
                            return;
                        }
                        continue;
                    }
                    PenLink.Sample prior = lastSent;
                    if (prior != null && (prior.mouse != sample.mouse || prior.relative != sample.relative))
                        sendRelease(device, target, prior);
                    byte[] key = shortcut;
                    boolean hover = HidReports.inRange(sample) && !HidReports.contact(sample) && !sample.eraser;
                    float dxFromPress = (sample.x - barrelX) * POINTER_UNITS_X;
                    float dyFromPress = (sample.y - barrelY) * POINTER_UNITS_Y;
                    float distanceSquared = dxFromPress * dxFromPress + dyFromPress * dyFromPress;
                    float deadzone = sample.mouse ? 8f : sample.hoverPanDeadzonePx;
                    float tapSlop = Math.max(1f, Math.min(8f, deadzone));
                    boolean traveled = barrelAnchor && distanceSquared > 0 && distanceSquared >= deadzone * deadzone;
                    boolean tapMoved = barrelAnchor && distanceSquared >= tapSlop * tapSlop;
                    boolean releasedTap = hover && !sample.barrel && barrel && shortcutTap && !tapMoved;
                    if (!hover || !sample.barrel) {
                        shortcutTap = barrelAnchor = barrelMoved = false;
                    } else if (!barrelAnchor) {
                        barrelAnchor = true;
                        barrelX = sample.x;
                        barrelY = sample.y;
                        shortcutTap = !barrel;
                    } else if (traveled) {
                        barrelMoved = true;
                        shortcutTap = false;
                    } else if (tapMoved) shortcutTap = false;
                    if (key != null && releasedTap && !sendKeyboardStroke(device, target, key)) {
                        main.post(() -> fail(Failure.SHORTCUT_OUTPUT));
                        return;
                    }
                    barrel = sample.barrel;
                    boolean penContact = !sample.mouse && HidReports.inRange(sample) && HidReports.contact(sample);
                    if (!penContact) penStrokeEraser = false;
                    else if (prior == null || prior.mouse || !HidReports.contact(prior)) {
                        boolean landedFromHeldHover = false;
                        if (prior != null && !prior.mouse && prior.barrel && HidReports.inRange(prior)
                                && next.time - lastSampleAt <= 100) {
                            float landingX = (sample.x - prior.x) * POINTER_UNITS_X;
                            float landingY = (sample.y - prior.y) * POINTER_UNITS_Y;
                            landedFromHeldHover = landingX * landingX + landingY * landingY <= 32f * 32f;
                        }
                        penStrokeEraser = sample.eraser || barrel || landedFromHeldHover;
                    }
                    boolean immediateBarrel = key == null && (sample.mouse || deadzone == 0);
                    boolean nativeBarrel = barrel && !sample.eraser && !penStrokeEraser && (immediateBarrel || barrelMoved);
                    boolean sent;
                    if (sample.relative) {
                        int dx = 0, dy = 0;
                        if (prior != null && prior.relative && HidReports.inRange(prior) && HidReports.inRange(sample)
                                && HidReports.contact(prior) == HidReports.contact(sample)
                                && !sample.kind.equals("down") && !sample.kind.equals("up")) {
                            carryX += (sample.x - prior.x) * POINTER_UNITS_X * sample.relativeGain;
                            carryY += (sample.y - prior.y) * POINTER_UNITS_Y * sample.relativeGain;
                            dx = Math.round(carryX);
                            dy = Math.round(carryY);
                            carryX -= dx;
                            carryY -= dy;
                        } else {
                            carryX = 0;
                            carryY = 0;
                        }
                        sent = device.sendReport(target, HidReports.RELATIVE_MOUSE,
                                HidReports.relativeMouse(sample, nativeBarrel, dx, dy));
                    } else if (sample.mouse) {
                        sent = device.sendReport(target, HidReports.MOUSE, HidReports.mouse(sample, nativeBarrel));
                    } else {
                        boolean erasePreview = hover && barrel && !nativeBarrel;
                        sent = device.sendReport(target, HidReports.PEN,
                                HidReports.pen(sample, nativeBarrel, penStrokeEraser, erasePreview));
                    }
                    if (!sent) {
                        main.post(() -> fail(Failure.OUTPUT));
                        return;
                    }
                    lastSent = sample;
                    lastSampleAt = next.time;
                }
            }
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        } catch (SecurityException error) {
            main.post(() -> fail(Failure.PERMISSION));
        }
    }

    private static final class TouchpadOutput {
        private double horizontal, vertical, zoom;
        private long lastTime;

        boolean send(BluetoothHidDevice profile, BluetoothDevice host, float[] gesture, long time) {
            if (time - lastTime > 300 || gesture[5] == 1) horizontal = vertical = zoom = 0;
            lastTime = time;
            if (gesture[5] == 1) {
                boolean pressed;
                boolean released;
                try {
                    pressed = profile.sendReport(host, HidReports.RELATIVE_MOUSE, HidReports.rightClick(true));
                } finally {
                    released = profile.sendReport(host, HidReports.RELATIVE_MOUSE, HidReports.rightClick(false));
                }
                return pressed && released;
            }

            horizontal -= gesture[0] * 12;
            vertical += gesture[1] * 12;
            zoom += Math.log(gesture[2]) * 8;
            int x = (int) horizontal, y = (int) vertical, z = (int) zoom;
            horizontal -= x;
            vertical -= y;
            zoom -= z;
            if ((x != 0 || y != 0) && !profile.sendReport(host, HidReports.NAVIGATION,
                    HidReports.wheel(y, x))) return false;
            if (z == 0) return true;

            byte[] control = new byte[8];
            control[0] = 1;
            boolean sent;
            boolean released;
            try {
                sent = profile.sendReport(host, HidReports.KEYBOARD, control)
                        && profile.sendReport(host, HidReports.NAVIGATION, HidReports.wheel(z, 0));
            } finally {
                released = profile.sendReport(host, HidReports.KEYBOARD, new byte[8]);
            }
            return sent && released;
        }
    }

    private static boolean sendKeyboardStroke(BluetoothHidDevice profile, BluetoothDevice host, byte[] report) {
        boolean pressed = profile.sendReport(host, HidReports.KEYBOARD, report);
        boolean released = profile.sendReport(host, HidReports.KEYBOARD, new byte[8]);
        return pressed && released;
    }

    private static void sendRelease(BluetoothHidDevice profile, BluetoothDevice host, PenLink.Sample sample) {
        if (sample.relative) profile.sendReport(host, HidReports.RELATIVE_MOUSE,
                HidReports.relativeMouse(sample.exit(), false, 0, 0));
        else if (sample.mouse) profile.sendReport(host, HidReports.MOUSE, HidReports.mouse(sample.exit(), false));
        else profile.sendReport(host, HidReports.PEN, HidReports.pen(sample.exit(), false));
    }

    private void fail(Failure failure) {
        if (closed) return;
        Log.w(TAG, failure.name());
        close();
        listener.failed(this, failure);
    }

    private void closeProxy(int kind, BluetoothProfile device) {
        try { adapter.closeProfileProxy(kind, device); }
        catch (SecurityException ignored) { }
    }

    @Override public void close() {
        if (closed) return;
        closed = true;
        pending.close();
        main.removeCallbacks(sessionTimeout);
        main.removeCallbacks(profileTimeout);
        main.removeCallbacks(registrationTimeout);
        main.removeCallbacks(hostTimeout);
        main.removeCallbacks(checkHostBond);
        main.removeCallbacks(checkSelectedHost);
        BluetoothHidDevice device = profile;
        BluetoothDevice target = host;
        host = null;
        profile = null;
        try {
            if (device == null) return;
            synchronized (outputLock) {
                try {
                    if (target != null && device.getConnectionState(target) == BluetoothProfile.STATE_CONNECTED) {
                        PenLink.Sample prior = lastSent;
                        if (prior != null) sendRelease(device, target, prior);
                        if (prior == null || !prior.relative)
                            device.sendReport(target, HidReports.RELATIVE_MOUSE, HidReports.rightClick(false));
                        device.sendReport(target, HidReports.KEYBOARD, new byte[8]);
                    }
                } catch (SecurityException ignored) {
                } finally {
                    try {
                        if (registered || registrationRequested) device.unregisterApp();
                    } catch (SecurityException ignored) {
                    } finally {
                        closeProxy(BluetoothProfile.HID_DEVICE, device);
                    }
                }
            }
        } finally {
            try {
                if (sessionBound) {
                    sessionBound = false;
                    context.unbindService(session);
                }
            } finally {
                if (sessionStarted) {
                    sessionStarted = false;
                    context.stopService(new Intent(context, HidSessionService.class));
                }
            }
        }
    }
}
