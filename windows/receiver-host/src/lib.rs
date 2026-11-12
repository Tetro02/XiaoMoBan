use std::ffi::{c_char, CStr, CString};
use std::fs;
use std::io::{ErrorKind, Write};
use std::net::{Ipv4Addr, SocketAddr, UdpSocket};
use std::os::windows::process::CommandExt;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::process::Command;
use std::ptr;
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Instant;

use pennote_bridge_receiver::{CanvasCommand, DisplayInfo, LanAccess, Receiver, RunningReceiver, ServerConfig,
    ServerIdentity, Transport, DEFAULT_LAN_PORT, DEFAULT_USB_PORT};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::runtime::Runtime;

mod input;
use input::Signals;
mod barrel_gestures;
use barrel_gestures::BarrelGestures;
mod discovery;
mod pairing;

mod bluetooth;

#[derive(Clone, Deserialize, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub(crate) struct Config {
    lan_enabled: bool,
    usb_enabled: bool,
    bluetooth_enabled: bool,
    first_wired_transport_wins: bool,
    lan_port: u16,
    usb_port: u16,
    pairing_port: u16,
    discovery_enabled: bool,
    injection_enabled: bool,
    side_key_action: SideKeyAction,
    side_key_virtual_key: u16,
    side_key_modifiers: u8,
    map_all: bool,
    left: i32,
    top: i32,
    width: i32,
    height: i32,
    pressure_gamma: f32,
    display_name: String,
    display_index: u32,
    display_refresh_hz: u32,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
enum SideKeyAction {
    #[default]
    Native,
    Disabled,
    Shortcut,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            lan_enabled: true, usb_enabled: true, bluetooth_enabled: false, first_wired_transport_wins: false,
            lan_port: DEFAULT_LAN_PORT, usb_port: DEFAULT_USB_PORT, pairing_port: pairing::PORT,
            discovery_enabled: true, injection_enabled: true,
            side_key_action: SideKeyAction::Native, side_key_virtual_key: 0, side_key_modifiers: 0,
            map_all: true, left: 0, top: 0, width: 0, height: 0, pressure_gamma: 1.0,
            display_name: "整个桌面".into(), display_index: 0, display_refresh_hz: 60,
        }
    }
}

impl Config {
    fn patched(&self, patch: &str) -> Result<Self, String> {
        let patch: Value = serde_json::from_str(patch).map_err(|error| error.to_string())?;
        let patch = patch.as_object().ok_or("接收器配置必须是 JSON 对象")?;
        let Value::Object(mut values) = serde_json::to_value(self).map_err(|error| error.to_string())? else {
            unreachable!("Config serializes as an object")
        };
        values.extend(patch.iter().map(|(key, value)| (key.clone(), value.clone())));
        let config: Self = serde_json::from_value(Value::Object(values)).map_err(|error| error.to_string())?;
        if !(0.25..=2.0).contains(&config.pressure_gamma) {
            return Err("压感倍率应为 0.25–2.0".into());
        }
        if config.side_key_action == SideKeyAction::Shortcut
            && (!input::valid_shortcut_key(config.side_key_virtual_key) || config.side_key_modifiers > 0b1111)
        {
            return Err("侧键快捷键无效".into());
        }
        if config.width < 0 || config.height < 0 || (!config.map_all && (config.width == 0 || config.height == 0))
            || config.left.checked_add(config.width).is_none() || config.top.checked_add(config.height).is_none()
        {
            return Err("显示器映射范围无效".into());
        }
        Ok(config)
    }

    fn listeners(&self) -> (bool, bool, bool, u16, u16, u16, bool, bool) {
        (self.lan_enabled, self.usb_enabled, self.bluetooth_enabled, self.lan_port,
            self.usb_port, self.pairing_port, self.discovery_enabled, self.first_wired_transport_wins)
    }

    fn display(&self) -> DisplayInfo {
        DisplayInfo {
            server_name: "小墨板".into(), width: self.width as u32, height: self.height as u32,
            display_name: self.display_name.clone(), display_index: self.display_index,
            host_os: "windows".into(), display_refresh_hz: vec![self.display_refresh_hz],
            keyboard_shortcuts: true, canvas_navigation: true,
        }
    }
}

#[derive(Default)]
struct Services {
    grpc: Option<RunningReceiver>,
    bluetooth: Option<bluetooth::Radio>,
    pairing: Option<pairing::Pairing>,
    discovery: Option<tokio::task::JoinHandle<()>>,
    addresses: Vec<Ipv4Addr>,
}

impl Services {
    fn running(&self) -> bool {
        self.grpc.is_some() || self.bluetooth.is_some()
    }
}

pub struct Host {
    runtime: Runtime,
    signal: Signals,
    gestures: Arc<BarrelGestures>,
    output: Option<JoinHandle<()>>,
    identity: ServerIdentity,
    code: String,
    services: Mutex<Services>,
}

impl Host {
    fn create(folder: &Path) -> Result<Self, String> {
        fs::create_dir_all(folder).map_err(|error| error.to_string())?;
        let identity = ServerIdentity::load_or_create(folder).map_err(|error| error.to_string())?;
        let code = load_pairing_code(folder)?;
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .thread_name("pennote-network")
            .build()
            .map_err(|error| error.to_string())?;
        let gestures = Arc::new(BarrelGestures::default());
        let (signal, output) = Signals::start(gestures.clone())?;
        let services = Services { addresses: network_addresses(), ..Services::default() };
        Ok(Self { runtime, signal, gestures, output: Some(output), identity, code, services: Mutex::new(services) })
    }

    fn start(&self, patch: &str) -> Result<(), String> {
        let mut services = self.services.lock().unwrap();
        if services.running() {
            return Err("接收器已启动，请先停止再切换连接方式".into());
        }
        let config = self.signal.data.lock().unwrap().config.patched(patch)?;
        if !config.lan_enabled && !config.usb_enabled && !config.bluetooth_enabled {
            return Err("请选择一种连接方式".into());
        }
        self.signal.configure(config.clone())?;
        if config.lan_enabled || config.usb_enabled {
            let signal = self.signal.clone();
            let receiver = Receiver::new(move |event| signal.submit(event))
                .first_wired_transport_wins(config.first_wired_transport_wins);
            let options = ServerConfig {
                lan: config.lan_enabled.then(|| SocketAddr::from((Ipv4Addr::UNSPECIFIED, config.lan_port))),
                usb: config.usb_enabled.then(|| SocketAddr::from((Ipv4Addr::LOCALHOST, config.usb_port))),
                lan_access: if config.lan_enabled { LanAccess::PairingCode(self.code.clone()) } else { LanAccess::Disabled },
                display: config.display(),
            };
            let result = self.runtime.block_on(receiver.start(self.identity.clone(), options))
                .map_err(|error| format!("Wi-Fi 或 USB 接收器启动失败 · {error}"));
            match result {
                Ok(grpc) => services.grpc = Some(grpc),
                Err(error) => self.signal.log(error),
            }
        }
        if let Some((lan, pairing_stopped, discovery_stopped)) = services.grpc.as_ref()
            .and_then(|grpc| grpc.lan_address().map(|address| (address, grpc.lan_stopped(), grpc.lan_stopped())))
        {
            let fingerprint = self.identity.fingerprint_sha256();
            match self.runtime.block_on(pairing::Pairing::start(config.pairing_port, fingerprint, &self.code, pairing_stopped)) {
                Ok(pairing) => {
                    if config.discovery_enabled {
                        match discovery::Discovery::start(pairing.address.port(), lan.port(), fingerprint) {
                            Ok(discovery) => services.discovery = Some(self.runtime.spawn(async move {
                                discovery_stopped.await;
                                let _ = tokio::task::spawn_blocking(move || drop(discovery)).await;
                            })),
                            Err(error) => self.signal.log(format!("局域网自动发现启动失败 · {error}")),
                        }
                    }
                    services.pairing = Some(pairing);
                }
                Err(error) => self.signal.log(format!("六位码配对启动失败 · {error}")),
            }
        }
        let mut radio_error = None;
        if config.bluetooth_enabled {
            match bluetooth::Radio::start(self.signal.clone(), self.code.clone()) {
                Ok(radio) => services.bluetooth = Some(radio),
                Err(error) => {
                    self.signal.log(error.clone());
                    radio_error = Some(error);
                }
            }
        }
        services.addresses = network_addresses();
        if services.running() {
            self.signal.log("等待平板连接");
        }
        if (config.lan_enabled || config.usb_enabled) && services.grpc.is_none() {
            return Err("Wi-Fi 或 USB 接收器启动失败，请查看连接日志".into());
        }
        radio_error.map_or(Ok(()), Err)
    }

    fn configure(&self, patch: &str) -> Result<(), String> {
        let services = self.services.lock().unwrap();
        let current = self.signal.data.lock().unwrap().config.clone();
        let config = current.patched(patch)?;
        if services.running() && config.listeners() != current.listeners() {
            return Err("请先停止接收器，再切换连接方式或端口".into());
        }
        let display = config.display();
        self.signal.configure(config)?;
        if let Some(grpc) = &services.grpc {
            grpc.set_display_info(display);
        }
        Ok(())
    }

    fn stop(&self) -> Result<(), String> {
        let mut services = self.services.lock().unwrap();
        let was_running = services.running();
        if let Some(pairing) = services.pairing.take() {
            self.runtime.block_on(pairing.stop());
        }
        if let Some(radio) = services.bluetooth.take() {
            radio.stop();
        }
        let result = services.grpc.take().map_or(Ok(()), |grpc| {
            self.runtime.block_on(grpc.shutdown()).map_err(|error| error.to_string())
        });
        if let Some(discovery) = services.discovery.take() {
            let _ = self.runtime.block_on(discovery);
        }
        self.signal.release()?;
        self.signal.data.lock().unwrap().clients.clear();
        if was_running {
            self.signal.log("接收器已停止");
        }
        result
    }

    fn disconnect(&self, connection_id: &str) {
        let services = self.services.lock().unwrap();
        let bluetooth = self.signal.data.lock().unwrap().clients.get(connection_id)
            .is_some_and(|client| client.transport == Transport::Bluetooth);
        if bluetooth {
            if let Some(radio) = &services.bluetooth { radio.disconnect(); }
        } else if let Some(grpc) = &services.grpc {
            if !grpc.disconnect(Transport::Lan, connection_id) {
                grpc.disconnect(Transport::Usb, connection_id);
            }
        }
    }

    fn snapshot(&self) -> Value {
        let services = self.services.lock().unwrap();
        let (lan_address, usb_address, wired_winner, mut clients) = services.grpc.as_ref()
            .map(RunningReceiver::live_wired).unwrap_or_default();
        let pairing = services.pairing.as_ref().filter(|_| lan_address.is_some());
        let (short_code, pairing_seconds_remaining) = pairing.map(pairing::Pairing::code).unwrap_or_default();
        let mut data = self.signal.data.lock().unwrap();
        data.diagnostics.update_rate(Instant::now());
        clients.extend(data.clients.values().filter(|client| client.transport == Transport::Bluetooth).cloned());
        clients.sort_by(|left, right| left.connection_id.cmp(&right.connection_id));
        let status = match (services.running(), clients.len()) {
            (false, _) => "接收器已停止".into(),
            (true, 0) => "等待平板连接".into(),
            (true, count) => format!("{count} 台平板已连接"),
        };
        json!({
            "running": services.running(),
            "transports": { "lan": lan_address.is_some(), "usb": usb_address.is_some(), "bluetooth": services.bluetooth.is_some() },
            "status": status,
            "lanAddress": lan_address,
            "usbAddress": usb_address,
            "wiredWinner": wired_winner,
            "addresses": services.addresses,
            "fingerprint": self.identity.fingerprint_sha256(),
            "pairingCode": self.code,
            "shortPairingCode": short_code,
            "pairingSecondsRemaining": pairing_seconds_remaining,
            "pairingAddress": pairing.map(|pairing| pairing.address),
            "discoveryEnabled": lan_address.is_some() && services.discovery.is_some(),
            "clients": clients,
            "diagnostics": data.diagnostics,
            "logs": data.logs,
            "config": data.config,
        })
    }

    fn pairing(&self, address: &str) -> Result<Value, String> {
        use std::fmt::Write as _;
        let address: Ipv4Addr = address.parse().map_err(|_| "请选择电脑 IPv4 地址")?;
        let payload = format!("PNB1|{address}|{}|{}", self.identity.fingerprint_sha256(), self.code);
        let code = qrcode::QrCode::new(payload.as_bytes()).map_err(|error| error.to_string())?;
        let width = code.width();
        let size = width + 8;
        let mut path = String::new();
        let mut modules = Vec::with_capacity(width * width);
        for y in 0..width {
            for x in 0..width {
                let dark = code[(x, y)] == qrcode::Color::Dark;
                modules.push(dark);
                if dark {
                    let _ = write!(path, "M{} {}h1v1h-1z", x + 4, y + 4);
                }
            }
        }
        let svg = format!("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {size} {size}' shape-rendering='crispEdges'><path fill='white' d='M0 0h{size}v{size}H0z'/><path fill='#182131' d='{path}'/></svg>");
        Ok(json!({ "payload": payload, "svg": svg, "width": width, "modules": modules }))
    }

    fn renew_pairing(&self) -> Result<(), String> {
        let services = self.services.lock().unwrap();
        let pairing = services.pairing.as_ref().filter(|_| services.grpc.as_ref()
            .and_then(RunningReceiver::lan_address).is_some()).ok_or("请先启动局域网接收器")?;
        pairing.renew().map_err(|error| error.to_string())
    }
}

impl Drop for Host {
    fn drop(&mut self) {
        let _ = self.stop();
        self.signal.shutdown();
        if let Some(output) = self.output.take() {
            let _ = output.join();
        }
    }
}

fn network_addresses() -> Vec<Ipv4Addr> {
    let local = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0)).and_then(|socket| {
        socket.connect((Ipv4Addr::new(192, 0, 2, 1), 9))?;
        socket.local_addr()
    });
    let mut found = Vec::new();
    if let Ok(SocketAddr::V4(address)) = local {
        if !address.ip().is_unspecified() {
            found.push(*address.ip());
        }
    }
    if let Ok(output) = Command::new("ipconfig.exe").creation_flags(0x08000000).output() {
        let output = String::from_utf8_lossy(&output.stdout);
        for line in output.lines().filter(|line| line.contains("IPv4")) {
            let address = line.rsplit_once(':').and_then(|(_, text)| {
                text.trim().split(|c: char| c == '(' || c.is_whitespace()).next()
            }).and_then(|text| text.parse::<Ipv4Addr>().ok());
            if let Some(address) = address {
                if !address.is_loopback() && !found.contains(&address) {
                    found.push(address);
                }
            }
        }
    }
    if found.is_empty() {
        found.push(Ipv4Addr::LOCALHOST);
    }
    found
}

fn load_pairing_code(folder: &Path) -> Result<String, String> {
    let path = folder.join("pairing-code");
    let read = || fs::read_to_string(&path).and_then(|value| {
        let value = value.trim();
        if value.len() == 24 && value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            Ok(value.to_owned())
        } else {
            Err(std::io::Error::new(ErrorKind::InvalidData, "保存的配对码格式无效"))
        }
    });
    match read() {
        Ok(code) => return Ok(code),
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(error) => return Err(error.to_string()),
    }
    let code = LanAccess::generate_pairing_code().map_err(|error| error.to_string())?;
    let mut file = tempfile::NamedTempFile::new_in(folder).map_err(|error| error.to_string())?;
    file.write_all(code.as_bytes()).and_then(|()| file.as_file().sync_all()).map_err(|error| error.to_string())?;
    match file.persist_noclobber(&path) {
        Ok(_) => Ok(code),
        Err(error) if error.error.kind() == ErrorKind::AlreadyExists => read().map_err(|error| error.to_string()),
        Err(error) => Err(error.error.to_string()),
    }
}

fn allocated(value: String) -> *mut c_char {
    CString::new(value.replace('\0', "�")).expect("NUL removed").into_raw()
}

fn status(action: impl FnOnce() -> Result<(), String>) -> *mut c_char {
    match catch_unwind(AssertUnwindSafe(action)) {
        Ok(Ok(())) => ptr::null_mut(),
        Ok(Err(error)) => allocated(error),
        Err(_) => allocated("接收器发生内部错误".into()),
    }
}

fn response(action: impl FnOnce() -> Result<Value, String>) -> *mut c_char {
    let result = match catch_unwind(AssertUnwindSafe(action)) {
        Ok(Ok(value)) => value,
        Ok(Err(error)) => json!({ "error": error }),
        Err(_) => json!({ "error": "接收器发生内部错误" }),
    };
    allocated(result.to_string())
}

unsafe fn text<'a>(input: *const c_char) -> Result<&'a str, String> {
    if input.is_null() {
        return Err("缺少参数".into());
    }
    CStr::from_ptr(input).to_str().map_err(|error| error.to_string())
}

unsafe fn host<'a>(input: *mut Host) -> Result<&'a Host, String> {
    input.as_ref().ok_or_else(|| "接收器尚未创建".into())
}

/// Strings are NUL-terminated UTF-8. Free returned strings with `pnb_receiver_free_string`.
/// `destroy` requires exclusive access. Other calls may be made concurrently.
#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_create(directory: *const c_char, out: *mut *mut Host) -> *mut c_char {
    status(|| {
        if out.is_null() {
            return Err("缺少接收器输出地址".into());
        }
        out.write(ptr::null_mut());
        let directory = text(directory)?;
        if directory.is_empty() {
            return Err("接收器数据目录为空".into());
        }
        out.write(Box::into_raw(Box::new(Host::create(Path::new(directory))?)));
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_start(input: *mut Host, patch: *const c_char) -> *mut c_char {
    status(|| host(input)?.start(text(patch)?))
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_configure(input: *mut Host, patch: *const c_char) -> *mut c_char {
    status(|| host(input)?.configure(text(patch)?))
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_annotation_capture(
    input: *mut Host, windows: *const isize, count: u32,
) -> *mut c_char {
    status(|| {
        let host = host(input)?;
        if count > 128 || (count != 0 && windows.is_null()) {
            return Err("批注输入窗口列表无效".into());
        }
        let windows = if count == 0 { &[] } else { std::slice::from_raw_parts(windows, count as usize) };
        host.signal.set_capture_windows(windows);
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_take_barrel_gestures(input: *mut Host) -> u32 {
    catch_unwind(AssertUnwindSafe(|| host(input).map_or(0, |host| host.gestures.take()))).unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_take_eraser_intent(
    input: *mut Host, window: isize, x: i32, y: i32, message_time: u32,
) -> u8 {
    catch_unwind(AssertUnwindSafe(|| {
        u8::from(host(input).is_ok_and(|host| host.signal.take_eraser_intent(window, x, y, message_time)))
    })).unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_take_canvas_navigation(input: *mut Host, output: *mut CanvasCommand) -> u8 {
    if output.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(command) = host(input).ok().and_then(|host| host.signal.take_canvas_navigation()) else {
            return 0;
        };
        output.write(command);
        1
    })).unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_stop(input: *mut Host) -> *mut c_char {
    status(|| host(input)?.stop())
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_disconnect(input: *mut Host, connection_id: *const c_char) -> *mut c_char {
    status(|| { host(input)?.disconnect(text(connection_id)?); Ok(()) })
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_snapshot(input: *mut Host) -> *mut c_char {
    response(|| Ok(host(input)?.snapshot()))
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_pairing(input: *mut Host, address: *const c_char) -> *mut c_char {
    response(|| host(input)?.pairing(text(address)?))
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_renew_pairing(input: *mut Host) -> *mut c_char {
    status(|| host(input)?.renew_pairing())
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_reset_diagnostics(input: *mut Host) -> *mut c_char {
    status(|| {
        host(input)?.signal.data.lock().unwrap().diagnostics = Default::default();
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_free_string(value: *mut c_char) {
    if !value.is_null() {
        drop(CString::from_raw(value));
    }
}

#[no_mangle]
pub unsafe extern "C" fn pnb_receiver_destroy(input: *mut Host) {
    if !input.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(input))));
    }
}
