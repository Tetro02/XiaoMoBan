use std::{net::SocketAddr, time::Duration};

use pennote_bridge_receiver::{
    protocol::{
        vt_service_client::VtServiceClient, AbsoluteMouseEvent, CanvasNavigation, Empty, HandshakeRequest,
        KeyboardShortcut, PenEvent,
    },
    DisplayInfo, InputMode, LanAccess, PointerFrame, Receiver, ReceiverError, ReceiverEvent,
    ServerConfig, ServerIdentity, Shortcut, Transport, PAIRING_HEADER,
};
use tokio::sync::mpsc;
use tokio_stream::wrappers::ReceiverStream;
use tonic::{
    transport::{Certificate, Channel, ClientTlsConfig, Endpoint},
    Code, Request,
};

const CODE: &str = "AABBCCDD1133";

fn request<T>(body: T, id: &'static str, code: Option<&'static str>) -> Request<T> {
    let mut request = Request::new(body);
    request.metadata_mut().insert("x-vt-client-id", "test-tablet".parse().unwrap());
    request.metadata_mut().insert("x-vt-connection-id", id.parse().unwrap());
    if let Some(code) = code {
        request.metadata_mut().insert(PAIRING_HEADER, code.parse().unwrap());
    }
    request
}

fn greeting() -> HandshakeRequest {
    HandshakeRequest {
        client_display_name: "Test tablet".into(),
        client_platform: "android".into(),
        client_version: "test".into(),
        supported_protocol_versions: vec![1, 2],
    }
}

async fn connect(addr: SocketAddr, certificate: &[u8]) -> Result<VtServiceClient<Channel>, ReceiverError> {
    let channel = Endpoint::from_shared(format!("https://{addr}"))?
        .tls_config(ClientTlsConfig::new().domain_name("localhost")
            .ca_certificate(Certificate::from_pem(certificate)))?
        .connect_timeout(Duration::from_secs(3))
        .connect()
        .await?;
    Ok(VtServiceClient::new(channel))
}

async fn event(events: &mut mpsc::UnboundedReceiver<ReceiverEvent>) -> ReceiverEvent {
    tokio::time::timeout(Duration::from_secs(4), events.recv()).await.unwrap().unwrap()
}

async fn frame(events: &mut mpsc::UnboundedReceiver<ReceiverEvent>, transport: Transport) -> PointerFrame {
    match event(events).await {
        ReceiverEvent::Frame { transport: actual, frame, .. } => {
            assert_eq!(actual, transport);
            frame
        }
        other => panic!("expected pointer frame, received {other:?}"),
    }
}

#[tokio::test]
async fn tls_lan_pairing_usb_and_pointer_lifecycle() -> Result<(), ReceiverError> {
    let directory = tempfile::tempdir()?;
    let identity = ServerIdentity::load_or_create(directory.path())?;
    let certificate = identity.certificate_pem().to_vec();
    let fingerprint = identity.fingerprint_sha256().to_string();
    assert_eq!(fingerprint.len(), 64);
    assert_eq!(ServerIdentity::load_or_create(directory.path())?.fingerprint_sha256(), fingerprint);
    assert_eq!(LanAccess::generate_pairing_code()?.len(), 24);

    let (events_tx, mut events) = mpsc::unbounded_channel();
    let receiver = Receiver::new(move |event| { let _ = events_tx.send(event); })
        .start(identity, ServerConfig {
            lan: Some(([127, 0, 0, 1], 0).into()),
            usb: Some(([127, 0, 0, 1], 0).into()),
            lan_access: LanAccess::PairingCode(CODE.into()),
            display: DisplayInfo { width: 1920, height: 1080,
                keyboard_shortcuts: true, canvas_navigation: true, ..Default::default() },
        }).await?;
    assert_eq!(receiver.fingerprint_sha256(), fingerprint);

    let mut lan = connect(receiver.lan_addr.unwrap(), &certificate).await?;
    let denied = lan.handshake(request(greeting(), "lan-session", None)).await.unwrap_err();
    assert_eq!(denied.code(), Code::Unauthenticated);
    let chord = KeyboardShortcut { usage: 29, modifiers: 1 };
    let denied = lan.send_shortcut(request(chord, "lan-session", Some("WRONGCODE000"))).await.unwrap_err();
    assert_eq!(denied.code(), Code::Unauthenticated);
    let denied = lan.send_shortcut(request(chord, "lan-session", Some(CODE))).await.unwrap_err();
    assert_eq!(denied.code(), Code::FailedPrecondition);
    let navigation = CanvasNavigation { pan_x: 0.1, pan_y: -0.15, scale: 1.2, focus_x: 0.4, focus_y: 0.5, action: 0 };
    let denied = lan.navigate_canvas(request(navigation, "lan-session", Some("WRONGCODE000"))).await.unwrap_err();
    assert_eq!(denied.code(), Code::Unauthenticated);
    let denied = lan.handshake(request(greeting(), "lan-session", Some("WRONGCODE000"))).await.unwrap_err();
    assert_eq!(denied.code(), Code::Unauthenticated);
    assert!(receiver.clients().is_empty());
    let mut unsupported = greeting();
    unsupported.supported_protocol_versions = vec![3];
    assert!(!lan.handshake(request(unsupported, "lan-session", Some(CODE))).await?.get_ref().compatible);
    let reply = lan.handshake(request(greeting(), "lan-session", Some(CODE))).await?.into_inner();
    assert_eq!(reply.negotiated_protocol_version, 2);
    let display = reply.server_info.unwrap();
    assert_eq!(display.width, 1920);
    assert!(display.keyboard_shortcuts);
    assert!(display.canvas_navigation);
    assert!(matches!(event(&mut events).await, ReceiverEvent::Connected { transport: Transport::Lan, .. }));
    let mut observer = lan.observe_server_info(request(Empty {}, "lan-session", Some(CODE))).await?.into_inner();
    assert_eq!(observer.message().await?.unwrap().height, 1080);
    receiver.set_display_info(DisplayInfo { width: 2560, height: 1440, ..display });
    assert_eq!(observer.message().await?.unwrap().width, 2560);

    let (pen_tx, pen_rx) = mpsc::channel(4);
    let mut pen_client = lan.clone();
    let pen = tokio::spawn(async move {
        pen_client.stream_pen_events(request(ReceiverStream::new(pen_rx), "lan-session", Some(CODE))).await
    });
    pen_tx.send(PenEvent {
        status_mask: 0x2f,
        x: 32767, y: 16384, pressure: 8192,
        tilt_x: -24, tilt_y: 43, twist: 121, client_send_us: 19, hover_pan_deadzone_px: Some(27),
    }).await?;
    let drawing = frame(&mut events, Transport::Lan).await;
    assert_eq!(drawing.mode, InputMode::Pen);
    assert!(drawing.in_range && drawing.contact && drawing.barrel && drawing.inverted && drawing.eraser);
    assert_eq!((drawing.x, drawing.tilt_x, drawing.tilt_y, drawing.rotation), (1.0, -24, 43, 121));
    assert_eq!(drawing.client_time_us, 19);
    assert_eq!(drawing.hover_pan_deadzone_px, Some(27));
    assert!((drawing.pressure - 0.25).abs() < 0.001);
    for command in [KeyboardShortcut { usage: 2, modifiers: 1 }, KeyboardShortcut { usage: 29, modifiers: 16 }] {
        let rejected = lan.send_shortcut(request(command, "lan-session", Some(CODE))).await.unwrap_err();
        assert_eq!(rejected.code(), Code::InvalidArgument);
    }
    lan.send_shortcut(request(chord, "lan-session", Some(CODE))).await?;
    assert!(matches!(event(&mut events).await,
        ReceiverEvent::Shortcut { transport: Transport::Lan, shortcut, .. }
            if shortcut == Shortcut::new(29, 1).unwrap()));
    let invalid = CanvasNavigation { scale: f32::NAN, ..navigation };
    let rejected = lan.navigate_canvas(request(invalid, "lan-session", Some(CODE))).await.unwrap_err();
    assert_eq!(rejected.code(), Code::InvalidArgument);
    let invalid = CanvasNavigation { action: 2, ..navigation };
    let rejected = lan.navigate_canvas(request(invalid, "lan-session", Some(CODE))).await.unwrap_err();
    assert_eq!(rejected.code(), Code::InvalidArgument);
    lan.navigate_canvas(request(navigation, "lan-session", Some(CODE))).await?;
    assert!(matches!(event(&mut events).await,
        ReceiverEvent::NavigateCanvas { transport: Transport::Lan, command, .. }
            if command.pan_x == navigation.pan_x && command.scale == navigation.scale));
    assert!(receiver.disconnect(Transport::Lan, "lan-session"));
    let release = frame(&mut events, Transport::Lan).await;
    assert!(!release.in_range && !release.contact && !release.barrel);
    assert!(matches!(event(&mut events).await, ReceiverEvent::Disconnected { transport: Transport::Lan, .. }));
    assert_eq!(pen.await?.unwrap_err().code(), Code::Cancelled);
    drop(pen_tx);
    assert!(observer.message().await?.is_none());

    let mut usb = connect(receiver.usb_addr.unwrap(), &certificate).await?;
    assert!(usb.handshake(request(greeting(), "usb-session", None)).await?.get_ref().compatible);
    assert!(matches!(event(&mut events).await, ReceiverEvent::Connected { transport: Transport::Usb, .. }));
    let observer = usb.observe_server_info(request(Empty {}, "usb-session", None)).await?.into_inner();
    usb.send_shortcut(request(chord, "usb-session", None)).await?;
    assert!(matches!(event(&mut events).await, ReceiverEvent::Shortcut { transport: Transport::Usb, .. }));
    let secondary_click = CanvasNavigation { action: 1, scale: 1.0, ..Default::default() };
    usb.navigate_canvas(request(secondary_click, "usb-session", None)).await?;
    assert!(matches!(event(&mut events).await,
        ReceiverEvent::NavigateCanvas { transport: Transport::Usb, command, .. } if command.action == 1));
    let motion = AbsoluteMouseEvent { buttons: 3, x: 32767, y: 0, client_send_us: 21, ..Default::default() };
    usb.stream_absolute_mouse_events(request(tokio_stream::iter([motion]), "usb-session", None)).await?;
    let click = frame(&mut events, Transport::Usb).await;
    assert_eq!(click.mode, InputMode::AbsoluteMouse);
    assert!(click.left_button && click.right_button && !click.middle_button);
    let release = frame(&mut events, Transport::Usb).await;
    assert!(!release.left_button && !release.right_button);
    let hover = AbsoluteMouseEvent { buttons: 0, x: 800, y: 1600, relative: true,
        relative_active: true, client_send_us: 22, relative_gain: 1.75 };
    let down = AbsoluteMouseEvent { buttons: 1, client_send_us: 23, ..hover };
    let lift = AbsoluteMouseEvent { client_send_us: 24, ..hover };
    let exit = AbsoluteMouseEvent { buttons: 0, x: 800, y: 1600, relative: true,
        relative_active: false, client_send_us: 25, ..Default::default() };
    usb.stream_absolute_mouse_events(request(tokio_stream::iter([hover, down, lift, exit]), "usb-session", None)).await?;
    let hover = frame(&mut events, Transport::Usb).await;
    assert_eq!(hover.mode, InputMode::RelativeMouse);
    assert!(hover.in_range && !hover.contact && !hover.left_button);
    assert_eq!(hover.relative_gain, 1.75);
    let relative = frame(&mut events, Transport::Usb).await;
    assert_eq!(relative.mode, InputMode::RelativeMouse);
    assert!(relative.in_range && relative.contact && relative.left_button);
    let lift = frame(&mut events, Transport::Usb).await;
    assert_eq!(lift.mode, InputMode::RelativeMouse);
    assert!(lift.in_range && !lift.contact && !lift.left_button);
    let exit = frame(&mut events, Transport::Usb).await;
    assert!(!exit.in_range && !exit.contact && !exit.left_button);
    assert_eq!(exit.relative_gain, 1.0);
    let (mouse_tx, mouse_rx) = mpsc::channel(2);
    let mut mouse_client = usb.clone();
    let mouse_stream = tokio::spawn(async move {
        mouse_client.stream_absolute_mouse_events(request(ReceiverStream::new(mouse_rx), "usb-session", None)).await
    });
    mouse_tx.send(down).await?;
    assert!(frame(&mut events, Transport::Usb).await.left_button);
    let pen_stream = tokio::spawn(async move {
        let contact = PenEvent { status_mask: 0x21, ..Default::default() };
        usb.stream_pen_events(request(tokio_stream::iter([contact]), "usb-session", None)).await
    });
    tokio::time::sleep(Duration::from_millis(220)).await;
    assert!(!pen_stream.is_finished(), "successor should wait for the same tablet's old stream");
    drop(mouse_tx);
    let mouse_release = frame(&mut events, Transport::Usb).await;
    assert_eq!(mouse_release.mode, InputMode::RelativeMouse);
    assert!(!mouse_release.left_button);
    let pen_contact = frame(&mut events, Transport::Usb).await;
    assert_eq!(pen_contact.mode, InputMode::Pen);
    assert!(pen_contact.contact);
    assert!(!frame(&mut events, Transport::Usb).await.contact);
    mouse_stream.await??;
    pen_stream.await??;
    drop(observer);
    receiver.shutdown().await?;
    Ok(())
}

#[tokio::test]
async fn configuration_refuses_nonlocal_usb_and_keeps_lan_disabled() -> Result<(), ReceiverError> {
    let directory = tempfile::tempdir()?;
    let identity = ServerIdentity::load_or_create(directory.path())?;
    let bad_usb = Receiver::new(|_| {}).start(identity, ServerConfig {
        usb: Some(([0, 0, 0, 0], 0).into()),
        ..Default::default()
    }).await;
    assert!(bad_usb.is_err());
    let identity = ServerIdentity::load_or_create(directory.path())?;
    let certificate = identity.certificate_pem().to_vec();
    let receiver = Receiver::new(|_| {}).start(identity, ServerConfig {
        lan: Some(([127, 0, 0, 1], 0).into()),
        usb: None,
        ..Default::default()
    }).await?;
    let mut client = connect(receiver.lan_addr.unwrap(), &certificate).await?;
    assert_eq!(client.handshake(request(greeting(), "disabled-session", None)).await.unwrap_err().code(),
        Code::PermissionDenied);
    receiver.shutdown().await?;
    Ok(())
}

#[tokio::test]
async fn first_wired_session_keeps_only_its_transport() -> Result<(), ReceiverError> {
    let directory = tempfile::tempdir()?;
    let identity = ServerIdentity::load_or_create(directory.path())?;
    let certificate = identity.certificate_pem().to_vec();
    let (events_tx, mut events) = mpsc::unbounded_channel();
    let receiver = Receiver::new(move |event| { let _ = events_tx.send(event); })
        .first_wired_transport_wins(true)
        .start(identity, ServerConfig {
            lan: Some(([127, 0, 0, 1], 0).into()),
            usb: Some(([127, 0, 0, 1], 0).into()),
            lan_access: LanAccess::PairingCode(CODE.into()),
            ..Default::default()
        }).await?;
    let lan_addr = receiver.lan_addr.unwrap();
    let usb_addr = receiver.usb_addr.unwrap();
    let mut lan = connect(lan_addr, &certificate).await?;
    let mut usb = connect(usb_addr, &certificate).await?;
    assert!(lan.handshake(request(greeting(), "bad-lan", None)).await.is_err());
    let mut unsupported = greeting();
    unsupported.supported_protocol_versions = vec![3];
    assert!(!usb.handshake(request(unsupported, "bad-usb", None)).await?.get_ref().compatible);
    assert!(receiver.wired_winner().is_none());
    assert!(receiver.lan_address().is_some() && receiver.usb_address().is_some());

    let (lan_result, usb_result) = tokio::join!(
        lan.handshake(request(greeting(), "lan-race", Some(CODE))),
        usb.handshake(request(greeting(), "usb-race", None)),
    );
    assert_ne!(lan_result.is_ok(), usb_result.is_ok());
    let (lan_open, usb_open, winner, clients) = receiver.live_wired();
    let winner = winner.unwrap();
    let (winner_client, id, code, loser_addr) = if winner == Transport::Lan {
        assert!(lan_open.is_some() && usb_open.is_none());
        (&mut lan, "lan-race", Some(CODE), usb_addr)
    } else {
        assert!(usb_open.is_some() && lan_open.is_none());
        (&mut usb, "usb-race", None, lan_addr)
    };
    assert_eq!(clients.len(), 1);
    assert_eq!(clients[0].connection_id, id);
    assert!(matches!(event(&mut events).await, ReceiverEvent::Connected { transport, .. } if transport == winner));
    tokio::time::timeout(Duration::from_secs(6), async {
        while tokio::net::TcpStream::connect(loser_addr).await.is_ok() {
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    }).await.expect("losing wired socket should stop accepting connections");

    let mut observer = winner_client.observe_server_info(request(Empty {}, id, code)).await?.into_inner();
    assert!(observer.message().await?.is_some());
    let down = PenEvent { status_mask: 0x23, x: 1200, y: 2400, pressure: 9000, ..Default::default() };
    winner_client.stream_pen_events(request(tokio_stream::iter([down]), id, code)).await?;
    let down = frame(&mut events, winner).await;
    assert!(down.contact && down.barrel && down.pressure > 0.0);
    assert!(!frame(&mut events, winner).await.contact);
    assert!(receiver.disconnect(winner, id));
    assert!(matches!(event(&mut events).await, ReceiverEvent::Disconnected { transport, .. } if transport == winner));
    assert!(receiver.live_wired().3.is_empty());
    assert_eq!(receiver.wired_winner(), Some(winner));
    drop(observer);
    receiver.shutdown().await?;
    Ok(())
}
