# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

VMCreate deploys Linux and Windows guests (mostly security distributions) to Hyper-V. It has a WPF wizard, a CLI, and VMCreate Manager, a separate Hyper-V Manager replacement. Windows only, .NET 10.

## Commands

```powershell
dotnet build VMCreate.sln
dotnet test VMCreate.sln --filter "TestCategory!=Integration"     # what CI runs

# One project, class or test (MSTest, so filter by FullyQualifiedName / TestCategory)
dotnet test VMCreateTests/VMCreate.Tests.csproj --filter "FullyQualifiedName~EmbeddedScriptSyntaxTests"
dotnet test VMCreate.Manager.Tests/VMCreate.Manager.Tests.csproj

# Real Hyper-V end-to-end (large downloads, Hyper-V admin); Inconclusive unless VMCREATE_E2E=1
dotnet test VMCreateTests/VMCreate.Tests.csproj --filter TestCategory=E2E

dotnet publish VMCreate/VMCreate.csproj -c Release -r win-x64 --self-contained
```

There is no separate lint step. `EmbeddedScriptSyntaxTests` runs `bash -n` over every embedded guest script, and skips with a warning if no bash is found.

Versions come from git tags through MinVer (`v1.2.3`); there is no version to edit by hand. The pre-push gitleaks hook is enabled per clone with `git config core.hooksPath .githooks`.

## Projects

| Project | Role |
|---|---|
| `VMCreate.Gallery.Contracts` | Shared models and extension interfaces (`GalleryItem`, `VmCustomizations`, `ICustomizationStep`, `IGuestShell`, `IGalleryLoader`). Targets plain `net10.0`. |
| `VMCreate.Core` | The deployment engine, with no WPF. New services and steps go here. |
| `VMCreate.Gallery.Security` | Security distro loaders, their desktop catalogs and distro-specific steps. |
| `VMCreate` / `VMCreate.CLI` | Front ends over Core: the WPF wizard, and System.CommandLine + Spectre.Console. |
| `VMCreate.Metadata` | The per-VM metadata store, audit log and DPAPI credentials, shared by both products. |
| `VMCreate.Manager` / `VMCreate.Manager.Core` | The Manager app and its logic. **Does not reference `VMCreate.Core`**; the only shared code is `VMCreate.Metadata`. |
| `VMCreateTests` / `VMCreate.Manager.Tests` | MSTest + Moq. `VMCreateTests` references the GUI, CLI and Core. Tests run in parallel at method level. |

## Deployment architecture

Flow: front end → `CreateVM` → `HyperVVmCreator` → `VmDeploymentOrchestrator` → media handler → `IVmCreationStrategy` → customization steps.

- **Shared DI.** `AddCoreServices` (`VMCreate.Core/ServiceCollectionExtensions.cs`) is the one registration point for both front ends. Each passes the assemblies to scan (Core and Gallery.Security), and `CliFrontEndParityTests` pins the two sets as equal.
- **Auto-discovery.** Implementations of `ICustomizationStep`, `IGalleryLoader` and `IDesktopEnvironmentCatalog` in the scanned assemblies are registered by reflection, so adding the class is enough.
- **Strategy selection is first match, so registration order matters.** The order is RamBoot, Iso, NativeHyperV, DiskImage: diskless items are also ISOs, so RamBoot must come first.
- **Hyper-V access** goes through hosted PowerShell behind `IHyperVManager`, a facade over five role managers (`IHyperVManagerRoles.cs`). The executor is a singleton because building its session state is expensive.

### Customization steps

A step declares `Platform`, `Order`, `IsApplicable` and `ExecuteAsync`, and runs through an `IGuestShell`: SSH for Linux (network, pinned IP, or vsock via `hvc.exe nc`), PowerShell Direct for Windows.

- **`PostBootStepPlanner` is the single source of truth for step order** (`Order`, then `Name` ordinal). The executor and the GUI's phase cards both build from it, so the two can't diverge.
- **Applicability can change mid-deployment.** `AutoRdpBackendResolveStep` mutates the shared `VmCustomizations`, so callers re-evaluate the plan rather than caching it.
- **`SupportsChroot`** steps run pre-boot in the ISO stage's chroot when there is one, and post-boot otherwise (the cloud-init flow), so they must work in both. They can't rely on systemd, D-Bus or a live session; branch on `IGuestShell.Context`. Steps that completed in the chroot are skipped post-boot, unless **`RunsAfterChroot`** is set, which runs the step again to verify the live system.
- **`Order` 900 (`SshRestoreOrder`)** is a boundary: the disk-image strategy runs the steps below it, then a hard KVP heartbeat gate, then the SSH-restore tail.
- **Rules shared by both front ends** belong in Core or Contracts. For example, `RdpDesktopCompatibility` is checked at input time and again by the orchestrator.

### Guest scripts

Bash lives in real files under `VMCreate.Core/HyperV/Steps/Scripts/` (`iso/` for the ISO stage) and is loaded with `ScriptResourceLoader.Load("name.sh")`. The csproj embeds `*.sh` by glob, so new scripts need no registration, despite what the loader's doc comment says.

The ISO stage boots a separate cloning ISO built from the `vmcreate-iso` repo, which is downloaded and trust-checked by `CloningIsoDownloader` / `IsoReleaseTrust`. ISO-stage changes go in that repo or in `Scripts/iso/`. The older `hyperv-convert-iso` repo is deprecated.

### Enhanced Session

Set `EnhancedSessionTransportType=HvSocket` **before a VM's first start**. On VMBus, vmms never polls vsock:3389 and Enhanced Session stays unavailable; tests named `*_SetsEnhancedSessionTransportBeforeFirstStart` guard this. A guest reset while the state is "no listener" also stops polling until Save + Start. Check the real state through WMI (`root\virtualization\v2`, `Msvm_ComputerSystem.EnhancedSessionModeState`: 2 = available, 6 = no listener seen, 3 = off or transient), because `Get-VM`'s property can be blank.

## Manager metadata

`%LOCALAPPDATA%\VMCreate\Manager\metadata.json` is the primary store, and a copy without credentials sits in a `[vmcreate]` block in each VM's Hyper-V Notes, so metadata travels with exports. `NotesReconciler` adopts outside edits to that block, with one exception: an outside edit can turn the protected flag on but never off. VMCreate writes to the same store at deploy time through `IVmMetadataRecorder`. The Manager must keep working without elevation for members of Hyper-V Administrators (its manifest is `asInvoker`).

## Rules

- **Leave no VMCreate remnants in guests.** VMCreate only deploys. Anything that stays in the guest is third-party tooling or neutrally named config: no `vmcreate-*` units, `/etc/vmcreate` paths or `vmcreate` helper commands. Prefer host-side integration (Manager, SSH over vsock).
- **Never delete or write real logs from tests or tooling.** `%TEMP%\VMCreateYYYYMMDD.log` is the user's evidence trail. Log-touching code takes an injectable path. An off-screen WPF harness must detach `App_OnStartup` and redirect `TEMP`/`TMP`, or it writes to the real log.
- **Don't move the user's mouse or drive the desktop to test GUIs.** Verify logic with view-model tests and off-screen renders to PNG, and ask the user to try real input such as drag-and-drop.
- **Check root causes before stating them.** Back a diagnosis with evidence (logs, a reproduction, WMI state), not plausible reasoning.
- **Keep guest scripts LF.** `.gitattributes` pins `*.sh` to LF, but `python3` under Git Bash is Windows Python and silently writes CRLF. Prefer the Edit tool; otherwise use `open(p, 'w', newline='\n')`.
- **Render-check new WPF-UI icons.** Some `SymbolRegular` values draw blank or a stray letter (`DesktopOff20`, `PlugConnected16`), and some names don't exist.
- **SSH stalling right after "Server accepts key"** usually means a passphrase-protected key, not a transport fault. Test with `ssh-keygen -y -P "" -f key`.

## Further reading

`working-docs/` is gitignored apart from a few tracked files, and holds design notes (`DEPLOYMENT.md`, `WAYLAND-RDP.md`, `KDE-SCREENCAST-STACK.md`, `VMCREATE-MANAGER-PLAN.md`, `AI-READINESS-PLAN.md`). They can lag behind the code, so check them against it.
