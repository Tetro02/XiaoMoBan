#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <new>

struct TabletFrame {
    float x, y, pressure;
    int32_t tilt_x, tilt_y;
    uint32_t rotation, flags, mode;
    int32_t area_x, area_y, area_width, area_height;
    float relative_gain;
};

namespace {
constexpr uint32_t RANGE = 1u, CONTACT = 2u, BARREL = 4u, INVERTED = 8u, ERASER = 16u;
constexpr uint32_t LEFT = 32u, RIGHT = 64u, MIDDLE = 128u, MOUSE_BUTTONS = LEFT | RIGHT | MIDDLE;
constexpr uint32_t PEN = 0, MOUSE = 1, RELATIVE_MOUSE = 2;

struct Desktop {
    int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
};

struct Injector {
    HSYNTHETICPOINTERDEVICE pen = nullptr;
    DWORD previous_mode = PEN;
    uint32_t previous_flags = 0;
    POINT point{};
    bool relative_active = false;
    bool relative_contact = false;
    double relative_x = 0.0, relative_y = 0.0;
    double residual_x = 0.0, residual_y = 0.0;
};

DWORD last_error() {
    DWORD code = GetLastError();
    return code ? code : ERROR_GEN_FAILURE;
}

POINT target(const TabletFrame& frame, const Desktop& screen) {
    int x = frame.area_width > 0 ? frame.area_x : screen.x;
    int y = frame.area_height > 0 ? frame.area_y : screen.y;
    int w = frame.area_width > 0 ? frame.area_width : screen.w;
    int h = frame.area_height > 0 ? frame.area_height : screen.h;
    POINT point{
        x + static_cast<LONG>(std::lround(std::clamp(frame.x, 0.0f, 1.0f) * std::max(0, w - 1))),
        y + static_cast<LONG>(std::lround(std::clamp(frame.y, 0.0f, 1.0f) * std::max(0, h - 1))),
    };
    MONITORINFO monitor{sizeof(MONITORINFO)};
    if (GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor)) {
        point.x = std::clamp(point.x, monitor.rcMonitor.left, monitor.rcMonitor.right - 1);
        point.y = std::clamp(point.y, monitor.rcMonitor.top, monitor.rcMonitor.bottom - 1);
    }
    return point;
}

DWORD write_pen(Injector& state, const TabletFrame& frame, POINT point, const Desktop& screen) {
    bool in_range = (frame.flags & RANGE) != 0;
    bool contact = in_range && (frame.flags & CONTACT) != 0;
    bool was_contact = (state.previous_flags & CONTACT) != 0;
    if (!in_range && !(state.previous_flags & RANGE)) return 0;
    if (!state.pen) {
        state.pen = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_NONE);
        if (!state.pen) return last_error();
    }
    POINTER_TYPE_INFO event{};
    event.type = PT_PEN;
    auto& pen = event.penInfo;
    pen.pointerInfo.pointerType = PT_PEN;
    pen.pointerInfo.pointerId = 1;
    pen.pointerInfo.historyCount = 1;
    pen.pointerInfo.ptPixelLocation = {point.x - screen.x, point.y - screen.y};
    pen.pointerInfo.pointerFlags = contact != was_contact
        ? (contact ? POINTER_FLAG_DOWN : POINTER_FLAG_UP) : POINTER_FLAG_UPDATE;
    if (in_range) pen.pointerInfo.pointerFlags |= POINTER_FLAG_INRANGE;
    if (contact) {
        pen.pointerInfo.pointerFlags |= POINTER_FLAG_INCONTACT;
        pen.pointerInfo.pointerFlags |= frame.flags & BARREL ? POINTER_FLAG_SECONDBUTTON : POINTER_FLAG_FIRSTBUTTON;
    }
    if (frame.flags & BARREL) pen.penFlags |= PEN_FLAG_BARREL;
    if (frame.flags & INVERTED) pen.penFlags |= PEN_FLAG_INVERTED;
    if (frame.flags & ERASER) pen.penFlags |= PEN_FLAG_ERASER;
    pen.penMask = PEN_MASK_PRESSURE | PEN_MASK_ROTATION | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
    pen.pressure = contact ? static_cast<UINT32>(std::lround(std::clamp(frame.pressure, 0.0f, 1.0f) * 1024)) : 0;
    pen.tiltX = std::clamp(frame.tilt_x, -90, 90);
    pen.tiltY = std::clamp(frame.tilt_y, -90, 90);
    pen.rotation = frame.rotation % 360;
    SetLastError(0);
    return InjectSyntheticPointerInput(state.pen, &event, 1) ? 0 : last_error();
}

DWORD write_pen_recovering(Injector& state, const TabletFrame& frame, POINT point, const Desktop& screen) {
    DWORD code = write_pen(state, frame, point, screen);
    if (code == ERROR_NOT_READY) {
        // A burst after a network delay can deliver consecutive reports together.
        Sleep(1);
        code = write_pen(state, frame, point, screen);
    }
    if (!state.pen || (code != ERROR_INVALID_HANDLE && code != ERROR_INVALID_PARAMETER)) return code;
    // Windows may cancel the injection state after a display or desktop change.
    // Retry once as a fresh pen so the next valid contact becomes a Down.
    DestroySyntheticPointerDevice(state.pen);
    state.pen = nullptr;
    state.previous_flags = 0;
    return write_pen(state, frame, point, screen);
}

POINT relative_motion(Injector& state, const TabletFrame& frame, const Desktop& screen) {
    if (!(frame.flags & RANGE)) {
        state.relative_active = false;
        return {};
    }
    double x = std::clamp(frame.x, 0.0f, 1.0f);
    double y = std::clamp(frame.y, 0.0f, 1.0f);
    bool contact = (frame.flags & CONTACT) != 0;
    POINT delta{};
    if (state.relative_active && state.relative_contact == contact) {
        int w = frame.area_width > 0 ? frame.area_width : screen.w;
        int h = frame.area_height > 0 ? frame.area_height : screen.h;
        double gain = std::isfinite(frame.relative_gain) && frame.relative_gain > 0.0f
            ? std::clamp(frame.relative_gain, 0.5f, 2.5f) : 1.0;
        double dx = (x - state.relative_x) * w * gain + state.residual_x;
        double dy = (y - state.relative_y) * h * gain + state.residual_y;
        delta = {std::lround(dx), std::lround(dy)};
        state.residual_x = dx - delta.x;
        state.residual_y = dy - delta.y;
    } else {
        state.residual_x = state.residual_y = 0.0;
    }
    state.relative_x = x;
    state.relative_y = y;
    state.relative_active = true;
    state.relative_contact = contact;
    return delta;
}

DWORD write_mouse(Injector& state, uint32_t flags, POINT motion, DWORD motion_flags) {
    INPUT input{};
    input.type = INPUT_MOUSE;
    input.mi.dwFlags = motion_flags;
    input.mi.dx = motion.x;
    input.mi.dy = motion.y;
    uint32_t change = flags ^ state.previous_flags;
    if (change & LEFT) input.mi.dwFlags |= flags & LEFT ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
    if (change & RIGHT) input.mi.dwFlags |= flags & RIGHT ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
    if (change & MIDDLE) input.mi.dwFlags |= flags & MIDDLE ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
    if (!input.mi.dwFlags) return 0;
    SetLastError(0);
    if ((change & flags & (LEFT | RIGHT)) == (LEFT | RIGHT)) {
        // A held barrel must be visible to the canvas when the tip contact arrives.
        INPUT ordered[] = {input, input};
        ordered[0].mi.dwFlags = motion_flags | MOUSEEVENTF_RIGHTDOWN;
        ordered[1].mi.dwFlags &= ~(motion_flags | MOUSEEVENTF_RIGHTDOWN);
        UINT sent = SendInput(2, ordered, sizeof(input));
        if (sent == 2) return 0;
        if (sent == 1) state.previous_flags |= RIGHT;
        return last_error();
    }
    return SendInput(1, &input, sizeof(input)) == 1 ? 0 : last_error();
}

DWORD release(Injector& state, const Desktop& screen) {
    state.relative_active = false;
    if (state.previous_mode != PEN) {
        DWORD code = write_mouse(state, 0, {}, 0);
        if (code) return code;
    } else if (state.previous_flags & RANGE) {
        POINT point{
            std::clamp(state.point.x, static_cast<LONG>(screen.x),
                static_cast<LONG>(screen.x + std::max(0, screen.w - 1))),
            std::clamp(state.point.y, static_cast<LONG>(screen.y),
                static_cast<LONG>(screen.y + std::max(0, screen.h - 1))),
        };
        DWORD code = write_pen_recovering(state, TabletFrame{}, point, screen);
        if (code) return code;
    }
    state.previous_flags = 0;
    return 0;
}
} // namespace

extern "C" {
void* tablet_pointer_open() {
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    return new (std::nothrow) Injector{};
}

unsigned tablet_pointer_write(void* raw, const TabletFrame* frame) {
    if (!raw || !frame || frame->mode > RELATIVE_MOUSE) return ERROR_INVALID_PARAMETER;
    auto& state = *static_cast<Injector*>(raw);
    Desktop screen{};
    if (screen.w <= 0 || screen.h <= 0) return ERROR_INVALID_DATA;
    if (state.previous_mode != frame->mode) {
        DWORD code = release(state, screen);
        if (code) return code;
        state.previous_mode = frame->mode;
    }
    DWORD code;
    if (frame->mode == RELATIVE_MOUSE) {
        POINT delta = relative_motion(state, *frame, screen);
        // The relative SendInput path uses the user's Windows mouse acceleration.
        code = write_mouse(state, frame->flags & MOUSE_BUTTONS, delta,
            delta.x || delta.y ? MOUSEEVENTF_MOVE : 0);
    } else {
        POINT point = target(*frame, screen);
        if (frame->mode == PEN) {
            code = write_pen_recovering(state, *frame, point, screen);
            if (!code) state.point = point;
        } else {
            POINT absolute{
                std::lround((point.x - screen.x) * 65535.0 / std::max(1, screen.w - 1)),
                std::lround((point.y - screen.y) * 65535.0 / std::max(1, screen.h - 1)),
            };
            code = write_mouse(state, frame->flags & MOUSE_BUTTONS, absolute,
                MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE);
        }
    }
    if (code) return code;
    state.previous_flags = frame->mode == PEN ? frame->flags : frame->flags & MOUSE_BUTTONS;
    return 0;
}

unsigned tablet_pointer_release(void* raw) {
    return raw ? release(*static_cast<Injector*>(raw), Desktop{}) : ERROR_INVALID_PARAMETER;
}

void tablet_pointer_close(void* raw) {
    if (!raw) return;
    auto* state = static_cast<Injector*>(raw);
    release(*state, Desktop{});
    if (state->pen) DestroySyntheticPointerDevice(state->pen);
    delete state;
}
}
