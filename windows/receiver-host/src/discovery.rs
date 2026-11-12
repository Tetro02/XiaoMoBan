use std::time::Duration;

use mdns_sd::{IfKind, ServiceDaemon, ServiceInfo};

pub(crate) struct Discovery {
    daemon: ServiceDaemon,
    name: String,
}

impl Discovery {
    pub(crate) fn start(pairing_port: u16, grpc_port: u16, fingerprint: &str) -> Result<Self, mdns_sd::Error> {
        let machine = std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows".into());
        let hostname: String = machine.chars().filter(|character| character.is_ascii_alphanumeric() || *character == '-').take(35).collect();
        let instance = format!("小墨板 ({hostname})");
        let domain = format!("pennote-{hostname}-{}.local.", &fingerprint[..8]);
        let grpc = grpc_port.to_string();
        let properties = [("v", "1"), ("grpc", grpc.as_str()), ("fp", fingerprint)];
        let service = ServiceInfo::new("_pennotebridge._tcp.local.", &instance, &domain, (), pairing_port, &properties[..])?
            .enable_addr_auto();
        let discovery = Self { daemon: ServiceDaemon::new()?, name: service.get_fullname().to_owned() };
        discovery.daemon.disable_interface(IfKind::IPv6)?;
        discovery.daemon.register(service)?;
        Ok(discovery)
    }
}

impl Drop for Discovery {
    fn drop(&mut self) {
        if let Ok(result) = self.daemon.unregister(&self.name) {
            let _ = result.recv_timeout(Duration::from_millis(500));
        }
        if let Ok(result) = self.daemon.shutdown() {
            let _ = result.recv_timeout(Duration::from_millis(500));
        }
    }
}
