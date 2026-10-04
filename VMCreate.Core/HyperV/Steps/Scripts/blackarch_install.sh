#!/bin/bash
# Converts a stock Arch Linux image into BlackArch: bootstrap the BlackArch
# repository (strap.sh + keyring), sync package databases, then install a
# curated subset of pentesting tools.
#
# Contract: the terminal line is BLACKARCH_RESULT=ok|degraded.
#   ok       — repo added, core group + all individual tools installed.
#   degraded — repo and core group installed; a best-effort individual
#              package failed to install.
# Hard failures (repo bootstrap, keyring, or core-group install) exit
# non-zero WITHOUT a result line so the caller treats them as failures.

set -o pipefail

STRAP_URL="https://www.blackarch.org/strap.sh"
STRAP_SHA1="d062038042c5f141755ea39dbd615e6ff9e23121"

if ! command -v pacman >/dev/null 2>&1; then
    echo "ERROR: pacman not found — BlackArch install requires an Arch base." >&2
    exit 1
fi

# -- Bootstrap the BlackArch repository (verified strap.sh) -----------------
workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT

curl -fsS "$STRAP_URL" -o "$workdir/strap.sh"
echo "$STRAP_SHA1  $workdir/strap.sh" | sha1sum -c - >/dev/null || {
    echo "ERROR: strap.sh checksum mismatch — aborting." >&2
    exit 1
}

bash "$workdir/strap.sh" || {
    echo "ERROR: strap.sh failed to add the BlackArch repository." >&2
    exit 1
}

# Refresh databases so the new [blackarch] repo is usable.
pacman -Sy --noconfirm || {
    echo "ERROR: pacman -Sy failed after adding the BlackArch repository." >&2
    exit 1
}

# BlackArch pacman keyring (provides the keys the repo is signed with).
pacman -S --noconfirm --needed blackarch-keyring || {
    echo "ERROR: failed to install blackarch-keyring." >&2
    exit 1
}

# -- Core tool group (required) --------------------------------------------
if ! pacman -S --noconfirm --needed blackarch-pentest; then
    echo "ERROR: failed to install the blackarch-pentest group." >&2
    exit 1
fi

# -- Common individual tools (best-effort) ----------------------------------
# A missing or failed individual package must not fail the deployment — the
# repo and core group are in place. Track failure to report 'degraded'.
degraded=0
for pkg in nmap wireshark-qt metasploit burpsuite john hashcat sqlmap aircrack-ng hydra; do
    if ! pacman -S --noconfirm --needed "$pkg"; then
        echo "WARN: failed to install $pkg (continuing)." >&2
        degraded=1
    fi
done

if [ "$degraded" -eq 1 ]; then
    echo "BLACKARCH_RESULT=degraded"
else
    echo "BLACKARCH_RESULT=ok"
fi
exit 0
