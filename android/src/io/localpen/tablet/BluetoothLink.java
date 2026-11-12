package io.localpen.tablet;

import android.annotation.SuppressLint;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothSocket;

import org.json.JSONException;
import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Timer;
import java.util.TimerTask;
import java.util.UUID;

final class BluetoothLink {
    static final UUID SERVICE_UUID = UUID.fromString("a02d496e-a54d-4f84-9a45-0b5c78f53251");
    private static final long CONNECT_TIMEOUT_MS = 15000;
    private static final int MAX_REPLY_BYTES = 1024;

    interface Listener {
        void state(BluetoothLink source, PenLink.State state, String message);
    }

    private final BluetoothDevice device;
    private final String token;
    private final Listener listener;
    private final Thread worker;
    private final PenSampleQueue pending = new PenSampleQueue();
    private volatile BluetoothSocket socket;
    private volatile boolean stopped;
    private volatile boolean ready;
    private volatile boolean overflow;
    private volatile boolean timedOut;
    private volatile boolean remoteEnded;
    private volatile OfficialProtocol.Display display = OfficialProtocol.Display.UNKNOWN;

    BluetoothLink(BluetoothDevice device, String token, Listener listener) {
        int size = token.getBytes(StandardCharsets.UTF_8).length;
        if (size == 0 || size > 256) throw new IllegalArgumentException("蓝牙配对码需要 1–256 个 UTF-8 字节");
        this.device = device;
        this.token = token;
        this.listener = listener;
        worker = new Thread(this::run, "PenNoteBridge Bluetooth");
        worker.setDaemon(true);
    }

    void start() {
        worker.start();
    }

    void send(PenLink.Sample sample) {
        if (!ready || stopped || overflow) return;
        if (!pending.offer(sample)) {
            overflow = true;
            closeSocket();
            worker.interrupt();
        }
    }

    OfficialProtocol.Display display() { return display; }

    boolean sendShortcut(byte[] report) {
        return ready && !stopped && !overflow && display.shortcuts && pending.offerShortcut(report);
    }

    boolean navigate(float[] motion) {
        return ready && !stopped && !overflow && display.canvasNavigation && pending.offerNavigation(motion);
    }

    void close() {
        stopped = true;
        pending.close();
        closeSocket();
        worker.interrupt();
    }

    private void run() {
        if (stopped) return;
        listener.state(this, PenLink.State.WAITING, "正在连接蓝牙接收器…");
        String failure = null;
        try {
            connect();
        } catch (SecurityException error) {
            failure = "请允许「附近的设备」权限后重试";
        } catch (IOException | JSONException | InterruptedException error) {
            if (overflow) failure = "蓝牙输入积压，连接已释放";
            else if (timedOut) failure = "蓝牙连接超时，请确认电脑接收器已启动";
            else if (remoteEnded) failure = "电脑蓝牙接收器已断开";
            else failure = error.getMessage() == null ? "蓝牙连接已中断" : error.getMessage();
        } finally {
            ready = false;
            pending.close();
            closeSocket();
        }
        if (!stopped && failure != null) listener.state(this, PenLink.State.FAILED, failure);
    }

    @SuppressLint("MissingPermission")
    private void connect() throws IOException, JSONException, InterruptedException {
        // The activity requests BLUETOOTH_CONNECT and offers bonded devices only.
        if (device.getBondState() != BluetoothDevice.BOND_BONDED) {
            throw new IOException("请先在 Android 蓝牙设置中与电脑配对");
        }
        BluetoothSocket connection = device.createRfcommSocketToServiceRecord(SERVICE_UUID);
        socket = connection;
        if (stopped) return;

        Timer deadline = new Timer("PenNoteBridge Bluetooth timeout", true);
        deadline.schedule(new TimerTask() {
            @Override public void run() {
                timedOut = true;
                closeSocket();
            }
        }, CONNECT_TIMEOUT_MS);

        BufferedInputStream input;
        OutputStream output;
        try {
            connection.connect();
            input = new BufferedInputStream(connection.getInputStream());
            output = connection.getOutputStream();
            write(output, new JSONObject().put("hello", 1).put("token", token));
            output.flush();
            JSONObject reply = new JSONObject(readReply(input));
            Object accepted = reply.opt("ok");
            if (!(accepted instanceof Number) || ((Number) accepted).doubleValue() != 1) {
                throw new IOException("蓝牙接收器拒绝连接，请核对配对码");
            }
            display = new OfficialProtocol.Display(reply.optInt("width"), reply.optInt("height"),
                    reply.optInt("shortcuts") == 1, reply.optInt("canvas") == 1);
        } finally {
            deadline.cancel();
        }
        if (stopped) return;
        if (timedOut) throw new IOException("Bluetooth handshake timed out");

        Thread watcher = new Thread(() -> watchDisconnect(input), "PenNoteBridge Bluetooth receive");
        watcher.setDaemon(true);
        watcher.start();
        ready = true;
        listener.state(this, PenLink.State.CONNECTED, "蓝牙已连接");
        long seq = 1;
        while (!stopped) {
            PenSampleQueue.Pending next = pending.take();
            if (next == null) return;
            if (next.shortcut != null) {
                write(output, new JSONObject().put("key", next.shortcut[2] & 255)
                        .put("modifiers", next.shortcut[0] & 255));
            } else if (next.navigation != null) {
                JSONArray values = new JSONArray();
                for (float value : next.navigation) values.put((double) value);
                write(output, new JSONObject().put("canvas", values));
            } else {
                write(output, frame(next.sample, seq++, next.time));
            }
        }
    }

    private void watchDisconnect(InputStream input) {
        try {
            // The receiver sends no data after its handshake acknowledgement.
            input.read();
        } catch (IOException ignored) {
            // Closing the Bluetooth socket also ends this read.
        } finally {
            if (!stopped) {
                remoteEnded = true;
                closeSocket();
                worker.interrupt();
            }
        }
    }

    private void closeSocket() {
        BluetoothSocket current = socket;
        if (current == null) return;
        try {
            // The receiver releases any held pen tip/button on EOF.
            current.close();
        } catch (IOException ignored) {
            // The peer may already have closed the connection.
        }
    }

    private static String readReply(InputStream input) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        for (int i = 0; i <= MAX_REPLY_BYTES; i++) {
            int next = input.read();
            if (next == -1) throw new IOException("蓝牙接收器没有接受连接，请核对配对码和服务");
            if (next == '\n') return new String(bytes.toByteArray(), StandardCharsets.UTF_8);
            bytes.write(next);
        }
        throw new IOException("蓝牙接收器回复过长");
    }

    private static void write(OutputStream output, JSONObject frame) throws IOException {
        output.write((frame + "\n").getBytes(StandardCharsets.UTF_8));
    }

    private static JSONObject frame(PenLink.Sample sample, long seq, long time) throws JSONException {
        JSONObject result = new JSONObject().put("seq", seq).put("t", time).put("kind", sample.kind)
                .put("x", sample.x).put("y", sample.y).put("pressure", sample.pressure)
                .put("mode", sample.relative ? "relative_mouse" : sample.mouse ? "absolute_mouse" : "pen")
                .put("tiltX", sample.axes.tiltX).put("tiltY", sample.axes.tiltY)
                .put("rotation", sample.axes.rotation).put("barrel", sample.barrel).put("eraser", sample.eraser);
        if (sample.relative && sample.relativeGain != 1f) result.put("gain", (double) sample.relativeGain);
        if (!sample.mouse) result.put("hoverPanDeadzonePx", sample.hoverPanDeadzonePx);
        return result;
    }
}
