package io.localpen.tablet;

import android.view.MotionEvent;

final class TouchpadGesture {
    private static final long TAP_MS = 250;
    private static final long NAVIGATION_INTERVAL_MS = 28;
    private static final long CHORD_MS = 70;
    private final ActiveArea area;
    private final PenSurface.Listener listener;
    private final float slop;
    private final float swipe;
    private int fingers;
    private boolean moving, dragging, dragCandidate, gestureSent, finished, mouseActive;
    private long beganAt, chordAt, lastTapAt = -1000, sentAt;
    private float startX, startY, startDistance, lastTapX, lastTapY;
    private float sentX, sentY, sentDistance, mouseX, mouseY;
    private float gain = 1f;
    private int width = 1, height = 1;

    TouchpadGesture(ActiveArea area, PenSurface.Listener listener, float density) {
        this.area = area;
        this.listener = listener;
        slop = 9 * density;
        swipe = 42 * density;
    }

    boolean handle(MotionEvent event, int width, int height) {
        int action = event.getActionMasked();
        int count = event.getPointerCount();
        if (action == MotionEvent.ACTION_CANCEL && fingers > 0) {
            cancel();
            return true;
        }
        if (count == 0 || event.getToolType(0) != MotionEvent.TOOL_TYPE_FINGER) return false;
        this.width = Math.max(width, 1);
        this.height = Math.max(height, 1);
        long time = event.getEventTime();
        if (action == MotionEvent.ACTION_DOWN) {
            stopMouse();
            fingers = 0;
            if (!inside(event.getX(), event.getY())) return true;
            fingers = 1;
            finished = moving = dragging = gestureSent = false;
            beganAt = time;
            startX = event.getX();
            startY = event.getY();
            dragCandidate = time - lastTapAt <= TAP_MS && distance(startX - lastTapX, startY - lastTapY) < slop * 3;
            mouseActive = true;
            mouse("hover", startX, startY);
            return true;
        }
        if (fingers == 0 || finished) return true;

        if (action == MotionEvent.ACTION_POINTER_DOWN) {
            if (moving || count > 4 || !fingerChord(event)) {
                cancel();
                return true;
            }
            stopMouse();
            fingers = count;
            lastTapAt = -1000;
            chordAt = sentAt = time;
            startX = sentX = centroid(event, true);
            startY = sentY = centroid(event, false);
            startDistance = sentDistance = count == 2 ? span(event) : 1;
        } else if (action == MotionEvent.ACTION_MOVE) {
            if (fingers == 1) moveMouse(event.getX(), event.getY());
            else if (count == fingers) navigate(event, false);
        } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_POINTER_UP) {
            if (fingers == 1 && action == MotionEvent.ACTION_UP) finishMouse(event);
            else if (fingers > 1 && count == fingers) finishChord(event);
            finished = true;
            stopMouse();
        }
        return true;
    }

    void cancel() {
        stopMouse();
        fingers = 0;
        lastTapAt = -1000;
    }

    void setGain(float value) {
        if (gain == value) return;
        cancel();
        gain = value;
    }

    private void moveMouse(float x, float y) {
        if (!moving && distance(x - startX, y - startY) >= slop) {
            moving = true;
            if (dragCandidate) {
                dragging = true;
                mouse("down", startX, startY);
            }
        }
        if (moving) mouse(dragging ? "move" : "hover", x, y);
    }

    private void finishMouse(MotionEvent event) {
        moveMouse(event.getX(), event.getY());
        if (!moving && event.getEventTime() - beganAt <= TAP_MS) {
            mouse("down", startX, startY);
            mouse("up", startX, startY);
            lastTapAt = event.getEventTime();
            lastTapX = startX;
            lastTapY = startY;
        } else {
            lastTapAt = -1000;
        }
    }

    private void stopMouse() {
        if (!mouseActive) return;
        if (dragging) mouse("up", mouseX, mouseY);
        mouse("exit", mouseX, mouseY);
        dragging = false;
        mouseActive = false;
    }

    private void mouse(String kind, float x, float y) {
        mouseX = x;
        mouseY = y;
        listener.touchpad(new PenLink.Sample(kind, area.screenX(x / width), area.screenY(y / height),
                kind.equals("down") || kind.equals("move") ? 1 : 0, true, true, PenAxes.ZERO, false, false, gain));
    }

    private void navigate(MotionEvent event, boolean ending) {
        float x = centroid(event, true);
        float y = centroid(event, false);
        float dx = x - startX;
        float dy = y - startY;
        long time = event.getEventTime();
        float span = fingers == 2 ? span(event) : 1;
        if (distance(dx, dy) >= slop || Math.abs(span - startDistance) >= slop) moving = true;
        if (fingers == 2 && moving && (ending || time - chordAt >= CHORD_MS && time - sentAt >= NAVIGATION_INTERVAL_MS)) {
            float panX = clamp((x - sentX) / (width * area.width), -1, 1);
            float panY = clamp((y - sentY) / (height * area.height), -1, 1);
            float scale = clamp(span / sentDistance, .5f, 2);
            if (Math.abs(panX) + Math.abs(panY) + Math.abs(scale - 1) > .0001f &&
                    listener.navigate(new float[] {panX, panY, scale, area.screenX(x / width), area.screenY(y / height), 0})) {
                sentX = x;
                sentY = y;
                sentDistance = span;
                sentAt = time;
            }
        } else if (fingers >= 3 && !gestureSent && Math.max(Math.abs(dx), Math.abs(dy)) >= swipe) {
            boolean horizontal = Math.abs(dx) >= Math.abs(dy);
            if (fingers == 3) listener.touchpadShortcut(horizontal ? (dx < 0 ? "Alt+Tab" : "Alt+Shift+Tab")
                    : dy < 0 ? "Win+Tab" : "Win+D");
            else if (horizontal) listener.touchpadShortcut(dx < 0 ? "Ctrl+Win+Right" : "Ctrl+Win+Left");
            gestureSent = true;
        }
    }

    private void finishChord(MotionEvent event) {
        navigate(event, true);
        if (moving || gestureSent || event.getEventTime() - beganAt > TAP_MS) return;
        if (fingers == 2) listener.navigate(new float[] {0, 0, 1,
                area.screenX(sentX / width), area.screenY(sentY / height), 1});
        else listener.touchpadShortcut(fingers == 3 ? "Win+S" : "Win+A");
    }

    private boolean fingerChord(MotionEvent event) {
        for (int i = 0; i < event.getPointerCount(); i++) {
            if (event.getToolType(i) != MotionEvent.TOOL_TYPE_FINGER || !inside(event.getX(i), event.getY(i))) return false;
        }
        return true;
    }

    private boolean inside(float x, float y) { return area.contains(x / width, y / height); }

    private static float centroid(MotionEvent event, boolean horizontal) {
        float sum = 0;
        for (int i = 0; i < event.getPointerCount(); i++) sum += horizontal ? event.getX(i) : event.getY(i);
        return sum / event.getPointerCount();
    }

    private static float span(MotionEvent event) {
        return Math.max(1, distance(event.getX(1) - event.getX(0), event.getY(1) - event.getY(0)));
    }

    private static float distance(float dx, float dy) { return (float) Math.hypot(dx, dy); }
    private static float clamp(float value, float min, float max) { return Math.max(min, Math.min(max, value)); }
}
