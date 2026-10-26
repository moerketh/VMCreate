# Deployment Pipeline

This document covers the VMCreate GUI's deployment architecture: the phase card system, progress reporting, VM creation orchestration, and how the WPF frontend drives the vmcreate-iso 2.x customization layer (a logic-free live ISO whose guest is orchestrated over SSH via Hyper-V vsock — all deployment logic lives host-side).

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Phase Card System](#phase-card-system)
- [Progress Reporting](#progress-reporting)
- [VM Creation Orchestrator](#vm-creation-orchestrator)
- [NoCloud Seed Deployments](#nocloud-seed-deployments)
- [Post-Boot Customization Pipeline](#post-boot-customization-pipeline)
- [Shipping Custom Steps with Gallery Items](#shipping-custom-steps-with-gallery-items)
- [KVP Today: Heartbeat Gate Only](#kvp-today-heartbeat-gate-only)
- [Static Addressing (`vmcreate create --switch / --static-ip / --gateway / --dns`)](#static-addressing-vmcreate-create---switch---static-ip---gateway---dns)
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

During the ISO boot cycle, progress flows from the guest over vsock SSH to the UI (no KVP progress protocol — the legacy `.kvp_pool_1` polling was removed with the hyperv-convert-iso path):

```
ISO guest                          Hyper-V Host                    WPF GUI
─────────                          ───────────                     ───────
echo "VMCREATE:PROGRESS:..."
  └─ sshd-vsock socket ──────►  VsockIsoBootCycleRunner /
                                 EmbeddedIsoScriptRunner parse
                                    VMCREATE:* protocol lines
                                    └─ IProgress<T>.Report() ──►  OnProgressReport()
                                                                    └─ Update phase card
```

The ISO guest boots to a socket-activated sshd on AF_VSOCK port 22 (~7 s after power-on). The host connects with `ssh -o ProxyCommand="hvc.exe nc -t vsock <guid> 22"`, streams the scenario scripts (`clone_disk.sh`, `customize_target.sh`, …), and the scripts report progress with `VMCREATE:PROGRESS:` / `VMCREATE:MSG:` lines the runner translates into `CreateVMProgressInfo` reports.

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
6. If an ISO boot cycle is needed (`Gen 1` or `Gen 2 + post-boot/customization`):
   - Boots the vmcreate-iso live environment; the guest brings up a
     socket-activated sshd on vsock (no IP or temp NIC needed for the cycle).
   - The host drives every scenario over that SSH channel (`VsockIsoBootCycleRunner`
     → `EmbeddedIsoScriptRunner`), streaming scripts and parsing `VMCREATE:*`
     progress lines.
   - Removes the original MBR disk (Gen 1 only) and the ISO; sets first boot
     to hard drive.
7. Enables Enhanced Session Mode.
8. Runs the **post-boot customization pipeline** (see below), split around the
   hard KVP heartbeat gate: the main steps run first, then the gate (with its
   SSH witness probe — it must run while SSH is still up), then the SSH-restore
   tail (`DisableSshStep`, Order 900).

### Workflow Decision

```csharp
bool needsPostBoot = _customizationSteps
    .Any(s => s.Phase == CustomizationPhase.PostBoot && s.IsApplicable(item, vmCustomizations));

bool needsIsoBootCycle = detectedGeneration == 1
    || (detectedGeneration == 2 && (vmCustomizations.ConfigureXrdp || needsPostBoot));
```

The ISO cycle runs whenever xRDP is configured or any Linux post-boot step applies — post-boot steps need the automation-SSH path the ISO stage provisions, and chroot-capable steps execute inside the ISO stage itself. Only a Gen 2 image with no xRDP and no applicable Linux steps boots directly from its disk.

---

## NoCloud Seed Deployments

Deployments carrying the gallery `cloud-init` tag or a user-supplied user-data
file (`--cloud-init <file>` on the CLI) are customized by a **NoCloud seed ISO**
instead of the cloning-ISO cycle. `DiskImageVmCreationStrategy` triggers the
seed path on either condition:

```csharp
bool isCloudInit = item.HasTag("cloud-init")
                   || !string.IsNullOrEmpty(vmCustomizations.CloudInitUserDataPath);
```

The trigger is what makes a raw cloud image deployable unattended: with
`--cloud-init` set, an untagged `--image-uri` deployment attaches the seed and
boots the disk directly — no cloning ISO, no temp-NIC dance — because cloud-init
provisions the automation user on first boot instead of the cloning ISO's
SSH-key injection.

### Seed construction (`CloudInitSeedBuilder`)

- **Volume label `cidata`**, ISO9660 + Joliet only. A UDF descriptor makes
  blkid report `TYPE=udf` and ds-identify skips the seed entirely.
- **`user-data`** is a plain `#cloud-config` document when no custom content is
  supplied, and a MIME multipart document when `--cloud-init` is used:
  - Part 1 — the user's content, emitted as `text/plain` so cloud-init re-sniffs
    its type from the leading marker (`#!`, `#cloud-config`, `#include`,
    `#part-handler`, `#cloud-boothook`, `#cloud-config-archive`, jinja).
  - Part 2 — the built-in `#cloud-config` block: the `vmcreate` automation
    user, the deployment's SSH public key, passwordless sudo, and growpart.
  - Order is load-bearing: cloud-init merges cloud-config parts in document
    order with `dict(replace)`, so the built-in block **wins any conflicting
    top-level key** — the user's content is strictly additive. Post-boot SSH
    machinery depends on the built-in block, which is why it is
    non-negotiable.
  - Both parts are **base64 transfer-encoded**. Every cloud-init release
    shipped in images before 2026-10 decodes an unencoded text part by
    re-encoding it with raw-unicode-escape (fixed upstream in #7102): `é`
    arrived as the lone byte `0xE9` and characters at U+0100+ as literal
    `\uXXXX` text — breaking scripts, YAML and SSH key comments with
    non-ASCII. The base64 path decodes to exact UTF-8 bytes on every
    version; cloud-init sniffs each part's type from the first line of the
    decoded payload, so dispatch is unchanged.
- **`meta-data`** carries a per-deploy unique `instance-id` (cloud-init treats
  every deploy as a first boot) and the sanitized hostname.

### Key contract

The seed embeds the SSH public key matching
`VmCustomizations.CustomSshPublicKeyPath` when set (`--ssh-key`), otherwise the
auto-generated per-user key. Post-boot SSH connects with the corresponding
private key — a seed that ignored `--ssh-key` would strand the deployment with
an unreachable guest.

### Cleanup

The seed ISO is detached (and the file deleted) when post-boot steps run; when
no post-boot steps apply, it stays attached — it is a tiny read-only DVD that
cloud-init ignores on subsequent boots.

---

## Post-Boot Customization Pipeline

After the VM is created and (optionally) customized via the ISO, VMCreate runs a **step pipeline** that connects to the guest over SSH and applies post-boot customizations. This pipeline follows the Strategy pattern — each step is a self-contained class implementing `ICustomizationStep`, auto-discovered via assembly scanning.

### Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  HyperVVmCreator                                                        │
│       │                                                                 │
│       ├─ IEnumerable<ICustomizationStep>  (injected via DI)            │
│       │     ├─ SyncTimezoneStep      (Order 100)                       │
│       │     ├─ InstallOpenVpnStep    (Order 200)                        │
│       │     └─ DeployVpnConfigsStep  (Order 300)                        │
│       │                                                                 │
│       ├─ PostBootStepPlanner (shared: execution + deploy page)          │
│       │     ├─ Filter: Phase == PostBoot && Platform matches            │
│       │     ├─ Order by: Order ascending, then Name (ordinal)           │
│       │     └─ GetPlannedSteps(): candidates where IsApplicable(...)     │
│       │                                                                 │
│       └─ For each applicable step (IsApplicable re-checked live):        │
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

Both the backend executor (`PostBootCustomizationService`) and the deploy
page's post-boot card list come from the same planner
(`IPostBootStepPlanner` in `VMCreate.Core/HyperV/VmCreation/PostBootStepPlanner.cs`),
so the cards always match the execution order: candidates sorted by `Order`,
then `Name` (ordinal) as a deterministic tie-break for steps that share an
`Order`. The `CardId(step)` helper maps every step to a card ID
(`ProgressPhaseId` when set, `Sub_Step_<Name>` otherwise), and
`SshRestoreOrder` (900) is the boundary both the strategy (KVP gate split) and
the card list (restore tail) split at. The deploy page re-reconciles its card
list on every post-boot step report, so steps whose applicability changes
mid-run (the Auto RDP resolver rewriting `RdpBackend`) get their cards
inserted or removed while a deployment is in progress.

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
| `InstallOpenVpnStep` | 200 | `InstallOpenVpn == true` (default) | Installs `openvpn` + `network-manager-openvpn` + `network-manager-openvpn-gnome` (auto-detects apt/dnf/yum/pacman/zypper), restarts NetworkManager |
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
| Implement `IStepDisplayInfo` | Gives the step a deploy-page card (title/description/icon); without it the card falls back to `Name` with a generic description |
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

## KVP Today: Heartbeat Gate Only

The legacy host→guest KVP delivery (configuration flags, corruption-mitigation
padding, `.kvp_pool_1` progress polling via `HyperVKVPPoller`) was **removed**
together with the hyperv-convert-iso path — the v2 ISO is configured and
monitored entirely over vsock SSH.

What remains of KVP:

### KvpHeartbeatGate

After post-boot, the strategy polls the HOST-side
`Msvm_KvpExchangeComponent` pools via WMI (`KvpHeartbeatGate`) to prove the
guest's `hv_kvp_daemon` is answering. On timeout, an SSH witness probe asks
the guest directly (`pgrep hv_kvp_daemon`) before failing:

- daemon answering, pools empty → known first-boot pool materialization lag —
  proceed with a warning;
- daemon absent (SSH reachable) → hard failure with diagnostics;
- SSH transport error during the probe → hard failure with a distinct
  "could not reach the guest" message.

The gate runs BEFORE `DisableSshStep` (Order 900) so the witness probe always
has a live SSH session.

### KvpBase (IVmShutdownWatcher)

WMI helpers shared by the gate: VM-GUID resolution and running-state polling.
The old progress-oriented poller (`IKvpPoller` / `HyperVKVPPoller`) is gone.

### Old cached ISO files

`hyperv-convert.iso*` left in the cache by pre-2.0 installs is orphaned — the
v2 downloader only manages `vmcreate-iso.iso` — and can be deleted manually.

---

## Static Addressing (`vmcreate create --switch / --static-ip / --gateway / --dns`)

The CLI can deploy a guest onto a chosen Hyper-V switch with a statically
configured IPv4 address. Both values ride the deployment's existing seed
mechanisms — no post-boot patching is involved.

### Switch selection

`--switch <name>` flows from `VmSettings.SwitchName` through the immutable
`VmDeploymentPlan` into `PowerShellVmNetworkManager.ConnectNetworkAdapter`,
which connects the VM's NIC to `plan.SwitchName` (falling back to the
Default Switch when null). The **temporary** post-boot SSH NIC
(`VMCreate Temp`) always stays on the Default Switch: it is deploy plumbing,
not part of the guest's final network. The CLI validates `--switch` against
`Get-VMSwitch` before the download starts (`IVmNetworkManager.SwitchExistsAsync`),
so a mistyped name fails as InvalidArguments instead of after the VM skeleton
exists.

### Static addressing via the NoCloud seed

`--static-ip <cidr>` (with optional `--gateway`/`--dns`) is carried on
`VmCustomizations` (`StaticIp`/`StaticGateway`/`StaticDns`) and rendered by
`NoCloudNetworkConfig` into a cloud-init NoCloud `network-config` file
(version 2) written into the seed ISO next to `user-data`/`meta-data`:

```yaml
version: 2
ethernets:
  eth0:
    match:
      driver: hv_netvsc
    addresses: [192.168.10.50/24]
    routes:
      - to: default
        via: 192.168.10.1
    nameservers:
      addresses: [8.8.8.8, 1.1.1.1]
```

- The interface is selected by **driver match** (`hv_netvsc`), not by name:
  the NoCloud seed path never runs the cloning ISO's interface-renaming
  fixups, so a cloud image naming its NIC `ens*`/`enp*` would never see a
  bare-`eth0` config apply — and supplying any network-config also turns
  off cloud-init's DHCP fallback, leaving the guest offline. A driver match
  covers every name the device can take; renderers without match support
  (e.g. sysconfig) fall back to the id, `eth0` — the name Hyper-V cloud
  images use. No `set-name:` (cloud-init only renames when a MAC is pinned,
  and MACs are host-allocated) and no MAC pinning.
- The default route uses `routes: [{to: default, via: …}]` (the modern v2
  form; `gateway4` is deprecated). When `--gateway` is omitted the route
  section is omitted entirely — the guest reaches only its own subnet.
- The CIDR prefix is **mandatory**: it is the netmask that decides which
  destinations the guest treats as on-link. A bare address is rejected at
  CLI validation rather than guessed `/24`.
- Address parsing lives in one place — `StaticNetworkConfig.Create` — with
  a **strict dotted-quad** parser. `IPAddress.TryParse` is not used: it
  accepts the shorthand forms `192.168.1`, `10` and `0x0a000001`, which
  netplan rejects; such a value would pass validation, land in the
  network-config and leave the guest with no address (and the pinned-IP
  SSH wait timing out against a host like `192.168.1`).

Both seed paths carry the file when static addressing is requested —
`CloudInitSeedBuilder` (cloud-init gallery items, e.g. Elastic SIEM) and
`CidataSeedBuilder` (Omarchy). On the Omarchy ISO path the file seeds the
live session's NIC via cloud-init, and the installer's
`network_config: {"type": "iso"}` copies the live configuration into the
installed system — the address survives the install with no installer
changes.

### Static IP as the SSH target

Hyper-V's IP discovery (`SshTransport.DiscoverVmIpAsync`) reads DHCP-lease
KVP data and the host neighbor table's MAC→IPv4 bindings. A statically
configured guest never gets a DHCP lease and may never appear in either
source — so when `--static-ip` is set, the post-boot shell is built on the
pinned address (`IGuestShellFactory.CreateWithPinnedIp`) instead of the
discovery shell. The target is the CIDR's host part; the seed carries the
full CIDR (the prefix drives the guest's netmask):

- `SshGuestShell` treats the pinned IP as a fixed target for the shell's
  lifetime: no discovery, and transport errors retry without re-discovery
  (the address is user-guaranteed; it cannot change).
- Mid-pipeline session drops (a VPN plugin install restarting
  NetworkManager) retry against the same pinned target; host-side command
  timeouts do not retry at all — the same side-effect discipline discovery
  mode enforces through its changed-IP gate (re-running a merely slow
  command could duplicate guest-side effects).
- Readiness probing connects directly to the pinned address with the full
  first-boot budget; the timeout error names the address and the
  `--static-ip` flag to verify.
- Both Linux post-boot paths use it: `DiskImageVmCreationStrategy`
  (cloud-init images) and `IsoVmCreationStrategy` (Omarchy, connecting as
  the seeded user). The vsock ISO boot-cycle path is unchanged — it never
  used IP discovery.

Unsupported combinations fail loudly — twice: once in the CLI **before the
download starts** (mirroring `--cloud-init`'s pre-flight: `--static-ip` on a
cloning-ISO image, a manual ISO install, or a native Hyper-V/Windows image),
and again in the strategies before any VM work as the library-side safety net
for non-CLI callers. None of those first boots consume a NoCloud seed, so the
address could never be delivered and the pinned-SSH wait would burn its
budget on an address the guest never has.

The GUI always deploys with DHCP; these flags are CLI-only.

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
