#!/usr/bin/env python3
"""Patch IronRDP on the VM for Enhanced Session support."""

import sys

# Patch 1: ironrdp-acceptor/src/connection.rs
acc_path = "/home/vmcreate/.cargo/git/checkouts/ironrdp-e7a6874919971f6f/a760e43/crates/ironrdp-acceptor/src/connection.rs"
with open(acc_path, "r") as f:
    acc = f.read()

# Insert new_for_enhanced_session after the new() constructor
marker = "            honor_client_desktop_size: false,\n        }\n    }\n"
if marker not in acc:
    print("ERROR: Could not find acceptor new() marker")
    sys.exit(1)

enhanced = """            honor_client_desktop_size: false,
        }
    }

    /// Create an acceptor for Hyper-V Enhanced Session (CredSSP before X.224).
    pub fn new_for_enhanced_session(
        desktop_size: DesktopSize,
        capabilities: Vec<CapabilitySet>,
        creds: Option<Credentials>,
    ) -> Self {
        let security = SecurityProtocol::HYBRID;
        Self {
            security,
            state: AcceptorState::Credssp {
                requested_protocol: security,
                protocol: security,
            },
            user_channel_id: USER_CHANNEL_ID,
            io_channel_id: IO_CHANNEL_ID,
            message_channel_id: None,
            desktop_size,
            keyboard_layout: 0,
            server_capabilities: capabilities,
            static_channels: StaticChannelSet::new(),
            saved_for_reactivation: Default::default(),
            creds,
            received_credentials: None,
            reactivation: false,
            honor_client_desktop_size: false,
        }
    }
"""

acc = acc.replace(marker, enhanced, 1)
with open(acc_path, "w") as f:
    f.write(acc)
print("Acceptor patched OK")

# Patch 2: ironrdp-server/src/server.rs
srv_path = "/home/vmcreate/.cargo/git/checkouts/ironrdp-e7a6874919971f6f/a760e43/crates/ironrdp-server/src/server.rs"
with open(srv_path, "r") as f:
    srv = f.read()

# Add PreconnectionBlob import
old_imp = "use ironrdp_pdu::mcs::{SendDataIndication, SendDataRequest};"
new_imp = old_imp + "\nuse ironrdp_pdu::pcb::PreconnectionBlob;"
if old_imp in srv and "pcb::PreconnectionBlob" not in srv:
    srv = srv.replace(old_imp, new_imp, 1)
    print("Import added OK")

# Add read_preconnection_blob before RdpServer struct
struct_marker = "\npub struct RdpServer {"
pcb_fn = """
/// Read a Preconnection Blob (PCB) from a pre-TLS stream.
async fn read_preconnection_blob<S>(framed: &mut TokioFramed<S>) -> Option<PreconnectionBlob>
where
    S: AsyncRead + AsyncWrite + Send + Sync + Unpin,
{
    use ironrdp_core::Decode;
    let len_bytes = framed.read_exact(4).await.ok()?;
    let pcb_len = u32::from_le_bytes(len_bytes[..4].try_into().unwrap_or([0; 4])) as usize;
    if pcb_len < 8 || pcb_len > 1024 { return None; }
    let rest = framed.read_exact(pcb_len - 4).await.ok()?;
    let mut pcb_buf = Vec::with_capacity(pcb_len);
    pcb_buf.extend_from_slice(&len_bytes[..4]);
    pcb_buf.extend_from_slice(&rest);
    ironrdp_core::decode::<PreconnectionBlob>(&pcb_buf).ok()
}

pub struct RdpServer {"""

if struct_marker in srv:
    srv = srv.replace(struct_marker, pcb_fn, 1)
    print("read_preconnection_blob added OK")
else:
    print("ERROR: Could not find RdpServer struct marker")
    sys.exit(1)

# Add run_connection_enhanced after run_connection
rc_end = "self.run_connection_with(stream, TransportTls::Managed).await\n    }"
if rc_end not in srv:
    print("ERROR: Could not find run_connection end")
    sys.exit(1)

enhanced_method = '''self.run_connection_with(stream, TransportTls::Managed).await
    }

    /// Run a Hyper-V Enhanced Session connection: PCB -> TLS -> CredSSP -> X.224 -> RDP.
    pub async fn run_connection_enhanced<S>(&mut self, stream: S) -> Result<Option<PreconnectionBlob>>
    where
        S: AsyncRead + AsyncWrite + Send + Sync + Unpin,
    {
        self.display_suppressed.store(false, Ordering::Relaxed);
        let mut framed = TokioFramed::new(stream);
        let pcb = read_preconnection_blob(&mut framed).await;
        if let Some(ref pcb) = pcb { debug!(?pcb, "Received PCB"); }

        let tls_acceptor = match &self.opts.security {
            RdpServerSecurity::Tls(a) => a.clone(),
            RdpServerSecurity::Hybrid((a, _)) => a.clone(),
            RdpServerSecurity::None => { warn!("Enhanced Session needs TLS"); return Ok(None); }
        };
        let raw_stream = framed.into_inner_no_leftover();
        let tls_stream = match tls_acceptor.accept(raw_stream).await {
            Ok(s) => s,
            Err(e) => { warn!("Enhanced Session TLS failed: {}", e); return Ok(None); }
        };
        let mut framed = TokioFramed::new(tls_stream);

        let size = self.display.lock().await.size().await;
        let caps = capabilities::capabilities(&self.opts, size);
        let mut acceptor = Acceptor::new_for_enhanced_session(size, caps, self.creds.clone());
        acceptor.set_honor_client_desktop_size(self.opts.honor_client_desktop_size);
        self.attach_channels(&mut acceptor);

        if acceptor.should_perform_credssp() {
            let pub_key = match &self.opts.security {
                RdpServerSecurity::Hybrid((_, k)) => k.clone(),
                _ => Vec::new(),
            };
            ironrdp_acceptor::accept_credssp(
                &mut framed, &mut acceptor,
                &mut ironrdp_tokio::reqwest::ReqwestNetworkClient::new(),
                "enhanced-session".into(), pub_key, None,
            ).await.context("Enhanced Session CredSSP failed")?;
        }

        let res = ironrdp_acceptor::accept_begin(framed, &mut acceptor)
            .await.context("Enhanced Session accept_begin failed")?;
        match res {
            BeginResult::ShouldUpgrade(stream) => {
                warn!("Enhanced Session: unexpected TLS upgrade after CredSSP");
                self.finalize_after_upgrade(TokioFramed::new(stream), acceptor, "Enhanced Session").await?;
            }
            BeginResult::Continue(framed) => {
                self.accept_finalize(framed, acceptor).await?;
            }
        }
        Ok(pcb)
    }'''

srv = srv.replace(rc_end, enhanced_method, 1)

with open(srv_path, "w") as f:
    f.write(srv)
print("Server patched OK")