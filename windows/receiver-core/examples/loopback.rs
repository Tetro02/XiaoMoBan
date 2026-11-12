//! Manual compatibility probe. Uses ephemeral loopback ports and never injects input.
use std::{io::{self, BufRead}, path::PathBuf};

use pennote_bridge_receiver::{LanAccess, Receiver, ReceiverError, ServerConfig, ServerIdentity};

const PROBE_CODE: &str = "AABBCCDD1133";

#[tokio::main]
async fn main() -> Result<(), ReceiverError> {
    let identity_directory = std::env::args_os().nth(1).map(PathBuf::from)
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "supply a local identity directory"))?;
    let identity = ServerIdentity::load_or_create(identity_directory)?;
    let receiver = Receiver::new(|event| println!("{event:?}")).start(identity, ServerConfig {
        lan: Some(([127, 0, 0, 1], 0).into()),
        usb: Some(([127, 0, 0, 1], 0).into()),
        lan_access: LanAccess::PairingCode(PROBE_CODE.into()),
        ..Default::default()
    }).await?;
    println!("LAN {} USB {} SHA256 {}", receiver.lan_addr.unwrap(), receiver.usb_addr.unwrap(),
        receiver.fingerprint_sha256());
    tokio::task::spawn_blocking(|| io::stdin().lock().lines().next()).await?;
    receiver.shutdown().await
}
