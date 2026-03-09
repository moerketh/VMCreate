# Deployment Pipeline

This document covers the VMCreate GUI's deployment architecture: the phase card system, progress reporting, VM creation orchestration, and how the WPF frontend communicates with the hyperv-convert-iso customization layer.

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Phase Card System](#phase-card-system)
- [Progress Reporting](#progress-reporting)
- [VM Creation Orchestrator](#vm-creation-orchestrator)
- [Post-Boot Customization Pipeline](#post-boot-customization-pipeline)
- [Shipping Custom Steps with Gallery Items](#shipping-custom-steps-with-gallery-items)
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
8. Runs the **post-boot customization pipeline** (see below).

### Workflow Decision

```csharp
bool needsPostBoot = _customizationSteps
    .Any(s => s.Phase == CustomizationPhase.PostBoot && s.IsApplicable(item, vmCustomizations));

bool needsIsoBootCycle = detectedGeneration == 1
    || (detectedGeneration == 2 && (vmCustomizations.ConfigureXrdp || needsPostBoot));
```

Gen 2 images without xRDP skip the ISO entirely — the VM boots directly from its disk.

---

## Post-Boot Customization Pipeline

After the VM is created and (optionally) customized via the ISO, VMCreate runs a **step pipeline** that connects to the guest over SSH and applies post-boot customizations. This pipeline follows the Strategy pattern — each step is a self-contained class implementing `ICustomizationStep`, auto-discovered via assembly scanning.

### Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  HyperVVmCreator                                                        │
│       │                                                                 │
│       ├─ IEnumerable<ICustomizationStep>  (injected via DI)             │
│       │     ├─ SyncTimezoneStep      (Order 100)                        │
│       │     ├─ InstallOpenVpnStep    (Order 200)                        │
│       │     └─ DeployVpnConfigsStep  (Order 300)                        │
│       │                                                                 │
│       ├─ Filter: Phase == PostBoot && IsApplicable(item, customizations)│
│       ├─ Order by: Order ascending                                      │
│       │                                                                 │
│       └─ For each applicable step:                                      │
│             ├─ Report progress (step name + percentage)                  │
│             ├─ step.ExecuteAsync(shell, item, customizations, logger, ct)│
│             └─ Log completion                                           │
│                                                                         │
│  SshGuestShell (implements IGuestShell)                                  │
│       ├─ Discovers VM IP via Get-VMNetworkAdapter                       │
│       ├─ Polls for SSH readiness (native ssh.exe)                       │
│       ├─ RunCommandAsync()   — executes bash commands                   │
│       ├─ CopyContentAsync()  — writes in-memory content to guest files  │
│       └─ CopyFileAsync()     — transfers host files to guest            │
└─────────────────────────────────────────────────────────────────────────┘
```

### Contracts (in `VMCreate.Gallery.Contracts`)

**`IGuestShell`** — Transport abstraction for guest command execution:

```csharp
public interface IGuestShell
{
    string VmName { get; }
    Task<string> RunCommandAsync(string bashCommand, CancellationToken ct);
    Task CopyContentAsync(string content, string guestPath, CancellationToken ct);
    Task CopyFileAsync(string hostPath, string guestPath, CancellationToken ct);
}
```

**`ICustomizationStep`** — A single, self-contained customization step:

```csharp
public interface ICustomizationStep
{
    string Name { get; }                  // Human-readable name for logging/UI
    CustomizationPhase Phase { get; }     // PreBoot or PostBoot
    int Order { get; }                    // Execution order (lower = first)

    bool IsApplicable(GalleryItem item, VmCustomizations customizations);
    Task ExecuteAsync(IGuestShell shell, GalleryItem item,
                      VmCustomizations customizations, ILogger logger,
                      CancellationToken ct);
}
```

**`CustomizationPhase`** — When a step runs:

| Value | Description |
|-------|-------------|
| `PreBoot` | Applied during ISO chroot before the VM's first boot (e.g. xRDP install) |
| `PostBoot` | Applied via SSH after the VM boots from its own hard drive (e.g. timezone, VPN) |

### Order Ranges

| Range | Category | Examples |
|-------|----------|----------|
| 100 | Early generic | `SyncTimezoneStep` |
| 200 | Package install | `InstallOpenVpnStep` |
| 300 | Config deployment | `DeployVpnConfigsStep` |
| 900 | Cleanup | (reserved) |

### Step Discovery (DI Registration)

Steps are auto-discovered via assembly scanning in `App.xaml.cs`, using the same pattern as `IGalleryLoader`:

```csharp
// Assemblies to scan for auto-discovered implementations
var scannableAssemblies = new[]
{
    Assembly.GetExecutingAssembly(),    // VMCreate (main)
    typeof(BlackArch).Assembly          // VMCreate.Gallery.Security
};

// Auto-register all ICustomizationStep implementations
var stepTypes = scannableAssemblies
    .SelectMany(a => a.GetTypes())
    .Where(t => typeof(ICustomizationStep).IsAssignableFrom(t)
                && !t.IsAbstract && !t.IsInterface);
foreach (var stepType in stepTypes)
    services.AddTransient(typeof(ICustomizationStep), stepType);
```

`HyperVVmCreator` receives `IEnumerable<ICustomizationStep>` via constructor injection and filters/orders them at execution time.

### Orchestration

```csharp
// HyperVVmCreator.CreateVMAsync()
var postBootSteps = _customizationSteps
    .Where(s => s.Phase == CustomizationPhase.PostBoot
             && s.IsApplicable(item, vmCustomizations))
    .OrderBy(s => s.Order)
    .ToList();

if (postBootSteps.Count > 0)
{
    var shell = new SshGuestShell(_logger, vmSettings.VMName, privateKeyPath);
    await shell.WaitForReadyAsync(ct);

    int completed = 0;
    foreach (var step in postBootSteps)
    {
        progress.Report(new CreateVMProgressInfo
        {
            Phase = "PostBoot",
            ProgressPercentage = (int)((double)completed / postBootSteps.Count * 100),
            StepName = step.Name
        });

        await step.ExecuteAsync(shell, item, vmCustomizations, _logger, ct);
        completed++;
    }
}
```

### Built-in Steps

| Step | Order | Condition | Description |
|------|-------|-----------|-------------|
| `SyncTimezoneStep` | 100 | `SyncTimezone == true` | Maps host Windows TZ to IANA via `TimeZoneInfo.TryConvertWindowsIdToIanaId`, applies with `timedatectl set-timezone` |
| `InstallOpenVpnStep` | 200 | `ConfigureHtbVpn == true` | Installs `openvpn` + `network-manager-openvpn` + `network-manager-openvpn-gnome` (auto-detects apt/dnf/yum/pacman/zypper), restarts NetworkManager |
| `DeployVpnConfigsStep` | 300 | `ConfigureHtbVpn == true` | Deploys `.ovpn` files to `/etc/openvpn/client/`, imports each into NetworkManager via `nmcli connection import` so VPNs appear in the system tray |

### Progress Reporting

The `StepName` field on `CreateVMProgressInfo` is displayed as progress text on the Post-Boot Config phase card. Progress percentage is calculated as `completedSteps / totalSteps * 100`.

---

## Shipping Custom Steps with Gallery Items

The `ICustomizationStep` pipeline is designed so that distro-specific steps can ship alongside their gallery loaders in plugin assemblies. Here's how to add a new step.

### Quick Start: Adding a Step to the Main Project

1. Create a new class in `VMCreate/HyperV/Steps/`:

```csharp
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public class MyCustomStep : ICustomizationStep
    {
        public string Name => "My Custom Step";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 250;  // Between package install (200) and config deploy (300)

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
        {
            // Control when this step runs:
            // - Check customizations flags for user selections
            // - Check item.Name or item.Publisher for distro-specific steps
            return customizations.ConfigureHtbVpn
                && item.Name.Contains("Kali", System.StringComparison.OrdinalIgnoreCase);
        }

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item,
            VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Running custom step on {VM}", shell.VmName);
            await shell.RunCommandAsync("sudo apt-get install -y my-package", ct);
            await shell.CopyContentAsync("config-content", "/etc/myapp/config", ct);
        }
    }
}
```

2. Add a `<Compile>` entry in `VMCreate.csproj`:

```xml
<Compile Include="HyperV\Steps\MyCustomStep.cs" />
```

3. **Done.** The assembly scanner will auto-discover and register it. No other wiring needed.

### Shipping a Step in a Gallery Plugin Assembly

Steps can also live in `VMCreate.Gallery.Security` (or any other assembly in the scan list) so they're co-located with the gallery loader they belong to:

1. The plugin assembly already references `VMCreate.Gallery.Contracts` (which defines `ICustomizationStep`, `IGuestShell`, `GalleryItem`, `VmCustomizations`).

2. Create the step class in the plugin project:

```csharp
// VMCreate.Gallery.Security/customizations/SetupKaliTools.cs
namespace VMCreate.Gallery
{
    public class SetupKaliToolsStep : ICustomizationStep
    {
        public string Name => "Setup Kali Metapackages";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 400;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => item.Name.Contains("Kali", System.StringComparison.OrdinalIgnoreCase);

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item,
            VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            await shell.RunCommandAsync(
                "sudo apt-get install -y kali-tools-top10", ct);
        }
    }
}
```

3. The class is auto-discovered because `App.xaml.cs` already scans `typeof(BlackArch).Assembly`:

```csharp
var scannableAssemblies = new[]
{
    Assembly.GetExecutingAssembly(),    // VMCreate (main)
    typeof(BlackArch).Assembly          // VMCreate.Gallery.Security ← your step lives here
};
```

### Adding a New Plugin Assembly

To add an entirely new assembly to the scan:

1. Create a new class library targeting `net8.0`.
2. Reference `VMCreate.Gallery.Contracts`.
3. Implement `IGalleryLoader` and/or `ICustomizationStep`.
4. In the main `VMCreate` project, add a `<ProjectReference>` to the new assembly.
5. Register the assembly in `App.xaml.cs`:

```csharp
var scannableAssemblies = new[]
{
    Assembly.GetExecutingAssembly(),
    typeof(BlackArch).Assembly,
    typeof(MyNewPlugin.SomeType).Assembly  // ← add your assembly
};
```

### Design Guidelines

| Guideline | Rationale |
|-----------|-----------|
| Steps must be **stateless** | A new instance is created per DI resolution; don't store VM-specific state in fields |
| Use `IsApplicable()` for gating | Don't throw from `ExecuteAsync` to skip; return `false` from `IsApplicable` instead |
| Keep `Order` ranges consistent | 100s = generic, 200s = packages, 300s = configs, 400+ = distro-specific, 900 = cleanup |
| Use `IGuestShell` for all guest ops | Never spawn `ssh.exe` directly — the shell handles retries, error detection, and transport |
| Log via the provided `ILogger` | Don't create your own — the logger is scoped and tied to the step pipeline |
| Check `item.Name`/`item.Publisher` for distro targeting | Steps can be universal or distro-specific based on `IsApplicable` logic |

### Project Structure

```
VMCreate.Gallery.Contracts/
    ICustomizationStep.cs      ← Interface
    IGuestShell.cs             ← Transport abstraction
    CustomizationPhase.cs      ← PreBoot / PostBoot enum
    GalleryItem.cs             ← Gallery item model
    VmCustomizations.cs        ← User customization options
    HtbVpnKey.cs               ← VPN key data model

VMCreate/HyperV/
    SshGuestShell.cs           ← SSH transport (implements IGuestShell)
    Steps/
        SyncTimezoneStep.cs    ← Order 100
        InstallOpenVpnStep.cs  ← Order 200
        DeployVpnConfigsStep.cs← Order 300

VMCreate.Gallery.Security/     ← Plugin assembly (scanned for steps too)
    distributions/             ← IGalleryLoader implementations
    customizations/            ← (future) ICustomizationStep implementations
```

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
