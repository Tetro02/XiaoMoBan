package io.localpen.tablet;

import java.util.Locale;

final class HidReports {
    static final int PEN = 1, KEYBOARD = 2, MOUSE = 3, RELATIVE_MOUSE = 4, NAVIGATION = 5;

    // Keep the descriptor within the older Android SDP stack's 251-byte payload.
    // The mouse collection has separate absolute, relative, and wheel report IDs.
    static final byte[] DESCRIPTOR = bytes(
        0x05,0x0d,0x09,0x01,0xa1,0x01,0x85,PEN,0x09,0x20,0xa1,0x00,
        0x09,0x42,0x09,0x32,0x09,0x44,0x09,0x3c,0x09,0x45,
        0x15,0,0x25,1,0x75,1,0x95,5,0x81,2,0x75,3,0x95,1,0x81,3,
        0x05,1,0x26,0xff,0x7f,0x35,0,0x55,0x0e,0x65,0x11,
        0x75,16,0x09,0x30,0x46,0x46,0x0a,0x81,2,
        0x09,0x31,0x46,0x72,0x06,0x81,2,
        0x05,0x0d,0x55,0,0x65,0,0x45,0,0x26,0xff,0x0f,0x09,0x30,0x81,2,
        0x09,0x3d,0x09,0x3e,0x15,0xa6,0x25,90,
        0x65,0x14,0x75,8,0x95,2,0x81,2,
        0x09,0x41,0x15,0,0x26,0x67,1,0x75,16,0x95,1,0x81,2,0xc0,0xc0,

        0x05,1,0x09,6,0xa1,1,0x85,KEYBOARD,0x65,0,
        0x05,7,0x19,0xe0,0x29,0xe7,0x25,1,0x75,1,0x95,8,0x81,2,
        0x75,8,0x95,1,0x81,3,0x19,0,0x29,0x65,0x25,0x65,0x95,6,0x81,0,0xc0,

        0x05,1,0x09,2,0xa1,1,0x09,1,0xa1,0,
        0x05,9,0x25,1,0x75,1,0x95,3,
        0x85,MOUSE,0x19,1,0x29,3,0x81,2,
        0x85,RELATIVE_MOUSE,0x19,1,0x29,3,0x81,2,
        0x75,5,0x95,1,0x81,3,0x85,MOUSE,0x81,3,
        0x05,1,0x09,0x30,0x09,0x31,0x26,0xff,0x7f,0x75,16,0x95,2,0x81,2,
        0x85,RELATIVE_MOUSE,0x09,0x30,0x09,0x31,0x16,1,0x80,0x81,6,
        0x85,NAVIGATION,0x09,0x38,0x15,0x81,0x25,0x7f,0x75,8,0x95,1,0x81,6,
        0x05,0x0c,0x0a,0x38,0x02,0x81,6,0xc0,0xc0
    );

    static byte[] pen(PenLink.Sample sample, boolean nativeBarrel) {
        return pen(sample, nativeBarrel, false, false);
    }

    static byte[] pen(PenLink.Sample sample, boolean nativeBarrel, boolean eraseContact, boolean erasePreview) {
        boolean range = inRange(sample);
        boolean down = range && (sample.kind.equals("down") || sample.kind.equals("move"));
        boolean eraser = sample.eraser || down && eraseContact;
        int flags = (down ? 1 : 0) | (range ? 2 : 0) | (range && nativeBarrel ? 4 : 0)
                | (range && (eraser || !down && erasePreview) ? 8 : 0) | (down && eraser ? 16 : 0);
        return bytes(flags, low(scaled(sample.x, 32767)), high(scaled(sample.x, 32767)),
                low(scaled(sample.y, 32767)), high(scaled(sample.y, 32767)),
                low(down ? scaled(sample.pressure, 4095) : 0), high(down ? scaled(sample.pressure, 4095) : 0),
                range ? sample.axes.tiltX : 0, range ? sample.axes.tiltY : 0,
                range ? low(sample.axes.rotation) : 0, range ? high(sample.axes.rotation) : 0);
    }

    static byte[] mouse(PenLink.Sample sample, boolean nativeBarrel) {
        boolean range = inRange(sample);
        int buttons = (range && (sample.kind.equals("down") || sample.kind.equals("move")) ? 1 : 0)
                | (range && nativeBarrel ? 2 : 0);
        int x = scaled(sample.x, 32767);
        int y = scaled(sample.y, 32767);
        return bytes(buttons, low(x), high(x), low(y), high(y));
    }

    static byte[] relativeMouse(PenLink.Sample sample, boolean nativeBarrel, int x, int y) {
        int buttons = (contact(sample) ? 1 : 0)
                | (inRange(sample) && nativeBarrel ? 2 : 0);
        return bytes(buttons, low(x), high(x), low(y), high(y));
    }

    static byte[] rightClick(boolean down) { return bytes(down ? 2 : 0, 0, 0, 0, 0); }

    static byte[] wheel(int vertical, int horizontal) {
        return bytes(Math.max(-127, Math.min(127, vertical)), Math.max(-127, Math.min(127, horizontal)));
    }

    static boolean contact(PenLink.Sample sample) {
        return sample.kind.equals("down") || sample.kind.equals("move");
    }

    static boolean inRange(PenLink.Sample sample) {
        return !sample.kind.equals("exit") && !sample.kind.equals("cancel");
    }

    static byte[] shortcut(String chord) {
        if (chord.trim().isEmpty()) return null;
        String[] parts = chord.trim().toUpperCase(Locale.ROOT).split("\\s*\\+\\s*", -1);
        int modifiers = 0;
        for (int i = 0; i < parts.length - 1; i++) {
            int bit;
            switch (parts[i]) {
                case "CTRL": case "CONTROL": bit = 1; break;
                case "SHIFT": bit = 2; break;
                case "ALT": bit = 4; break;
                case "WIN": bit = 8; break;
                default: throw new IllegalArgumentException("请使用 Ctrl、Shift、Alt 或 Win 作为修饰键");
            }
            if ((modifiers & bit) != 0) throw new IllegalArgumentException("修饰键重复");
            modifiers |= bit;
        }
        String key = parts[parts.length - 1];
        int id;
        if (key.matches("[A-Z]")) id = 4 + key.charAt(0) - 'A';
        else if (key.matches("[1-9]")) id = 30 + key.charAt(0) - '1';
        else if (key.equals("0")) id = 39;
        else if (key.matches("F([1-9]|1[0-2])")) id = 57 + Integer.parseInt(key.substring(1));
        else if (key.equals("ESC")) id = 41;
        else if (key.equals("SPACE")) id = 44;
        else if (key.equals("ENTER")) id = 40;
        else if (key.equals("TAB")) id = 43;
        else if (key.equals("RIGHT")) id = 79;
        else if (key.equals("LEFT")) id = 80;
        else if (key.equals("DOWN")) id = 81;
        else if (key.equals("UP")) id = 82;
        else throw new IllegalArgumentException("主键可用字母、数字、F1–F12、Esc、Space、Enter、Tab 或方向键");
        return bytes(modifiers, 0, id, 0, 0, 0, 0, 0);
    }

    private static int scaled(float value, int max) {
        return Math.round(Math.min(1, Math.max(0, value)) * max);
    }

    private static int low(int value) { return value & 255; }
    private static int high(int value) { return value >>> 8 & 255; }

    private static byte[] bytes(int... values) {
        byte[] result = new byte[values.length];
        for (int i = 0; i < values.length; i++) result[i] = (byte) values[i];
        return result;
    }
}
