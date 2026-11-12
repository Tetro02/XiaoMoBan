package io.localpen.tablet;

final class ReportRate {
    private static final long MILLIS = 1_000_000L;
    private static final long ROUNDING = MILLIS - 1;
    private long period = 1_000_000_000L / 120;
    private long due;
    private long previous = -1;

    void set(int hz) {
        period = 1_000_000_000L / hz;
        previous = -1;
        due = 0;
    }

    boolean take(long time, boolean immediate) {
        if (!immediate && (time <= previous || time + ROUNDING < due)) return false;
        time = Math.max(time, previous);
        if (immediate || previous < 0 || time >= due + period) due = time + period;
        else due += period;
        previous = time;
        return true;
    }

    long delayMillis(long time) {
        return Math.max(1, (due - time + MILLIS - 1) / MILLIS);
    }
}
