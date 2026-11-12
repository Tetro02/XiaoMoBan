use std::collections::{HashMap, VecDeque};
use std::ffi::c_void;
use std::sync::{mpsc, Arc, Mutex};
use std::time::{Duration, Instant};

use pennote_bridge_receiver::{CanvasCommand, ClientInfo, InputMode, PointerFrame, ReceiverEvent, Shortcut};
use serde::Serialize;
use windows_sys::Win32::Foundation::{HWND, POINT};
use windows_sys::Win32::Graphics::Gdi::{GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTONEAREST};
use windows_sys::Win32::System::SystemInformation::GetTickCount;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, SendInput, INPUT,
    INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_EXTENDEDKEY, KEYEVENTF_KEYUP,
    VK_CONTROL, VK_LCONTROL, VK_LMENU, VK_LSHIFT, VK_LWIN, VK_MENU, VK_RWIN, VK_SHIFT};
use windows_sys::Win32::UI::WindowsAndMessaging::{GetPhysicalCursorPos, GetSystemMetrics, GetWindowThreadProcessId,
    IsChild, WindowFromPhysicalPoint, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN};

use crate::{BarrelGestures, Config, SideKeyAction};

#[derive(Clone, Default, Serialize)]
pub(crate) struct Detected {
    pressure: bool,
    tilt: bool,
    rotation: bool,
    hover: bool,
    barrel: bool,
    eraser: bool,
}

#[derive(Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Diagnostics {
    sample: Option<PointerFrame>,
    rate: usize,
    received: u64,
    detected: Detected,
    injection_error: Option<u32>,
    #[serde(skip)]
    times: VecDeque<Instant>,
}

impl Diagnostics {
    fn record(&mut self, frame: &PointerFrame) {
        let now = Instant::now();
        self.times.push_back(now);
        self.update_rate(now);
        self.received = self.received.saturating_add(1);
        self.detected.pressure |= frame.pressure > 0.0;
        self.detected.tilt |= frame.tilt_x != 0 || frame.tilt_y != 0;
        self.detected.rotation |= frame.rotation != 0;
        self.detected.hover |= frame.in_range && !frame.contact;
        self.detected.barrel |= frame.barrel;
        self.detected.eraser |= frame.eraser || frame.inverted;
        self.sample = Some(frame.clone());
    }

    pub(crate) fn update_rate(&mut self, now: Instant) {
        while self.times.front().is_some_and(|time| now.duration_since(*time) >= Duration::from_secs(1)) {
            self.times.pop_front();
        }
        self.rate = self.times.len();
    }
}

pub(crate) struct Data {
    pub(crate) config: Config,
    pub(crate) clients: HashMap<String, ClientInfo>,
    pub(crate) diagnostics: Diagnostics,
    pub(crate) logs: VecDeque<String>,
    canvas_navigation: VecDeque<NavigationEvent>,
    tip_intents: VecDeque<TipIntent>,
}

struct NavigationEvent {
    client: String,
    command: CanvasCommand,
    capture: Option<Arc<[isize]>>,
}

struct TipIntent {
    client: String,
    window: isize,
    point: POINT,
    tick: u32,
    created: Instant,
    erase: bool,
    capture: Arc<[isize]>,
}

impl Data {
    fn new() -> Self {
        Self { config: Config::default(), clients: HashMap::new(), diagnostics: Diagnostics::default(),
            logs: VecDeque::new(), canvas_navigation: VecDeque::new(), tip_intents: VecDeque::new() }
    }

    fn log(&mut self, message: String) {
        if self.logs.len() == 80 {
            self.logs.pop_front();
        }
        self.logs.push_back(message);
    }

    fn queue_navigation(&mut self, client: &str, command: CanvasCommand, capture: Option<Arc<[isize]>>) {
        if let (Some(scope), Some(previous)) = (capture.as_ref(), self.canvas_navigation.back_mut()) {
            if previous.client == client && previous.capture.as_ref().is_some_and(|old| Arc::ptr_eq(old, scope)) {
                previous.command.pan_x += command.pan_x;
                previous.command.pan_y += command.pan_y;
                return;
            }
        }
        if self.canvas_navigation.len() >= 64 {
            self.canvas_navigation.pop_front();
        }
        self.canvas_navigation.push_back(NavigationEvent { client: client.into(), command, capture });
    }

    fn take_eraser_intent(&mut self, capture: &Arc<[isize]>, window: isize, point: POINT, tick: u32) -> bool {
        self.tip_intents.retain(|intent| {
            Arc::ptr_eq(&intent.capture, capture) && intent.created.elapsed() <= Duration::from_millis(280)
        });
        let matched = self.tip_intents.iter().enumerate().filter_map(|(index, intent)| {
            let x = intent.point.x.abs_diff(point.x);
            let y = intent.point.y.abs_diff(point.y);
            let time = if tick == 0 { 0 } else { (tick.wrapping_sub(intent.tick) as i32).unsigned_abs() };
            (intent.window == window && x <= 9 && y <= 9 && time <= 48).then(|| (time, x + y, index))
        }).min();
        matched.and_then(|(_, _, index)| self.tip_intents.remove(index)).is_some_and(|intent| intent.erase)
    }
}

enum OutputEvent {
    Receiver(ReceiverEvent),
    Configure(Config, mpsc::Sender<()>),
    Release(mpsc::Sender<()>),
    Shutdown,
}

#[derive(Clone)]
pub(crate) struct Signals {
    pub(crate) data: Arc<Mutex<Data>>,
    capture_windows: Arc<Mutex<Arc<[isize]>>>,
    tx: mpsc::SyncSender<OutputEvent>,
}

impl Signals {
    pub(crate) fn start(gestures: Arc<BarrelGestures>) -> Result<(Self, std::thread::JoinHandle<()>), String> {
        let (tx, rx) = mpsc::sync_channel(512);
        let data = Arc::new(Mutex::new(Data::new()));
        let output_data = data.clone();
        let capture_windows = Arc::new(Mutex::new(Arc::from([])));
        let output_windows = capture_windows.clone();
        let worker = std::thread::Builder::new()
            .name("pennote-input".into())
            .spawn(move || run_output(output_data, output_windows, gestures, rx))
            .map_err(|error| error.to_string())?;
        Ok((Self { data, capture_windows, tx }, worker))
    }

    pub(crate) fn set_capture_windows(&self, windows: &[isize]) {
        let mut requested: Vec<_> = windows.iter().copied().filter(|&window| window != 0).collect();
        requested.sort_unstable();
        requested.dedup();
        let mut stored = self.capture_windows.lock().unwrap();
        if stored.as_ref() != requested {
            *stored = Arc::from(requested);
        }
    }

    pub(crate) fn submit(&self, event: ReceiverEvent) {
        // Backpressure preserves transitions between tip, hover, and buttons.
        let _ = self.tx.send(OutputEvent::Receiver(event));
    }

    pub(crate) fn log(&self, message: impl Into<String>) {
        self.data.lock().unwrap().log(message.into());
    }

    pub(super) fn bluetooth_capabilities(&self) -> (bool, bool, u32, u32) {
        let data = self.data.lock().unwrap();
        (true, true, data.config.width as u32, data.config.height as u32)
    }

    pub(super) fn take_canvas_navigation(&self) -> Option<CanvasCommand> {
        let capture = self.capture_windows.lock().unwrap().clone();
        let mut data = self.data.lock().unwrap();
        while let Some(event) = data.canvas_navigation.pop_front() {
            if event.capture.as_ref().is_none_or(|scope| Arc::ptr_eq(scope, &capture)) {
                return Some(event.command);
            }
        }
        None
    }

    pub(super) fn take_eraser_intent(&self, window: isize, x: i32, y: i32, tick: u32) -> bool {
        let capture = self.capture_windows.lock().unwrap().clone();
        self.data.lock().unwrap().take_eraser_intent(&capture, window, POINT { x, y }, tick)
    }

    pub(crate) fn configure(&self, config: Config) -> Result<(), String> {
        self.sync(|done| OutputEvent::Configure(config, done))
    }

    pub(crate) fn release(&self) -> Result<(), String> {
        self.sync(OutputEvent::Release)
    }

    pub(crate) fn shutdown(&self) {
        let _ = self.tx.send(OutputEvent::Shutdown);
    }

    fn sync(&self, event: impl FnOnce(mpsc::Sender<()>) -> OutputEvent) -> Result<(), String> {
        let (done, response) = mpsc::channel();
        self.tx.send(event(done)).map_err(|_| "Windows 输入线程已关闭".to_owned())?;
        response.recv().map_err(|_| "Windows 输入线程已关闭".to_owned())
    }
}

#[repr(C)]
struct NativeFrame {
    x: f32,
    y: f32,
    pressure: f32,
    tilt_x: i32,
    tilt_y: i32,
    rotation: u32,
    flags: u32,
    mode: u32,
    area_x: i32,
    area_y: i32,
    area_width: i32,
    area_height: i32,
    relative_gain: f32,
}

const FLAG_BARREL: u32 = 1 << 2;
const FLAG_INVERTED: u32 = 1 << 3;
const FLAG_ERASER: u32 = 1 << 4;
const FLAG_RIGHT: u32 = 1 << 6;

extern "C" {
    fn tablet_pointer_open() -> *mut c_void;
    fn tablet_pointer_write(handle: *mut c_void, frame: *const NativeFrame) -> u32;
    fn tablet_pointer_release(handle: *mut c_void) -> u32;
    fn tablet_pointer_close(handle: *mut c_void);
}

impl NativeFrame {
    fn from_frame(frame: &PointerFrame, config: &Config) -> Self {
        let native_side_key = config.side_key_action == SideKeyAction::Native;
        let states = [frame.in_range, frame.contact, frame.barrel && native_side_key, frame.inverted, frame.eraser,
            frame.left_button, frame.right_button && native_side_key, frame.middle_button];
        let flags = states.iter().enumerate().fold(0, |flags, (bit, state)| flags | (u32::from(*state) << bit));
        Self {
            x: frame.x, y: frame.y,
            pressure: (frame.pressure * config.pressure_gamma).min(1.0),
            tilt_x: frame.tilt_x, tilt_y: frame.tilt_y, rotation: frame.rotation, flags,
            mode: match frame.mode {
                InputMode::Pen => 0,
                InputMode::AbsoluteMouse => 1,
                InputMode::RelativeMouse => 2,
            },
            area_x: config.left, area_y: config.top,
            area_width: if config.map_all { 0 } else { config.width },
            area_height: if config.map_all { 0 } else { config.height },
            relative_gain: frame.relative_gain,
        }
    }

    fn annotation_controls(&mut self, frame: &PointerFrame, erase: bool) {
        if frame.mode != InputMode::Pen {
            if frame.right_button {
                self.flags |= FLAG_RIGHT;
            }
            return;
        }
        self.flags &= !FLAG_BARREL;
        if frame.contact {
            // Contact is an explicit state. Very light touches can quantize to
            // zero in the Windows pressure field despite a valid tip-down.
            self.pressure = self.pressure.max(1.0 / 1024.0);
        }
        if erase && frame.in_range {
            self.flags |= FLAG_INVERTED;
            if frame.contact {
                self.flags |= FLAG_ERASER;
            }
        }
    }

    fn annotation_at(&self, mode: InputMode, windows: &[isize]) -> Option<(isize, POINT)> {
        if windows.is_empty() {
            return None;
        }
        let point = if mode == InputMode::RelativeMouse {
            let mut cursor = POINT::default();
            if unsafe { GetPhysicalCursorPos(&mut cursor) } == 0 {
                return None;
            }
            cursor
        } else {
            self.absolute_target()
        };
        let hit = unsafe { WindowFromPhysicalPoint(point) };
        let mut process = 0;
        if hit.is_null() || unsafe { GetWindowThreadProcessId(hit, &mut process) } == 0 || process != std::process::id() {
            return None;
        }
        windows.iter().copied().find(|&window| {
            let window = window as HWND;
            hit == window || unsafe { IsChild(window, hit) } != 0
        }).map(|window| (window, point))
    }

    fn absolute_target(&self) -> POINT {
        let (left, top, width, height) = if self.area_width > 0 && self.area_height > 0 {
            (self.area_x, self.area_y, self.area_width, self.area_height)
        } else {
            unsafe {
                (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                    GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN))
            }
        };
        let mut point = POINT {
            x: left + (self.x.clamp(0.0, 1.0) * (width - 1).max(0) as f32).round() as i32,
            y: top + (self.y.clamp(0.0, 1.0) * (height - 1).max(0) as f32).round() as i32,
        };
        let mut info = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let monitor = unsafe { MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) };
        if unsafe { GetMonitorInfoW(monitor, &mut info) } != 0 {
            point.x = point.x.clamp(info.rcMonitor.left, info.rcMonitor.right - 1);
            point.y = point.y.clamp(info.rcMonitor.top, info.rcMonitor.bottom - 1);
        }
        point
    }
}

#[derive(Default)]
struct AnnotationStroke {
    windows: Arc<[isize]>,
    mode: Option<InputMode>,
    contact: bool,
    captured: Option<isize>,
    erase: bool,
    hover_barrel: Option<HoverBarrel>,
    hover_pan: HoverPan,
}

struct HoverBarrel {
    window: isize,
    position: (f32, f32),
    exit_at: Option<Instant>,
}

impl HoverBarrel {
    fn can_erase(&self, window: Option<isize>, frame: &PointerFrame, now: Instant) -> bool {
        if window != Some(self.window) {
            return false;
        }
        self.exit_at.is_none_or(|exit| {
            now.duration_since(exit) <= Duration::from_millis(160)
                && (frame.x - self.position.0).abs() <= 0.03
                && (frame.y - self.position.1).abs() <= 0.03
        })
    }
}

#[derive(Default)]
struct HoverPan {
    anchor: Option<(isize, POINT)>,
    deadzone: Option<u32>,
    moving: bool,
}

impl HoverPan {
    fn update(&mut self, target: Option<(isize, POINT)>, deadzone: Option<u32>) -> Option<CanvasCommand> {
        let Some((window, point)) = target else {
            self.anchor = None;
            self.moving = false;
            return None;
        };
        let (previous_window, previous_point) = self.anchor.unwrap_or((0, point));
        if self.deadzone != deadzone || previous_window != window {
            self.anchor = target;
            self.deadzone = deadzone;
            self.moving = deadzone.unwrap_or(0) == 0;
            return None;
        }
        let dx = point.x.saturating_sub(previous_point.x);
        let dy = point.y.saturating_sub(previous_point.y);
        if let Some(radius) = deadzone.filter(|_| !self.moving) {
            if (f64::from(dx)).hypot(f64::from(dy)) >= f64::from(radius) {
                self.anchor = target;
                self.moving = true;
            }
            return None;
        }
        if dx.unsigned_abs().saturating_add(dy.unsigned_abs()) < if deadzone.is_some() { 1 } else { 2 } {
            return None;
        }
        self.anchor = target;
        // Local action 2 carries desktop pixels. External clients can only
        // send actions 0 and 1 through the validated canvas commands.
        Some(CanvasCommand { pan_x: dx as f32, pan_y: dy as f32, scale: 1.0, focus_x: 0.0, focus_y: 0.0, action: 2 })
    }
}

impl AnnotationStroke {
    fn tip_down(frame: &PointerFrame) -> bool {
        if frame.mode == InputMode::Pen { frame.contact } else { frame.left_button }
    }

    fn lost_capture(&self, windows: &Arc<[isize]>, frame: &PointerFrame) -> bool {
        self.contact && self.mode == Some(frame.mode) && !Arc::ptr_eq(windows, &self.windows)
            && self.captured.is_some_and(|window| !windows.contains(&window))
    }

    fn captures(&mut self, windows: Arc<[isize]>, frame: &PointerFrame, hit: impl FnOnce(&[isize]) -> Option<isize>) -> bool {
        if self.mode != Some(frame.mode) {
            *self = Self { mode: Some(frame.mode), ..Self::default() };
        }
        if !Arc::ptr_eq(&windows, &self.windows) {
            self.windows = windows;
            self.hover_pan = HoverPan::default();
            if self.captured.is_some_and(|window| !self.windows.contains(&window)) {
                self.captured = None;
            }
            if self.hover_barrel.as_ref().is_some_and(|held| !self.windows.contains(&held.window)) {
                self.hover_barrel = None;
            }
        }
        if self.windows.is_empty() {
            return false;
        }
        let in_range = frame.mode == InputMode::AbsoluteMouse || frame.in_range;
        let contact = Self::tip_down(frame);
        // A down stroke and its eventual up retain their original scope.
        let now = Instant::now();
        if !self.contact {
            self.captured = if in_range { hit(&self.windows) } else { None };
            self.erase = frame.barrel || (contact && self.hover_barrel.as_ref()
                .is_some_and(|held| held.can_erase(self.captured, frame, now)));
        }
        if frame.mode == InputMode::Pen && !contact && !in_range {
            if let Some(held) = self.hover_barrel.as_mut() {
                held.exit_at.get_or_insert(now);
            }
        } else {
            self.hover_barrel = self.captured.filter(|_| frame.mode == InputMode::Pen && !contact && frame.barrel)
                .map(|window| HoverBarrel { window, position: (frame.x, frame.y), exit_at: None });
        }
        self.contact = contact;
        self.captured.is_some()
    }
}

pub(crate) fn valid_shortcut_key(key: u16) -> bool {
    matches!(key,
        0x08..=0x09 | 0x0D | 0x13..=0x14 | 0x1B | 0x20..=0x28 | 0x2C..=0x2E |
        0x30..=0x39 | 0x41..=0x5A | 0x60..=0x87 | 0x90..=0x91 | 0xBA..=0xC0 | 0xDB..=0xDF | 0xE2)
}

fn shortcut_virtual_key(shortcut: Shortcut) -> u16 {
    let usage = u16::from(shortcut.usage());
    match usage {
        4..=29 => b'A' as u16 + usage - 4,
        30..=38 => b'1' as u16 + usage - 30,
        39 => b'0' as u16,
        40 => 0x0d,
        41 => 0x1b,
        43 => 0x09,
        44 => 0x20,
        58..=69 => 0x70 + usage - 58,
        79 => 0x27,
        80 => 0x25,
        81 => 0x28,
        82 => 0x26,
        _ => unreachable!("validated HID keyboard usage"),
    }
}

fn key_event(key: u16, release: bool) -> INPUT {
    let extended = matches!(key, 0x21..=0x28 | 0x2C..=0x2E | 0x5B..=0x5C | 0x6F | 0x90 | 0xA3 | 0xA5);
    let mut flags = if extended { KEYEVENTF_EXTENDEDKEY } else { 0 };
    if release {
        flags |= KEYEVENTF_KEYUP;
    }
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: key, dwFlags: flags, ..Default::default() } },
    }
}

fn key_down(key: u16) -> bool {
    unsafe { GetAsyncKeyState(i32::from(key)) < 0 }
}

fn send_shortcut(key: u16, modifiers: u8) -> Result<(), u32> {
    if key_down(key) {
        return Ok(());
    }
    let mut keys = Vec::with_capacity(5);
    for (bit, check, key) in [(1, VK_CONTROL, VK_LCONTROL), (2, VK_SHIFT, VK_LSHIFT),
        (4, VK_MENU, VK_LMENU), (8, VK_LWIN, VK_LWIN)]
    {
        if modifiers & bit != 0 && !key_down(check) && !(check == VK_LWIN && key_down(VK_RWIN)) {
            keys.push(key);
        }
    }
    keys.push(key);
    let mut input: Vec<_> = keys.iter().map(|&key| key_event(key, false)).collect();
    input.extend(keys.iter().rev().map(|&key| key_event(key, true)));
    let size = std::mem::size_of::<INPUT>() as i32;
    let sent = unsafe { SendInput(input.len() as u32, input.as_ptr(), size) } as usize;
    if sent == input.len() {
        return Ok(());
    }
    let error = std::io::Error::last_os_error().raw_os_error().unwrap_or(31) as u32;
    // If Windows accepted only the start of the chord, release any keys we pressed.
    let release: Vec<_> = keys.iter().enumerate().rev()
        .filter(|&(index, _)| index < sent && input.len() - 1 - index >= sent)
        .map(|(_, &key)| key_event(key, true)).collect();
    if !release.is_empty() {
        unsafe { SendInput(release.len() as u32, release.as_ptr(), size) };
    }
    Err(if error == 0 { 31 } else { error })
}

fn run_output(data: Arc<Mutex<Data>>, capture_windows: Arc<Mutex<Arc<[isize]>>>, gestures: Arc<BarrelGestures>,
    rx: mpsc::Receiver<OutputEvent>,
) {
    let injector = unsafe { tablet_pointer_open() };
    let release = || {
        if !injector.is_null() {
            unsafe { tablet_pointer_release(injector) };
        }
    };
    let mut active = None::<String>;
    let mut side_key_down = HashMap::<String, bool>::new();
    let mut annotation = AnnotationStroke::default();
    let mut retiring_stroke = None::<InputMode>;
    while let Ok(event) = rx.recv() {
        match event {
            OutputEvent::Shutdown => break,
            OutputEvent::Configure(config, done) => {
                release();
                active = None;
                retiring_stroke = None;
                let mut data = data.lock().unwrap();
                data.config = config;
                data.tip_intents.clear();
                let _ = done.send(());
            }
            OutputEvent::Release(done) => {
                release();
                active = None;
                retiring_stroke = None;
                gestures.reset();
                let mut data = data.lock().unwrap();
                data.canvas_navigation.clear();
                data.tip_intents.clear();
                let _ = done.send(());
            }
            OutputEvent::Receiver(ReceiverEvent::Connected { client, .. }) => {
                let mut data = data.lock().unwrap();
                data.log(format!("已连接 · {}", client.display_name));
                data.clients.insert(client.connection_id.clone(), client);
            }
            OutputEvent::Receiver(ReceiverEvent::Disconnected { client, .. }) => {
                gestures.disconnect(&client);
                side_key_down.remove(&client.connection_id);
                if active.as_deref() == Some(client.connection_id.as_str()) {
                    release();
                    active = None;
                    retiring_stroke = None;
                }
                let mut data = data.lock().unwrap();
                data.canvas_navigation.retain(|event| event.client != client.connection_id);
                data.tip_intents.retain(|intent| intent.client != client.connection_id);
                data.clients.remove(&client.connection_id);
                data.log(format!("已断开 · {}", client.display_name));
            }
            OutputEvent::Receiver(ReceiverEvent::Shortcut { client, shortcut, .. }) => {
                let enabled = {
                    let data = data.lock().unwrap();
                    data.config.injection_enabled && data.clients.contains_key(&client.connection_id)
                };
                if enabled {
                    if let Err(error) = send_shortcut(shortcut_virtual_key(shortcut), shortcut.modifiers()) {
                        data.lock().unwrap().log(format!("平板快捷键发送失败 · Windows 错误 {error}"));
                    }
                }
            }
            OutputEvent::Receiver(ReceiverEvent::NavigateCanvas { client, command, .. }) => {
                let mut data = data.lock().unwrap();
                if data.clients.contains_key(&client.connection_id) {
                    data.queue_navigation(&client.connection_id, command, None);
                }
            }
            OutputEvent::Receiver(ReceiverEvent::Frame { client, frame, .. }) => {
                let (mut native, shortcut) = {
                    let mut data = data.lock().unwrap();
                    if !data.clients.contains_key(&client.connection_id) {
                        continue;
                    }
                    data.diagnostics.record(&frame);
                    gestures.note(&client, &frame);
                    // The absolute-mouse wire format encodes the side key as right button.
                    let down = frame.barrel || frame.right_button;
                    let was_down = side_key_down.insert(client.connection_id.clone(), down).unwrap_or(false);
                    if !data.config.injection_enabled {
                        continue;
                    }
                    let shortcut = (down && !was_down && data.config.side_key_action == SideKeyAction::Shortcut)
                        .then_some((data.config.side_key_virtual_key, data.config.side_key_modifiers));
                    (NativeFrame::from_frame(&frame, &data.config), shortcut)
                };
                if active.as_deref() != Some(client.connection_id.as_str()) {
                    release();
                    annotation = AnnotationStroke::default();
                    retiring_stroke = None;
                    active = Some(client.connection_id.clone());
                }
                let windows = capture_windows.lock().unwrap().clone();
                if annotation.lost_capture(&windows, &frame) {
                    release();
                    annotation = AnnotationStroke::default();
                    retiring_stroke = Some(frame.mode);
                }
                if retiring_stroke == Some(frame.mode) && AnnotationStroke::tip_down(&frame) {
                    continue;
                }
                retiring_stroke = None;
                let mut target = None;
                let captured = annotation.captures(windows.clone(), &frame, |windows| {
                    target = native.annotation_at(frame.mode, windows);
                    target.map(|(window, _)| window)
                });
                let pan = annotation.hover_pan.update(target.filter(|_| {
                    frame.mode == InputMode::Pen && frame.in_range && !frame.contact && frame.barrel
                }), frame.hover_pan_deadzone_px);
                if captured {
                    native.annotation_controls(&frame, annotation.erase);
                }
                if let Some((window, point)) = target.filter(|_| AnnotationStroke::tip_down(&frame)) {
                    // Windows can coalesce or omit pen details before the canvas
                    // processes a down message. Retain the exact scoped tip edge.
                    let mut data = data.lock().unwrap();
                    if data.tip_intents.len() >= 64 {
                        data.tip_intents.pop_front();
                    }
                    let erase = if frame.mode == InputMode::Pen {
                        annotation.erase || frame.inverted || frame.eraser
                    } else {
                        frame.right_button
                    };
                    data.tip_intents.push_back(TipIntent {
                        client: client.connection_id.clone(), window, point,
                        tick: unsafe { GetTickCount() }, created: Instant::now(),
                        erase, capture: windows.clone(),
                    });
                }
                let error = unsafe { tablet_pointer_write(injector, &native) };
                let shortcut_error = if captured { None } else {
                    shortcut.and_then(|(key, modifiers)| send_shortcut(key, modifiers).err())
                };
                let mut data = data.lock().unwrap();
                if let Some(command) = pan {
                    data.queue_navigation(&client.connection_id, command, Some(windows));
                }
                if let Some(error) = shortcut_error {
                    data.log(format!("侧键快捷键注入失败 · Windows 错误 {error}"));
                }
                let error = (error != 0).then_some(error);
                if data.diagnostics.injection_error != error {
                    data.diagnostics.injection_error = error;
                    if let Some(error) = error {
                        data.log(format!("Windows 输入错误 {error}"));
                    }
                }
            }
        }
    }
    unsafe { tablet_pointer_close(injector) };
}

#[cfg(test)]
mod tests {
    use super::*;
    use pennote_bridge_receiver::protocol::PenEvent;

    fn pen(flags: u32) -> PointerFrame {
        PenEvent { status_mask: flags, x: 8000, y: 16000, pressure: 14000, ..Default::default() }.into()
    }

    #[test]
    fn annotation_uses_raw_barrel_without_losing_tip_or_pressure() {
        let frame = pen(0x23);
        let config = Config { side_key_action: SideKeyAction::Shortcut, ..Default::default() };
        let mut native = NativeFrame::from_frame(&frame, &config);
        assert_eq!(native.flags, 0b00011);
        native.annotation_controls(&frame, true);
        assert_eq!(native.flags, 0b11011);
        assert_eq!(native.pressure, frame.pressure);

        let hover = pen(0x22);
        let mut native = NativeFrame::from_frame(&hover, &Config::default());
        assert_eq!(native.flags, 0b00101);
        native.annotation_controls(&hover, true);
        assert_eq!(native.flags, 0b01001);
        let unmodified = NativeFrame::from_frame(&frame, &Config::default());
        assert_eq!(unmodified.flags, 0b00111);
    }

    #[test]
    fn only_an_owned_capture_scope_carries_the_stroke_through_release() {
        let windows: Arc<[isize]> = Arc::from([1]);
        let mut stroke = AnnotationStroke::default();
        assert!(stroke.captures(windows.clone(), &pen(0x22), |_| Some(1)));
        assert!(stroke.captures(windows.clone(), &pen(0x23), |_| Some(1)));
        assert!(!stroke.lost_capture(&Arc::from([1, 2]), &pen(0x23)));
        assert!(stroke.lost_capture(&Arc::from([2]), &pen(0x23)));
        assert!(stroke.captures(windows.clone(), &pen(0x23), |_| None));
        assert!(stroke.captures(windows.clone(), &pen(0x20), |_| None));
        assert!(!stroke.captures(windows.clone(), &pen(0x20), |_| None));
        assert!(!stroke.captures(windows.clone(), &pen(0x23), |_| None));
        assert!(!stroke.captures(windows, &pen(0x23), |_| Some(1)));
        assert!(!stroke.captures(Arc::from([]), &pen(0x23), |_| panic!("empty scope")));
    }

    #[test]
    fn side_held_at_landing_stays_an_eraser_until_tip_up() {
        let windows: Arc<[isize]> = Arc::from([1]);
        let mut stroke = AnnotationStroke::default();
        assert!(stroke.captures(windows.clone(), &pen(0x22), |_| Some(1)));
        assert!(stroke.captures(windows.clone(), &pen(0x21), |_| Some(1)));
        assert!(stroke.erase, "barrel was present immediately before landing");
        stroke.captures(windows.clone(), &pen(0x21), |_| Some(1));
        assert!(stroke.erase, "a barrel dropout cannot switch the active tool");
        stroke.captures(windows.clone(), &pen(0x20), |_| Some(1));
        stroke.captures(windows, &pen(0x21), |_| Some(1));
        assert!(!stroke.erase, "the next tip contact needs its own barrel state");
    }

    #[test]
    fn brief_hover_exit_keeps_barrel_eraser_for_the_same_canvas() {
        let windows: Arc<[isize]> = Arc::from([1, 2]);
        let mut stroke = AnnotationStroke::default();
        stroke.captures(windows.clone(), &pen(0x22), |_| Some(1));
        stroke.captures(windows.clone(), &pen(0), |_| None);
        assert!(stroke.captures(windows.clone(), &pen(0x21), |_| Some(1)));
        assert!(stroke.erase);

        let mut stroke = AnnotationStroke::default();
        stroke.captures(windows.clone(), &pen(0x22), |_| Some(1));
        stroke.captures(windows.clone(), &pen(0), |_| None);
        assert!(stroke.captures(windows.clone(), &pen(0x21), |_| Some(2)));
        assert!(!stroke.erase);

        let mut stroke = AnnotationStroke::default();
        stroke.captures(windows.clone(), &pen(0x22), |_| Some(1));
        stroke.captures(windows.clone(), &pen(0x20), |_| Some(1));
        stroke.captures(windows, &pen(0x21), |_| Some(1));
        assert!(!stroke.erase, "a reported in-range barrel release is not carried into the next stroke");
    }

    #[test]
    fn hover_pan_uses_desktop_pixels_and_reanchors_after_scope_or_contact_change() {
        let mut pan = HoverPan::default();
        assert!(pan.update(Some((1, POINT { x: 100, y: 200 })), None).is_none());
        assert!(pan.update(Some((1, POINT { x: 101, y: 200 })), None).is_none());
        let movement = pan.update(Some((1, POINT { x: 105, y: 198 })), None).unwrap();
        assert_eq!((movement.action, movement.pan_x, movement.pan_y), (2, 5.0, -2.0));
        assert!(pan.update(Some((2, POINT { x: 200, y: 400 })), None).is_none());
        assert!(pan.update(None, None).is_none());
        assert!(pan.update(Some((2, POINT { x: 500, y: 500 })), None).is_none());
    }

    #[test]
    fn configured_hover_deadzone_activates_without_replaying_discarded_movement() {
        let mut pan = HoverPan::default();
        let target = |x, y| Some((1, POINT { x, y }));
        assert!(pan.update(target(100, 100), Some(10)).is_none());
        assert!(pan.update(target(106, 106), Some(10)).is_none());
        assert!(pan.update(target(108, 108), Some(10)).is_none());
        assert_eq!(pan.update(target(109, 108), Some(10)).unwrap().pan_x, 1.0);
        assert!(pan.update(target(110, 108), Some(0)).is_none());
        assert_eq!(pan.update(target(111, 108), Some(0)).unwrap().pan_x, 1.0);
    }

    #[test]
    fn disabled_menu_still_allows_right_tip_in_a_mouse_mode_canvas() {
        use pennote_bridge_receiver::protocol::AbsoluteMouseEvent;
        let config = Config { side_key_action: SideKeyAction::Disabled, ..Default::default() };
        for relative in [false, true] {
            let frame: PointerFrame = AbsoluteMouseEvent {
                buttons: 3, relative, relative_active: true, ..Default::default()
            }.into();
            let mut native = NativeFrame::from_frame(&frame, &config);
            assert_eq!(native.flags & FLAG_RIGHT, 0);
            let mut stroke = AnnotationStroke::default();
            assert!(stroke.captures(Arc::from([1]), &frame, |_| Some(1)));
            native.annotation_controls(&frame, false);
            assert_eq!(native.flags & FLAG_RIGHT, FLAG_RIGHT);
        }
    }

    #[test]
    fn canvas_preserves_light_tip_contact_without_altering_raw_pressure() {
        let frame = PointerFrame { pressure: 0.0, ..pen(0x21) };
        let mut native = NativeFrame::from_frame(&frame, &Config::default());
        assert_eq!(native.pressure, 0.0);
        native.annotation_controls(&frame, false);
        assert_eq!(native.pressure, 1.0 / 1024.0);
        let hover = pen(0x20);
        let mut native = NativeFrame::from_frame(&hover, &Config::default());
        native.annotation_controls(&hover, false);
        assert_eq!(native.pressure, 0.0);
    }

    #[test]
    fn supported_hid_shortcuts_map_to_windows_keyboard_keys() {
        for (usage, key) in [(4, 0x41), (29, 0x5a), (30, 0x31), (39, 0x30),
            (40, 0x0d), (41, 0x1b), (43, 0x09), (44, 0x20), (58, 0x70),
            (69, 0x7b), (79, 0x27), (80, 0x25), (81, 0x28), (82, 0x26)] {
            assert_eq!(shortcut_virtual_key(Shortcut::new(usage, 0).unwrap()), key);
        }
    }

    #[test]
    fn canvas_commands_are_bounded_and_available_without_pointer_injection() {
        use pennote_bridge_receiver::Transport;
        assert_eq!(std::mem::size_of::<CanvasCommand>(), 24);
        let (signal, worker) = Signals::start(Arc::new(BarrelGestures::default())).unwrap();
        let disabled = Config { injection_enabled: false, ..Default::default() };
        signal.configure(disabled.clone()).unwrap();
        let transport = Transport::Usb;
        let client = ClientInfo {
            client_id: "tablet".into(), connection_id: "test".into(), display_name: "test".into(),
            platform: "Android".into(), version: "test".into(), transport, peer: None,
        };
        signal.submit(ReceiverEvent::Connected { transport, client: client.clone() });
        for index in 0..=64 {
            let command = CanvasCommand::new([index as f32 / 64.0, 0.0, 1.0, 0.5, 0.5], 0).unwrap();
            signal.submit(ReceiverEvent::NavigateCanvas { transport, client: client.clone(), command });
        }
        signal.configure(disabled.clone()).unwrap();
        assert_eq!(signal.take_canvas_navigation().unwrap().pan_x, 1.0 / 64.0);
        signal.submit(ReceiverEvent::Disconnected { transport, client });
        signal.configure(disabled).unwrap();
        assert!(signal.take_canvas_navigation().is_none());
        signal.shutdown();
        worker.join().unwrap();
    }

    #[test]
    fn queued_hover_pan_combines_deltas_and_drops_them_when_capture_changes() {
        let (signal, worker) = Signals::start(Arc::new(BarrelGestures::default())).unwrap();
        signal.set_capture_windows(&[1]);
        let capture = signal.capture_windows.lock().unwrap().clone();
        let pan = CanvasCommand { action: 2, pan_x: 4.0, pan_y: -3.0, scale: 1.0, focus_x: 0.0, focus_y: 0.0 };
        {
            let mut data = signal.data.lock().unwrap();
            data.queue_navigation("tablet", pan, Some(capture.clone()));
            data.queue_navigation("tablet", pan, Some(capture.clone()));
        }
        assert_eq!(signal.take_canvas_navigation().unwrap().pan_x, 8.0);
        signal.data.lock().unwrap().queue_navigation("tablet", pan, Some(capture));
        signal.set_capture_windows(&[2]);
        assert!(signal.take_canvas_navigation().is_none());
        signal.shutdown();
        worker.join().unwrap();
    }

    #[test]
    fn server_eraser_intent_is_scoped_to_matching_window_position_and_tick_once() {
        let (signal, worker) = Signals::start(Arc::new(BarrelGestures::default())).unwrap();
        signal.set_capture_windows(&[1, 2]);
        let capture = signal.capture_windows.lock().unwrap().clone();
        let push = |erase, tick, created| signal.data.lock().unwrap().tip_intents.push_back(TipIntent {
            client: "tablet".into(), window: 1, point: POINT { x: 300, y: -55 },
            tick, created, erase, capture: capture.clone(),
        });
        push(true, 100, Instant::now());
        assert!(!signal.take_eraser_intent(2, 300, -55, 102));
        assert!(!signal.take_eraser_intent(1, 312, -55, 102));
        assert!(!signal.take_eraser_intent(1, 300, -55, 200));
        assert!(signal.take_eraser_intent(1, 302, -55, 102));
        assert!(!signal.take_eraser_intent(1, 302, -55, 102));
        push(true, 210, Instant::now());
        push(false, 245, Instant::now());
        assert!(!signal.take_eraser_intent(1, 300, -55, 248));
        signal.set_capture_windows(&[1]);
        assert!(!signal.take_eraser_intent(1, 300, -55, 215));
        signal.shutdown();
        worker.join().unwrap();
    }
}
