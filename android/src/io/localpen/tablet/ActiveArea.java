package io.localpen.tablet;

final class ActiveArea {
    static final float MIN_SIZE = 0.08f;
    float left;
    float top;
    float width;
    float height;
    private float aspect;

    ActiveArea(float left, float top, float width, float height) {
        this.width = clamp(width, MIN_SIZE, 1);
        this.height = clamp(height, MIN_SIZE, 1);
        move(left, top);
    }

    void move(float x, float y) {
        left = clamp(x, 0, 1 - width);
        top = clamp(y, 0, 1 - height);
    }

    void resize(float right, float bottom, int viewportWidth, int viewportHeight) {
        if (aspect == 0) {
            width = clamp(right - left, MIN_SIZE, 1 - left);
            height = clamp(bottom - top, MIN_SIZE, 1 - top);
            return;
        }
        // Project the pointer onto the fixed resize diagonal in viewport pixels.
        float slope = (float) viewportHeight / viewportWidth / aspect;
        float verticalWeight = slope * slope;
        float requested = ((right - left) + (bottom - top) * aspect * verticalWeight)
                / (1 + verticalWeight);
        float maximum = Math.min(1 - left, (1 - top) * aspect);
        float minimum = Math.min(maximum, Math.max(MIN_SIZE, MIN_SIZE * aspect));
        width = clamp(requested, minimum, maximum);
        height = width / aspect;
    }

    boolean setAspect(float value) {
        if (!Float.isFinite(value) || value < 0.01f || value > 100f) value = 0;
        if (Math.abs(aspect - value) < 0.0001f) return false;
        aspect = value;
        if (aspect == 0) return true;
        float centerX = left + width / 2;
        float centerY = top + height / 2;
        float maximum = Math.min(1, aspect);
        float minimum = Math.min(maximum, Math.max(MIN_SIZE, MIN_SIZE * aspect));
        width = clamp(Math.min(width, height * aspect), minimum, maximum);
        height = width / aspect;
        move(centerX - width / 2, centerY - height / 2);
        return true;
    }

    boolean contains(float x, float y) {
        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    float screenX(float x) {
        return clamp((x - left) / width, 0, 1);
    }

    float screenY(float y) {
        return clamp((y - top) / height, 0, 1);
    }

    private static float clamp(float value, float min, float max) {
        return Float.isNaN(value) ? min : Math.min(max, Math.max(min, value));
    }
}
