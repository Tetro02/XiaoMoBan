package io.localpen.tablet;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.os.Handler;
import android.os.Looper;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

final class BridgeDiscovery implements AutoCloseable {
    private static final String TYPE = "_pennotebridge._tcp.";

    static final class Computer {
        final String name;
        final String host;
        final String fingerprint;
        final int port;

        Computer(NsdServiceInfo info, InetAddress host) {
            name = info.getServiceName();
            this.host = host.getHostAddress();
            port = info.getPort();
            byte[] pin = info.getAttributes().get("fp");
            fingerprint = pin == null ? "" : new String(pin, StandardCharsets.US_ASCII);
        }

        @Override public String toString() { return name + "  ·  " + host; }
    }

    interface Listener { void changed(List<Computer> computers); }

    private final Handler main = new Handler(Looper.getMainLooper());
    private final NsdManager nsd;
    private final Listener listener;
    private final ArrayDeque<NsdServiceInfo> waiting = new ArrayDeque<>();
    private final Map<String, Computer> found = new LinkedHashMap<>();
    private boolean resolving;
    private boolean started;
    private boolean closed;

    BridgeDiscovery(Context context, Listener listener) {
        nsd = context.getSystemService(NsdManager.class);
        this.listener = listener;
    }

    void start() {
        if (nsd == null || closed || started) return;
        try {
            nsd.discoverServices(TYPE, NsdManager.PROTOCOL_DNS_SD, discovery);
            started = true;
        } catch (IllegalArgumentException | IllegalStateException | SecurityException ignored) {
            // Manual IP entry remains available.
        }
    }

    private final NsdManager.DiscoveryListener discovery = new NsdManager.DiscoveryListener() {
        @Override public void onStartDiscoveryFailed(String type, int error) {
            main.post(() -> { started = false; });
        }
        @Override public void onStopDiscoveryFailed(String type, int error) { }
        @Override public void onDiscoveryStarted(String type) { }
        @Override public void onDiscoveryStopped(String type) { }
        @Override public void onServiceFound(NsdServiceInfo service) {
            main.post(() -> {
                if (closed || !TYPE.equals(service.getServiceType())) return;
                if (waiting.size() < 16) waiting.addLast(service);
                resolve();
            });
        }
        @Override public void onServiceLost(NsdServiceInfo service) {
            main.post(() -> {
                if (closed) return;
                waiting.removeIf(item -> item.getServiceName().equals(service.getServiceName()));
                if (found.remove(service.getServiceName()) != null) publish();
            });
        }
    };

    @SuppressWarnings("deprecation")
    private void resolve() {
        if (closed || resolving || waiting.isEmpty()) return;
        NsdServiceInfo service = waiting.removeFirst();
        resolving = true;
        try {
            nsd.resolveService(service, new NsdManager.ResolveListener() {
                @Override public void onResolveFailed(NsdServiceInfo info, int error) { complete(null); }
                @Override public void onServiceResolved(NsdServiceInfo info) { complete(info); }
            });
        } catch (IllegalArgumentException | IllegalStateException | SecurityException ignored) {
            complete(null);
        }
    }

    @SuppressWarnings("deprecation")
    private void complete(NsdServiceInfo info) {
        main.post(() -> {
            resolving = false;
            if (closed) return;
            if (info != null && info.getHost() instanceof Inet4Address && info.getPort() == LanPairing.PORT) {
                found.put(info.getServiceName(), new Computer(info, info.getHost()));
                publish();
            }
            resolve();
        });
    }

    private void publish() { listener.changed(new ArrayList<>(found.values())); }

    @Override public void close() {
        closed = true;
        waiting.clear();
        found.clear();
        if (started) {
            try { nsd.stopServiceDiscovery(discovery); }
            catch (IllegalArgumentException | IllegalStateException ignored) { }
        }
    }
}
