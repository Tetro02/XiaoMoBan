package io.localpen.tablet;

import android.os.SystemClock;
import java.util.ArrayDeque;

final class PenSampleQueue {
    private static final int CAPACITY = 12;
    private static final int AUXILIARY_LIMIT = 8;

    static final class Pending {
        final PenLink.Sample sample;
        final byte[] shortcut;
        final float[] navigation;
        final long time;

        Pending(PenLink.Sample sample) {
            this.sample = sample;
            shortcut = null;
            navigation = null;
            time = SystemClock.uptimeMillis();
        }

        Pending(byte[] shortcut) {
            sample = null;
            this.shortcut = shortcut.clone();
            navigation = null;
            time = SystemClock.uptimeMillis();
        }

        Pending(float[] navigation) {
            sample = null;
            shortcut = null;
            this.navigation = navigation;
            time = SystemClock.uptimeMillis();
        }
    }

    private final ArrayDeque<Pending> pending = new ArrayDeque<>(CAPACITY);
    private boolean closed;

    synchronized boolean offer(PenLink.Sample sample) {
        if (closed) return false;
        Pending previous = pending.peekLast();
        if (previous != null && previous.sample != null && sameMotion(previous.sample, sample)) pending.removeLast();
        if (pending.size() >= CAPACITY) return false;
        pending.addLast(new Pending(sample));
        notifyAll();
        return true;
    }

    synchronized boolean offerShortcut(byte[] report) {
        if (closed || report == null || report.length != 8 || pending.size() >= AUXILIARY_LIMIT) return false;
        pending.addLast(new Pending(report));
        notifyAll();
        return true;
    }

    synchronized boolean offerNavigation(float[] navigation) {
        if (closed || navigation == null || navigation.length != 6) return false;
        for (float value : navigation) {
            if (!Float.isFinite(value)) return false;
        }
        float action = navigation[5];
        if (navigation[2] <= 0 || (action != 0 && action != 1)) return false;
        float[] report = navigation.clone();
        Pending previous = pending.peekLast();
        if (previous != null && previous.navigation != null && previous.navigation[5] == 0 && action == 0
                && (previous.navigation[2] == 1) == (report[2] == 1)) {
            report[0] += previous.navigation[0];
            report[1] += previous.navigation[1];
            report[2] *= previous.navigation[2];
            pending.removeLast();
        } else if (pending.size() >= AUXILIARY_LIMIT) return false;
        report[0] = Math.max(-1, Math.min(1, report[0]));
        report[1] = Math.max(-1, Math.min(1, report[1]));
        report[2] = Math.max(.25f, Math.min(4, report[2]));
        pending.addLast(new Pending(report));
        notifyAll();
        return true;
    }

    synchronized Pending take() throws InterruptedException {
        while (pending.isEmpty() && !closed) wait();
        return pending.pollFirst();
    }

    synchronized void close() {
        closed = true;
        pending.clear();
        notifyAll();
    }

    private static boolean sameMotion(PenLink.Sample before, PenLink.Sample after) {
        return (after.kind.equals("move") || after.kind.equals("hover")) && after.kind.equals(before.kind)
                && after.mouse == before.mouse && after.relative == before.relative
                && after.barrel == before.barrel && after.eraser == before.eraser
                && after.relativeGain == before.relativeGain;
    }
}
