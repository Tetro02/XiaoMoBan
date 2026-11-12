package io.localpen.tablet;

final class PenAxes {
    static final PenAxes ZERO = new PenAxes(0, 0, 0);
    final int tiltX;
    final int tiltY;
    final int rotation;

    private PenAxes(int tiltX, int tiltY, int rotation) {
        this.tiltX = tiltX;
        this.tiltY = tiltY;
        this.rotation = rotation;
    }

    static PenAxes fromAndroid(float tilt, float direction, boolean sendTilt, boolean rotateWithDirection) {
        if (!Float.isFinite(tilt) || !Float.isFinite(direction) || tilt <= 0) return ZERO;
        double angle = Math.min(tilt, Math.PI / 2);
        double radius = Math.sin(angle);
        double z = Math.cos(angle);
        // Android orientation follows the tip, while Windows tilt follows the shaft.
        int x = sendTilt ? degrees(Math.atan2(-Math.sin(direction) * radius, z)) : 0;
        int y = sendTilt ? degrees(Math.atan2(Math.cos(direction) * radius, z)) : 0;
        int twist = rotateWithDirection && tilt >= Math.PI / 90
                ? Math.floorMod(degrees(direction), 360) : 0;
        return new PenAxes(x, y, twist);
    }

    private static int degrees(double radians) {
        return (int) Math.round(Math.toDegrees(radians));
    }
}
