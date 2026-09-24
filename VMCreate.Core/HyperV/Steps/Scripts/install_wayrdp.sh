#!/bin/bash
set -o pipefail

# =========================================================================
# wayrdp — install + configure (Wayland-native, MIT, FreeRDP 3)
# =========================================================================
# PLACEHOLDER — wayrdp is at Phase 0 (protocol spike on a synthetic desktop);
# there is no installable artifact yet (no releases, no packaging line).
#
# This script exists so the VMCreate deployment pipeline can be exercised
# end-to-end TODAY: the step plumbing, guest-shell transport, /tmp hardening,
# and result-line contract are all identical to install_lamco.sh — only the
# payload differs. When the first wayrdp deb lands (moerketh/wayrdp
# releases), this script gets the same pinned-tag + sha256-verified contract
# as LAMCO_FORK_* above (see install_lamco.sh).
#
# SECURITY/RELIABILITY CONTRACT (same as install_lamco.sh):
#   - FAIL LOUDLY: no fallback path, no partial install silently accepted.
#   - STREAM RULE: STDOUT ONLY is returned on zero-exit SSH runs. Every
#     DEGRADED/WARNING/NOTICE line prints to stdout; only hard-failure ERROR
#     lines use stderr (there stderr IS captured into the SSH exception).
#   - Result contract: prints exactly one terminal line
#     WAYRDP_RESULT=ok|degraded — hard failures exit non-zero BEFORE that
#     line is reached. (Currently it always fails hard below; the contract
#     line is implemented now so the host step's parser can be validated.)

echo "WAYRDP NOTICE: wayrdp selected — the replacement for the Lamco fork." >&1
echo "WAYRDP NOTICE: MIT-licensed, FreeRDP 3 (libfreerdp-server) core." >&1
echo "WAYRDP ERROR: wayrdp is not yet installable (Phase 0: no releases, no" >&2
echo "WAYRDP ERROR: packaging pipeline). This deployment path is wired for" >&2
echo "WAYRDP ERROR: E2E testing of the VMCreate plumbing only. Select the" >&2
echo "WAYRDP ERROR: 'lamco' or 'xrdp' backend for a working VM today, or" >&2
echo "WAYRDP ERROR: track progress in the wayrdp repo (docs/plan.md)." >&2

# TODO(wayrdp Phase 2): replace this block with the real install:
#   1. download pinned deb from github.com/moerketh/wayrdp releases
#      (WAYRDP_TAG / WAYRDP_DEB_VERSION / WAYRDP_DEB_SHA256 constants),
#   2. sha256-verify, dpkg -i,
#   3. runtime deps: freerdp3 server libs, pipewire, libei (KDE backend),
#   4. TLS cert generation + /etc/wayrdp/config.toml,
#   5. systemd user unit + linger (same pattern as lamco; no portal consent
#      needed on the KDE/zkde path — that's the design win over the portal),
#   6. terminal line: echo "WAYRDP_RESULT=ok".
exit 1
