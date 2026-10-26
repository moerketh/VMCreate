# Gallery checksum coverage & loader update policy

Status after the Arch/BlackArch verification pass (2026-09). The download pipeline
verifies whenever a `GalleryItem` carries `Checksum` (inline) or
`ChecksumUri` (publisher's checksum file); inline takes precedence.

## Coverage

| Loader | Verification | Notes |
|---|---|---|
| AlpineLinux | `ChecksumUri` (.sha256 sidecar) | |
| Arch | `ChecksumUri` (.SHA256 sidecar) | Mirror publishes GNU-format `hash␠␠filename` beside the qcow2; extension is uppercase — lowercase `.sha256` 404s |
| AthenaOS | `ChecksumUri` (.iso.sha256) | |
| BlackArch | `ChecksumUri` (.SHA256 sidecar) | Rides the Arch base image, so verification reuses the Arch mirror's sidecar |
| BlackArchOmarchy | `ChecksumUri` (.iso.sha256) | |
| CAINE | none | No checksum file at a stable URL |
| Debian | `ChecksumUri` | |
| ElasticSiem | `ChecksumUri` (.CHECKSUM) | |
| FedoraSecurityLab | none | Labs `-CHECKSUM` is a 404 via download.fedoraproject.org — no usable sidecar |
| FedoraWorkstation | `ChecksumUri` | |
| FedoraSilverBlue | `ChecksumUri` | |
| FlareVm | inline `Checksum` (SHA-256) | Pinned dev VM |
| Kali (stable/weekly variants) | `ChecksumUri` (SHA256SUMS) | Per-directory sums: the item carries /current/'s, each `ReleaseVariant` its own directory's; `ResolveRelease` swaps the checksum with the disk |
| LinuxMint | `ChecksumUri` | |
| NixOS | none | Upstream publishes no checksum file for the unversioned `latest-nixos-*.iso` symlinks |
| Omarchy | `ChecksumUri` (.iso.sha256) | |
| OpenSuseTumbleweed | `ChecksumUri` | |
| Parrot (all editions) | `ChecksumUri` (signed-hashes.txt, SHA-256 section) | Multi-algorithm PGP-cleartext file; disambiguated by digest length |
| PentooCurrent | none | No checksums published |
| PwnCloudOS | inline `Checksum` (scraped) | |
| REMnux | inline `Checksum` (scraped live, pinned fallback) | Live-scrapes URL + SHA-256 from docs.remnux.org; falls back to the pinned known-good pair; mixed provenance (new URL + stale hash) is rejected |
| RockyLinux | `ChecksumUri` | |
| RockyLinuxCloud | `ChecksumUri` (.CHECKSUM) | Per-major sidecar; each `ReleaseVariant` carries its own major's file |
| SecurityOnion | none | download.securityonion.net returns an SPA for .sha256 URLs — no usable sidecar |
| Tails | inline `Checksum` (SHA-256) | Added; the Tails JSON API ships the hash per target file |
| Tsurugi | none | No checksums published |
| Ubuntu | `ChecksumUri` (SHA256SUMS) | Discovered per supported series; each channel dir publishes GNU-format SHA256SUMS (the earlier `.vhdx.zip.sha256` 404 was the wrong probe, not a missing file). GA + daily variants carry their own directory's sums; the interim-series desktop ISO card uses releases.ubuntu.com's SHA256SUMS |
| Whonix | `ChecksumUri` (.ova.sha512sums, SHA-512) | Added; upstream publishes SHA-512 only — keep the algorithm at SHA512, do not "fix" it to SHA-256 |

Unverified loaders log a visible warning at download time ("proceeding
unverified") and the gallery card shows the amber `⚠ unverified` badge.

## Update policy

- **Live-scrape loaders** (REMnux, Kali, Parrot, …) must keep a pinned
  known-good fallback so a docs/mirror restructure degrades to the pinned
  release instead of an empty gallery.
- **Pinned loaders** (Whonix, Tsurugi, …): update `PinnedVersion` + URL +
  checksum together, in the same commit.
- Whonix pins the current **stable** release only (checked against
  whonix.org/wiki/VirtualBox).
