use std::{
    ffi::c_void,
    sync::{atomic::{AtomicBool, Ordering}, mpsc, Arc},
    thread,
    time::{Duration, Instant},
};

use pennote_bridge_receiver::{relative_gain, CanvasCommand, ClientInfo, InputMode, PointerFrame, ReceiverEvent, Shortcut, Transport};
use serde::Deserialize;
use subtle::ConstantTimeEq;

use super::Signals;

extern "C" {
    fn tablet_bt_open(error: *mut u32) -> *mut c_void;
    fn tablet_bt_accept(listener: *mut c_void, error: *mut u32) -> usize;
    fn tablet_bt_recv(peer: usize, output: *mut c_void, length: i32) -> i32;
    fn tablet_bt_send(peer: usize, input: *const c_void, length: i32) -> i32;
    fn tablet_bt_close_peer(peer: usize);
    fn tablet_bt_close(listener: *mut c_void);
}

#[derive(Default)]
struct Control {
    stop: AtomicBool,
    disconnect: AtomicBool,
}

pub(super) struct Radio {
    control: Arc<Control>,
    thread: Option<thread::JoinHandle<()>>,
}

impl Radio {
    pub(super) fn start(signal: Signals, code: String) -> Result<Self, String> {
        let control = Arc::new(Control::default());
        let worker = control.clone();
        let (ready, result) = mpsc::sync_channel(1);
        let thread = thread::Builder::new().name("pennote-bluetooth".into()).spawn(move || {
            let mut error = 0;
            let listener = unsafe { tablet_bt_open(&mut error) };
            if listener.is_null() {
                let _ = ready.send(Err(format!("蓝牙接收器启动失败 · Windows 错误 {error}")));
                return;
            }
            if ready.send(Ok(())).is_ok() {
                listen(listener, &signal, &worker, &code);
            }
            unsafe { tablet_bt_close(listener) };
        }).map_err(|error| error.to_string())?;
        let radio = Self { control, thread: Some(thread) };
        result.recv_timeout(Duration::from_secs(5))
            .map_err(|_| "蓝牙接收器启动超时".to_string())??;
        Ok(radio)
    }

    pub(super) fn disconnect(&self) {
        self.control.disconnect.store(true, Ordering::Release);
    }

    pub(super) fn stop(mut self) {
        self.control.stop.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for Radio {
    fn drop(&mut self) {
        self.control.stop.store(true, Ordering::Release);
    }
}

fn listen(listener: *mut c_void, signal: &Signals, control: &Control, code: &str) {
    let mut next_client = 0u64;
    while !control.stop.load(Ordering::Acquire) {
        control.disconnect.store(false, Ordering::Release);
        let mut error = 0;
        let peer = unsafe { tablet_bt_accept(listener, &mut error) };
        if peer == 0 {
            if error != 0 {
                signal.log(format!("蓝牙等待连接失败 · Windows 错误 {error}"));
                thread::sleep(Duration::from_secs(2));
            }
            continue;
        }
        next_client += 1;
        handle_peer(peer, signal, control, code, next_client);
        unsafe { tablet_bt_close_peer(peer) };
    }
}

#[derive(Deserialize)]
struct Hello {
    hello: u8,
    token: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ShortcutLine {
    key: u32,
    modifiers: u32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct CanvasLine {
    canvas: CanvasValues,
}

#[derive(Deserialize)]
#[serde(untagged)]
enum CanvasValues {
    WithAction((f32, f32, f32, f32, f32, u32)),
    Motion([f32; 5]),
}

impl CanvasValues {
    fn command(self) -> Option<CanvasCommand> {
        match self {
            Self::WithAction((dx, dy, scale, x, y, action)) => CanvasCommand::new([dx, dy, scale, x, y], action),
            Self::Motion(values) => CanvasCommand::new(values, 0),
        }
    }
}

#[derive(Deserialize)]
#[serde(untagged)]
enum CommandLine {
    Shortcut(ShortcutLine),
    Canvas(CanvasLine),
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
enum Kind {
    Hover, Down, Move, Up, Exit, Cancel,
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
enum Mode {
    Pen, AbsoluteMouse, RelativeMouse,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Sample {
    seq: u64,
    t: u64,
    kind: Kind,
    mode: Mode,
    x: f32,
    y: f32,
    pressure: f32,
    tilt_x: i32,
    tilt_y: i32,
    rotation: u32,
    barrel: bool,
    eraser: bool,
    #[serde(default)]
    gain: f32,
    hover_pan_deadzone_px: Option<u32>,
}

impl Sample {
    fn frame(self) -> Option<PointerFrame> {
        if [self.x, self.y, self.pressure].iter().any(|v| !(0.0..=1.0).contains(v))
            || !(-90..=90).contains(&self.tilt_x) || !(-90..=90).contains(&self.tilt_y)
            || self.rotation >= 360
        {
            return None;
        }
        let contact = matches!(self.kind, Kind::Down | Kind::Move);
        let in_range = !matches!(self.kind, Kind::Exit | Kind::Cancel);
        let pen = matches!(self.mode, Mode::Pen);
        Some(PointerFrame {
            mode: match self.mode {
                Mode::Pen => InputMode::Pen,
                Mode::AbsoluteMouse => InputMode::AbsoluteMouse,
                Mode::RelativeMouse => InputMode::RelativeMouse,
            },
            x: self.x, y: self.y, pressure: if contact && pen { self.pressure } else { 0.0 },
            tilt_x: self.tilt_x, tilt_y: self.tilt_y, rotation: self.rotation,
            in_range, contact, barrel: in_range && self.barrel,
            inverted: pen && in_range && self.eraser, eraser: pen && contact && self.eraser,
            left_button: !pen && contact, right_button: !pen && in_range && self.barrel,
            middle_button: false, client_time_us: self.t.saturating_mul(1000),
            relative_gain: relative_gain(self.gain),
            hover_pan_deadzone_px: self.hover_pan_deadzone_px.map(|px| px.min(96)),
        })
    }
}

struct Stream<'a> {
    peer: usize,
    control: &'a Control,
    buffered: Vec<u8>,
}

impl Stream<'_> {
    fn line(&mut self, deadline: Option<Instant>) -> Option<Vec<u8>> {
        const MAX_LINE: usize = 4096;
        let mut buffer = [0u8; 2048];
        loop {
            if self.control.stop.load(Ordering::Acquire)
                || self.control.disconnect.load(Ordering::Acquire)
                || deadline.is_some_and(|time| Instant::now() >= time)
            {
                return None;
            }
            if let Some(end) = self.buffered.iter().position(|byte| *byte == b'\n') {
                if end > MAX_LINE { return None; }
                let mut line: Vec<_> = self.buffered.drain(..=end).collect();
                line.pop();
                return Some(line);
            }
            if self.buffered.len() > MAX_LINE { return None; }
            let count = unsafe { tablet_bt_recv(self.peer, buffer.as_mut_ptr().cast(), buffer.len() as i32) };
            if count == -2 { continue; }
            if count <= 0 { return None; }
            self.buffered.extend_from_slice(&buffer[..count as usize]);
        }
    }
}

fn handle_peer(peer: usize, signal: &Signals, control: &Control, code: &str, id: u64) {
    let mut stream = Stream { peer, control, buffered: Vec::with_capacity(2048) };
    let hello = stream.line(Some(Instant::now() + Duration::from_secs(5)))
        .and_then(|line| serde_json::from_slice::<Hello>(&line).ok());
    if !hello.is_some_and(|hello| hello.hello == 1
        && bool::from(hello.token.as_bytes().ct_eq(code.as_bytes())))
    {
        signal.log("蓝牙配对失败 · 请检查配对码");
        return;
    }
    let (shortcuts, canvas, width, height) = signal.bluetooth_capabilities();
    let response = format!("{{\"ok\":1,\"shortcuts\":{},\"canvas\":{},\"width\":{width},\"height\":{height}}}\n",
        u8::from(shortcuts), u8::from(canvas));
    if unsafe { tablet_bt_send(peer, response.as_ptr().cast(), response.len() as i32) } != response.len() as i32 {
        return;
    }
    let transport = Transport::Bluetooth;
    let client = ClientInfo {
        client_id: "bluetooth".into(), connection_id: format!("bluetooth-{id}"),
        display_name: "Android 蓝牙数位板".into(), platform: "Android".into(),
        version: "0.4".into(), transport, peer: None,
    };
    signal.submit(ReceiverEvent::Connected { transport, client: client.clone() });
    let mut seq = 0;
    let mut deadline = None;
    while let Some(line) = stream.line(deadline) {
        let sample = match serde_json::from_slice::<Sample>(&line) {
            Ok(sample) => sample,
            Err(_) => {
                let Ok(input) = serde_json::from_slice::<CommandLine>(&line) else { break };
                let event = match input {
                    CommandLine::Shortcut(input) if shortcuts => Shortcut::new(input.key, input.modifiers)
                        .map(|shortcut| ReceiverEvent::Shortcut { transport, client: client.clone(), shortcut }),
                    CommandLine::Canvas(input) if canvas => input.canvas.command()
                        .map(|command| ReceiverEvent::NavigateCanvas { transport, client: client.clone(), command }),
                    _ => None,
                };
                if let Some(event) = event {
                    signal.submit(event);
                }
                continue;
            }
        };
        if sample.seq <= seq { break; }
        seq = sample.seq;
        let Some(frame) = sample.frame() else { break };
        // Android repeats contact frames while the pen rests. Release held
        // input if the link stalls before sending a terminal frame.
        deadline = (frame.contact || frame.right_button).then(|| Instant::now() + Duration::from_secs(2));
        signal.submit(ReceiverEvent::Frame { transport, client: client.clone(), frame });
    }
    signal.submit(ReceiverEvent::Disconnected { transport, client });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn canvas_lines_support_motion_and_integral_actions() {
        for (line, action) in [
            (r#"{"canvas":[0.1,0,1.2,0.5,0.4]}"#, 0),
            (r#"{"canvas":[0,0,1,0.5,0.5,1]}"#, 1),
        ] {
            let CommandLine::Canvas(parsed) = serde_json::from_str(line).unwrap() else { panic!("wrong command type"); };
            assert_eq!(parsed.canvas.command().unwrap().action, action);
        }
        assert!(serde_json::from_str::<CommandLine>(r#"{"canvas":[0,0,1,0,0,1.2]}"#).is_err());
    }
}
