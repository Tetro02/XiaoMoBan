package io.localpen.tablet;

import io.grpc.CallOptions;
import io.grpc.ClientCall;
import io.grpc.ManagedChannel;
import io.grpc.Metadata;
import io.grpc.MethodDescriptor;
import io.grpc.Status;
import io.grpc.okhttp.OkHttpChannelBuilder;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.UUID;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

final class PenLink {
    enum State { WAITING, CONNECTED, FAILED }

    interface Listener {
        void state(PenLink source, State state, String message);
        default void display(PenLink source, OfficialProtocol.Display display) {}
        default void shortcutFailed(PenLink source) {}
    }

    static final class Sample {
        final String kind;
        final float x;
        final float y;
        final float pressure;
        final boolean mouse;
        final boolean relative;
        final PenAxes axes;
        final boolean barrel;
        final boolean eraser;
        final float relativeGain;
        final int hoverPanDeadzonePx;

        Sample(String kind, float x, float y, float pressure, boolean mouse,
               PenAxes axes, boolean barrel, boolean eraser) {
            this(kind, x, y, pressure, mouse, false, axes, barrel, eraser);
        }

        Sample(String kind, float x, float y, float pressure, boolean mouse, boolean relative,
               PenAxes axes, boolean barrel, boolean eraser) {
            this(kind, x, y, pressure, mouse, relative, axes, barrel, eraser, 1f);
        }

        Sample(String kind, float x, float y, float pressure, boolean mouse, boolean relative,
               PenAxes axes, boolean barrel, boolean eraser, float relativeGain) {
            this(kind, x, y, pressure, mouse, relative, axes, barrel, eraser, relativeGain, 2);
        }

        Sample(String kind, float x, float y, float pressure, boolean mouse, boolean relative,
               PenAxes axes, boolean barrel, boolean eraser, float relativeGain, int hoverPanDeadzonePx) {
            this.kind = kind;
            this.x = x;
            this.y = y;
            this.pressure = pressure;
            this.mouse = mouse;
            this.relative = relative;
            this.axes = axes;
            this.barrel = barrel;
            this.eraser = eraser;
            this.relativeGain = relativeGain;
            this.hoverPanDeadzonePx = hoverPanDeadzonePx;
        }

        Sample exit() {
            return new Sample("exit", x, y, 0, mouse, relative, PenAxes.ZERO, false, false, relativeGain, hoverPanDeadzonePx);
        }

        Sample continued() {
            return new Sample("move", x, y, pressure, mouse, relative, axes, barrel, eraser, relativeGain, hoverPanDeadzonePx);
        }

        Sample withButton(boolean contact, boolean barrel, boolean eraser) {
            return new Sample(contact ? "move" : "hover", x, y, contact ? pressure : 0,
                    mouse, relative, axes, barrel, eraser, relativeGain, hoverPanDeadzonePx);
        }
    }

    private static final MethodDescriptor.Marshaller<byte[]> BYTES = new MethodDescriptor.Marshaller<byte[]>() {
        @Override public InputStream stream(byte[] value) { return new ByteArrayInputStream(value); }

        @Override public byte[] parse(InputStream input) {
            try {
                ByteArrayOutputStream result = new ByteArrayOutputStream();
                byte[] bytes = new byte[1024];
                int read;
                while ((read = input.read(bytes)) != -1) result.write(bytes, 0, read);
                return result.toByteArray();
            } catch (IOException error) {
                throw Status.INTERNAL.withCause(error).asRuntimeException();
            }
        }
    };

    private static final MethodDescriptor<byte[], byte[]> HANDSHAKE =
            method("Handshake", MethodDescriptor.MethodType.UNARY);
    private static final MethodDescriptor<byte[], byte[]> OBSERVE =
            method("ObserveServerInfo", MethodDescriptor.MethodType.SERVER_STREAMING);
    private static final MethodDescriptor<byte[], byte[]> PEN =
            method("StreamPenEvents", MethodDescriptor.MethodType.CLIENT_STREAMING);
    private static final MethodDescriptor<byte[], byte[]> MOUSE =
            method("StreamAbsoluteMouseEvents", MethodDescriptor.MethodType.CLIENT_STREAMING);
    private static final MethodDescriptor<byte[], byte[]> SHORTCUT =
            method("SendShortcut", MethodDescriptor.MethodType.UNARY);
    private static final MethodDescriptor<byte[], byte[]> NAVIGATE =
            method("NavigateCanvas", MethodDescriptor.MethodType.UNARY);
    private static final Metadata.Key<String> CLIENT_ID =
            Metadata.Key.of("x-vt-client-id", Metadata.ASCII_STRING_MARSHALLER);
    private static final Metadata.Key<String> CONNECTION_ID =
            Metadata.Key.of("x-vt-connection-id", Metadata.ASCII_STRING_MARSHALLER);
    private static final Metadata.Key<String> PAIRING_CODE =
            Metadata.Key.of("x-pzb-pairing-code", Metadata.ASCII_STRING_MARSHALLER);

    private static final class QueuedSample {
        final Sample sample;
        final long time = micros();

        QueuedSample(Sample sample) { this.sample = sample; }
    }

    private final ArrayBlockingQueue<QueuedSample> pending = new ArrayBlockingQueue<>(256);
    private final String host;
    private final boolean usb;
    private final int port;
    private final String pairingCode;
    private final byte[] fingerprint;
    private final String clientId;
    private final String device;
    private final Listener listener;
    private final Thread worker;
    private volatile boolean ready;
    private volatile boolean stopped;
    private volatile boolean overflow;
    private volatile boolean wrongCertificate;
    private volatile Session activeSession;

    PenLink(String host, boolean usb, byte[] fingerprint, String clientId, String device, Listener listener) {
        this(host, usb, fingerprint, clientId, device,
                usb ? BridgeProfile.USB_PORT : BridgeProfile.LAN_PORT, "", listener);
    }

    PenLink(String host, boolean usb, byte[] fingerprint, String clientId, String device,
            int port, String pairingCode, Listener listener) {
        this.host = host;
        this.usb = usb;
        this.port = port;
        this.pairingCode = pairingCode;
        this.fingerprint = fingerprint;
        this.clientId = clientId;
        this.device = device;
        this.listener = listener;
        worker = new Thread(this::run, "XiaoMoBan connection");
    }

    void start() { worker.start(); }

    void send(Sample sample) {
        if (ready && !stopped && !pending.offer(new QueuedSample(sample))) overflow = true;
    }

    OfficialProtocol.Display display() {
        Session session = activeSession;
        return session == null ? OfficialProtocol.Display.UNKNOWN : session.display;
    }

    boolean sendShortcut(byte[] report) {
        Session session = activeSession;
        return ready && !stopped && session != null && session.display.shortcuts
                && session.command(SHORTCUT, OfficialProtocol.shortcut(report), session.shortcutPermit, true);
    }

    boolean navigate(float[] motion) {
        Session session = activeSession;
        return ready && !stopped && session != null && session.display.canvasNavigation
                && session.command(NAVIGATE, OfficialProtocol.navigate(motion), session.navigationPermit, false);
    }

    boolean supportsRelativeMouse() {
        return port == BridgeProfile.LAN_PORT || port == BridgeProfile.USB_PORT || display().canvasNavigation;
    }

    void close() {
        stopped = true;
        worker.interrupt();
    }

    private void run() {
        while (!stopped) {
            String message;
            try {
                connect();
                return;
            } catch (InterruptedException error) {
                return;
            } catch (Exception error) {
                message = describe(error);
            }
            if (stopped) return;
            if (!usb || wrongCertificate) {
                listener.state(this, State.FAILED, message);
                return;
            }
            listener.state(this, State.WAITING, "等待 USB · 请在电脑 Server 选择平板");
            try {
                Thread.sleep(1200);
            } catch (InterruptedException error) {
                return;
            }
        }
    }

    private void connect() throws Exception {
        overflow = false;
        wrongCertificate = false;
        ManagedChannel channel = makeChannel();
        Session session = new Session(channel);
        try {
            OfficialProtocol.Greeting greeting = session.handshake();
            if (!greeting.compatible || greeting.protocol < 1 || greeting.protocol > 2) {
                throw new IOException("协议版本不兼容");
            }
            session.display = greeting.display;
            session.observe();
            if (stopped) return;
            activeSession = session;
            ready = true;
            listener.state(this, State.CONNECTED, (usb ? "USB 已连接" : "局域网已连接")
                    + (greeting.version.isEmpty() ? "" : " · Server " + greeting.version));
            while (!stopped) {
                if (session.failure != null) throw session.failure.asRuntimeException();
                if (overflow) throw new IOException("输入积压，连接已释放");
                QueuedSample queued = pending.poll(100, TimeUnit.MILLISECONDS);
                if (queued != null) session.send(queued.sample, queued.time);
            }
        } finally {
            ready = false;
            activeSession = null;
            pending.clear();
            session.finish();
            channel.shutdownNow();
        }
    }

    private ManagedChannel makeChannel() throws Exception {
        OkHttpChannelBuilder builder = OkHttpChannelBuilder.forAddress(host, port)
                .directExecutor().maxInboundMessageSize(65536);
        X509TrustManager pinned = new X509TrustManager() {
            @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }

            @Override public void checkClientTrusted(X509Certificate[] chain, String auth) throws CertificateException {
                throw new CertificateException("Server certificate required");
            }

            @Override public void checkServerTrusted(X509Certificate[] chain, String auth) throws CertificateException {
                if (chain.length == 0 || !matchesPin(chain[0])) {
                    wrongCertificate = true;
                    throw new CertificateException("XiaoMoBan Server fingerprint mismatch");
                }
                chain[0].checkValidity();
            }
        };
        SSLContext tls = SSLContext.getInstance("TLS");
        tls.init(null, new TrustManager[] {pinned}, null);
        return builder.sslSocketFactory(tls.getSocketFactory())
                .hostnameVerifier((ignored, session) -> {
                    // The saved leaf fingerprint identifies this XiaoMoBan receiver.
                    try {
                        return matchesPin((X509Certificate) session.getPeerCertificates()[0]);
                    } catch (Exception error) {
                        return false;
                    }
                }).build();
    }

    private boolean matchesPin(X509Certificate certificate) throws CertificateException {
        try {
            return fingerprint != null && MessageDigest.isEqual(fingerprint,
                    MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded()));
        } catch (java.security.GeneralSecurityException error) {
            throw new CertificateException(error);
        }
    }

    private String describe(Exception error) {
        if (wrongCertificate) return "证书指纹与电脑不匹配";
        Status status = Status.fromThrowable(error);
        if (status.getCode() == Status.Code.UNAUTHENTICATED
                || status.getCode() == Status.Code.PERMISSION_DENIED) {
            return usb ? "USB 接收器未接受连接" : "小墨板配对码错误或电脑未开放局域网";
        }
        if (error instanceof IOException && error.getMessage() != null) return error.getMessage();
        return "连接失败 · 请核对电脑地址和小墨板接收状态";
    }

    private static MethodDescriptor<byte[], byte[]> method(String name, MethodDescriptor.MethodType type) {
        return MethodDescriptor.<byte[], byte[]>newBuilder().setType(type)
                .setFullMethodName(MethodDescriptor.generateFullMethodName(OfficialProtocol.SERVICE, name))
                .setRequestMarshaller(BYTES).setResponseMarshaller(BYTES).build();
    }

    private final class Session {
        final ManagedChannel channel;
        final String connectionId = UUID.randomUUID().toString();
        volatile Status failure;
        volatile ClientCall<byte[], byte[]> stream;
        volatile OfficialProtocol.Display display = OfficialProtocol.Display.UNKNOWN;
        final Semaphore shortcutPermit = new Semaphore(1);
        final Semaphore navigationPermit = new Semaphore(1);
        CountDownLatch streamDone;
        Sample last;

        Session(ManagedChannel channel) { this.channel = channel; }

        Metadata headers() {
            Metadata headers = new Metadata();
            headers.put(CLIENT_ID, clientId);
            headers.put(CONNECTION_ID, connectionId);
            if (!pairingCode.isEmpty()) headers.put(PAIRING_CODE, pairingCode);
            return headers;
        }

        OfficialProtocol.Greeting handshake() throws Exception {
            ClientCall<byte[], byte[]> call = channel.newCall(HANDSHAKE,
                    CallOptions.DEFAULT.withDeadlineAfter(3, TimeUnit.SECONDS));
            CountDownLatch done = new CountDownLatch(1);
            byte[][] response = new byte[1][];
            Status[] result = new Status[1];
            call.start(new ClientCall.Listener<byte[]>() {
                @Override public void onMessage(byte[] message) { response[0] = message; }
                @Override public void onClose(Status status, Metadata trailers) {
                    result[0] = status;
                    done.countDown();
                }
            }, headers());
            call.request(1);
            call.sendMessage(OfficialProtocol.handshake(device));
            call.halfClose();
            if (!done.await(3500, TimeUnit.MILLISECONDS)) throw Status.DEADLINE_EXCEEDED.asRuntimeException();
            if (!result[0].isOk()) throw result[0].asRuntimeException();
            if (response[0] == null) throw new IOException("电脑未返回握手响应");
            return OfficialProtocol.greeting(response[0]);
        }

        void observe() {
            ClientCall<byte[], byte[]> call = channel.newCall(OBSERVE, CallOptions.DEFAULT);
            call.start(new ClientCall.Listener<byte[]>() {
                @Override public void onMessage(byte[] message) {
                    try {
                        OfficialProtocol.Display received = OfficialProtocol.display(message);
                        if (!display.sameAs(received)) {
                            display = received;
                            listener.display(PenLink.this, received);
                        }
                    } catch (IOException ignored) {
                        // A malformed display update does not invalidate pen samples.
                    }
                    call.request(1);
                }
                @Override public void onClose(Status status, Metadata trailers) {
                    if (!status.isOk()) failure = status;
                }
            }, headers());
            call.request(1);
            call.sendMessage(new byte[0]);
            call.halfClose();
        }

        boolean command(MethodDescriptor<byte[], byte[]> method, byte[] message, Semaphore permit, boolean notify) {
            if (!permit.tryAcquire()) return false;
            ClientCall<byte[], byte[]> call = channel.newCall(method,
                    CallOptions.DEFAULT.withDeadlineAfter(2, TimeUnit.SECONDS));
            AtomicBoolean waiting = new AtomicBoolean(true);
            try {
                call.start(new ClientCall.Listener<byte[]>() {
                    @Override public void onClose(Status status, Metadata trailers) {
                        if (waiting.getAndSet(false)) {
                            permit.release();
                            if (notify && !status.isOk() && !stopped) listener.shortcutFailed(PenLink.this);
                        }
                    }
                }, headers());
                call.request(1);
                call.sendMessage(message);
                call.halfClose();
                return true;
            } catch (RuntimeException error) {
                if (waiting.getAndSet(false)) permit.release();
                call.cancel("输入发送失败", error);
                return false;
            }
        }

        void send(Sample sample, long time) throws InterruptedException {
            if (stream == null || last.mouse != sample.mouse || last.relative != sample.relative) {
                finish();
                ClientCall<byte[], byte[]> call = channel.newCall(sample.mouse ? MOUSE : PEN, CallOptions.DEFAULT);
                CountDownLatch done = new CountDownLatch(1);
                stream = call;
                streamDone = done;
                call.start(new ClientCall.Listener<byte[]>() {
                    @Override public void onClose(Status status, Metadata trailers) {
                        if (!status.isOk() && stream == call) failure = status;
                        done.countDown();
                    }
                }, headers());
                call.request(1);
            }
            long timeout = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(900);
            while (!stream.isReady() && failure == null && !stopped) {
                if (System.nanoTime() >= timeout) throw Status.DEADLINE_EXCEEDED.asRuntimeException();
                Thread.sleep(10);
            }
            if (failure != null) throw failure.asRuntimeException();
            if (!stopped) {
                stream.sendMessage(OfficialProtocol.pointer(sample, time));
                last = sample;
            }
        }

        void finish() {
            ClientCall<byte[], byte[]> call = stream;
            Sample sample = last;
            stream = null;
            last = null;
            if (call == null) return;
            try {
                if (sample != null && !(sample.kind.equals("exit") || sample.kind.equals("cancel"))) {
                    call.sendMessage(OfficialProtocol.pointer(sample.exit(), micros()));
                }
                call.halfClose();
                streamDone.await(180, TimeUnit.MILLISECONDS);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
            } catch (RuntimeException ignored) {
                // A disconnected server may already have closed the call.
            }
        }
    }

    private static long micros() {
        return Math.max(0, System.nanoTime() / 1000);
    }
}
