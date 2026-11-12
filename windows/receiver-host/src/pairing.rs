use std::future::Future;
use std::io;
use std::net::{Ipv4Addr, SocketAddr};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ring::aead::{Aad, LessSafeKey, Nonce, UnboundKey, AES_256_GCM};
use sha2::{Digest, Sha256};
use srp::{ClientG2048, ServerG2048};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::{JoinHandle, JoinSet};
use tokio::time::timeout;

const MAGIC: &[u8; 4] = b"PNB1";
const IDENTITY: &[u8] = b"PenNoteBridge";
const CREDENTIAL_LABEL: &[u8] = b"PNB1 credential encryption";
const GROUP_BYTES: usize = 256;
const LIFETIME: Duration = Duration::from_secs(120);
const CONNECTION_TIMEOUT: Duration = Duration::from_secs(6);
const MAX_ATTEMPTS: u8 = 8;
const MAX_CONNECTIONS: usize = 12;

pub(crate) const PORT: u16 = 57213;

struct Window {
    code: String,
    salt: [u8; 16],
    verifier: Vec<u8>,
    expires: Instant,
    attempts: u8,
}

impl Window {
    fn new() -> io::Result<Self> {
        let code = pairing_code()?;
        let salt = random()?;
        let verifier = ClientG2048::<Sha256>::new().compute_verifier(IDENTITY, code.as_bytes(), &salt);
        Ok(Self { code, salt, verifier, expires: Instant::now() + LIFETIME, attempts: 0 })
    }

    fn available(&self) -> bool {
        self.attempts < MAX_ATTEMPTS && self.expires > Instant::now()
    }
}

struct State {
    window: Mutex<Window>,
    credentials: Vec<u8>,
}

pub(crate) struct Pairing {
    pub(crate) address: SocketAddr,
    state: Arc<State>,
    task: JoinHandle<()>,
}

impl Pairing {
    pub(crate) async fn start(port: u16, fingerprint: &str, token: &str,
        stopped: impl Future<Output = ()> + Send + 'static,
    ) -> io::Result<Self> {
        let listener = TcpListener::bind((Ipv4Addr::UNSPECIFIED, port)).await?;
        let address = listener.local_addr()?;
        let mut credentials = Vec::with_capacity(88);
        credentials.extend_from_slice(fingerprint.as_bytes());
        credentials.extend_from_slice(token.as_bytes());
        let state = Arc::new(State { window: Mutex::new(Window::new()?), credentials });
        let worker = state.clone();
        let task = tokio::spawn(async move {
            tokio::select! {
                _ = stopped => {}
                _ = serve(listener, worker) => {}
            }
        });
        Ok(Self { address, state, task })
    }

    pub(crate) fn renew(&self) -> io::Result<()> {
        *self.state.window.lock().unwrap() = Window::new()?;
        Ok(())
    }

    pub(crate) fn code(&self) -> (Option<String>, u64) {
        let window = self.state.window.lock().unwrap();
        if !window.available() {
            return (None, 0);
        }
        let milliseconds = window.expires.saturating_duration_since(Instant::now()).as_millis() as u64;
        (Some(window.code.clone()), milliseconds.div_ceil(1000))
    }

    pub(crate) async fn stop(self) {
        self.task.abort();
        let _ = self.task.await;
    }
}

async fn serve(listener: TcpListener, state: Arc<State>) {
    let mut connections = JoinSet::new();
    loop {
        tokio::select! {
            _ = connections.join_next(), if !connections.is_empty() => {}
            peer = listener.accept() => {
                let Ok((stream, _)) = peer else { break };
                if connections.len() < MAX_CONNECTIONS {
                    let state = state.clone();
                    connections.spawn(async move {
                        let _ = timeout(CONNECTION_TIMEOUT, exchange(stream, state)).await;
                    });
                }
            }
        }
    }
}

async fn exchange(mut stream: TcpStream, state: Arc<State>) -> io::Result<()> {
    let mut header = [0; 4 + GROUP_BYTES];
    stream.read_exact(&mut header).await?;
    if &header[..MAGIC.len()] != MAGIC {
        return Ok(());
    }
    let padded_a = &header[MAGIC.len()..];
    let Some(first_nonzero) = padded_a.iter().position(|&byte| byte != 0) else { return Ok(()) };
    let a = &padded_a[first_nonzero..];
    let (salt, verifier) = {
        let mut window = state.window.lock().unwrap();
        if !window.available() {
            return Ok(());
        }
        window.attempts += 1;
        (window.salt, window.verifier.clone())
    };

    let mut secret: [u8; 48] = random()?;
    while secret.iter().all(|&byte| byte == 0) {
        secret = random()?;
    }
    let server = ServerG2048::<Sha256>::new();
    let Ok(proof) = server.process_reply(IDENTITY, &salt, &secret, &verifier, a) else { return Ok(()) };
    let b = server.compute_public_ephemeral(&secret, &verifier);
    let mut response = [0; 16 + GROUP_BYTES];
    response[..salt.len()].copy_from_slice(&salt);
    let b_start = response.len() - b.len();
    response[b_start..].copy_from_slice(&b);
    stream.write_all(&response).await?;

    let mut client_proof = [0; 32];
    stream.read_exact(&mut client_proof).await?;
    let Ok(session_key) = proof.verify_client(&client_proof) else { return Ok(()) };
    {
        let window = state.window.lock().unwrap();
        if window.salt != salt || window.expires <= Instant::now() {
            return Ok(());
        }
    }

    let mut hash = Sha256::new();
    hash.update(CREDENTIAL_LABEL);
    hash.update(session_key);
    let key = UnboundKey::new(&AES_256_GCM, &hash.finalize()).map_err(|_| crypto_error())?;
    let nonce = random()?;
    let mut credentials = state.credentials.clone();
    LessSafeKey::new(key).seal_in_place_append_tag(Nonce::assume_unique_for_key(nonce), Aad::from(MAGIC), &mut credentials)
        .map_err(|_| crypto_error())?;
    let mut reply = Vec::with_capacity(proof.proof().len() + nonce.len() + credentials.len());
    reply.extend_from_slice(proof.proof());
    reply.extend_from_slice(&nonce);
    reply.extend_from_slice(&credentials);
    stream.write_all(&reply).await?;
    stream.shutdown().await
}

fn random<const N: usize>() -> io::Result<[u8; N]> {
    let mut bytes = [0; N];
    getrandom::fill(&mut bytes).map_err(|error| io::Error::other(error.to_string()))?;
    Ok(bytes)
}

fn pairing_code() -> io::Result<String> {
    // Reject the tail before reducing to preserve a uniform six-digit distribution.
    const LIMIT: u32 = 1_000_000;
    const UPPER: u32 = (u32::MAX / LIMIT) * LIMIT;
    loop {
        let sample = u32::from_le_bytes(random()?);
        if sample < UPPER {
            return Ok(format!("{:06}", sample % LIMIT));
        }
    }
}

fn crypto_error() -> io::Error {
    io::Error::other("配对凭据加密失败")
}
