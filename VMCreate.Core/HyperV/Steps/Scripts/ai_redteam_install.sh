#!/bin/bash
# Installs the AI red-teaming toolset for LLM/application security testing:
#   garak     — NVIDIA's LLM vulnerability scanner ("nmap for LLMs").
#   PyRIT     — Microsoft/Azure Python Risk Identification Toolkit for genAI.
#   promptmap2 — automated prompt-injection scanner (dual-LLM, white/black-box).
# Installed via pipx so each gets an isolated environment. These only exercise
# models the user supplies; nothing here is enabled until credentials exist.
#
# Contract: the terminal line is AIREdTEAM_RESULT=ok|skipped|degraded.
#   ok       — all three tools installed.
#   skipped  — no usable python/pipx base; install not attempted.
#   degraded — at least one tool failed (best-effort; others in place).

set -o pipefail

RESULT_TOKEN="AIREdTEAM_RESULT"

if ! command -v python3 >/dev/null 2>&1; then
    echo "SKIP: python3 not found — cannot bootstrap pipx."
    echo "$RESULT_TOKEN=skipped"; exit 0
fi

# Bootstrap pipx if absent (Arch packages pipx).
if ! command -v pipx >/dev/null 2>&1; then
    if command -v pacman >/dev/null 2>&1; then
        pacman -S --noconfirm --needed python-pipx >/dev/null 2>&1 || true
    fi
    if ! command -v pipx >/dev/null 2>&1; then
        python3 -m pip install --user --quiet pipx >/dev/null 2>&1 || true
        python3 -m pipx ensurepath >/dev/null 2>&1 || true
    fi
fi
export PATH="$HOME/.local/bin:$PATH"
if ! command -v pipx >/dev/null 2>&1; then
    echo "SKIP: pipx unavailable."
    echo "$RESULT_TOKEN=skipped"; exit 0
fi

degraded=0
install_pipx() {
    local name="$1" spec="$2"
    if pipx install "$spec" >/dev/null 2>&1 || pipx upgrade "$spec" >/dev/null 2>&1; then
        echo "installed: $name"
    else
        echo "WARN: failed to install $name ($spec) — continuing." >&2
        degraded=1
    fi
}

install_pipx garak garak
install_pipx pyrit pyrit
install_pipx promptmap2 "git+https://github.com/utkusen/promptmap.git"

if [ "$degraded" -eq 1 ]; then
    echo "$RESULT_TOKEN=degraded"
else
    echo "$RESULT_TOKEN=ok"
fi
exit 0
