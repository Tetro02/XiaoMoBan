mod identity;

use std::{
    collections::HashMap,
    fmt,
    future::Future,
    io,
    net::SocketAddr,
    pin::Pin,
    sync::{Arc, Mutex, MutexGuard},
    time::{Duration, Instant},
};

use futures_core::Stream;
use serde::Serialize;
use subtle::ConstantTimeEq;
use tokio::{net::TcpListener, sync::watch, task::JoinHandle};
use tokio_stream::wrappers::TcpListenerStream;
use tokio_util::sync::CancellationToken;
use tonic::{transport::{Server, ServerTlsConfig}, Request, Response, Status, Streaming};

pub use identity::ServerIdentity;

pub mod protocol {
    tonic::include_proto!("vt");
}

pub use protocol::ServerInfo as DisplayInfo;
use protocol::{
    vt_service_server::{VtService, VtServiceServer},
    AbsoluteMouseEvent, CanvasNavigation, Empty, HandshakeRequest, HandshakeResponse, KeyboardShortcut, PenEvent,
};

pub type ReceiverError = Box<dyn std::error::Error + Send + Sync>;
pub const DEFAULT_LAN_PORT: u16 = 57211;
pub const DEFAULT_USB_PORT: u16 = 57212;
pub const PAIRING_HEADER: &str = "x-pzb-pairing-code";

const CLIENT_LIMIT: usize = 64;
const MAX_MESSAGE_SIZE: usize = 8 * 1024;
const STALE_HANDSHAKE: Duration = Duration::from_secs(15);
const AUTO_STALE_HANDSHAKE: Duration = Duration::from_secs(3);
const CONTACT_TIMEOUT: Duration = Duration::from_secs(2);
const INPUT_HANDOFF_TIMEOUT: Duration = Duration::from_secs(1);

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Transport {
    Lan,
    Usb,
    Bluetooth,
}

#[derive(Debug)]
pub struct ListenerBindError {
    pub transport: Transport,
    pub address: SocketAddr,
    pub error: io::Error,
}

impl fmt::Display for ListenerBindError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{:?} listener {}: {}", self.transport, self.address, self.error)
    }
}

impl std::error::Error for ListenerBindError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.error)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum InputMode {
    Pen,
    AbsoluteMouse,
    RelativeMouse,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PointerFrame {
    pub mode: InputMode,
    pub x: f32,
    pub y: f32,
    pub pressure: f32,
    pub tilt_x: i32,
    pub tilt_y: i32,
    pub rotation: u32,
    pub in_range: bool,
    pub contact: bool,
    pub barrel: bool,
    pub inverted: bool,
    pub eraser: bool,
    pub left_button: bool,
    pub right_button: bool,
    pub middle_button: bool,
    pub client_time_us: u64,
    pub relative_gain: f32,
    pub hover_pan_deadzone_px: Option<u32>,
}

impl PointerFrame {
    pub fn released(&self) -> Self {
        Self {
            pressure: 0.0, in_range: false, contact: false, barrel: false, inverted: false,
            eraser: false, left_button: false, right_button: false, middle_button: false,
            ..self.clone()
        }
    }

    fn pressed(&self) -> bool {
        self.contact || self.barrel || self.left_button || self.right_button || self.middle_button
    }

    fn needs_release(&self) -> bool {
        self.in_range || self.pressed()
    }
}

impl From<PenEvent> for PointerFrame {
    fn from(event: PenEvent) -> Self {
        let mask = event.status_mask;
        let in_range = mask & 0x20 != 0;
        let contact = in_range && mask & 0x09 != 0;
        Self {
            mode: InputMode::Pen,
            x: normalize(event.x),
            y: normalize(event.y),
            pressure: if contact { normalize(event.pressure) } else { 0.0 },
            tilt_x: event.tilt_x.clamp(-90, 90),
            tilt_y: event.tilt_y.clamp(-90, 90),
            rotation: event.twist % 360,
            in_range,
            contact,
            barrel: in_range && mask & 0x02 != 0,
            inverted: in_range && mask & 0x04 != 0,
            eraser: in_range && mask & 0x08 != 0,
            left_button: false,
            right_button: false,
            middle_button: false,
            client_time_us: event.client_send_us,
            relative_gain: 1.0,
            hover_pan_deadzone_px: event.hover_pan_deadzone_px.map(|px| px.min(96)),
        }
    }
}

impl From<AbsoluteMouseEvent> for PointerFrame {
    fn from(event: AbsoluteMouseEvent) -> Self {
        Self {
            mode: if event.relative { InputMode::RelativeMouse } else { InputMode::AbsoluteMouse },
            x: normalize(event.x),
            y: normalize(event.y),
            pressure: 0.0,
            tilt_x: 0,
            tilt_y: 0,
            rotation: 0,
            in_range: event.relative && event.relative_active,
            contact: event.relative && event.relative_active && event.buttons & 0x01 != 0,
            barrel: false,
            inverted: false,
            eraser: false,
            left_button: event.buttons & 0x01 != 0,
            right_button: event.buttons & 0x02 != 0,
            middle_button: event.buttons & 0x04 != 0,
            client_time_us: event.client_send_us,
            relative_gain: relative_gain(event.relative_gain),
            hover_pan_deadzone_px: None,
        }
    }
}

pub fn relative_gain(value: f32) -> f32 {
    if value.is_finite() && value > 0.0 { value.clamp(0.5, 2.5) } else { 1.0 }
}

fn normalize(axis: u32) -> f32 {
    axis.min(32767) as f32 / 32767.0
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientInfo {
    pub client_id: String,
    pub connection_id: String,
    pub display_name: String,
    pub platform: String,
    pub version: String,
    pub transport: Transport,
    pub peer: Option<SocketAddr>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ReceiverEvent {
    Connected { transport: Transport, client: ClientInfo },
    Disconnected { transport: Transport, client: ClientInfo },
    Frame { transport: Transport, client: ClientInfo, frame: PointerFrame },
    Shortcut { transport: Transport, client: ClientInfo, shortcut: Shortcut },
    NavigateCanvas { transport: Transport, client: ClientInfo, command: CanvasCommand },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct Shortcut {
    usage: u8,
    modifiers: u8,
}

impl Shortcut {
    pub fn new(usage: u32, modifiers: u32) -> Option<Self> {
        if !matches!(usage, 4..=41 | 43..=44 | 58..=69 | 79..=82) || modifiers > 0b1111 {
            return None;
        }
        Some(Self { usage: usage as u8, modifiers: modifiers as u8 })
    }

    pub fn usage(self) -> u8 { self.usage }

    pub fn modifiers(self) -> u8 { self.modifiers }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Serialize)]
pub struct CanvasCommand {
    pub pan_x: f32,
    pub pan_y: f32,
    pub scale: f32,
    pub focus_x: f32,
    pub focus_y: f32,
    pub action: u32,
}

impl CanvasCommand {
    pub fn new([pan_x, pan_y, scale, focus_x, focus_y]: [f32; 5], action: u32) -> Option<Self> {
        if !(-1.0..=1.0).contains(&pan_x) || !(-1.0..=1.0).contains(&pan_y)
            || !(0.5..=2.0).contains(&scale) || !(0.0..=1.0).contains(&focus_x)
            || !(0.0..=1.0).contains(&focus_y) || action > 1
        {
            return None;
        }
        Some(Self { pan_x, pan_y, scale, focus_x, focus_y, action })
    }
}

#[derive(Clone, Default)]
pub enum LanAccess {
    #[default]
    Disabled,
    Manual,
    PairingCode(String),
}

impl LanAccess {
    pub fn generate_pairing_code() -> Result<String, ReceiverError> {
        let mut bytes = [0u8; 12];
        getrandom::fill(&mut bytes).map_err(|error| io::Error::other(error.to_string()))?;
        Ok(bytes.iter().map(|byte| format!("{byte:02X}")).collect())
    }

    fn validate(&self) -> Result<(), ReceiverError> {
        if let Self::PairingCode(code) = self {
            if !(8..=128).contains(&code.len()) || !code.bytes().all(|b| b.is_ascii_graphic()) {
                return Err(io::Error::new(io::ErrorKind::InvalidInput,
                    "LAN pairing code must be 8 to 128 visible ASCII characters").into());
            }
        }
        Ok(())
    }
}

#[derive(Clone)]
pub struct ServerConfig {
    pub lan: Option<SocketAddr>,
    pub usb: Option<SocketAddr>,
    pub lan_access: LanAccess,
    pub display: DisplayInfo,
}

impl Default for ServerConfig {
    fn default() -> Self {
        Self {
            lan: None,
            usb: Some(([127, 0, 0, 1], DEFAULT_USB_PORT).into()),
            lan_access: LanAccess::Disabled,
            display: DisplayInfo {
                server_name: "小墨板".into(),
                host_os: "windows".into(),
                ..Default::default()
            },
        }
    }
}

type EventCallback = dyn Fn(ReceiverEvent) + Send + Sync;

pub struct Receiver {
    on_event: Arc<EventCallback>,
    first_wired_transport_wins: bool,
}

impl Receiver {
    pub fn new(callback: impl Fn(ReceiverEvent) + Send + Sync + 'static) -> Self {
        Self { on_event: Arc::new(callback), first_wired_transport_wins: false }
    }

    pub fn first_wired_transport_wins(mut self, enabled: bool) -> Self {
        self.first_wired_transport_wins = enabled;
        self
    }

    pub async fn start(self, identity: ServerIdentity, config: ServerConfig) -> Result<RunningReceiver, ReceiverError> {
        config.lan_access.validate()?;
        if config.usb.is_some_and(|address| !address.ip().is_loopback()) {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "USB must bind to loopback").into());
        }
        if config.lan.is_none() && config.usb.is_none() {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "no receiver listener was configured").into());
        }
        let lan = bind(config.lan, Transport::Lan).await?;
        let usb = bind(config.usb, Transport::Usb).await?;
        let lan_addr = lan.as_ref().map(TcpListener::local_addr).transpose()?;
        let usb_addr = usb.as_ref().map(TcpListener::local_addr).transpose()?;
        let fingerprint = identity.fingerprint_sha256().to_owned();
        let mut listeners = Vec::new();
        for (transport, listener) in [(Transport::Lan, lan), (Transport::Usb, usb)] {
            if let Some(listener) = listener {
                let server = Server::builder()
                    .tls_config(
                        ServerTlsConfig::new()
                            .identity(identity.tls_identity())
                            .timeout(Duration::from_secs(5)),
                    )?
                    .concurrency_limit_per_connection(8);
                listeners.push((transport, listener, server));
            }
        }
        let shutdown = CancellationToken::new();
        let shared = Arc::new(Shared {
            clients: Mutex::new(Clients::default()),
            display: watch::channel(config.display).0,
            lan_access: config.lan_access,
            on_event: self.on_event,
            shutdown: shutdown.clone(),
            lan_stop: shutdown.child_token(),
            usb_stop: shutdown.child_token(),
            first_wired_transport_wins: self.first_wired_transport_wins && lan_addr.is_some() && usb_addr.is_some(),
        });
        let mut tasks = Vec::new();
        for (transport, listener, mut server) in listeners {
            let service = VtServiceServer::new(Service { transport, shared: shared.clone() })
                .max_decoding_message_size(MAX_MESSAGE_SIZE);
            let signal = shared.transport_stop(transport).clone().cancelled_owned();
            tasks.push(tokio::spawn(async move {
                server.add_service(service)
                    .serve_with_incoming_shutdown(TcpListenerStream::new(listener), signal)
                    .await
                    .map_err(Into::into)
            }));
        }
        let cleanup = shared.clone();
        tasks.push(tokio::spawn(async move {
            let mut tick = tokio::time::interval(Duration::from_secs(2));
            loop {
                tokio::select! {
                    _ = cleanup.shutdown.cancelled() => break,
                    _ = tick.tick() => cleanup.prune(),
                }
            }
            cleanup.disconnect_all();
            Ok(())
        }));
        Ok(RunningReceiver { lan_addr, usb_addr, fingerprint, shared, tasks })
    }
}

async fn bind(addr: Option<SocketAddr>, transport: Transport) -> Result<Option<TcpListener>, ListenerBindError> {
    match addr {
        Some(address) => TcpListener::bind(address).await.map(Some)
            .map_err(|error| ListenerBindError { transport, address, error }),
        None => Ok(None),
    }
}

pub struct RunningReceiver {
    pub lan_addr: Option<SocketAddr>,
    pub usb_addr: Option<SocketAddr>,
    fingerprint: String,
    shared: Arc<Shared>,
    tasks: Vec<JoinHandle<Result<(), ReceiverError>>>,
}

impl RunningReceiver {
    pub fn lan_address(&self) -> Option<SocketAddr> {
        self.lan_addr.filter(|_| !self.shared.lan_stop.is_cancelled())
    }

    pub fn usb_address(&self) -> Option<SocketAddr> {
        self.usb_addr.filter(|_| !self.shared.usb_stop.is_cancelled())
    }

    pub fn wired_winner(&self) -> Option<Transport> {
        self.shared.clients().wired_winner
    }

    pub fn live_wired(&self) -> (Option<SocketAddr>, Option<SocketAddr>, Option<Transport>, Vec<ClientInfo>) {
        let clients = self.shared.clients();
        let connected = clients.sessions.values().filter(|session| !session.stop.is_cancelled())
            .map(|session| session.client.clone()).collect();
        (self.lan_address(), self.usb_address(), clients.wired_winner, connected)
    }

    pub fn lan_stopped(&self) -> impl Future<Output = ()> + Send + 'static {
        self.shared.lan_stop.clone().cancelled_owned()
    }

    pub fn fingerprint_sha256(&self) -> &str {
        &self.fingerprint
    }

    pub fn clients(&self) -> Vec<ClientInfo> {
        self.shared.clients().sessions.values().filter(|session| !session.stop.is_cancelled())
            .map(|session| session.client.clone()).collect()
    }

    pub fn set_display_info(&self, display: DisplayInfo) {
        self.shared.display.send_replace(display);
    }

    pub fn disconnect(&self, transport: Transport, connection_id: &str) -> bool {
        let key = self.shared.clients().sessions.keys()
            .find(|key| key.transport == transport && key.connection_id == connection_id).cloned();
        key.is_some_and(|key| self.shared.disconnect(&key))
    }

    pub async fn shutdown(mut self) -> Result<(), ReceiverError> {
        self.shared.shutdown.cancel();
        let mut result = Ok(());
        let deadline = tokio::time::Instant::now() + Duration::from_secs(3);
        for mut task in self.tasks.drain(..) {
            let task_result = match tokio::time::timeout_at(deadline, &mut task).await {
                Ok(Ok(result)) => result,
                Ok(Err(error)) => Err(error.into()),
                Err(_) => {
                    task.abort();
                    Err(io::Error::new(io::ErrorKind::TimedOut, "receiver shutdown timed out").into())
                }
            };
            if result.is_ok() {
                result = task_result;
            }
        }
        result
    }
}

impl Drop for RunningReceiver {
    fn drop(&mut self) {
        self.shared.shutdown.cancel();
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
struct ClientKey {
    transport: Transport,
    peer: SocketAddr,
    client_id: String,
    connection_id: String,
}

struct Session {
    client: ClientInfo,
    created: Instant,
    observer: bool,
    stop: CancellationToken,
}

#[derive(Default)]
struct Clients {
    sessions: HashMap<ClientKey, Session>,
    active: Option<ActiveInput>,
    wired_winner: Option<Transport>,
}

struct ActiveInput {
    key: ClientKey,
    finished: CancellationToken,
}

struct Shared {
    clients: Mutex<Clients>,
    display: watch::Sender<DisplayInfo>,
    lan_access: LanAccess,
    on_event: Arc<EventCallback>,
    shutdown: CancellationToken,
    lan_stop: CancellationToken,
    usb_stop: CancellationToken,
    first_wired_transport_wins: bool,
}

impl Shared {
    fn transport_stop(&self, transport: Transport) -> &CancellationToken {
        match transport {
            Transport::Lan => &self.lan_stop,
            Transport::Usb => &self.usb_stop,
            Transport::Bluetooth => &self.shutdown,
        }
    }

    fn claim_wired_transport(&self, clients: &mut Clients, transport: Transport) -> Result<(), Status> {
        if !self.first_wired_transport_wins {
            return Ok(());
        }
        match clients.wired_winner {
            Some(winner) if winner != transport => Err(Status::unavailable("another wired transport is connected")),
            Some(_) => Ok(()),
            None => {
                clients.wired_winner = Some(transport);
                let loser = if transport == Transport::Lan { Transport::Usb } else { Transport::Lan };
                self.transport_stop(loser).cancel();
                Ok(())
            }
        }
    }

    fn clients(&self) -> MutexGuard<'_, Clients> {
        self.clients.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    fn disconnect(&self, key: &ClientKey) -> bool {
        let mut clients = self.clients();
        let active = clients.active.as_ref().is_some_and(|input| &input.key == key);
        let Some(session) = clients.sessions.get(key) else { return false; };
        if session.stop.is_cancelled() {
            return false;
        }
        session.stop.cancel();
        if !active {
            let removed = clients.sessions.remove(key);
            drop(clients);
            self.disconnected(removed);
        }
        true
    }

    fn disconnected(&self, removed: Option<Session>) {
        if let Some(session) = removed {
            (self.on_event)(ReceiverEvent::Disconnected {
                transport: session.client.transport, client: session.client,
            });
        }
    }

    fn disconnect_all(&self) {
        let keys: Vec<_> = self.clients().sessions.keys().cloned().collect();
        for key in keys {
            self.disconnect(&key);
        }
    }

    fn prune(&self) {
        let mut clients = self.clients();
        let lifetime = if self.first_wired_transport_wins { AUTO_STALE_HANDSHAKE } else { STALE_HANDSHAKE };
        let stale: Vec<_> = clients.sessions.iter()
            .filter(|(key, session)| !session.observer && !clients.active.as_ref().is_some_and(|input| &input.key == *key)
                && session.created.elapsed() >= lifetime)
            .map(|(key, _)| key.clone()).collect();
        let removed: Vec<_> = stale.iter().filter_map(|key| clients.sessions.remove(key)).collect();
        drop(clients);
        for session in removed {
            session.stop.cancel();
            self.disconnected(Some(session));
        }
    }
}

struct Service {
    transport: Transport,
    shared: Arc<Shared>,
}

impl Service {
    fn authorize<T>(&self, request: &Request<T>) -> Result<ClientKey, Status> {
        if self.shared.transport_stop(self.transport).is_cancelled() {
            return Err(Status::unavailable("transport is stopping"));
        }
        let peer = request.remote_addr().ok_or_else(|| Status::unauthenticated("unknown peer"))?;
        let metadata = request.metadata();
        match self.transport {
            Transport::Usb if !peer.ip().is_loopback() =>
                return Err(Status::permission_denied("USB is local-only")),
            Transport::Lan => match &self.shared.lan_access {
                LanAccess::Disabled => return Err(Status::permission_denied("LAN input is disabled")),
                LanAccess::Manual => {}
                LanAccess::PairingCode(expected) => {
                    let supplied = metadata.get(PAIRING_HEADER).map(|value| value.as_encoded_bytes());
                    if !supplied.is_some_and(|supplied| bool::from(supplied.ct_eq(expected.as_bytes()))) {
                        return Err(Status::unauthenticated("incorrect pairing code"));
                    }
                }
            },
            _ => {}
        }
        let read_id = |header: &'static str| -> Result<String, Status> {
            let id = metadata.get(header).and_then(|value| value.to_str().ok())
                .ok_or_else(|| Status::unauthenticated(format!("missing {header}")))?;
            if id.is_empty() || id.len() > 128 || !id.bytes().all(|b| b.is_ascii_graphic()) {
                return Err(Status::invalid_argument(format!("invalid {header}")));
            }
            Ok(id.to_owned())
        };
        Ok(ClientKey {
            transport: self.transport, peer,
            client_id: read_id("x-vt-client-id")?,
            connection_id: read_id("x-vt-connection-id")?,
        })
    }

    async fn input(&self, key: &ClientKey) -> Result<InputLease, Status> {
        let deadline = tokio::time::Instant::now() + INPUT_HANDOFF_TIMEOUT;
        loop {
            let finished = {
                let mut clients = self.shared.clients();
                let session = clients.sessions.get(key)
                    .ok_or_else(|| Status::failed_precondition("handshake required"))?;
                if session.stop.is_cancelled() {
                    return Err(Status::cancelled("session closed"));
                }
                if let Some(active) = clients.active.as_ref() {
                    if &active.key != key {
                        return Err(Status::resource_exhausted("another tablet is sending input"));
                    }
                    active.finished.clone()
                } else {
                    let client = session.client.clone();
                    let stop = session.stop.clone();
                    clients.active = Some(ActiveInput { key: key.clone(), finished: CancellationToken::new() });
                    return Ok(InputLease { key: key.clone(), client, shared: self.shared.clone(), stop, last: None });
                }
            };
            // A pen/mouse change can start its new RPC before the same tablet's
            // previous RPC has sent its final release. Preserve that ordering.
            tokio::select! {
                _ = self.shared.shutdown.cancelled() => return Err(Status::cancelled("receiver stopped")),
                result = tokio::time::timeout_at(deadline, finished.cancelled()) => {
                    if result.is_err() {
                        return Err(Status::resource_exhausted("previous input stream is still active"));
                    }
                }
            }
        }
    }

    fn command_client(&self, key: &ClientKey, supported: bool) -> Result<ClientInfo, Status> {
        if !supported {
            return Err(Status::unimplemented("tablet command is unavailable"));
        }
        if self.transport == Transport::Lan && !matches!(&self.shared.lan_access, LanAccess::PairingCode(_)) {
            return Err(Status::permission_denied("tablet commands require LAN pairing"));
        }
        let clients = self.shared.clients();
        let session = clients.sessions.get(key)
            .ok_or_else(|| Status::failed_precondition("handshake required"))?;
        if session.stop.is_cancelled() {
            return Err(Status::cancelled("session closed"));
        }
        Ok(session.client.clone())
    }

    async fn receive<T: Into<PointerFrame>>(
        &self, request: Request<Streaming<T>>,
    ) -> Result<Response<Empty>, Status> {
        let key = self.authorize(&request)?;
        let mut lease = self.input(&key).await?;
        let mut stream = request.into_inner();
        loop {
            tokio::select! {
                biased;
                _ = lease.stop.cancelled() => return Err(Status::cancelled("session closed")),
                _ = self.shared.shutdown.cancelled() => return Err(Status::cancelled("receiver stopped")),
                message = stream.message() => match message? {
                    Some(frame) => lease.send(frame.into()),
                    None => break,
                },
                _ = tokio::time::sleep(CONTACT_TIMEOUT), if lease.last.as_ref().is_some_and(PointerFrame::pressed) => {
                    lease.release();
                }
            }
        }
        Ok(Response::new(Empty {}))
    }
}

#[tonic::async_trait]
impl VtService for Service {
    async fn handshake(&self, request: Request<HandshakeRequest>) -> Result<Response<HandshakeResponse>, Status> {
        let key = self.authorize(&request)?;
        let request = request.into_inner();
        let negotiated = request.supported_protocol_versions.iter().copied()
            .filter(|version| (1..=2).contains(version)).max().unwrap_or(0);
        if negotiated > 0 {
            let mut clients = self.shared.clients();
            if clients.sessions.get(&key).is_some_and(|session| session.stop.is_cancelled()) {
                return Err(Status::cancelled("session closed"));
            }
            if !clients.sessions.contains_key(&key) {
                if clients.sessions.len() >= CLIENT_LIMIT {
                    return Err(Status::resource_exhausted("too many receiver sessions"));
                }
                self.shared.claim_wired_transport(&mut clients, self.transport)?;
                let clean = |value: String| value.chars().filter(|ch| !ch.is_control()).take(128).collect();
                let client = ClientInfo {
                    client_id: key.client_id.clone(),
                    connection_id: key.connection_id.clone(),
                    display_name: clean(request.client_display_name),
                    platform: clean(request.client_platform),
                    version: clean(request.client_version),
                    transport: key.transport,
                    peer: Some(key.peer),
                };
                clients.sessions.insert(key, Session {
                    client: client.clone(), observer: false, created: Instant::now(), stop: CancellationToken::new(),
                });
                drop(clients);
                (self.shared.on_event)(ReceiverEvent::Connected { transport: self.transport, client });
            }
        }
        Ok(Response::new(HandshakeResponse {
            server_version: env!("CARGO_PKG_VERSION").into(),
            negotiated_protocol_version: negotiated,
            compatible: negotiated > 0,
            server_info: Some(self.shared.display.borrow().clone()),
            server_max_protocol_version: 2,
        }))
    }

    type ObserveServerInfoStream = Pin<Box<dyn Stream<Item = Result<DisplayInfo, Status>> + Send>>;

    async fn observe_server_info(&self, request: Request<Empty>) -> Result<Response<Self::ObserveServerInfoStream>, Status> {
        let key = self.authorize(&request)?;
        let stop = {
            let mut clients = self.shared.clients();
            let session = clients.sessions.get_mut(&key)
                .ok_or_else(|| Status::failed_precondition("handshake required"))?;
            if session.stop.is_cancelled() {
                return Err(Status::cancelled("session closed"));
            }
            if session.observer {
                return Err(Status::resource_exhausted("session already has a display observer"));
            }
            session.observer = true;
            session.stop.clone()
        };
        let lease = ObserverLease { key, shared: self.shared.clone() };
        let mut display = self.shared.display.subscribe();
        let shutdown = self.shared.shutdown.clone();
        let stream = async_stream::stream! {
            let _lease = lease;
            loop {
                let value = display.borrow_and_update().clone();
                yield Ok(value);
                tokio::select! {
                    _ = stop.cancelled() => break,
                    _ = shutdown.cancelled() => break,
                    changed = display.changed() => if changed.is_err() { break; }
                }
            }
        };
        Ok(Response::new(Box::pin(stream)))
    }

    async fn stream_pen_events(&self, request: Request<Streaming<PenEvent>>) -> Result<Response<Empty>, Status> {
        self.receive(request).await
    }

    async fn stream_absolute_mouse_events(
        &self, request: Request<Streaming<AbsoluteMouseEvent>>,
    ) -> Result<Response<Empty>, Status> {
        self.receive(request).await
    }

    async fn send_shortcut(&self, request: Request<KeyboardShortcut>) -> Result<Response<Empty>, Status> {
        let key = self.authorize(&request)?;
        let supported = self.shared.display.borrow().keyboard_shortcuts;
        let client = self.command_client(&key, supported)?;
        let command = request.into_inner();
        let shortcut = Shortcut::new(command.usage, command.modifiers)
            .ok_or_else(|| Status::invalid_argument("unsupported keyboard shortcut"))?;
        (self.shared.on_event)(ReceiverEvent::Shortcut { transport: self.transport, client, shortcut });
        Ok(Response::new(Empty {}))
    }

    async fn navigate_canvas(&self, request: Request<CanvasNavigation>) -> Result<Response<Empty>, Status> {
        let key = self.authorize(&request)?;
        let supported = self.shared.display.borrow().canvas_navigation;
        let client = self.command_client(&key, supported)?;
        let input = request.into_inner();
        let command = CanvasCommand::new([input.pan_x, input.pan_y, input.scale, input.focus_x, input.focus_y], input.action)
            .ok_or_else(|| Status::invalid_argument("invalid canvas navigation"))?;
        (self.shared.on_event)(ReceiverEvent::NavigateCanvas { transport: self.transport, client, command });
        Ok(Response::new(Empty {}))
    }
}

struct InputLease {
    key: ClientKey,
    client: ClientInfo,
    shared: Arc<Shared>,
    stop: CancellationToken,
    last: Option<PointerFrame>,
}

impl InputLease {
    fn send(&mut self, frame: PointerFrame) {
        self.last = Some(frame.clone());
        (self.shared.on_event)(ReceiverEvent::Frame {
            transport: self.key.transport, client: self.client.clone(), frame,
        });
    }

    fn release(&mut self) {
        if let Some(last) = self.last.take().filter(PointerFrame::needs_release) {
            (self.shared.on_event)(ReceiverEvent::Frame {
                transport: self.key.transport, client: self.client.clone(), frame: last.released(),
            });
        }
    }
}

impl Drop for InputLease {
    fn drop(&mut self) {
        self.release();
        let mut clients = self.shared.clients();
        if clients.active.as_ref().is_some_and(|input| input.key == self.key) {
            clients.active.take().unwrap().finished.cancel();
        }
        if self.stop.is_cancelled() {
            let removed = clients.sessions.remove(&self.key);
            drop(clients);
            self.shared.disconnected(removed);
        }
    }
}

struct ObserverLease {
    key: ClientKey,
    shared: Arc<Shared>,
}

impl Drop for ObserverLease {
    fn drop(&mut self) {
        self.shared.disconnect(&self.key);
    }
}
