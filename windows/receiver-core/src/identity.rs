use std::{fs, io, io::Write, path::Path};

use rcgen::generate_simple_self_signed;
use sha2::{Digest, Sha256};
use tonic::transport::Identity;

use crate::ReceiverError;

const IDENTITY_FILE: &str = "receiver-identity.pem";

#[derive(Clone)]
pub struct ServerIdentity {
    certificate: Vec<u8>,
    private_key: Vec<u8>,
    fingerprint: String,
}

impl ServerIdentity {
    pub fn load_or_create(directory: impl AsRef<Path>) -> Result<Self, ReceiverError> {
        let directory = directory.as_ref();
        fs::create_dir_all(directory)?;
        let path = directory.join(IDENTITY_FILE);
        match fs::read(&path) {
            Ok(contents) => Self::from_bundle(&contents),
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                let generated = generate_simple_self_signed(vec!["localhost".to_owned()])?;
                let mut pending = tempfile::NamedTempFile::new_in(directory)?;
                pending.write_all(generated.cert.pem().as_bytes())?;
                pending.write_all(generated.key_pair.serialize_pem().as_bytes())?;
                pending.as_file().sync_all()?;
                match pending.persist_noclobber(&path) {
                    Ok(_) => {}
                    Err(error) if error.error.kind() == io::ErrorKind::AlreadyExists => {}
                    Err(error) => return Err(error.error.into()),
                }
                Self::from_bundle(&fs::read(path)?)
            }
            Err(error) => Err(error.into()),
        }
    }

    fn from_bundle(contents: &[u8]) -> Result<Self, ReceiverError> {
        let label = b"-----BEGIN PRIVATE KEY-----";
        let key_start = contents.windows(label.len()).position(|part| part == label)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "missing receiver private key"))?;
        let (certificate, private_key) = contents.split_at(key_start);
        let der = rustls_pemfile::certs(&mut &certificate[..])
            .next()
            .transpose()?
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "missing receiver certificate"))?;
        let fingerprint = format!("{:X}", Sha256::digest(der.as_ref()));
        Ok(Self { certificate: certificate.to_vec(), private_key: private_key.to_vec(), fingerprint })
    }

    pub fn fingerprint_sha256(&self) -> &str {
        &self.fingerprint
    }

    pub fn certificate_pem(&self) -> &[u8] {
        &self.certificate
    }

    pub(crate) fn tls_identity(&self) -> Identity {
        Identity::from_pem(&self.certificate, &self.private_key)
    }
}
