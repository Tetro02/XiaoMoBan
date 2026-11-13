package io.localpen.tablet;

final class BridgeProfile {
    static final int LAN_PORT = 57211;
    static final int USB_PORT = 57212;
    static final String PREFIX = "PNB1|";
    final String host;
    final String fingerprint;
    final String code;
    final int port;

    BridgeProfile(String host, String fingerprint, String code) {
        this(host, fingerprint, code, LAN_PORT);
    }

    BridgeProfile(String host, String fingerprint, String code, int port) {
        this.host = host;
        this.fingerprint = fingerprint;
        this.code = code;
        this.port = port;
    }

    static BridgeProfile parse(String text) {
        if (text == null) return null;
        String[] fields = text.trim().split("[|]", -1);
        boolean withPort = fields.length == 5 && fields[0].equals("PNB2");
        if (!withPort && (fields.length != 4 || !fields[0].equals("PNB1"))) return null;
        int port = withPort ? parsePort(fields[2]) : LAN_PORT;
        int pin = withPort ? 3 : 2;
        if (port == 0 || !fields[1].matches("[a-zA-Z0-9._-]{1,253}")
                || !fields[pin].matches("[a-fA-F0-9]{64}") || !isCode(fields[pin + 1])) return null;
        return new BridgeProfile(fields[1], fields[pin], fields[pin + 1], port);
    }

    static int parsePort(String text) {
        if (!text.matches("[0-9]{1,5}")) return 0;
        int port = Integer.parseInt(text);
        return port <= 65535 ? port : 0;
    }

    static boolean isCode(String text) {
        return text.matches("[a-zA-Z0-9-]{4,48}");
    }
}
