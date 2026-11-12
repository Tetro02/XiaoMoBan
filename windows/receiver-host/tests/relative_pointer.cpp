// Exercise the Windows flags without generating any real mouse movement.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cassert>
#include <cstdint>
#include <vector>

static std::vector<INPUT> recorded;
static std::vector<POINTER_PEN_INFO> pen_frames;
static DWORD next_pen_error;
static unsigned devices_created;
static unsigned devices_destroyed;

static UINT capture_input(UINT count, LPINPUT input, int size) {
    assert(size == sizeof(INPUT));
    recorded.insert(recorded.end(), input, input + count);
    return count;
}

static int display_metric(int metric) {
    return metric == SM_CXVIRTUALSCREEN || metric == SM_CYVIRTUALSCREEN ? 1000 : 0;
}

static HSYNTHETICPOINTERDEVICE create_pen(POINTER_INPUT_TYPE type, ULONG count, POINTER_FEEDBACK_MODE) {
    assert(type == PT_PEN && count == 1);
    return reinterpret_cast<HSYNTHETICPOINTERDEVICE>(static_cast<uintptr_t>(++devices_created));
}

static void destroy_pen(HSYNTHETICPOINTERDEVICE device) {
    assert(device);
    ++devices_destroyed;
}

static BOOL capture_pen(HSYNTHETICPOINTERDEVICE device, const POINTER_TYPE_INFO* sample, UINT32 count) {
    assert(device && count == 1 && sample->type == PT_PEN);
    if (next_pen_error) {
        SetLastError(next_pen_error);
        next_pen_error = 0;
        return FALSE;
    }
    pen_frames.push_back(sample->penInfo);
    return TRUE;
}

#define SendInput capture_input
#define GetSystemMetrics display_metric
#define CreateSyntheticPointerDevice create_pen
#define DestroySyntheticPointerDevice destroy_pen
#define InjectSyntheticPointerInput capture_pen
#include "../native/tablet_pointer.cpp"
#undef InjectSyntheticPointerInput
#undef DestroySyntheticPointerDevice
#undef CreateSyntheticPointerDevice
#undef GetSystemMetrics
#undef SendInput

int main() {
    void* injector = tablet_pointer_open();
    assert(injector);
    TabletFrame frame{};
    frame.mode = RELATIVE_MOUSE;
    frame.area_width = frame.area_height = 1000;
    frame.flags = RANGE;
    frame.x = frame.y = 0.25f;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.empty());

    frame.x += 0.05f;
    frame.y -= 0.02f;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.size() == 1 && recorded.back().mi.dwFlags == MOUSEEVENTF_MOVE);
    assert(recorded.back().mi.dx == 50 && recorded.back().mi.dy == -20);

    frame.flags = RANGE | CONTACT | LEFT;
    frame.x += 0.1f;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.size() == 2 && recorded.back().mi.dwFlags == MOUSEEVENTF_LEFTDOWN);

    frame.y += 0.01f;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.size() == 3 && recorded.back().mi.dwFlags == MOUSEEVENTF_MOVE);
    assert(recorded.back().mi.dx == 0 && recorded.back().mi.dy == 10);

    frame.flags = RANGE;
    frame.y += 0.1f;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.size() == 4 && recorded.back().mi.dwFlags == MOUSEEVENTF_LEFTUP);

    frame.flags = 0;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.size() == 4);
    frame.x = 0.75f;
    frame.flags = RANGE | CONTACT | LEFT;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.size() == 5 && recorded.back().mi.dwFlags == MOUSEEVENTF_LEFTDOWN);

    tablet_pointer_close(injector);
    assert(recorded.size() == 6 && recorded.back().mi.dwFlags == MOUSEEVENTF_LEFTUP);

    recorded.clear();
    injector = tablet_pointer_open();
    frame.flags = RANGE | CONTACT | LEFT | RIGHT;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.size() == 2);
    assert(recorded[0].mi.dwFlags == MOUSEEVENTF_RIGHTDOWN);
    assert(recorded[1].mi.dwFlags == MOUSEEVENTF_LEFTDOWN);
    tablet_pointer_close(injector);

    recorded.clear();
    injector = tablet_pointer_open();
    TabletFrame pen{};
    pen.mode = PEN;
    pen.flags = RANGE | CONTACT;
    pen.pressure = 0.25f;
    assert(tablet_pointer_write(injector, &pen) == 0);
    const auto down = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON;
    assert(pen_frames.back().pointerInfo.pointerFlags == down && pen_frames.back().pressure == 256);

    // The next movement arrives after Windows has invalidated the synthetic pen.
    next_pen_error = ERROR_INVALID_HANDLE;
    assert(tablet_pointer_write(injector, &pen) == 0);
    assert(devices_created == 2 && devices_destroyed == 1);
    assert(pen_frames.back().pointerInfo.pointerFlags == down);

    next_pen_error = ERROR_NOT_READY;
    assert(tablet_pointer_write(injector, &pen) == 0);
    assert(pen_frames.back().pointerInfo.pointerFlags & POINTER_FLAG_UPDATE);
    assert(devices_created == 2 && devices_destroyed == 1);

    // A cancelled pen must not block a following mouse or pen mode.
    next_pen_error = ERROR_INVALID_PARAMETER;
    frame.flags = RANGE | CONTACT | LEFT;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(devices_destroyed == 2 && recorded.back().mi.dwFlags == MOUSEEVENTF_LEFTDOWN);
    assert(tablet_pointer_write(injector, &pen) == 0);
    assert(recorded.back().mi.dwFlags == MOUSEEVENTF_LEFTUP);
    assert(devices_created == 3 && pen_frames.back().pointerInfo.pointerFlags == down);
    tablet_pointer_close(injector);
    assert(devices_destroyed == 3);

    recorded.clear();
    injector = tablet_pointer_open();
    frame.flags = RANGE;
    frame.x = frame.y = 0.2f;
    frame.relative_gain = 2.25f;
    assert(tablet_pointer_write(injector, &frame) == 0 && recorded.empty());
    frame.x += 0.04f;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.back().mi.dx == 90 && recorded.back().mi.dy == 0);
    frame.relative_gain = 0.5f;
    frame.y += 0.06f;
    assert(tablet_pointer_write(injector, &frame) == 0);
    assert(recorded.back().mi.dx == 0 && recorded.back().mi.dy == 30);
    tablet_pointer_close(injector);
}
