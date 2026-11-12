package io.localpen.tablet;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.content.Intent;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.SystemClock;
import android.provider.Settings;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowManager;
import android.widget.ArrayAdapter;
import android.widget.AdapterView;
import android.widget.Button;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import java.util.Locale;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.UUID;
import java.util.function.IntFunction;

import static io.localpen.tablet.TabletIcon.Shape.*;
import static io.localpen.tablet.TabletStyle.*;

public final class MainActivity extends Activity implements PenSurface.Listener {
    private static final int[] REPORT_RATES = {30, 60, 90, 120, 240};
    private static final int REQUEST_BLUETOOTH = 10;
    private static final int REQUEST_QR = 11;
    private static final int REQUEST_ADVERTISE = 12;
    private static final int REQUEST_DISCOVERABLE = 13;
    private static final int REQUEST_HID_SCAN = 14;
    private static final String[] DEFAULT_SHORTCUTS = {"Ctrl+Z", "Ctrl+Y", "Ctrl+S", ""};
    private enum Route { USB, LAN, BLUETOOTH }
    private static final class HidAttempt {
        final BluetoothDevice device;
        final boolean discover;
        final boolean retried;

        HidAttempt(BluetoothDevice device, boolean discover, boolean retried) {
            this.device = device;
            this.discover = discover;
            this.retried = retried;
        }
    }
    private SharedPreferences prefs;
    private ActiveArea area;
    private PenSurface surface;
    private TextView connectionStatus;
    private View connectionDot;
    private TextView areaStatus;
    private TextView inputStatus;
    private TextView inputPhase;
    private Button disconnectButton;
    private Button editButton;
    private Button aspectButton;
    private final Button[] shortcutButtons = new Button[DEFAULT_SHORTCUTS.length];
    private final String[] shortcuts = new String[DEFAULT_SHORTCUTS.length];
    private Button modeButton;
    private Button usbButton;
    private Button lanButton;
    private Button bluetoothButton;
    private boolean editing;
    private boolean matchDisplayAspect;
    private boolean touchpadEnabled;
    private int touchpadSpeed;
    private int hoverPanDeadzonePx;
    private OfficialProtocol.Display desktopDisplay = OfficialProtocol.Display.UNKNOWN;
    private boolean absoluteMouse;
    private boolean relativeMouse;
    private boolean sendTilt;
    private boolean rotateWithDirection;
    private int reportRate;
    private int barrelMode;
    private boolean autoUsb;
    private boolean usbPaused;
    private String clientId;
    private PenLink link;
    private BluetoothLink bluetoothLink;
    private HidLink hidLink;
    private AlertDialog bluetoothDialog;
    private BluetoothDevice hidFallbackDevice;
    private String hidFallbackMessage;
    private boolean bluetoothUiResumed;
    private boolean showingDiscoverability;
    private boolean waitingForHidPairing;
    private HidLink hidReadyToPair;
    private HidAttempt pendingHid;
    private HidHostScan hidHostScan;
    private final Runnable startPendingHid = this::continuePendingHid;
    private long connectionRequest;
    private long lastStatusAt;
    private boolean statusBarrel;
    private final ArrayDeque<String> penTrace = new ArrayDeque<>();
    private long traceStart;
    private boolean tracingPen;
    private String lastInputTrace = "";
    private long lastInputTraceTime;
    private String lastOutputTrace = "";
    private long lastOutputTraceTime;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
        prefs = getSharedPreferences("tablet", MODE_PRIVATE);
        area = new ActiveArea(prefs.getFloat("left", 0.2f), prefs.getFloat("top", 0.2f),
                prefs.getFloat("width", 0.6f), prefs.getFloat("height", 0.6f));
        matchDisplayAspect = prefs.getBoolean("match_display_aspect", false);
        touchpadEnabled = prefs.getBoolean("touchpad_enabled", true);
        touchpadSpeed = Math.max(5, Math.min(25, prefs.getInt("touchpad_speed", 10)));
        hoverPanDeadzonePx = Math.max(0, Math.min(96, prefs.getInt("hover_pan_deadzone_px", 2)));
        for (int i = 0; i < shortcuts.length; i++) {
            shortcuts[i] = prefs.getString("shortcut_" + i, DEFAULT_SHORTCUTS[i]);
        }
        absoluteMouse = prefs.getBoolean("mouse", false);
        relativeMouse = absoluteMouse && prefs.getBoolean("relative_mouse", false);
        reportRate = REPORT_RATES[reportRateIndex(prefs.getInt("report_rate", 120))];
        sendTilt = prefs.getBoolean("send_tilt", true);
        rotateWithDirection = prefs.getBoolean("rotate_with_direction", false);
        barrelMode = prefs.getInt("barrel_mode", PenSurface.BARREL_ERASER) == PenSurface.BARREL_OFF
                ? PenSurface.BARREL_OFF : PenSurface.BARREL_ERASER;
        autoUsb = prefs.getBoolean("auto_official_usb", true);
        clientId = prefs.getString("official_client_id", "");
        if (clientId.isEmpty()) {
            clientId = UUID.randomUUID().toString();
            prefs.edit().putString("official_client_id", clientId).apply();
        }
        buildUi();
    }

    private void buildUi() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setBackgroundColor(BACKGROUND);
        page.addView(header());
        addRule(page);

        surface = new PenSurface(this, area, this);
        surface.setPenOptions(reportRate, sendTilt, rotateWithDirection, barrelMode);
        surface.setTouchpadEnabled(touchpadEnabled);
        surface.setTouchpadGain(touchpadSpeed / 10f);
        surface.setHoverPanDeadzone(hoverPanDeadzonePx);
        surface.addOnLayoutChangeListener((view, l, t, r, b, oldL, oldT, oldR, oldB) -> syncAreaAspect());
        updateOutput();
        page.addView(surface, new LinearLayout.LayoutParams(-1, 0, 1));
        addRule(page);
        page.addView(footer());
        showArea();
        setContentView(page);
    }

    private LinearLayout header() {
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        header.setBackgroundColor(CHROME);
        LinearLayout top = row(16);
        header.addView(top, new LinearLayout.LayoutParams(-1, dp(66)));
        top.addView(identity(), new LinearLayout.LayoutParams(0, -2, 1));

        LinearLayout tools = row(0);
        modeButton = tool(this, "绝对笔", PEN, this::selectMode);
        addTool(tools, modeButton);
        editButton = tool(this, "工作区", CROP, () -> setEditing(!editing));
        addTool(tools, editButton);
        aspectButton = tool(this, "跟随电脑比例", null, () -> {
            matchDisplayAspect = !matchDisplayAspect;
            prefs.edit().putBoolean("match_display_aspect", matchDisplayAspect).apply();
            setTool(aspectButton, "跟随电脑比例", null, matchDisplayAspect);
            syncAreaAspect();
            showArea();
        });
        setTool(aspectButton, "跟随电脑比例", null, matchDisplayAspect);
        aspectButton.setVisibility(View.GONE);
        addTool(tools, aspectButton);
        addTool(tools, tool(this, "笔设置", SETTINGS, this::showPenSettings));

        LinearLayout connections = row(0);
        connections.setBackground(rounded(this, PANEL, 12));
        connections.setPadding(dp(3), dp(2), dp(3), dp(2));
        usbButton = tool(this, "USB 自动", USB, this::startUsb);
        lanButton = tool(this, "局域网", WIFI, this::showLan);
        bluetoothButton = tool(this, "蓝牙", BLUETOOTH, this::showBluetooth);
        addTool(connections, usbButton);
        addTool(connections, lanButton);
        addTool(connections, bluetoothButton);
        disconnectButton = tool(this, "断开", STOP, () -> {
            usbPaused = true;
            disconnect("未连接");
        });
        disconnectButton.setVisibility(View.GONE);
        addTool(connections, disconnectButton);
        top.addView(tools, new LinearLayout.LayoutParams(-2, -2));
        LinearLayout.LayoutParams transport = new LinearLayout.LayoutParams(-2, -2);
        transport.setMarginStart(dp(12));
        top.addView(connections, transport);

        HorizontalScrollView compact = new HorizontalScrollView(this);
        compact.setHorizontalScrollBarEnabled(false);
        compact.setFillViewport(true);
        compact.setPadding(dp(12), 0, dp(12), dp(6));
        compact.setClipToPadding(false);
        compact.setVisibility(View.GONE);
        header.addView(compact, new LinearLayout.LayoutParams(-1, dp(54)));
        header.addOnLayoutChangeListener((view, left, upper, right, lower, oldLeft, oldUpper, oldRight, oldLower) -> {
            boolean narrow = right - left < dp(875 * Math.max(1f, getResources().getConfiguration().fontScale));
            if (narrow == (tools.getParent() == compact)) return;
            header.post(() -> {
                if (narrow && tools.getParent() == top) {
                    top.removeView(tools);
                    compact.addView(tools, new HorizontalScrollView.LayoutParams(-2, -1));
                    compact.setVisibility(View.VISIBLE);
                } else if (!narrow && tools.getParent() == compact) {
                    compact.removeView(tools);
                    compact.setVisibility(View.GONE);
                    top.addView(tools, 1, new LinearLayout.LayoutParams(-2, -2));
                }
            });
        });
        header.addView(shortcutBar(), new LinearLayout.LayoutParams(-1, dp(48)));
        return header;
    }

    private View shortcutBar() {
        HorizontalScrollView scroll = new HorizontalScrollView(this);
        scroll.setHorizontalScrollBarEnabled(false);
        LinearLayout bar = row(12);
        scroll.addView(bar, new HorizontalScrollView.LayoutParams(-2, -1));
        for (int i = 0; i < shortcutButtons.length; i++) {
            final int slot = i;
            Button button = tool(this, shortcuts[i], null, () -> sendShortcut(slot));
            button.setOnLongClickListener(ignored -> { showShortcutSettings(); return true; });
            shortcutButtons[i] = button;
            addTool(bar, button);
        }
        addTool(bar, tool(this, "编辑快捷键", SETTINGS, this::showShortcutSettings));
        refreshShortcutBar();
        return scroll;
    }

    private void refreshShortcutBar() {
        for (int i = 0; i < shortcutButtons.length; i++) {
            Button button = shortcutButtons[i];
            setTool(button, shortcuts[i], null, false);
            button.setVisibility(shortcuts[i].isEmpty() ? View.GONE : View.VISIBLE);
        }
    }

    private void showShortcutSettings() {
        surface.setCapture(false);
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(24), dp(12), dp(24), dp(8));
        fields.addView(label("填写 Windows 组合键，例如 Ctrl+Z。留空可隐藏对应按钮。", 13, MUTED));
        EditText[] values = new EditText[shortcuts.length];
        for (int i = 0; i < values.length; i++) {
            values[i] = field("快捷键 " + (i + 1), shortcuts[i], InputType.TYPE_CLASS_TEXT
                    | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
            fields.addView(values[i]);
        }
        ScrollView scroll = new ScrollView(this);
        scroll.addView(fields);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("电脑快捷键").setView(scroll)
                .setNegativeButton("取消", null).setPositiveButton("保存", null).create();
        dialog.setOnDismissListener(ignored -> surface.setCapture(true));
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE)
                .setOnClickListener(button -> {
                    String[] updated = new String[values.length];
                    for (int i = 0; i < updated.length; i++) {
                        updated[i] = values[i].getText().toString().trim();
                        try {
                            HidReports.shortcut(updated[i]);
                        } catch (IllegalArgumentException error) {
                            values[i].setError(error.getMessage());
                            values[i].requestFocus();
                            return;
                        }
                    }
                    SharedPreferences.Editor saved = prefs.edit();
                    for (int i = 0; i < updated.length; i++) {
                        shortcuts[i] = updated[i];
                        saved.putString("shortcut_" + i, updated[i]);
                    }
                    saved.apply();
                    refreshShortcutBar();
                    dialog.dismiss();
                }));
        dialog.show();
    }

    private void sendShortcut(int slot) {
        sendShortcut(shortcuts[slot], true);
    }

    private void sendShortcut(String chord, boolean showFailure) {
        byte[] report = HidReports.shortcut(chord);
        if (report == null) return;
        boolean sent;
        if (hidLink != null) sent = hidLink.sendShortcut(report);
        else if (link != null) sent = link.sendShortcut(report);
        else sent = bluetoothLink != null && bluetoothLink.sendShortcut(report);
        if (!sent && showFailure) shortcutMessage("快捷键未发送，请确认电脑已连接并支持快捷键");
    }

    private void shortcutMessage(String message) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show();
    }

    private LinearLayout identity() {
        LinearLayout brand = row(0);
        ImageView mark = new ImageView(this);
        mark.setImageResource(getResources().getIdentifier("ic_pen", "drawable", getPackageName()));
        mark.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        brand.addView(mark, new LinearLayout.LayoutParams(dp(38), dp(38)));
        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams copySize = new LinearLayout.LayoutParams(0, -2, 1);
        copySize.setMarginStart(dp(11));
        brand.addView(copy, copySize);
        TextView name = label("小墨板", 18, TEXT);
        name.setSingleLine(true);
        name.setEllipsize(TextUtils.TruncateAt.END);
        medium(name);
        copy.addView(name);
        LinearLayout status = row(0);
        LinearLayout.LayoutParams statusSize = new LinearLayout.LayoutParams(-1, -2);
        statusSize.topMargin = dp(5);
        copy.addView(status, statusSize);
        connectionDot = new View(this);
        connectionDot.setBackground(rounded(this, MUTED, 4));
        status.addView(connectionDot, new LinearLayout.LayoutParams(dp(6), dp(6)));
        connectionStatus = label("未连接", 12, MUTED);
        connectionStatus.setSingleLine(true);
        connectionStatus.setEllipsize(TextUtils.TruncateAt.END);
        connectionStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        LinearLayout.LayoutParams stateSize = new LinearLayout.LayoutParams(0, -2, 1);
        stateSize.setMarginStart(dp(6));
        status.addView(connectionStatus, stateSize);
        return brand;
    }

    private LinearLayout footer() {
        LinearLayout footer = row(16);
        footer.setMinimumHeight(dp(43));
        footer.setBackgroundColor(CHROME);
        inputPhase = label("", 12, ACCENT);
        inputPhase.setPadding(dp(8), dp(4), dp(8), dp(4));
        medium(inputPhase);
        inputPhase.setBackground(rounded(this, PANEL, 6));
        footer.addView(inputPhase, new LinearLayout.LayoutParams(-2, -2));
        inputStatus = label("", 12, TEXT);
        inputStatus.setSingleLine(true);
        inputStatus.setEllipsize(TextUtils.TruncateAt.END);
        inputStatus.setFontFeatureSettings("'tnum'");
        LinearLayout.LayoutParams inputSize = new LinearLayout.LayoutParams(0, -2, 1);
        inputSize.setMarginStart(dp(10));
        footer.addView(inputStatus, inputSize);
        areaStatus = label("", 12, MUTED);
        areaStatus.setSingleLine(true);
        areaStatus.setFontFeatureSettings("'tnum'");
        LinearLayout.LayoutParams areaSize = new LinearLayout.LayoutParams(-2, -2);
        areaSize.setMarginStart(dp(8));
        footer.addView(areaStatus, areaSize);
        return footer;
    }

    private LinearLayout row(int padding) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(padding), 0, dp(padding), 0);
        return row;
    }

    private void addRule(LinearLayout parent) {
        View rule = new View(this);
        rule.setBackgroundColor(LINE);
        parent.addView(rule, new LinearLayout.LayoutParams(-1, dp(1)));
    }

    private void addTool(LinearLayout row, Button tool) {
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-2, -2);
        size.setMarginEnd(dp(3));
        row.addView(tool, size);
    }

    private TextView label(String text, int size, int color) {
        return TabletStyle.label(this, text, size, color);
    }

    private void setEditing(boolean value) {
        editing = value;
        surface.resetPointer();
        surface.setEditing(value);
        setTool(editButton, value ? "完成" : "工作区", value ? CHECK : CROP, value);
        aspectButton.setVisibility(value ? View.VISIBLE : View.GONE);
        if (!value) storeArea();
        showArea();
    }

    private void setDesktopDisplay(OfficialProtocol.Display display) {
        desktopDisplay = display;
        syncAreaAspect();
        if (editing) showArea();
    }

    private void syncAreaAspect() {
        if (surface.getWidth() <= 0 || surface.getHeight() <= 0) return;
        float aspect = matchDisplayAspect && desktopDisplay.width > 0 && desktopDisplay.height > 0
                ? (float) desktopDisplay.width / desktopDisplay.height * surface.getHeight() / surface.getWidth() : 0;
        if (area.setAspect(aspect)) {
            surface.resetPointer();
            surface.invalidate();
            storeArea();
            showArea();
        }
    }

    private void selectMode() {
        surface.resetPointer();
        surface.setCapture(false);
        AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle("输入模式")
                .setSingleChoiceItems(new String[] {
                        "绝对笔 · 压感和悬停",
                        "绝对鼠标 · 映射屏幕位置",
                        "相对鼠标 · 使用 Windows 指针速度与加速"
                }, relativeMouse ? 2 : absoluteMouse ? 1 : 0, (choice, selected) -> {
                    absoluteMouse = selected != 0;
                    relativeMouse = selected == 2;
                    prefs.edit().putBoolean("mouse", absoluteMouse).putBoolean("relative_mouse", relativeMouse).apply();
                    updateOutput();
                    choice.dismiss();
                })
                .setNegativeButton("取消", null)
                .create();
        dialog.setOnDismissListener(ignored -> surface.setCapture(true));
        showDialog(dialog);
    }

    private void updateOutput() {
        setTool(modeButton, relativeMouse ? "相对鼠标" : absoluteMouse ? "绝对鼠标" : "绝对笔",
                absoluteMouse ? MOUSE : PEN, true);
        surface.setOutput(absoluteMouse, relativeMouse);
    }

    private void showPenSettings() {
        surface.setCapture(false);
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(24), dp(8), dp(24), dp(12));

        fields.addView(label("回报率上限", 14, MUTED));
        Spinner rate = choices("30 Hz", "60 Hz", "90 Hz", "120 Hz", "240 Hz");
        rate.setSelection(reportRateIndex(reportRate));
        fields.addView(rate);
        Switch tilt = toggle("传输倾斜角（X / Y）", sendTilt);
        fields.addView(tilt);
        Switch rotation = toggle("旋转跟随笔尖方向", rotateWithDirection);
        fields.addView(rotation);
        TextView rotationHint = label("用笔尖朝向驱动旋转。原装 S Pen 的 Android 笔事件没有独立绕轴角度。", 13, MUTED);
        rotationHint.setLineSpacing(dp(4), 1);
        fields.addView(rotationHint);
        Switch touchpad = toggle("笔离开时使用触控板", touchpadEnabled);
        fields.addView(touchpad);
        SeekBar speed = settingSlider(fields, "触控板指针灵敏度", 20, touchpadSpeed - 5,
                step -> String.format(Locale.ROOT, "%.1f×", (step + 5) / 10f));
        speed.setEnabled(touchpadEnabled);
        touchpad.setOnCheckedChangeListener((button, checked) -> speed.setEnabled(checked));
        Switch barrel = toggle("启用 S Pen 侧键", barrelMode != PenSurface.BARREL_OFF);
        fields.addView(barrel);
        SeekBar deadzone = settingSlider(fields, "悬空拖动死区", 96, hoverPanDeadzonePx,
                pixels -> pixels == 0 ? "关闭" : pixels + " px");
        deadzone.setEnabled(barrel.isChecked());
        barrel.setOnCheckedChangeListener((button, checked) -> deadzone.setEnabled(checked));
        TextView menuHint = label("绝对笔模式下，悬空按住侧键移动画布，按住侧键落笔擦除。三星侧键浮窗可在系统「S Pen → 浮窗指令」中关闭。", 13, MUTED);
        menuHint.setLineSpacing(dp(4), 1);
        fields.addView(menuHint);
        Button diagnosis = tool(this, "侧键诊断", null, () -> {});
        fields.addView(diagnosis);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(fields);
        AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle("笔输入设置")
                .setView(scroll)
                .setPositiveButton("保存", (ignored, selected) -> {
                    reportRate = REPORT_RATES[rate.getSelectedItemPosition()];
                    sendTilt = tilt.isChecked();
                    rotateWithDirection = rotation.isChecked();
                    touchpadEnabled = touchpad.isChecked();
                    touchpadSpeed = speed.getProgress() + 5;
                    hoverPanDeadzonePx = deadzone.getProgress();
                    barrelMode = barrel.isChecked() ? PenSurface.BARREL_ERASER : PenSurface.BARREL_OFF;
                    prefs.edit().putInt("report_rate", reportRate).putBoolean("send_tilt", sendTilt)
                            .putBoolean("rotate_with_direction", rotateWithDirection)
                            .putBoolean("touchpad_enabled", touchpadEnabled)
                            .putInt("touchpad_speed", touchpadSpeed)
                            .putInt("hover_pan_deadzone_px", hoverPanDeadzonePx)
                            .putInt("barrel_mode", barrelMode).apply();
                    surface.setPenOptions(reportRate, sendTilt, rotateWithDirection, barrelMode);
                    surface.setTouchpadEnabled(touchpadEnabled);
                    surface.setTouchpadGain(touchpadSpeed / 10f);
                    surface.setHoverPanDeadzone(hoverPanDeadzonePx);
                    showArea();
                })
                .setNegativeButton("取消", null)
                .create();
        dialog.setOnDismissListener(ignored -> surface.setCapture(true));
        showDialog(dialog);
        diagnosis.setOnClickListener(ignored -> { dialog.dismiss(); showPenDiagnosis(); });
    }

    private SeekBar settingSlider(LinearLayout fields, String title, int maximum, int value,
                                  IntFunction<String> display) {
        TextView caption = label(title + "  " + display.apply(value), 14, MUTED);
        fields.addView(caption);
        SeekBar slider = new SeekBar(this);
        slider.setMax(maximum);
        slider.setProgress(value);
        slider.setContentDescription(title);
        slider.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar bar, int progress, boolean fromUser) {
                caption.setText(title + "  " + display.apply(progress));
            }
            @Override public void onStartTrackingTouch(SeekBar bar) {}
            @Override public void onStopTrackingTouch(SeekBar bar) {}
        });
        fields.addView(slider);
        return slider;
    }

    private Spinner choices(String... values) {
        Spinner spinner = new Spinner(this);
        spinner.setMinimumHeight(dp(44));
        spinner.setPopupBackgroundDrawable(rounded(this, PANEL, 10));
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, values);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        spinner.setAdapter(adapter);
        return spinner;
    }

    private Switch toggle(String label, boolean checked) {
        Switch box = new Switch(this);
        box.setText(label);
        box.setTextSize(14);
        box.setTextColor(TEXT);
        box.setMinimumHeight(dp(48));
        box.setChecked(checked);
        return box;
    }

    private void showDialog(AlertDialog dialog) {
        dialog.show();
        if (dialog.getWindow() == null) return;
        dialog.getWindow().setBackgroundDrawable(rounded(this, CHROME, 22));
        int available = getResources().getDisplayMetrics().widthPixels - dp(32);
        dialog.getWindow().setLayout(Math.min(dp(510), available), -2);
    }

    private static int reportRateIndex(int hz) {
        for (int i = 0; i < REPORT_RATES.length; i++) {
            if (REPORT_RATES[i] == hz) return i;
        }
        return 3;
    }

    private void showLan() {
        surface.setCapture(false);
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(24), dp(8), dp(24), dp(12));
        TextView hint = label("在电脑端开启局域网接收，选择附近电脑或手动输入 IP。", 14, MUTED);
        fields.addView(hint);
        Spinner nearby = choices("正在查找附近的小墨板…");
        fields.addView(nearby);
        EditText host = field("电脑 IP", prefs.getString("bridge_host", ""),
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        EditText code = field("首次连接输入电脑上的六位配对码", "", InputType.TYPE_CLASS_NUMBER);
        Button repair = tool(this, "重新配对", null, () -> {});
        fields.addView(host);
        fields.addView(code);
        fields.addView(repair);
        Button scan = tool(this, "扫连接二维码", null, () -> {});
        Button online = tool(this, "使用在线连接码", null, () -> {});
        fields.addView(scan);
        fields.addView(online);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(fields);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("连接电脑")
                .setView(scroll).setNegativeButton("取消", null).setPositiveButton("连接", null).create();
        List<BridgeDiscovery.Computer> computers = new ArrayList<>();
        String[] repairAddress = {""};
        Runnable updatePairingFields = () -> {
            String address = host.getText().toString().trim();
            boolean known = savedLanProfile(address, computers) != null;
            boolean pairing = !known || address.equalsIgnoreCase(repairAddress[0]);
            code.setVisibility(pairing ? View.VISIBLE : View.GONE);
            repair.setVisibility(known ? View.VISIBLE : View.GONE);
            repair.setText(pairing ? "使用已保存的配对" : "重新配对");
            if (!pairing && code.length() != 0) code.setText("");
            hint.setText(known && !pairing ? "已配对电脑，可以直接连接。"
                    : "选择附近电脑或输入 IP，再填写电脑显示的六位配对码。");
            Button connect = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
            if (connect != null) connect.setText(pairing ? "配对并连接" : "直接连接");
        };
        BridgeDiscovery discovery = new BridgeDiscovery(this, found -> {
            int current = nearby.getSelectedItemPosition();
            String selectedName = current > 0 && current <= computers.size() ? computers.get(current - 1).name : "";
            String selectedHost = current > 0 && current <= computers.size() ? computers.get(current - 1).host : "";
            computers.clear();
            computers.addAll(found);
            List<String> names = new ArrayList<>();
            names.add(found.isEmpty() ? "可手动输入电脑 IP" : "选择发现的电脑");
            int selection = 0;
            for (BridgeDiscovery.Computer computer : found) {
                if (computer.name.equals(selectedName)) selection = names.size();
                names.add(computer.toString());
            }
            ArrayAdapter<String> options = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, names);
            options.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
            nearby.setAdapter(options);
            nearby.setSelection(selection, false);
            if (selection > 0 && host.getText().toString().trim().equalsIgnoreCase(selectedHost)) {
                host.setText(computers.get(selection - 1).host);
            }
            updatePairingFields.run();
        });
        nearby.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View item, int position, long id) {
                if (position == nearby.getSelectedItemPosition() && position > 0 && position <= computers.size()) {
                    host.setText(computers.get(position - 1).host);
                }
            }
            @Override public void onNothingSelected(AdapterView<?> parent) {}
        });
        host.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence text, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence text, int start, int before, int count) {
                if (!text.toString().trim().equalsIgnoreCase(repairAddress[0])) repairAddress[0] = "";
                updatePairingFields.run();
            }
            @Override public void afterTextChanged(Editable text) { }
        });
        dialog.setOnDismissListener(ignored -> { discovery.close(); surface.setCapture(true); });
        showDialog(dialog);
        updatePairingFields.run();
        repair.setOnClickListener(ignored -> {
            String address = host.getText().toString().trim();
            repairAddress[0] = address.equalsIgnoreCase(repairAddress[0]) ? "" : address;
            updatePairingFields.run();
            if (!repairAddress[0].isEmpty()) code.requestFocus();
        });
        discovery.start();
        scan.setOnClickListener(ignored -> {
            dialog.dismiss();
            startActivityForResult(new Intent(this, QrScanActivity.class), REQUEST_QR);
        });
        online.setOnClickListener(ignored -> { dialog.dismiss(); showOnlinePairing(); });
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(ignored -> {
            String address = host.getText().toString().trim();
            if (!address.matches("[A-Za-z0-9._-]{1,253}")) {
                host.setError("请输入电脑的局域网 IP");
                return;
            }
            BridgeProfile known = savedLanProfile(address, computers);
            if (known != null && !address.equalsIgnoreCase(repairAddress[0])) {
                dialog.dismiss();
                connectProfile(known);
                return;
            }
            String value = code.getText().toString().trim();
            if (!value.matches("[0-9]{6}")) {
                code.setError("请输入电脑显示的六位配对码");
                return;
            }
            dialog.dismiss();
            beginPair("正在验证局域网配对码…", () -> LanPairing.pair(address, LanPairing.PORT, value));
        });
    }

    private BridgeProfile savedLanProfile(String address, List<BridgeDiscovery.Computer> computers) {
        if (address.isEmpty()) return null;
        String pin = prefs.getString("bridge_pin", "").replaceAll("[\\s:]", "");
        String token = prefs.getString("bridge_code", "");
        if (parseFingerprint(pin) == null || !BridgeProfile.isCode(token)) return null;
        if (address.equalsIgnoreCase(prefs.getString("bridge_host", ""))) {
            return new BridgeProfile(address, pin, token);
        }
        for (BridgeDiscovery.Computer computer : computers) {
            // Discovery locates the computer. PenLink still authenticates its saved TLS certificate.
            if (address.equalsIgnoreCase(computer.host) && pin.equalsIgnoreCase(computer.fingerprint)) {
                return new BridgeProfile(address, pin, token);
            }
        }
        return null;
    }

    private interface PairAttempt { BridgeProfile run() throws Exception; }

    private void beginPair(String status, PairAttempt attempt) {
        autoUsb = false;
        disconnect(status);
        long request = connectionRequest;
        setConnection(status, PenLink.State.WAITING, Route.LAN);
        disconnectButton.setVisibility(View.VISIBLE);
        Thread worker = new Thread(() -> {
            try {
                BridgeProfile profile = attempt.run();
                runOnUiThread(() -> { if (request == connectionRequest && !isFinishing()) connectProfile(profile); });
            } catch (Exception error) {
                runOnUiThread(() -> {
                    if (request == connectionRequest && !isFinishing()) {
                        setConnection("配对失败 · 检查网络和配对码", PenLink.State.FAILED, Route.LAN);
                        disconnectButton.setVisibility(View.GONE);
                    }
                });
            }
        }, "PenNoteBridge pairing");
        worker.setDaemon(true);
        worker.start();
    }

    private void connectProfile(BridgeProfile profile) {
        autoUsb = false;
        prefs.edit().putBoolean("auto_official_usb", false)
                .putString("bridge_host", profile.host).putString("bridge_pin", profile.fingerprint)
                .putString("bridge_code", profile.code).apply();
        connect(profile.host, false, parseFingerprint(profile.fingerprint), profile.code);
    }

    private void showOnlinePairing() {
        surface.setCapture(false);
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(24), dp(8), dp(24), dp(8));
        fields.addView(label("两端使用同一个 HTTPS 配对服务，配对后仍通过局域网传笔迹。", 14, MUTED));
        EditText endpoint = field("HTTPS 配对服务地址", prefs.getString("online_endpoint", ""),
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        EditText code = field("电脑显示的十位在线码", "", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS);
        fields.addView(endpoint);
        fields.addView(code);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("在线配对").setView(fields)
                .setNegativeButton("取消", null).setPositiveButton("连接", null).create();
        dialog.setOnDismissListener(ignored -> surface.setCapture(true));
        showDialog(dialog);
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(ignored -> {
            final String url;
            try { url = OnlinePairing.endpoint(endpoint.getText().toString()); }
            catch (IllegalArgumentException error) { endpoint.setError("请输入 HTTPS 配对服务地址"); return; }
            String value = code.getText().toString();
            if (!value.replace("-", "").replace(" ", "").toUpperCase(Locale.ROOT).matches("[0-9ABCDEFGHJKMNPQRSTVWXYZ]{10}")) {
                code.setError("请输入十位在线码");
                return;
            }
            prefs.edit().putString("online_endpoint", url).apply();
            dialog.dismiss();
            beginPair("正在查找在线配对…", () -> OnlinePairing.resolve(url, value));
        });
    }

    private void showBluetooth() {
        if (!bluetoothConnectAllowed()) {
            requestPermissions(new String[] {Manifest.permission.BLUETOOTH_CONNECT}, REQUEST_BLUETOOTH);
            return;
        }
        showBluetoothServer(null, true);
    }

    @SuppressLint("MissingPermission")
    private void showBluetoothDirect(BluetoothAdapter adapter) {
        if (!adapter.isEnabled()) {
            showBluetoothServer(null, true);
            return;
        }
        List<BluetoothDevice> devices = bondedDevices(adapter);
        devices.removeIf(device -> !HidHostScan.possibleComputer(device));
        boolean scanAvailable = Build.VERSION.SDK_INT >= 31;
        int discoverChoice = scanAvailable ? 1 : 0;
        int firstDevice = discoverChoice + 1;
        String[] names = new String[devices.size() + firstDevice];
        if (scanAvailable) names[0] = "在平板搜索电脑";
        names[discoverChoice] = "让 Windows 查找平板";
        for (int i = 0; i < devices.size(); i++) names[i + firstDevice] = deviceName(devices.get(i));
        Spinner target = choices(names);
        String last = prefs.getString("hid_device", "");
        for (int i = 0; i < devices.size(); i++) {
            if (devices.get(i).getAddress().equals(last)) target.setSelection(i + firstDevice);
        }
        EditText shortcut = field("侧键快捷键（可留空，如 Ctrl+Z）", prefs.getString("hid_shortcut", ""),
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(24), dp(8), dp(24), dp(8));
        fields.addView(label("直连无需 Windows Server。Android 可能暂停平板上的蓝牙键盘或鼠标；要保留外设，请使用 Server。", 14, MUTED));
        fields.addView(label("请选择电脑的经典蓝牙。Windows 列表里的 Bluetooth LE Device 记录无法确认 HID 已连接。", 13, MUTED));
        fields.addView(target);
        fields.addView(shortcut);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("蓝牙 HID 直连").setView(fields)
                .setNegativeButton("取消", null).setNeutralButton("使用 Server", (d, which) -> showBluetoothServer(null, true))
                .setPositiveButton("直连", null).create();
        showBluetoothDialog(dialog);
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(ignored -> {
            String value = shortcut.getText().toString().trim();
            try { HidReports.shortcut(value); }
            catch (IllegalArgumentException error) { shortcut.setError(error.getMessage()); return; }
            prefs.edit().putString("hid_shortcut", value).apply();
            int choice = target.getSelectedItemPosition();
            dialog.dismiss();
            if (scanAvailable && choice == 0) showHidHostScan(adapter);
            else if (choice == discoverChoice) queueHid(new HidAttempt(null, true, false));
            else if (choice >= firstDevice) queueHid(new HidAttempt(devices.get(choice - firstDevice), false, false));
        });
    }

    @SuppressLint("MissingPermission")
    private void showHidHostScan(BluetoothAdapter adapter) {
        if (Build.VERSION.SDK_INT < 31 || adapter == null) return;
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] {Manifest.permission.BLUETOOTH_SCAN}, REQUEST_HID_SCAN);
            return;
        }
        List<BluetoothDevice> devices = new ArrayList<>();
        ArrayAdapter<String> names = new ArrayAdapter<>(this, android.R.layout.simple_list_item_1);
        TextView status = label("打开 Windows 的蓝牙设置，保持电脑可被发现，然后选择下方电脑。", 14, MUTED);
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(24), dp(8), dp(24), dp(8));
        fields.addView(status);
        ListView list = new ListView(this);
        list.setAdapter(names);
        fields.addView(list, new LinearLayout.LayoutParams(-1, dp(180)));
        HidHostScan scan = new HidHostScan(this, adapter, device -> {
            if (devices.contains(device)) return;
            devices.add(device);
            names.add(deviceName(device));
        }, status::setText);
        hidHostScan = scan;
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("搜索电脑的蓝牙").setView(fields)
                .setNegativeButton("取消", null).setNeutralButton("让 Windows 查找平板",
                        (d, choice) -> queueHid(new HidAttempt(null, true, false))).create();
        list.setOnItemClickListener((parent, view, position, id) -> {
            queueHid(new HidAttempt(devices.get(position), false, false));
            dialog.dismiss();
        });
        showBluetoothDialog(dialog, () -> {
            scan.close();
            if (hidHostScan == scan) hidHostScan = null;
        });
        scan.start();
    }

    @SuppressLint("MissingPermission")
    private void connectHid(BluetoothAdapter adapter, HidAttempt attempt) {
        autoUsb = false;
        SharedPreferences.Editor settings = prefs.edit().putBoolean("auto_official_usb", false);
        if (attempt.device != null) settings.putString("hid_device", attempt.device.getAddress());
        settings.apply();
        disconnect("正在启动蓝牙 HID…");
        waitingForHidPairing = attempt.discover || (attempt.device != null
                && attempt.device.getBondState() != BluetoothDevice.BOND_BONDED);
        hidLink = new HidLink(this, adapter, attempt.device, HidReports.shortcut(prefs.getString("hid_shortcut", "")),
                this::hidForeground, new HidLink.Listener() {
                    @Override public void state(HidLink source, PenLink.State state, String message) {
                        if (hidLink != source) return;
                        if (state == PenLink.State.CONNECTED) {
                            waitingForHidPairing = false;
                            hidReadyToPair = null;
                        }
                        if (state != PenLink.State.WAITING) surface.resetPointer();
                        setConnection(message, state, Route.BLUETOOTH);
                        disconnectButton.setVisibility(View.VISIBLE);
                    }

                    @Override public void readyToPair(HidLink source) {
                        if (hidLink != source || !attempt.discover) return;
                        hidReadyToPair = source;
                        showPendingHidDiscovery();
                    }

                    @Override public void failed(HidLink source, HidLink.Failure failure) {
                        if (hidLink != source) return;
                        hidLink = null;
                        hidReadyToPair = null;
                        waitingForHidPairing = false;
                        surface.resetPointer();
                        if (failure.retryable && !attempt.retried && !isFinishing()) {
                            setConnection(failure.message + "，准备重试…", PenLink.State.WAITING, Route.BLUETOOTH);
                            disconnectButton.setVisibility(View.VISIBLE);
                            queueHid(new HidAttempt(attempt.device, attempt.discover, true));
                            return;
                        }
                        disconnectButton.setVisibility(View.GONE);
                        boolean ended = failure == HidLink.Failure.HOST_DISCONNECTED
                                || (failure == HidLink.Failure.UNREGISTERED && !hidForeground());
                        setConnection(failure.message, ended ? null : PenLink.State.FAILED, Route.BLUETOOTH);
                        if (!ended) offerHidFallback(attempt.device, failure.message + "（" + failure.name() + "）");
                    }
                });
        hidLink.start();
    }

    private boolean hidForeground() {
        return bluetoothUiResumed && bluetoothDialog == null && hasWindowFocus() && !isFinishing();
    }

    private void queueHid(HidAttempt attempt) {
        pendingHid = attempt;
        schedulePendingHid();
    }

    private void schedulePendingHid() {
        if (pendingHid == null || !hidForeground()) return;
        surface.removeCallbacks(startPendingHid);
        surface.postDelayed(startPendingHid, pendingHid.retried ? 650 : 150);
    }

    private void showPendingHidDiscovery() {
        if (hidReadyToPair == null || hidReadyToPair != hidLink || !hidForeground()) return;
        hidReadyToPair = null;
        Intent discoverable = new Intent(BluetoothAdapter.ACTION_REQUEST_DISCOVERABLE);
        discoverable.putExtra(BluetoothAdapter.EXTRA_DISCOVERABLE_DURATION, 120);
        try {
            showingDiscoverability = true;
            startActivityForResult(discoverable, REQUEST_DISCOVERABLE);
        } catch (RuntimeException ignored) {
            showingDiscoverability = false;
            disconnect("无法打开系统蓝牙配对界面");
            offerHidFallback(null, "无法打开系统蓝牙配对界面");
        }
    }

    private void offerHidFallback(BluetoothDevice device, String message) {
        hidFallbackDevice = device;
        hidFallbackMessage = message;
        showPendingHidFallback();
    }

    private void showPendingHidFallback() {
        if (!hidForeground() || hidFallbackMessage == null) return;
        String message = hidFallbackMessage;
        BluetoothDevice target = hidFallbackDevice;
        hidFallbackMessage = null;
        hidFallbackDevice = null;
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("蓝牙 HID 直连不可用")
                .setMessage(message + "。可通过小墨板 Server 连接，保留平板上的蓝牙外设。")
                .setNegativeButton("取消", null)
                .setPositiveButton("通过 Server 继续", (d, which) -> showBluetoothServer(target, false)).create();
        showBluetoothDialog(dialog);
    }

    @SuppressLint("MissingPermission")
    private void showBluetoothServer(BluetoothDevice preferred, boolean allowDirect) {
        if (!bluetoothConnectAllowed()) { showBluetooth(); return; }
        BluetoothAdapter adapter = bluetoothAdapter();
        boolean enabled = adapter != null && adapter.isEnabled();
        boolean canDirect = allowDirect && enabled && Build.VERSION.SDK_INT >= 28;
        List<BluetoothDevice> devices = enabled ? bondedDevices(adapter) : new ArrayList<>();
        if (devices.isEmpty()) {
            AlertDialog.Builder dialog = new AlertDialog.Builder(this).setTitle("蓝牙 · 保留外设")
                    .setMessage(adapter == null ? "这台设备没有可用的蓝牙适配器。"
                            : "请在系统蓝牙设置中将平板和电脑配对，再通过小墨板 Server 连接。")
                    .setNegativeButton("取消", null);
            if (adapter != null) dialog.setPositiveButton("蓝牙设置", (d, which) ->
                    startActivity(new Intent(Settings.ACTION_BLUETOOTH_SETTINGS)));
            if (canDirect) dialog.setNeutralButton("HID 直连", (d, which) -> showBluetoothDirect(adapter));
            showBluetoothDialog(dialog.create());
            return;
        }
        String[] names = new String[devices.size()];
        for (int i = 0; i < names.length; i++) names[i] = deviceName(devices.get(i));
        Spinner device = choices(names);
        EditText code = field("兼容蓝牙密钥", "",
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        fields.setPadding(dp(24), dp(8), dp(24), dp(8));
        fields.addView(label("通过小墨板 Server 连接，可继续使用平板上的蓝牙键盘和鼠标。请在 Windows 端开启兼容蓝牙并复制密钥。", 14, MUTED));
        fields.addView(device);
        fields.addView(code);
        device.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View item, int position, long id) {
                code.setText(prefs.getString("bt_code_" + devices.get(position).getAddress(), ""));
            }

            @Override public void onNothingSelected(AdapterView<?> parent) {}
        });
        String lastDevice = prefs.getString("bt_device", "");
        int selected = 0;
        for (int i = 0; i < devices.size(); i++) {
            BluetoothDevice candidate = devices.get(i);
            if (candidate.getAddress().equals(lastDevice)) selected = i;
            if (candidate.equals(preferred)) { selected = i; break; }
        }
        device.setSelection(selected);
        code.setText(prefs.getString("bt_code_" + devices.get(selected).getAddress(), ""));
        AlertDialog.Builder builder = new AlertDialog.Builder(this).setTitle("蓝牙 · 保留外设").setView(fields)
                .setNegativeButton("取消", null).setPositiveButton("通过 Server 连接", null);
        if (canDirect) builder.setNeutralButton("HID 直连", (d, which) -> showBluetoothDirect(adapter));
        AlertDialog dialog = builder.create();
        showBluetoothDialog(dialog);
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(ignored -> {
            String value = code.getText().toString().trim();
            if (!BridgeProfile.isCode(value)) {
                code.setError("请输入电脑上的兼容蓝牙密钥");
                return;
            }
            connectBluetooth(devices.get(device.getSelectedItemPosition()), value);
            dialog.dismiss();
        });
    }

    private BluetoothAdapter bluetoothAdapter() {
        BluetoothManager manager = getSystemService(BluetoothManager.class);
        return manager == null ? null : manager.getAdapter();
    }

    private boolean bluetoothConnectAllowed() {
        return Build.VERSION.SDK_INT < 31 || checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)
                == PackageManager.PERMISSION_GRANTED;
    }

    @SuppressLint("MissingPermission")
    private static List<BluetoothDevice> bondedDevices(BluetoothAdapter adapter) {
        List<BluetoothDevice> devices = new ArrayList<>(adapter.getBondedDevices());
        devices.sort(Comparator.comparing(MainActivity::deviceName));
        return devices;
    }

    private void showBluetoothDialog(AlertDialog dialog) {
        showBluetoothDialog(dialog, () -> {});
    }

    private void showBluetoothDialog(AlertDialog dialog, Runnable onDismiss) {
        bluetoothDialog = dialog;
        surface.setCapture(false);
        dialog.setOnDismissListener(ignored -> {
            onDismiss.run();
            if (bluetoothDialog != dialog) return;
            bluetoothDialog = null;
            surface.setCapture(true);
            schedulePendingHid();
            showPendingHidFallback();
        });
        showDialog(dialog);
    }

    @SuppressLint("MissingPermission")
    private static String deviceName(BluetoothDevice device) {
        String name = device.getName();
        return TextUtils.isEmpty(name) ? device.getAddress() : name + "  " + device.getAddress().substring(12);
    }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] result) {
        super.onRequestPermissionsResult(request, permissions, result);
        boolean granted = result.length > 0 && result[0] == PackageManager.PERMISSION_GRANTED;
        if (request == REQUEST_BLUETOOTH) {
            if (granted) showBluetooth();
            else showBluetoothDialog(new AlertDialog.Builder(this).setTitle("蓝牙权限")
                    .setMessage("通过 Server 或 HID 直连都需要「附近的设备」权限。")
                    .setPositiveButton("知道了", null).create());
        } else if (request == REQUEST_ADVERTISE) {
            if (granted) queueHid(new HidAttempt(null, true, false));
            else offerHidFallback(null, "新电脑配对需要允许平板被发现");
        } else if (request == REQUEST_HID_SCAN) {
            if (granted) showHidHostScan(bluetoothAdapter());
            else offerHidFallback(null, "搜索电脑需要「附近的设备」权限");
        }
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == REQUEST_DISCOVERABLE) {
            showingDiscoverability = false;
            if (result <= 0 && waitingForHidPairing) disconnect("已取消蓝牙 HID 配对");
            return;
        }
        if (request != REQUEST_QR || result != RESULT_OK || data == null) return;
        BridgeProfile parsed = BridgeProfile.parse(data.getStringExtra(QrScanActivity.EXTRA_QR_TEXT));
        if (parsed == null) {
            setConnection("无法读取此连接码", PenLink.State.FAILED, Route.LAN);
            return;
        }
        connectProfile(parsed);
    }

    private void startUsb() {
        autoUsb = true;
        usbPaused = false;
        prefs.edit().putBoolean("auto_official_usb", true).apply();
        byte[] fingerprint = parseFingerprint(prefs.getString("bridge_pin", ""));
        if (fingerprint == null) {
            disconnect("首次使用 USB，请先配对小墨板电脑端");
            return;
        }
        connect("127.0.0.1", true, fingerprint, "");
    }

    private static byte[] parseFingerprint(String value) {
        String hex = value.replaceAll("[\\s:]", "");
        if (!hex.matches("[0-9a-fA-F]{64}")) return null;
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.length; i++) {
            bytes[i] = (byte) Integer.parseInt(hex.substring(i * 2, i * 2 + 2), 16);
        }
        return bytes;
    }

    private EditText field(String hint, String value, int inputType) {
        EditText field = new EditText(this);
        field.setTextSize(15);
        field.setTextColor(TEXT);
        field.setHintTextColor(MUTED);
        field.setBackground(rounded(this, PANEL, 10));
        field.setPadding(dp(12), dp(10), dp(12), dp(10));
        field.setMinimumHeight(dp(46));
        field.setSingleLine(true);
        field.setHint(hint);
        field.setInputType(inputType);
        field.setText(value);
        return field;
    }

    private void connect(String address, boolean usb, byte[] fingerprint, String pairingCode) {
        String waiting = usb ? "等待 USB，请在电脑端选择平板" : "正在连接局域网…";
        disconnect(waiting);
        Route route = usb ? Route.USB : Route.LAN;
        int port = usb ? BridgeProfile.USB_PORT : BridgeProfile.LAN_PORT;
        setConnection(waiting, PenLink.State.WAITING, route);
        disconnectButton.setVisibility(View.VISIBLE);
        link = new PenLink(address, usb, fingerprint, clientId, Build.MODEL, port, pairingCode, new PenLink.Listener() {
            @Override public void state(PenLink source, PenLink.State state, String status) {
                runOnUiThread(() -> {
                    if (link != source) return;
                    surface.resetPointer();
                    setConnection(status, state, route);
                    setDesktopDisplay(source.display());
                    if (state == PenLink.State.FAILED) {
                        link = null;
                        disconnectButton.setVisibility(View.GONE);
                    }
                });
            }

            @Override public void display(PenLink source, OfficialProtocol.Display display) {
                runOnUiThread(() -> { if (link == source) setDesktopDisplay(source.display()); });
            }

            @Override public void shortcutFailed(PenLink source) {
                runOnUiThread(() -> {
                    if (link == source) shortcutMessage("电脑未接受快捷键，请检查桌面端输入设置");
                });
            }
        });
        link.start();
    }

    private void connectBluetooth(BluetoothDevice device, String code) {
        autoUsb = false;
        String address = device.getAddress();
        prefs.edit().putBoolean("auto_official_usb", false).putString("bt_device", address).apply();
        disconnect("正在通过 Server 连接蓝牙…");
        setConnection("正在通过 Server 连接蓝牙…", PenLink.State.WAITING, Route.BLUETOOTH);
        disconnectButton.setVisibility(View.VISIBLE);
        bluetoothLink = new BluetoothLink(device, code, (source, state, status) -> runOnUiThread(() -> {
            if (bluetoothLink != source) return;
            if (state != PenLink.State.WAITING) surface.resetPointer();
            setConnection(state == PenLink.State.CONNECTED ? "蓝牙已通过 Server 连接" : status, state, Route.BLUETOOTH);
            setDesktopDisplay(state == PenLink.State.CONNECTED ? source.display() : OfficialProtocol.Display.UNKNOWN);
            if (state == PenLink.State.CONNECTED) prefs.edit().putString("bt_code_" + address, code).apply();
            if (state == PenLink.State.FAILED) {
                bluetoothLink = null;
                disconnectButton.setVisibility(View.GONE);
            }
        }));
        bluetoothLink.start();
    }

    private void disconnect(String status) {
        connectionRequest++;
        pendingHid = null;
        waitingForHidPairing = false;
        hidReadyToPair = null;
        surface.removeCallbacks(startPendingHid);
        hidFallbackMessage = null;
        hidFallbackDevice = null;
        surface.resetPointer();
        PenLink current = link;
        link = null;
        if (current != null) current.close();
        BluetoothLink radio = bluetoothLink;
        bluetoothLink = null;
        if (radio != null) radio.close();
        HidLink hid = hidLink;
        hidLink = null;
        if (hid != null) hid.close();
        setDesktopDisplay(OfficialProtocol.Display.UNKNOWN);
        setConnection(status, null, null);
        disconnectButton.setVisibility(View.GONE);
    }

    private void setConnection(String status, PenLink.State state, Route route) {
        if (!TextUtils.equals(connectionStatus.getText(), status)) connectionStatus.setText(status);
        int dot = state == PenLink.State.CONNECTED ? CONNECTED : state == null ? MUTED : ERASER_COLOR;
        connectionDot.setBackground(rounded(this, dot, 4));
        boolean active = state == PenLink.State.CONNECTED || state == PenLink.State.WAITING;
        setRouteButton(usbButton, "USB 自动", USB, active && route == Route.USB);
        setRouteButton(lanButton, "局域网", WIFI, active && route == Route.LAN);
        setRouteButton(bluetoothButton, "蓝牙", BLUETOOTH, active && route == Route.BLUETOOTH);
    }

    private void setRouteButton(Button button, String label, TabletIcon.Shape icon, boolean active) {
        if (button.isSelected() != active) setTool(button, label, icon, active);
    }

    @Override protected void onResume() {
        super.onResume();
        bluetoothUiResumed = true;
        schedulePendingHid();
        showPendingHidFallback();
        if (autoUsb && !usbPaused && link == null && bluetoothLink == null && hidLink == null
                && pendingHid == null && !showingDiscoverability) startUsb();
    }

    private void continuePendingHid() {
        if (pendingHid == null || !hidForeground()) return;
        HidAttempt attempt = pendingHid;
        pendingHid = null;
        if (!bluetoothConnectAllowed()) {
            offerHidFallback(attempt.device, HidLink.Failure.PERMISSION.message + "（PERMISSION）");
            return;
        }
        if (attempt.discover && Build.VERSION.SDK_INT >= 31
                && checkSelfPermission(Manifest.permission.BLUETOOTH_ADVERTISE) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] {Manifest.permission.BLUETOOTH_ADVERTISE}, REQUEST_ADVERTISE);
            return;
        }
        BluetoothAdapter adapter = bluetoothAdapter();
        if (adapter != null) connectHid(adapter, attempt);
        else offerHidFallback(attempt.device, "系统没有可用的蓝牙适配器（ADAPTER）");
    }

    @Override protected void onPause() {
        bluetoothUiResumed = false;
        surface.removeCallbacks(startPendingHid);
        super.onPause();
        traceLine("window paused");
        storeArea();
        surface.resetPointer();
    }

    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (focused) {
            schedulePendingHid();
            showPendingHidDiscovery();
            showPendingHidFallback();
        }
        traceLine(focused ? "window focused" : "window unfocused");
    }

    @Override protected void onStop() {
        super.onStop();
        if (!showingDiscoverability && !waitingForHidPairing) disconnect("未连接");
    }

    @Override protected void onDestroy() {
        if (hidHostScan != null) hidHostScan.close();
        if (surface != null) disconnect("未连接");
        super.onDestroy();
    }

    @Override public void areaChanged() {
        showArea();
    }

    private void showArea() {
        String size = String.format(Locale.ROOT, "%d%% × %d%%",
                Math.round(area.width * 100), Math.round(area.height * 100));
        areaStatus.setText(editing ? size : size + "   /   上限 " + reportRate + " Hz");
        inputPhase.setText(editing ? "调整中" : "就绪");
        inputPhase.setTextColor(ACCENT);
        String help = "拖动框内移动，拖右下角缩放";
        if (matchDisplayAspect) help += desktopDisplay.width > 0 && desktopDisplay.height > 0
                ? " · " + desktopDisplay.width + "×" + desktopDisplay.height
                : " · 等待电脑显示比例";
        inputStatus.setText(editing ? help : "");
    }

    private void storeArea() {
        prefs.edit().putFloat("left", area.left).putFloat("top", area.top)
                .putFloat("width", area.width).putFloat("height", area.height).apply();
    }

    @Override public void sample(PenLink.Sample sample) {
        if (tracingPen) {
            String signature = sample.kind + " barrel=" + sample.barrel;
            long now = SystemClock.uptimeMillis();
            if (!signature.equals(lastOutputTrace) || now - lastOutputTraceTime >= 100) {
                lastOutputTrace = signature;
                lastOutputTraceTime = now;
                traceLine("out " + signature + " p=" + sample.pressure);
            }
        }
        PenLink current = link;
        if (current != null) current.send(sample);
        BluetoothLink radio = bluetoothLink;
        if (radio != null) radio.send(sample);
        HidLink hid = hidLink;
        if (hid != null) hid.send(sample);
        long now = android.os.SystemClock.uptimeMillis();
        String phase = sample.kind.equals("down") || sample.kind.equals("move") ? "落笔"
                : (sample.kind.equals("exit") || sample.kind.equals("cancel") ? "离开" : "悬停");
        if (!editing && (now - lastStatusAt > 75 || sample.barrel != statusBarrel
                || !phase.contentEquals(inputPhase.getText()))) {
            lastStatusAt = now;
            statusBarrel = sample.barrel;
            inputPhase.setText(phase);
            inputPhase.setTextColor(sample.eraser ? ERASER_COLOR : ACCENT);
            String coords = String.format(Locale.ROOT, "X %.3f    Y %.3f", sample.x, sample.y);
            if (!sample.mouse) {
                coords += String.format(Locale.ROOT, "   压力 %.3f", sample.pressure);
                if (sendTilt) coords += "   倾斜 " + sample.axes.tiltX + "° / " + sample.axes.tiltY + "°";
                if (rotateWithDirection) coords += "   旋转 " + sample.axes.rotation + "°";
                if (sample.eraser) coords += "   橡皮";
            }
            if (sample.barrel) coords += "   侧键";
            inputStatus.setText(coords);
        }
    }

    @Override public void touchpad(PenLink.Sample sample) {
        if (hidLink != null) hidLink.send(sample);
        else if (bluetoothLink != null) bluetoothLink.send(sample);
        else if (link != null && link.supportsRelativeMouse()) link.send(sample);
        else return;
        inputPhase.setText("触控板");
        inputPhase.setTextColor(ACCENT);
    }

    @Override public boolean navigate(float[] motion) {
        if (hidLink != null) return hidLink.sendTouchpadNavigation(motion);
        if (bluetoothLink != null) return bluetoothLink.navigate(motion);
        return link != null && link.navigate(motion);
    }

    @Override public void touchpadShortcut(String chord) { sendShortcut(chord, false); }

    @Override public boolean dispatchTouchEvent(MotionEvent event) {
        traceMotion(event);
        return (surface != null && surface.handleSamsungContactFromWindow(event)) || super.dispatchTouchEvent(event);
    }

    @Override public boolean dispatchGenericMotionEvent(MotionEvent event) {
        traceMotion(event);
        return (surface != null && (surface.handleSamsungContactFromWindow(event) || surface.handleBarrelEvent(event)))
                || super.dispatchGenericMotionEvent(event);
    }

    private void traceMotion(MotionEvent event) {
        if (!tracingPen) return;
        for (int i = 0; i < event.getPointerCount(); i++) {
            int tool = event.getToolType(i);
            InputDevice device = event.getDevice();
            if (tool != MotionEvent.TOOL_TYPE_STYLUS && tool != MotionEvent.TOOL_TYPE_ERASER
                    && !event.isFromSource(InputDevice.SOURCE_STYLUS)
                    && (device == null || !device.supportsSource(InputDevice.SOURCE_STYLUS))) continue;
            String signature = MotionEvent.actionToString(event.getActionMasked()) + " tool=" + tool
                    + " src=" + Integer.toHexString(event.getSource()) + " buttons="
                    + Integer.toHexString(event.getButtonState()) + " changed=" + event.getActionButton();
            long now = SystemClock.uptimeMillis();
            if (!signature.equals(lastInputTrace) || now - lastInputTraceTime >= 100) {
                lastInputTrace = signature;
                lastInputTraceTime = now;
                traceLine("in " + signature + " p=" + event.getPressure(i));
            }
            break;
        }
    }

    private void traceLine(String value) {
        if (!tracingPen) return;
        long elapsed = SystemClock.uptimeMillis() - traceStart;
        if (elapsed > 20000) { tracingPen = false; return; }
        if (penTrace.size() == 96) penTrace.removeFirst();
        penTrace.addLast(elapsed + " " + value);
    }

    private void showPenDiagnosis() {
        tracingPen = false;
        surface.setCapture(false);
        String report = TextUtils.join("\n", penTrace);
        TextView text = label(report.isEmpty()
                ? "开始记录后，按住侧键再落笔，然后回这里查看。只记录应用收到的事件类型、侧键和压感。"
                : report, 13, TEXT);
        text.setPadding(dp(22), dp(12), dp(22), dp(8));
        ScrollView scroll = new ScrollView(this);
        scroll.addView(text);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("侧键诊断")
                .setView(scroll).setNegativeButton("关闭", null)
                .setNeutralButton("复制", (d, which) -> {
                    ClipboardManager clipboard = getSystemService(ClipboardManager.class);
                    if (clipboard != null && !report.isEmpty())
                        clipboard.setPrimaryClip(ClipData.newPlainText("小墨板侧键诊断", report));
                }).setPositiveButton("开始记录", (d, which) -> {
                    penTrace.clear();
                    lastInputTrace = "";
                    lastOutputTrace = "";
                    traceStart = SystemClock.uptimeMillis();
                    tracingPen = true;
                }).create();
        dialog.setOnDismissListener(ignored -> surface.setCapture(true));
        showDialog(dialog);
    }

    private int dp(float value) {
        return TabletStyle.dp(this, value);
    }
}
