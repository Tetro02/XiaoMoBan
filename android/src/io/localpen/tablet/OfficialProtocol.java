package io.localpen.tablet;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;

final class OfficialProtocol {
    static final String SERVICE = "vt.VtService";

    static final class Greeting {
        String version = "";
        boolean compatible;
        int protocol;
        Display display = Display.UNKNOWN;
    }

    static final class Display {
        static final Display UNKNOWN = new Display(0, 0, false, false);
        final int width;
        final int height;
        final boolean shortcuts;
        final boolean canvasNavigation;

        Display(int width, int height, boolean shortcuts, boolean canvasNavigation) {
            this.width = width;
            this.height = height;
            this.shortcuts = shortcuts;
            this.canvasNavigation = canvasNavigation;
        }

        boolean sameAs(Display other) {
            return width == other.width && height == other.height && shortcuts == other.shortcuts
                    && canvasNavigation == other.canvasNavigation;
        }
    }

    static byte[] handshake(String device) {
        Encoder e = new Encoder();
        e.text(1, "XiaoMoBan 0.6.7");
        e.bytes(2, new byte[] {1, 2});
        e.text(3, device);
        e.text(4, "android");
        return e.output.toByteArray();
    }

    static Greeting greeting(byte[] message) throws IOException {
        Decoder d = new Decoder(message);
        Greeting reply = new Greeting();
        while (d.position < message.length) {
            long tag = d.number();
            if (tag == 10) reply.version = new String(d.bytes(), StandardCharsets.UTF_8);
            else if (tag == 16) reply.protocol = (int) d.number();
            else if (tag == 24) reply.compatible = d.number() != 0;
            else if (tag == 34) reply.display = display(d.bytes());
            else d.skip((int) tag & 7);
        }
        return reply;
    }

    static Display display(byte[] message) throws IOException {
        Decoder d = new Decoder(message);
        int width = 0;
        int height = 0;
        boolean shortcuts = false;
        boolean canvasNavigation = false;
        while (d.position < message.length) {
            long tag = d.number();
            if (tag == 16) width = (int) d.number();
            else if (tag == 24) height = (int) d.number();
            else if (tag == 64) shortcuts = d.number() != 0;
            else if (tag == 72) canvasNavigation = d.number() != 0;
            else d.skip((int) tag & 7);
        }
        return new Display(width, height, shortcuts, canvasNavigation);
    }

    static byte[] shortcut(byte[] keyboardReport) {
        Encoder e = new Encoder();
        e.field(1, keyboardReport[2] & 255);
        e.field(2, keyboardReport[0] & 255);
        return e.output.toByteArray();
    }

    static byte[] navigate(float[] motion) {
        Encoder e = new Encoder();
        for (int i = 0; i < 5; i++) e.floating(i + 1, motion[i]);
        e.field(6, (int) motion[5]);
        return e.output.toByteArray();
    }

    static byte[] pointer(PenLink.Sample sample, long time) {
        Encoder e = new Encoder();
        boolean contact = sample.kind.equals("down") || sample.kind.equals("move");
        boolean inRange = !(sample.kind.equals("exit") || sample.kind.equals("cancel"));
        int status = 0;
        if (inRange) {
            if (sample.mouse) {
                status = contact ? 1 : 0;
            } else if (sample.eraser) {
                status = 0x24 | (contact ? 8 : 0);
            } else {
                status = 0x20 | (contact ? 1 : 0);
            }
            if (sample.barrel) status |= 2;
        }
        e.field(1, status);
        e.field(2, axis(sample.x));
        e.field(3, axis(sample.y));
        if (!sample.mouse) {
            e.field(4, contact ? axis(sample.pressure) : 0);
            e.signed(5, sample.axes.tiltX);
            e.signed(6, sample.axes.tiltY);
            e.field(7, sample.axes.rotation);
            e.field(9, sample.hoverPanDeadzonePx);
        }
        e.field(sample.mouse ? 4 : 8, time);
        if (sample.relative) {
            e.field(5, 1);
            e.field(6, inRange ? 1 : 0);
            if (sample.relativeGain != 1f) e.floating(7, sample.relativeGain);
        }
        return e.output.toByteArray();
    }

    private static int axis(float value) {
        return Math.round(Math.max(0, Math.min(1, value)) * 32767);
    }

    private static final class Encoder {
        final ByteArrayOutputStream output = new ByteArrayOutputStream();

        void number(long value) {
            while ((value & ~0x7fL) != 0) {
                output.write((int) value & 0x7f | 0x80);
                value >>>= 7;
            }
            output.write((int) value);
        }

        void field(int tag, long value) {
            number(tag << 3);
            number(value);
        }

        void signed(int tag, int value) {
            field(tag, (value << 1) ^ (value >> 31));
        }

        void floating(int tag, float value) {
            number((tag << 3) | 5);
            int bits = Float.floatToRawIntBits(value);
            for (int shift = 0; shift < 32; shift += 8) output.write(bits >>> shift & 255);
        }

        void text(int tag, String value) {
            bytes(tag, value.getBytes(StandardCharsets.UTF_8));
        }

        void bytes(int tag, byte[] value) {
            number((tag << 3) | 2);
            number(value.length);
            output.write(value, 0, value.length);
        }
    }

    private static final class Decoder {
        final byte[] input;
        int position;

        Decoder(byte[] input) {
            this.input = input;
        }

        long number() throws IOException {
            long value = 0;
            for (int shift = 0; shift < 64 && position < input.length; shift += 7) {
                int b = input[position++] & 255;
                value |= (long) (b & 127) << shift;
                if (b < 128) return value;
            }
            throw new IOException("Invalid protobuf varint");
        }

        byte[] bytes() throws IOException {
            int start = advance(number());
            return java.util.Arrays.copyOfRange(input, start, position);
        }

        void skip(int wireType) throws IOException {
            switch (wireType) {
                case 0: number(); break;
                case 1: advance(8); break;
                case 2: advance(number()); break;
                case 5: advance(4); break;
                default: throw new IOException("Invalid protobuf wire type");
            }
        }

        private int advance(long size) throws IOException {
            if (size < 0 || size > input.length - position) throw new IOException("Truncated protobuf field");
            int start = position;
            position += (int) size;
            return start;
        }
    }
}
