# VMCreate

A Windows desktop application that replaces Hyper-V Quick Create with a streamlined workflow for downloading, converting, and deploying Linux virtual machines — with built-in support for 20+ distributions including security-focused images.

## Features

- **One-click VM creation** — select a distribution, click Create, and VMCreate handles the download, extraction, disk conversion, VM provisioning, and post-boot customization automatically
- **Multi-format media support** — ISO, QCOW2, VMDK, VHD/VHDX, OVA, 7z, XZ, and Zstandard archives
- **Gen 1 & Gen 2 Hyper-V VMs** — automatic MBR-to-GPT migration and cloning for Gen 2 UEFI boot
- **Post-boot customization** — SSH-based guest automation for timezone sync, VPN deployment, and Enhanced Session setup (xRDP)
- **Hack The Box integration** — deploy OpenVPN configs from HTB Labs, Starting Point, and Academy directly into the guest
- **SHA-256 checksum verification** on all downloads
- **Phase-card progress UI** — real-time status for each stage of the deployment pipeline

## Supported Distributions

| General | Security |
|---------|----------|
| Alpine Linux | BlackArch |
| Arch Linux | CAINE |
| Debian | Fedora Security Lab |
| Fedora Workstation | Kali Linux |
| Fedora Silverblue | Parrot OS |
| Linux Mint | Pentoo |
| NixOS | PwnCloud OS |
| openSUSE Tumbleweed | REMnux |
| Rocky Linux | Security Onion |
| Ubuntu | Tails |
| | Tsurugi |
| | Whonix |

Additional images can be loaded from the Microsoft gallery, local JSON files, or custom registry entries.

## Requirements

- Windows 10/11 with Hyper-V enabled
- .NET 8.0 (bundled with the self-contained build)

## Installation

Download the latest release from [GitHub Releases](../../releases):

- **MSI installer** — per-machine install that replaces the built-in Hyper-V Quick Create and restores it on uninstall
- **Standalone EXE** — self-contained portable executable, no installation required

Both builds include SHA-256 checksums for verification.

## Building from Source

```
dotnet build VMCreate.sln
dotnet test VMCreate.sln
dotnet publish VMCreate/VMCreate.csproj -c Release -r win-x64 --self-contained
```

## Documentation

- [DEPLOYMENT.md](DEPLOYMENT.md) — technical architecture, phase pipeline, KVP delivery, and progress reporting
- [SECURITY.md](SECURITY.md) — guest-to-host attack surface analysis, Hyper-V integration service hardening, and comparison with VMware/Proxmox

## License

[MIT](LICENSE.txt)