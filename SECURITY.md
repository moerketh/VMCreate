# Security Considerations

This document covers the guest-to-host attack surface introduced by hypervisor integration services, how VMCreate compares to other hypervisor platforms, and guidance for running malware analysis VMs.

## Hypervisor Integration Comparison

The table below compares the guest-to-host communication surface across the three hypervisors that REMnux, Parrot OS, and similar security distributions ship with:

| Surface | Hyper-V (VMCreate) | VMware (open-vm-tools) | Proxmox (qemu-guest-agent) |
|---|---|---|---|
| Guest agent daemon | hv_kvp_daemon (KVP key-value exchange only) | vmtoolsd (many capabilities) | qemu-ga |
| Clipboard sharing | Yes — via Enhanced Session (RDP) | Yes — via vmtoolsd | Via SPICE client |
| Drive redirection | Yes — via Enhanced Session (RDP), user-controlled per session | Yes — HGFS shared folders (always available) | Via 9p/virtiofs mount |
| Drag-and-drop files | No | Yes — bidirectional by default | No |
| File copy service | No | Yes — enabled by default | No |
| Display channel | RDP over VMBus (HvSocket) | VMCI + virtual SVGA | SPICE or VNC |
| Guest-to-host IPC | VMBus (narrow, typed messages) | VMCI (broad socket API) | virtio-serial |
| Network info push | Automatic via VMBus at boot | Via vmtoolsd | Via qemu-ga |

### Key Observations

- **Hyper-V's VMBus** is a Type-1 hypervisor interface hardened for Azure's multi-tenant threat model — arguably the most scrutinised hypervisor boundary in production.
- **VMware open-vm-tools** exposes a wider surface: shared folders (HGFS), drag-and-drop, file copy, and a broad VMCI socket API that guest code can address directly.
- **Enhanced Session clipboard and drive redirection** are provided by the RDP protocol, not by a separate guest agent. Drive redirection is opt-in per VMConnect session — the user controls which host drives are exposed.
- The **KVP daemon** (`hv_kvp_daemon`) is an extremely narrow protocol: fixed-size 2560-byte key-value records over VMBus. It cannot execute commands, transfer files, or open arbitrary channels.

## What VMCreate Enables

By default, VMCreate enables two Hyper-V features on every VM:

| Feature | PowerShell Command | Purpose |
|---|---|---|
| **Guest Service Interface** | `Enable-VMIntegrationService -Name "Guest Service Interface"` | Enables KVP data exchange (IP discovery, configuration). Required for post-creation customisation. |
| **Enhanced Session Mode** | `Set-VM -EnhancedSessionTransportType HvSocket` | Enables RDP over VMBus, providing clipboard sharing, audio, and optional drive redirection. |

### Integration Services Toggle

VMCreate provides a checkbox on the customisation page:

> **Enable Hyper-V integration services**
>
> Controls Guest Service Interface and Enhanced Session Mode. When disabled, the VM runs without clipboard, drive redirection, or IP discovery.

The toggle is **enabled by default**. Disabling it removes both Guest Service Interface and Enhanced Session from the VM, leaving only the basic VMBus heartbeat and time synchronisation that Hyper-V provides unconditionally.

## Recommendations for Malware Analysis

| Scenario | Recommendation |
|---|---|
| **General security tooling** (CTF, forensics, pen-testing labs) | Keep integration services **enabled**. The convenience of clipboard and session management outweighs the minimal risk from the narrow KVP channel. |
| **Active malware detonation** | Disable integration services. Use basic VMConnect (no Enhanced Session) and avoid exposing host drives. Consider also disabling the network adapter or using an isolated virtual switch. |
| **Snapshot-and-revert workflows** | Keep enabled during setup, then disable before detonating samples. Hyper-V snapshots preserve the integration service state. |

### Additional Hardening (Manual)

These options are outside VMCreate's scope but available through Hyper-V Manager or PowerShell:

```powershell
# Disable all integration services except heartbeat
Get-VMIntegrationService -VMName "MyVM" |
    Where-Object { $_.Name -ne "Heartbeat" } |
    Disable-VMIntegrationService

# Remove the network adapter entirely for air-gapped analysis
Remove-VMNetworkAdapter -VMName "MyVM"

# Disable Enhanced Session after creation
Set-VM -VMName "MyVM" -EnhancedSessionTransportType "None"
```

## Attack Surface Summary

```
┌─────────────────────────────────────────────────────────────────┐
│  Guest VM (e.g. REMnux)                                        │
│                                                                 │
│   hv_kvp_daemon ─── VMBus KVP ──┐                              │
│                                  │  narrow: 512-byte keys,     │
│                                  │  2048-byte values,           │
│                                  │  no command execution         │
│   xrdp ─── HvSocket (RDP) ──────┤                              │
│                                  │  standard RDP protocol,      │
│                                  │  clipboard + drive redirect   │
│                                  │  (user-controlled per session)│
│                                  │                              │
└──────────────────────────────────┼──────────────────────────────┘
                                   │ VMBus
┌──────────────────────────────────┼──────────────────────────────┐
│  Hyper-V Host                    │                              │
│                                  │  Type-1 hypervisor boundary  │
│   vmms.exe ◄─────────────────────┘  (Azure-hardened)            │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

When integration services are disabled, only the VMBus heartbeat and time sync remain — these are kernel-level Hyper-V services that cannot be disabled without removing the VM's enlightenments entirely.
