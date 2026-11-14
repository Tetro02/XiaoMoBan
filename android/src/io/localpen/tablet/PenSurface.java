package io.localpen.tablet;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Build;
import android.os.SystemClock;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.View;

final class PenSurface extends View {
    interface Listener {
        void sample(PenLink.Sample sample);
        void areaChanged();
        default void touchpad(PenLink.Sample sample) {}
        default boolean navigate(float[] motion) { return false; }
        default void touchpadShortcut(String chord) {}
    }

    static final int BARREL_NATIVE = 0;
    static final int BARREL_ERASER = 1;
    static final int BARREL_OFF = 2;
    private static final int BARREL_BUTTONS = MotionEvent.BUTTON_STYLUS_PRIMARY
            | MotionEvent.BUTTON_STYLUS_SECONDARY | MotionEvent.BUTTON_SECONDARY;
    private static final long HEARTBEAT_MS = 200;
    private static final long HOVER_EXIT_GRACE_MS = 48;
    private static final long PRECONTACT_BUTTON_MS = 1800;

    private final ActiveArea area;
    private final Listener listener;
    private final TouchpadGesture touchpad;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF frame = new RectF();
    private final float[] dots = new float[28 * 18 * 2];
    private final int[] windowLocation = new int[2];
    private final ReportRate rate = new ReportRate();
    private final float density;
    private boolean editing;
    private boolean enabled = true;
    private boolean touchpadEnabled = true;
    private boolean down;
    private boolean hover;
    private boolean penHovering;
    private boolean quadrantGuides;
    private boolean samsungStroke;
    private boolean resize;
    private boolean drag;
    private boolean mouse;
    private boolean relativeMouse;
    private boolean sendTilt = true;
    private boolean rotateWithDirection;
    private boolean strokeEraser;
    private boolean hardwareEraser;
    private boolean physicalBarrel;
    private int barrelMode = BARREL_NATIVE;
    private int hoverPanDeadzonePx = 2;
    private int pointer = -1;
    private int penDevice = -1;
    private int buttonDevice = -1;
    private int lastStylusDevice = -1;
    private long lastStylusTime;
    private int pressedButtons;
    private long buttonTime;
    private boolean hoverExitPending;
    private float penHoverX;
    private float penHoverY;
    private float dragX;
    private float dragY;
    private PenLink.Sample latest;
    private PenLink.Sample pending;
    private long lastTransmission;
    private final Runnable flush = this::flushMotion;
    private final Runnable finishHover = () -> {
        int rememberedDevice = buttonDevice;
        int rememberedButtons = rememberedDevice == penDevice ? pressedButtons : 0;
        release();
        if (rememberedButtons != 0) {
            buttonDevice = rememberedDevice;
            pressedButtons = rememberedButtons;
            buttonTime = SystemClock.uptimeMillis();
        }
    };
    private final Runnable heartbeat = new Runnable() {
        @Override public void run() {
            if (down) {
                long elapsed = SystemClock.uptimeMillis() - lastTransmission;
                if (elapsed >= HEARTBEAT_MS) {
                    record(latest.continued(), nowNanos(), true);
                    elapsed = 0;
                }
                postDelayed(this, HEARTBEAT_MS - elapsed);
            }
        }
    };

    PenSurface(Context context, ActiveArea area, Listener listener) {
        super(context);
        this.area = area;
        this.listener = listener;
        density = getResources().getDisplayMetrics().density;
        touchpad = new TouchpadGesture(area, listener, density);
        setContentDescription("数位板输入区域");
    }

    void setEditing(boolean editing) {
        release();
        this.editing = editing;
        drag = false;
        invalidate();
    }

    void setCapture(boolean enabled) {
        if (!enabled) resetPointer();
        this.enabled = enabled;
    }

    void setQuadrantGuides(boolean visible) {
        if (quadrantGuides == visible) return;
        quadrantGuides = visible;
        invalidate();
    }

    int hoveredQuadrant() {
        if (!enabled || editing || !penHovering || down || pointer != -1 || !isShown() || !hasWindowFocus()
                || !area.contains(penHoverX, penHoverY)) return 0;
        boolean left = penHoverX < area.left + area.width / 2;
        boolean top = penHoverY < area.top + area.height / 2;
        if (top) return left ? 1 : 2;
        return left ? 3 : 4;
    }

    void setTouchpadEnabled(boolean enabled) {
        if (!enabled) touchpad.cancel();
        touchpadEnabled = enabled;
    }

    void setTouchpadGain(float gain) {
        touchpad.setGain(gain);
    }

    void setHoverPanDeadzone(int pixels) {
        hoverPanDeadzonePx = pixels;
    }

    void setPenOptions(int reportRate, boolean tilt, boolean rotation, int barrel) {
        resetPointer();
        rate.set(reportRate);
        sendTilt = tilt;
        rotateWithDirection = rotation;
        barrelMode = barrel;
    }

    void setOutput(boolean mouse, boolean relative) {
        if (this.mouse != mouse || relativeMouse != relative) resetPointer();
        this.mouse = mouse;
        relativeMouse = relative;
    }

    void release() {
        touchpad.cancel();
        if (down || hover) record(latest.exit(), nowNanos(), true);
        pending = null;
        removeCallbacks(flush);
        removeCallbacks(heartbeat);
        cancelHoverExit();
        down = false;
        hover = false;
        penHovering = false;
        samsungStroke = false;
        hardwareEraser = false;
        physicalBarrel = false;
        penDevice = -1;
        buttonDevice = -1;
        pressedButtons = 0;
        invalidate();
    }

    void resetPointer() {
        release();
        pointer = -1;
        lastStylusDevice = -1;
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        canvas.drawColor(TabletStyle.BACKGROUND);
        frame.set(area.left * getWidth(), area.top * getHeight(),
                (area.left + area.width) * getWidth(), (area.top + area.height) * getHeight());
        paint.setStyle(Paint.Style.FILL);
        paint.setColor(editing ? TabletStyle.BOARD_EDIT : TabletStyle.BOARD);
        canvas.drawRect(frame, paint);
        drawGuideDots(canvas);
        if (quadrantGuides && !editing) {
            paint.setColor(TabletStyle.GRID);
            paint.setStrokeWidth(density);
            canvas.drawLine(frame.centerX(), frame.top, frame.centerX(), frame.bottom, paint);
            canvas.drawLine(frame.left, frame.centerY(), frame.right, frame.centerY(), paint);
        }
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(density);
        paint.setColor(editing ? TabletStyle.ACCENT : TabletStyle.LINE);
        canvas.drawRect(frame.left + density, frame.top + density,
                frame.right - density, frame.bottom - density, paint);
        drawCorners(canvas);
        paint.setStyle(Paint.Style.FILL);
        if (editing) {
            drawHandle(canvas);
        } else if (down || hover) {
            drawPen(canvas);
        }
    }

    private void drawGuideDots(Canvas canvas) {
        int columns = Math.min(28, Math.max(1, (int) (frame.width() / (24 * density))));
        int rows = Math.min(18, Math.max(1, (int) (frame.height() / (24 * density))));
        int count = 0;
        for (int y = 1; y <= rows; y++) {
            float row = frame.top + frame.height() * y / (rows + 1);
            for (int x = 1; x <= columns; x++) {
                dots[count++] = frame.left + frame.width() * x / (columns + 1);
                dots[count++] = row;
            }
        }
        paint.setColor(TabletStyle.GRID);
        paint.setStrokeWidth((editing ? 2.1f : 1.5f) * density);
        paint.setStrokeCap(Paint.Cap.ROUND);
        canvas.drawPoints(dots, 0, count, paint);
        paint.setStrokeCap(Paint.Cap.BUTT);
    }

    private void drawCorners(Canvas canvas) {
        float length = Math.min(13 * density, Math.min(frame.width(), frame.height()) / 4);
        paint.setColor(editing ? TabletStyle.ACCENT : 0xff829fc9);
        paint.setStrokeWidth(1.5f * density);
        corner(canvas, frame.left + density, frame.top + density, length, length);
        corner(canvas, frame.right - density, frame.top + density, -length, length);
        corner(canvas, frame.left + density, frame.bottom - density, length, -length);
        corner(canvas, frame.right - density, frame.bottom - density, -length, -length);
    }

    private void corner(Canvas canvas, float x, float y, float horizontal, float vertical) {
        canvas.drawLine(x, y, x + horizontal, y, paint);
        canvas.drawLine(x, y, x, y + vertical, paint);
    }

    private void drawHandle(Canvas canvas) {
        float edge = Math.min(24 * density, Math.min(frame.width(), frame.height()) / 2);
        float right = frame.right - 3 * density;
        float bottom = frame.bottom - 3 * density;
        paint.setColor(TabletStyle.ACCENT);
        canvas.drawRoundRect(right - edge, bottom - edge, right, bottom, 5 * density, 5 * density, paint);
        paint.setColor(TabletStyle.INK);
        paint.setStrokeWidth(1.2f * density);
        float a = edge * 0.34f;
        float b = edge * 0.60f;
        canvas.drawLine(right - b, bottom - a, right - a, bottom - b, paint);
        canvas.drawLine(right - b - edge * 0.12f, bottom - a,
                right - a, bottom - b - edge * 0.12f, paint);
    }

    private void drawPen(Canvas canvas) {
        float x = frame.left + latest.x * frame.width();
        float y = frame.top + latest.y * frame.height();
        float radius = (down ? 4 + 7 * Math.min(1, latest.pressure) : 4) * density;
        paint.setColor(latest.eraser ? TabletStyle.ERASER_COLOR : TabletStyle.ACCENT);
        if (down) canvas.drawCircle(x, y, Math.max(density, radius - 2 * density), paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(density);
        canvas.drawCircle(x, y, radius + (down ? density : 0), paint);
        paint.setStyle(Paint.Style.FILL);
    }

    @Override protected void onSizeChanged(int width, int height, int oldWidth, int oldHeight) {
        super.onSizeChanged(width, height, oldWidth, oldHeight);
        resetPointer();
    }

    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (!focused) penHovering = false;
    }

    @Override protected void onDetachedFromWindow() {
        penHovering = false;
        super.onDetachedFromWindow();
    }

    @Override public boolean onHoverEvent(MotionEvent event) {
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_HOVER_EXIT && event.getDeviceId() == penDevice) penHovering = false;
        if (action != MotionEvent.ACTION_HOVER_EXIT && event.getPointerCount() > 0 && stylus(event, 0))
            touchpad.cancel();
        if (down && action == MotionEvent.ACTION_HOVER_ENTER
                && event.getDeviceId() == penDevice && event.getPointerCount() > 0
                && stylus(event, 0) && event.getPressure(0) == 0) {
            // Hover from this pen also ends a stroke if the terminal event was lost.
            resetPointer();
        }
        if (!enabled || editing || event.getPointerCount() == 0 || !stylus(event, 0) || pointer != -1) {
            return super.onHoverEvent(event);
        }
        if (action == MotionEvent.ACTION_HOVER_EXIT) {
            if (hover && penDevice == event.getDeviceId()) {
                // A hover exit also occurs on the way from hover to contact.
                physicalBarrel |= readBarrel(event);
                removeCallbacks(finishHover);
                hoverExitPending = true;
                postDelayed(finishHover, HOVER_EXIT_GRACE_MS);
            }
        } else if (action == MotionEvent.ACTION_HOVER_ENTER || action == MotionEvent.ACTION_HOVER_MOVE) {
            updateHover(event, 0);
        }
        return true;
    }

    @Override public boolean onGenericMotionEvent(MotionEvent event) {
        return handleBarrelEvent(event) || super.onGenericMotionEvent(event);
    }

    boolean handleBarrelEvent(MotionEvent event) {
        int action = event.getActionMasked();
        if (enabled && !editing && (action == MotionEvent.ACTION_BUTTON_PRESS
                || action == MotionEvent.ACTION_BUTTON_RELEASE) && (event.getActionButton() & BARREL_BUTTONS) != 0) {
            for (int i = 0; i < event.getPointerCount(); i++) {
                if (!stylus(event, i) || (down && event.getPointerId(i) != pointer)) continue;
                updateButton(event);
                return true;
            }
            if (event.isFromSource(InputDevice.SOURCE_STYLUS)
                    && (!down || event.getPointerCount() == 0 || event.findPointerIndex(pointer) >= 0)) {
                updateButton(event);
                return true;
            }
        }
        return false;
    }

    private void updateButton(MotionEvent event) {
        int device = event.getDeviceId();
        if ((down || hover) && penDevice != device) return;
        int changed = event.getActionButton() & BARREL_BUTTONS;
        if (event.getActionMasked() == MotionEvent.ACTION_BUTTON_PRESS) {
            if (buttonDevice != device) pressedButtons = 0;
            buttonDevice = device;
            pressedButtons |= changed;
            buttonTime = event.getEventTime();
        } else if (buttonDevice == device) {
            pressedButtons &= ~changed;
        }
        if (!(down || hover)) return;
        physicalBarrel = readBarrel(event) || explicitBarrel(event);
        boolean barrel = sendBarrel();
        boolean eraser = down ? strokeEraser : !mouse && hardwareEraser;
        if (barrel != latest.barrel || eraser != latest.eraser) {
            // A button action may carry no position, pressure, or orientation.
            record(latest.withButton(down, barrel, eraser), eventNanos(event, -1), true);
        }
    }

    private void updateHover(MotionEvent event, int index) {
        float px = fraction(event.getX(index), getWidth());
        float py = fraction(event.getY(index), getHeight());
        if (area.contains(px, py)) {
            if (hover && penDevice != event.getDeviceId()) release();
            cancelHoverExit();
            trackStylus(event);
            penDevice = event.getDeviceId();
            hardwareEraser = event.getToolType(index) == MotionEvent.TOOL_TYPE_ERASER;
            physicalBarrel = readBarrel(event) || explicitBarrel(event);
            PenLink.Sample sample = makeSample("hover", event, index, -1, area.screenX(px), area.screenY(py), 0,
                    !mouse && hardwareEraser);
            boolean edge = !hover || sample.barrel != latest.barrel || sample.eraser != latest.eraser;
            hover = true;
            penHovering = true;
            penHoverX = px;
            penHoverY = py;
            record(sample, eventNanos(event, -1), edge);
        } else if (hover) {
            release();
        }
    }

    @Override public boolean onTouchEvent(MotionEvent event) {
        if (!enabled) return true;
        if (editing) return edit(event);
        int action = event.getActionMasked();
        int index = event.getActionIndex();
        boolean penStarting = (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN)
                && stylus(event, index);
        if (penStarting) touchpad.cancel();
        else if (touchpadEnabled && !down && !hover && pointer == -1 && touchpad.handle(event, getWidth(), getHeight()))
            return true;
        handleContactEvent(event, action, index, false);
        return true;
    }

    boolean handleSamsungContactFromWindow(MotionEvent event) {
        if (!enabled || editing) return false;
        int rawAction = event.getActionMasked();
        int action;
        // Samsung's contact actions may reach the Activity as generic motion.
        switch (rawAction) {
            case 211: action = MotionEvent.ACTION_DOWN; break;
            case 212: action = MotionEvent.ACTION_UP; break;
            case 213: action = MotionEvent.ACTION_MOVE; break;
            case 214: action = MotionEvent.ACTION_CANCEL; break;
            default:
                if (!samsungStroke || (rawAction != MotionEvent.ACTION_MOVE && rawAction != MotionEvent.ACTION_UP
                        && rawAction != MotionEvent.ACTION_CANCEL)) return false;
                action = rawAction;
        }
        if (!event.isFromSource(InputDevice.SOURCE_STYLUS)) return false;
        boolean samePen = down && event.getDeviceId() == penDevice;
        if (action == MotionEvent.ACTION_CANCEL) {
            if (event.getDeviceId() == penDevice) penHovering = false;
            if (!samePen) return false;
            resetPointer();
            return true;
        }
        if ((down && !samePen) || (action == MotionEvent.ACTION_UP && !samePen)) return false;
        int index = samePen ? event.findPointerIndex(pointer) : event.getActionIndex();
        if (index < 0 || index >= event.getPointerCount()) return false;
        int tool = event.getToolType(index);
        if (tool != MotionEvent.TOOL_TYPE_STYLUS && tool != MotionEvent.TOOL_TYPE_ERASER) return false;
        getLocationInWindow(windowLocation);
        if (!samePen) {
            float x = event.getX(index) - windowLocation[0];
            float y = event.getY(index) - windowLocation[1];
            if (!isShown() || getWidth() <= 0 || getHeight() <= 0
                    || !area.contains(fraction(x, getWidth()), fraction(y, getHeight()))
                    || (action == MotionEvent.ACTION_MOVE && !(event.getPressure(index) > 0))) return false;
        }
        MotionEvent local = MotionEvent.obtain(event);
        try {
            // The copy shifts historical coordinates too, without altering the original event.
            local.offsetLocation(-windowLocation[0], -windowLocation[1]);
            handleContactEvent(local, action, index, true);
            samsungStroke = down;
        } finally {
            local.recycle();
        }
        return true;
    }

    private void handleContactEvent(MotionEvent event, int action, int index, boolean samsung) {
        boolean starts = (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN)
                && stylus(event, index);
        if (starts && action == MotionEvent.ACTION_DOWN && pointer != -1 && event.getDeviceId() == penDevice) {
            release();
            pointer = -1;
        }
        if (action == MotionEvent.ACTION_CANCEL) {
            if (event.getDeviceId() == penDevice) penHovering = false;
            if (pointer != -1 && event.getDeviceId() == penDevice) {
                release();
                pointer = -1;
            }
        } else if (starts && pointer == -1) {
            pointer = event.getPointerId(index);
            if (!samsung && action == MotionEvent.ACTION_DOWN) requestUnbufferedDispatch(event);
            contact(event, index, -1, "down", samsung);
            if (down) postDelayed(heartbeat, HEARTBEAT_MS);
        } else if (action == MotionEvent.ACTION_MOVE) {
            int current = event.findPointerIndex(pointer);
            if (down && event.getDeviceId() != penDevice) return;
            if (down && current < 0) {
                release();
                pointer = -1;
            }
            if (down) {
                for (int i = 0; i < event.getHistorySize() && down; i++) contact(event, current, i, "move", samsung);
                if (down) contact(event, current, -1, "move", samsung);
            } else {
                // A valid pressure sample can recover contact when the landing edge was missing.
                for (int i = 0; i < event.getPointerCount(); i++) {
                    float pressure = event.getPressure(i);
                    if (stylus(event, i) && Float.isFinite(pressure) && pressure > 0
                            && area.contains(fraction(event.getX(i), getWidth()), fraction(event.getY(i), getHeight()))) {
                        pointer = event.getPointerId(i);
                        contact(event, i, -1, "down", samsung);
                        if (down) postDelayed(heartbeat, HEARTBEAT_MS);
                        break;
                    }
                }
            }
        } else if ((action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_POINTER_UP)
                && event.getPointerId(index) == pointer && event.getDeviceId() == penDevice) {
            if (down) contact(event, index, -1, "up", samsung);
            pointer = -1;
        }
    }

    private void contact(MotionEvent event, int index, int history, String kind, boolean samsung) {
        touchpad.cancel();
        // The pen can enter real hover again only when Android delivers a hover event.
        penHovering = false;
        boolean historical = history >= 0;
        float px = fraction(historical ? event.getHistoricalX(index, history) : event.getX(index), getWidth());
        float py = fraction(historical ? event.getHistoricalY(index, history) : event.getY(index), getHeight());
        long time = eventNanos(event, history);
        float nx = area.screenX(px);
        float ny = area.screenY(py);
        if (!area.contains(px, py)) {
            if (down) {
                record(makeSample("up", event, index, history, nx, ny, 0, strokeEraser), time, true);
            }
            release();
            pointer = -1;
            return;
        }
        boolean landingWithBarrel = !down && hover && penDevice == event.getDeviceId() && physicalBarrel;
        cancelHoverExit();
        if (!historical) {
            trackStylus(event);
            boolean reported = readBarrel(event);
            physicalBarrel = reported || landingWithBarrel || ((!samsung || !down) && explicitBarrel(event));
            if (samsung && kind.equals("up") && !reported && buttonDevice == event.getDeviceId()) pressedButtons = 0;
        }
        if (kind.equals("up")) {
            record(makeSample(kind, event, index, history, nx, ny, 0, strokeEraser), time, true);
            down = false;
            hover = true;
            samsungStroke = false;
            removeCallbacks(heartbeat);
        } else {
            if (!down) {
                penDevice = event.getDeviceId();
                hardwareEraser = event.getToolType(index) == MotionEvent.TOOL_TYPE_ERASER;
                strokeEraser = !mouse && (hardwareEraser || barrelMode == BARREL_ERASER && physicalBarrel);
            }
            float p = historical ? event.getHistoricalPressure(index, history) : event.getPressure(index);
            PenLink.Sample sample = makeSample(kind, event, index, history, nx, ny,
                    Float.isFinite(p) ? Math.max(0, Math.min(1, p)) : 0, strokeEraser);
            boolean edge = !down || sample.barrel != latest.barrel;
            down = true;
            hover = false;
            record(sample, time, edge);
        }
    }

    private PenLink.Sample makeSample(String kind, MotionEvent event, int index, int history,
                                      float x, float y, float pressure, boolean eraser) {
        PenAxes axes = PenAxes.ZERO;
        if (!mouse && (sendTilt || rotateWithDirection)) {
            float tilt = axis(event, index, history, MotionEvent.AXIS_TILT);
            float direction = axis(event, index, history, MotionEvent.AXIS_ORIENTATION);
            if (tilt > 0 && (direction != 0 || hasOrientation(event))) {
                axes = PenAxes.fromAndroid(tilt, direction, sendTilt, rotateWithDirection);
            }
        }
        return new PenLink.Sample(kind, x, y, pressure, mouse, relativeMouse, axes, sendBarrel(), eraser,
                1f, hoverPanDeadzonePx);
    }

    private boolean sendBarrel() {
        return physicalBarrel && barrelMode != BARREL_OFF;
    }

    private static boolean readBarrel(MotionEvent event) {
        int state = event.getButtonState();
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_BUTTON_PRESS) state |= event.getActionButton();
        if (action == MotionEvent.ACTION_BUTTON_RELEASE) state &= ~event.getActionButton();
        return (state & BARREL_BUTTONS) != 0;
    }

    private boolean explicitBarrel(MotionEvent event) {
        return pressedButtons != 0 && buttonDevice == event.getDeviceId()
                && (penDevice == buttonDevice && (hover || down)
                || event.getEventTime() - buttonTime <= PRECONTACT_BUTTON_MS);
    }

    private void cancelHoverExit() {
        if (hoverExitPending) removeCallbacks(finishHover);
        hoverExitPending = false;
    }

    private void record(PenLink.Sample sample, long time, boolean immediate) {
        latest = sample;
        removeCallbacks(flush);
        if (rate.take(time, immediate)) {
            pending = null;
            transmit(sample);
        } else {
            pending = sample;
            postDelayed(flush, rate.delayMillis(nowNanos()));
        }
        invalidate();
    }

    private void flushMotion() {
        if (pending == null) return;
        long now = nowNanos();
        if (rate.take(now, false)) {
            transmit(pending);
            pending = null;
        } else {
            postDelayed(flush, rate.delayMillis(now));
        }
    }

    private void transmit(PenLink.Sample sample) {
        lastTransmission = SystemClock.uptimeMillis();
        listener.sample(sample);
    }

    private static float axis(MotionEvent event, int index, int history, int axis) {
        return history < 0 ? event.getAxisValue(axis, index) : event.getHistoricalAxisValue(axis, index, history);
    }

    private static boolean hasOrientation(MotionEvent event) {
        InputDevice device = event.getDevice();
        return device != null && (device.getMotionRange(MotionEvent.AXIS_ORIENTATION, event.getSource()) != null
                || device.getMotionRange(MotionEvent.AXIS_ORIENTATION) != null);
    }

    private static long eventNanos(MotionEvent event, int history) {
        if (Build.VERSION.SDK_INT >= 34) {
            return history < 0 ? event.getEventTimeNanos() : event.getHistoricalEventTimeNanos(history);
        }
        return (history < 0 ? event.getEventTime() : event.getHistoricalEventTime(history)) * 1_000_000L;
    }

    private static long nowNanos() {
        return SystemClock.uptimeMillis() * 1_000_000L;
    }

    private boolean edit(MotionEvent event) {
        int viewportWidth = Math.max(1, getWidth());
        int viewportHeight = Math.max(1, getHeight());
        float px = event.getX() / viewportWidth;
        float py = event.getY() / viewportHeight;
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) {
            float dx = (px - area.left - area.width) * getWidth();
            float dy = (py - area.top - area.height) * getHeight();
            resize = Math.abs(dx) <= 36 * density && Math.abs(dy) <= 36 * density;
            drag = resize || area.contains(px, py);
            dragX = px - (resize ? area.left + area.width : area.left);
            dragY = py - (resize ? area.top + area.height : area.top);
        } else if (action == MotionEvent.ACTION_MOVE && drag) {
            if (resize) area.resize(px - dragX, py - dragY, viewportWidth, viewportHeight);
            else area.move(px - dragX, py - dragY);
            listener.areaChanged();
            invalidate();
        } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL
                || action == MotionEvent.ACTION_POINTER_DOWN) {
            drag = false;
        }
        return true;
    }

    private boolean stylus(MotionEvent event, int index) {
        int tool = event.getToolType(index);
        if (tool == MotionEvent.TOOL_TYPE_STYLUS || tool == MotionEvent.TOOL_TYPE_ERASER) return true;
        if (tool != MotionEvent.TOOL_TYPE_UNKNOWN && tool != MotionEvent.TOOL_TYPE_MOUSE) return false;
        if (event.isFromSource(InputDevice.SOURCE_STYLUS)) return true;
        InputDevice device = event.getDevice();
        return event.getDeviceId() == lastStylusDevice && event.getEventTime() - lastStylusTime <= PRECONTACT_BUTTON_MS
                && device != null && device.supportsSource(InputDevice.SOURCE_STYLUS)
                && (readBarrel(event) || explicitBarrel(event) || physicalBarrel);
    }

    private void trackStylus(MotionEvent event) {
        lastStylusDevice = event.getDeviceId();
        lastStylusTime = event.getEventTime();
    }

    private static float fraction(float position, int size) {
        // Android may report subpixel positions in the final on-screen pixel.
        float normalized = position / Math.max(1, size - 1);
        return position >= 0 && position < size ? Math.min(1, normalized) : normalized;
    }
}
