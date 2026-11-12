package io.localpen.tablet;

final class BridgeProfile {
    static final int LAN_PORT = 57211;
    static final int USB_PORT = 57212;
    static final String PREFIX = "PNB1|";
    final String host;
    final String fingerprint;
    final String code;

    BridgeProfile(String host, String fingerprint, String code) {
        this.host = host;
        this.fingerprint = fingerprint;
        this.code = code;
    }

    static BridgeProfile parse(String text) {
        if (text == null || !text.trim().startsWith(PREFIX)) return null;
        String[] fields = text.trim().split("[|]", -1);
        if (fields.length != 4 || !fields[1].matches("[a-zA-Z0-9._-]{1,253}")
                || !fields[2].matches("[a-fA-F0-9]{64}") || !isCode(fields[3])) return null;
        return new BridgeProfile(fields[1], fields[2], fields[3]);
    }

    static boolean isCode(String text) {
        return text.matches("[a-zA-Z0-9-]{4,48}");
    }
}
