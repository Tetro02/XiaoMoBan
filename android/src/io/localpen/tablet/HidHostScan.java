package io.localpen.tablet;

import android.annotation.SuppressLint;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothClass;
import android.bluetooth.BluetoothDevice;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.os.Build;

import java.util.function.Consumer;

@SuppressLint("MissingPermission")
final class HidHostScan implements AutoCloseable {
    private final Context context;
    private final BluetoothAdapter adapter;
    private final Consumer<BluetoothDevice> found;
    private final Consumer<String> status;
    private boolean registered;
    private boolean started;

    HidHostScan(Context context, BluetoothAdapter adapter, Consumer<BluetoothDevice> found, Consumer<String> status) {
        this.context = context;
        this.adapter = adapter;
        this.found = found;
        this.status = status;
    }

    static boolean possibleComputer(BluetoothDevice device) {
        if (device.getType() == BluetoothDevice.DEVICE_TYPE_LE) return false;
        BluetoothClass kind = device.getBluetoothClass();
        if (kind == null) return true;
        int major = kind.getMajorDeviceClass();
        return major == BluetoothClass.Device.Major.COMPUTER || major == BluetoothClass.Device.Major.UNCATEGORIZED
                || major == BluetoothClass.Device.Major.MISC;
    }

    private final BroadcastReceiver broadcasts = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (BluetoothAdapter.ACTION_DISCOVERY_FINISHED.equals(intent.getAction())) {
                started = false;
                status.accept("搜索结束。找不到电脑时，请确认 Windows 允许蓝牙设备发现此电脑。");
                close();
            } else if (BluetoothDevice.ACTION_FOUND.equals(intent.getAction())) {
                BluetoothDevice device = intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE);
                if (device == null) return;
                try {
                    if (possibleComputer(device)) found.accept(device);
                } catch (SecurityException ignored) {
                    status.accept("缺少「附近的设备」权限。");
                    close();
                }
            }
        }
    };

    void start() {
        IntentFilter filter = new IntentFilter(BluetoothDevice.ACTION_FOUND);
        filter.addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED);
        try {
            if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(broadcasts, filter, Context.RECEIVER_EXPORTED);
            else context.registerReceiver(broadcasts, filter);
            registered = true;
            if (adapter.isDiscovering()) return;
            started = adapter.startDiscovery();
            if (!started) {
                status.accept("Android 未启动蓝牙搜索。请确认蓝牙已开启。");
                close();
            }
        } catch (SecurityException ignored) {
            status.accept("缺少「附近的设备」权限。");
            close();
        }
    }

    @Override public void close() {
        if (registered) {
            registered = false;
            context.unregisterReceiver(broadcasts);
        }
        if (started) {
            started = false;
            try { adapter.cancelDiscovery(); }
            catch (SecurityException ignored) {}
        }
    }
}
