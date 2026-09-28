# Gallery checksum coverage & loader update policy

Status after the quickspawn-backport pass (2026-09). The download pipeline
verifies whenever a `GalleryItem` carries `Checksum` (inline) or
`ChecksumUri` (publisher's checksum file); inline takes precedence.

## Coverage

| Loader | Verification | Notes |
|---|---|---|
| AlpineLinux | `ChecksumUri` (.sha256 sidecar) | |
| AthenaOS | `ChecksumUri` (.iso.sha256) | |
| BlackArch | none | No checksums published for the pinned ISO/OVA |
| CAINE | none | No checksum file at a stable URL |
| Debian | `ChecksumUri` | |
| FedoraWorkstation | `ChecksumUri` | |
| FedoraSilverBlue | `ChecksumUri` | |
| FlareVm | inline `Checksum` (SHA-256) | Pinned dev VM |
| Kali (stable/weekly variants) | `ChecksumUri` (SHA256SUMS) | Per-directory sums: the item carries /current/'s, each `ReleaseVariant` its own directory's; `ResolveRelease` swaps the checksum with the disk |
| LinuxMint | `ChecksumUri` | |
| Omarchy | `ChecksumUri` (.iso.sha256) | |
| OpenSuseTumbleweed | `ChecksumUri` | |
| Parrot (all editions) | `ChecksumUri` (signed-hashes.txt, SHA-256 section) | Multi-algorithm PGP-cleartext file; disambiguated by digest length |
| PentooCurrent | none | No checksums published |
| PwnCloudOS | inline `Checksum` (scraped) | |
| REMnux | inline `Checksum` (scraped live, pinned fallback) | Live-scrapes URL + SHA-256 from docs.remnux.org; falls back to the pinned known-good pair; mixed provenance (new URL + stale hash) is rejected |
| RockyLinux | `ChecksumUri` | |
| SecurityOnion | none | download.securityonion.net returns an SPA for .sha256 URLs — no usable sidecar |
| Tails | inline `Checksum` (SHA-256) | Added; the Tails JSON API ships the hash per target file |
| Tsurugi | none | No checksums published |
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
