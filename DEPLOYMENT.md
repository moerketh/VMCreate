# Deployment Pipeline

This document covers the VMCreate GUI's deployment architecture: the phase card system, progress reporting, VM creation orchestration, and how the WPF frontend communicates with the hyperv-convert-iso customization layer.

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Phase Card System](#phase-card-system)
- [Progress Reporting](#progress-reporting)
- [VM Creation Orchestrator](#vm-creation-orchestrator)
- [KVP Delivery & Corruption Mitigation](#kvp-delivery--corruption-mitigation)
- [Error Monitoring via PowerShell Direct](#error-monitoring-via-powershell-direct)
- [Technologies](#technologies)

---

## Architecture Overview

The deployment pipeline spans three layers:

```
┌─────────────────────────────────────────────────────────────────────┐
│  WPF GUI                                                           │
│  DeployPage.xaml ← DeployPageViewModel ← DeploymentPhase cards     │
│       │                                                            │
│       ▼                                                            │
│  CreateVM → HyperVVmCreator                                        │
│       │         │                                                  │
│       │         ├─ IHyperVManager (PowerShell-based Hyper-V mgmt)  │
│       │         └─ KvpHostToGuest (WMI AddKvpItems)                │
│       │                                                            │
│       ▼                                                            │
│  IProgress<CreateVMProgressInfo>                                   │
│       │                                                            │
└───────┼────────────────────────────────────────────────────────────┘
        │ Progress reports
        ▼
  DeployPage.OnProgressReport()
        │
        ├─ Phase transitions (Pending → Active → Completed)
        ├─ Dynamic card insertion (MBR/GPT detection)
        └─ Progress bar / speed text updates
```

`DeployPage` is a WPF `Page` that owns a `DeployPageViewModel` and a `CancellationTokenSource`. When loaded, it calls `CreateVM.StartCreateVMAsync()` with an `IProgress<CreateVMProgressInfo>` callback. Progress reports drive phase card state transitions on the UI thread.

---

## Phase Card System

### DeploymentPhase

Each card is a `DeploymentPhase` ViewModel with:

| Property | Type | Purpose |
|----------|------|---------|
| `Id` | `string` | Machine-readable key matching `CreateVMProgressInfo.Phase` |
| `Name` | `string` | Display title (e.g. "Download", "Clone Disk") |
| `Description` | `string` | Subtitle text |
| `Icon` | `SymbolRegular` | Fluent UI icon (overridden by status: ✓ for Completed, ✕ for Failed) |
| `Status` | `DeploymentPhaseStatus` | Pending / Active / Completed / Failed / Skipped |
| `ProgressPercentage` | `int` | 0–100 progress within the phase |
| `ProgressText` | `string` | Secondary text (download speed, URI, error message) |
| `IsIndeterminate` | `bool` | Shows spinner instead of progress bar |

### Well-Known Phase IDs

```csharp
public const string PhaseDownload   = "Download";
public const string PhaseExtract    = "Extract";
public const string PhaseConvert    = "Convert";
public const string PhaseCreateVM   = "CreateVM";
public const string PhaseStartVM    = "StartVM";
public const string PhaseCloneDisk  = "CloneDisk";
public const string PhaseCustomize  = "Customize";
public const string PhaseDone       = "Done";
```

### Phase Sequences

The initial card list is built from the gallery item's file type. Extract and Convert cards are added conditionally:

| File Type | Phases |
|-----------|--------|
| ISO / QCOW2 | Download → (Convert) → Create VM → Start VM → ... → Done |
| VMDK | Download → Extract → Convert → Create VM → Start VM → ... → Done |
| Others (7z, zip) | Download → Extract → Create VM → Start VM → ... → Done |

Generation-specific cards are **inserted dynamically** after `StartVM` reports the detected generation:

**Gen 1 (MBR → GPT clone):**
```
... → Start VM → Clone Disk → Apply Customizations → Done
```

**Gen 2 (GPT, with xRDP):**
```
... → Start VM → Customize VM → Done
```

**Gen 2 (no customization needed):**
```
... → Start VM → Done
```

### Dynamic Phase Insertion

`HyperVVmCreator` reports `DetectedGeneration` in its first `CreateVM` progress report. `DeployPage.OnProgressReport()` uses this to insert the appropriate cards before the Done card:

```csharp
// DeployPage.xaml.cs — OnProgressReport
if (info.DetectedGeneration == "1")
    _viewModel.InsertMbrPhases();      // Clone Disk + Apply Customizations
else if (info.DetectedGeneration == "2")
    _viewModel.InsertCustomizePhase(); // Customize VM
```

Both methods are guarded against double-insertion with `Phases.Any(p => p.Id == ...)`.

---

## Progress Reporting

### CreateVMProgressInfo

The progress DTO carries:

```csharp
public class CreateVMProgressInfo
{
    public string Phase { get; set; }               // Triggers phase transitions
    public string URI { get; set; }                  // Shown as progress text
    public int ProgressPercentage { get; set; }      // 0–100
    public double DownloadSpeed { get; set; }        // MB/s (shown as progress text)
    public string DetectedGeneration { get; set; }   // "1" or "2" — triggers card insertion
}
```

### Phase String Mapping

Raw phase strings from `HyperVVmCreator` and media handlers are mapped to well-known IDs:

```csharp
return phase switch
{
    "Download"   => PhaseDownload,
    "Extract"    => PhaseExtract,
    "Convert"    => PhaseConvert,
    "CreateVM"   => PhaseCreateVM,
    "StartVM"    => PhaseStartVM,
    "CloneDisk"  => PhaseCloneDisk,
    "Customize"  => PhaseCustomize,
    // Legacy / descriptive strings
    var p when p.Contains("Extracting")   => PhaseExtract,
    var p when p.Contains("Converting")   => PhaseConvert,
    var p when p.Contains("Cloning")      => PhaseCloneDisk,
    var p when p.Contains("customizations") => PhaseCustomize,
    _ => null
};
```

### ISO Guest → Host → GUI Flow

During the ISO boot cycle, progress flows from the guest through KVP to the UI:

```
ISO guest                          Hyper-V Host                    WPF GUI
─────────                          ───────────                     ───────
report_progress("CLONE_ROOT",...)
  └─ send_kvp("WorkflowProgress",...)
     └─ .kvp_pool_1 ──────────►  HyperVKVPPoller reads pool
                                    └─ IProgress<T>.Report() ──►  OnProgressReport()
                                                                    └─ Update phase card
```

`HyperVKVPPoller` reads `.kvp_pool_1` from the guest (via WMI) and translates `WorkflowProgress` / `PartcloneProgress` KVP entries into `CreateVMProgressInfo` reports.

---

## VM Creation Orchestrator

`HyperVVmCreator.CreateVMAsync()` is the central orchestration method. It:

1. Optionally replaces a previous VM with the same base name (stop → collect VHDX paths → remove VM → delete VHDXs).
2. Prepares media via `MediaHandlerFactory` — downloads, extracts, converts to VHDX.
3. Creates a Gen 2 VM with CPU, memory, secure boot, guest services, and network.
4. Attaches disks based on the detected generation:
   - **Gen 2 (GPT):** Attach VHDX as primary. Optionally attach the customization ISO.
   - **Gen 1 (MBR):** Create a new empty VHDX + attach the original as secondary + attach ISO.
5. Starts the VM.
6. If an ISO boot cycle is needed (`Gen 1` or `Gen 2 + xRDP`):
   - Sends KVP configuration flags (with corruption mitigation padding).
   - Polls guest KVP for progress.
   - Waits for the VM to shut down.
   - Removes the original MBR disk (Gen 1 only) and the ISO.
   - Sets first boot to hard drive.
7. Enables Enhanced Session Mode.

### Workflow Decision

```csharp
bool needsIsoBootCycle = detectedGeneration == 1
    || (detectedGeneration == 2 && vmCustomizations.ConfigureXrdp);
```

Gen 2 images without xRDP skip the ISO entirely — the VM boots directly from its disk.

---

## KVP Delivery & Corruption Mitigation

### The Problem

When a Gen 2 VM boots, Hyper-V pushes network configuration (IPv6 multicast prefixes, DNS) through the same VMBus KVP channel that `AddKvpItems` uses. The first two records in `.kvp_pool_0` are consistently corrupted by this overlap — e.g., key `DUMMY` becomes `DUMMYcastprefix` with `ff02::` multicast data.

This is **not** a timing issue — even with a 10 s delay, the first two slots are corrupted.

### The Fix

```csharp
// HyperVVmCreator.cs
await Task.Delay(TimeSpan.FromSeconds(10), ct);

// Throwaway padding — absorbs corruption in slots 1–2
await kvp.SendKVPToGuestAsync(vmName, "PADDING_1", "true", ct);
await kvp.SendKVPToGuestAsync(vmName, "PADDING_2", "true", ct);

// Real configuration lands in slot 3+ (clean)
await kvp.SendKVPToGuestAsync(vmName, "VMCREATE_MODE", "customize", ct);
await kvp.SendKVPToGuestAsync(vmName, "VMCREATE_XRDP", "true", ct);
```

### KvpHostToGuest

Uses WMI `Msvm_VirtualSystemManagementService.AddKvpItems` with retry logic (5 attempts, 5 s delay) and WMI job completion waiting.

### HyperVKVPPoller

Reads guest-to-host KVP (`.kvp_pool_1`) via WMI `Msvm_KvpExchangeComponent.GuestExchangeItems`. Translates `WorkflowProgress` and `PartcloneProgress` entries into `CreateVMProgressInfo` reports for the UI.

---

## Error Monitoring via PowerShell Direct

### Overview

When the ISO customization workflow fails or stalls, the VMCreate GUI can use **PowerShell Direct** to reach into the ISO guest, collect diagnostic information, and shut the VM down cleanly. This ensures the user gets actionable error details instead of a silently stuck VM.

### Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│  VMCreate GUI                                                        │
│                                                                      │
│  HyperVVmCreator                                                     │
│       │                                                              │
│       ├─ Poll KVP (normal path)                                      │
│       │     └─ WorkflowProgress updated regularly → report to UI     │
│       │                                                              │
│       ├─ Timeout detection                                           │
│       │     └─ No KVP progress for N minutes                         │
│       │                                                              │
│       └─ Error collection (PowerShell Direct)                        │
│             │                                                        │
│             ├─ Invoke-Command -VMName $vm -Credential $cred          │
│             │     └─ journalctl -u autorun.service --no-pager        │
│             │     └─ systemctl show autorun.service -p Result        │
│             │     └─ dmesg | tail -20                                │
│             │                                                        │
│             ├─ Save error log to deployment record                   │
│             ├─ Stop-VM -Force                                        │
│             └─ Report Failed phase to UI                             │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

### Detection Strategy

The host detects ISO failures through **KVP staleness** — if no `WorkflowProgress` update arrives for a configurable timeout (e.g. 5 minutes), the workflow is considered stalled. This covers:

| Scenario | How Detected |
|----------|-------------|
| Script crash / non-zero exit | `OnSuccess=poweroff.target` doesn't fire → VM stays running → KVP timeout |
| Script hangs (infinite loop, blocked mount) | KVP stops updating → timeout |
| Kernel panic / guest crash | VM state changes to Off unexpectedly |
| Successful completion | VM shuts down via `poweroff.target` → normal path |

### Error Collection via PowerShell Direct

When a timeout is detected, the host uses PowerShell Direct (Invoke-Command over VMBus) to collect diagnostics before stopping the VM:

```csharp
// Pseudocode for error collection
var cred = new PSCredential("ubuntu", SecureString("ubuntu"));

var diagnostics = await InvokeCommandAsync(vmName, cred, @"
    echo '=== service status ==='
    systemctl show autorun.service -p ExecMainStatus -p Result -p ActiveState
    echo '=== journal ==='
    journalctl -u autorun.service --no-pager -n 100
    echo '=== mounts ==='
    mount | grep /mnt
    echo '=== dmesg ==='
    dmesg | tail -30
");

// Save to deployment record for later review
deployment.ErrorLog = diagnostics;
deployment.ErrorMessage = ParseErrorSummary(diagnostics);

// Force stop the VM
await StopVMAsync(vmName, force: true);

// Report failure to UI
progress.Report(new CreateVMProgressInfo {
    Phase = "Customize",
    Status = PhaseStatus.Failed,
    ErrorMessage = deployment.ErrorMessage
});
```

### PowerShell Direct Requirements

PowerShell Direct on Linux guests requires both **openssh-server** and **pwsh** installed in the guest, plus the SSH subsystem configured for PowerShell remoting:

| Component | Provided By | Purpose |
|-----------|------------|---------|
| `openssh-server` | ISO `chroot_setup.sh` | SSH transport over VMBus |
| `powershell` (pwsh) | Microsoft apt repo, installed by `chroot_setup.sh` | `Invoke-Command` remoting endpoint |
| `Subsystem powershell` | `sshd_config` entry | Routes PS Direct to `pwsh -sshs` |
| `hv_sock` module | `linux-azure` kernel | VMBus socket for SSH without network |
| Password auth | `sshd_config` edit | Credential-based login (`ubuntu/ubuntu`) |

Without `pwsh`, the SSH connection succeeds but PS remoting fails with:
```
OpenError: An error has occurred which PowerShell cannot handle.
A remote session might have ended.
```

> **Note:** PowerShell Direct only works against the **ISO guest**, not the target distro. Stock images (Parrot, Kali, etc.) lack `openssh-server` + `pwsh` + `hv_sock`. For network-based SSH debugging during development, use `plink` — see `CUSTOMIZATION.md` in `hyperv-convert-iso`.

### Phase Card Error State

When an error is detected by the host:

1. The active phase card transitions from **Active** → **Failed** (icon changes to ✕, shows error message)
2. The Done card is never reached
3. The error log is persisted with the deployment record for later review

---

## Technologies

| Technology | Role |
|------------|------|
| **C# / .NET 8.0** | Application framework |
| **WPF + WPF UI (Fluent)** | UI framework with Mica backdrop, Fluent icons |
| **WMI** | Hyper-V VM management, KVP delivery, guest KVP polling |
| **PowerShell** | Hyper-V cmdlet execution via `IHyperVManager` |
| **PowerShell Direct** | Error collection from ISO guest via `Invoke-Command` over VMBus |
| **MVVM** | `DeployPageViewModel` + `DeploymentPhase` drive the deploy UI |
| **IProgress&lt;T&gt;** | Thread-safe progress reporting from async operations to the UI |
| **Hyper-V KVP Exchange** | Host↔guest communication over VMBus (no network required) |
