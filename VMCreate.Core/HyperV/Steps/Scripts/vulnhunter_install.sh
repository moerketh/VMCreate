#!/bin/bash
# Installs nealbridges/VulnHunter (agentic AI security scanner) into an Omarchy
# deployment: clone the repo, then run its no-guess installer pointed at the
# detected agent harness's skills/agents directories so its skills and the `vh`
# launcher land in the right place.
#
# Environment contract (all injected by the caller if set):
#   VULNHUNT_SKILLS_DIR — harness skills dir (auto-detected when empty)
#   VULNHUNT_AGENTS_DIR — harness agents dir (may stay empty = skip)
#   VULNHUNT_MODEL      — harness model selector (passed through when set)
#
# Contract: the terminal line is VULNHUNTER_RESULT=ok|skipped|degraded.
#   ok       — repo cloned and skills + vh launcher installed.
#   skipped  — required prerequisites missing (git, python3.11+, or a harness
#              skills dir to target); install not attempted.
#   degraded — install.sh exited non-zero after clone (best-effort).
# Missing prerequisites must NOT fail the deployment — the model access and an
# agent harness are user-supplied at run time.

set -o pipefail

REPO_URL="https://github.com/nealbridges/VulnHunter.git"
INSTALL_ROOT="/opt/VulnHunter"
BIN_DIR="$HOME/.local/bin"

echo "VulnHunter install for $(id -un) (HOME=$HOME)"

# -- Prerequisites -----------------------------------------------------------
if ! command -v git >/dev/null 2>&1; then
    echo "SKIP: git not found."
    echo "VULNHUNTER_RESULT=skipped"; exit 0
fi

find_python() {
    for cand in python3.11 python3.13 python3.12 python3; do
        if command -v "$cand" >/dev/null 2>&1 && \
           "$cand" -c 'import sys; sys.exit(0 if sys.version_info[:2] >= (3,11) else 1)' 2>/dev/null; then
            command -v "$cand"; return 0
        fi
    done
    return 1
}
PYBIN="$(find_python)" || {
    echo "SKIP: python 3.11+ not found (needed for VulnHunter's bundled venv)."
    echo "VULNHUNTER_RESULT=skipped"; exit 0
}
echo "Using python: $PYBIN"

# -- Resolve the harness skills directory -----------------------------------
# Honor an explicit override first, else probe the common harness homes.
SKILLS_DIR="${VULNHUNT_SKILLS_DIR:-}"
if [ -z "$SKILLS_DIR" ]; then
    for cand in \
        "$HOME/.claude/skills" \
        "$HOME/.config/opencode/skills" \
        "$HOME/.grok/skills"; do
        if [ -d "$cand" ]; then SKILLS_DIR="$cand"; break; fi
    done
    # Fall back to the Omarchy-shipped default if nothing exists yet.
    [ -z "$SKILLS_DIR" ] && SKILLS_DIR="$HOME/.claude/skills"
fi
AGENTS_DIR="${VULNHUNT_AGENTS_DIR:-}"
mkdir -p "$SKILLS_DIR"
[ -n "$AGENTS_DIR" ] && mkdir -p "$AGENTS_DIR"
echo "Harness skills dir: $SKILLS_DIR   agents dir: ${AGENTS_DIR:-<none>}"

# -- Clone -------------------------------------------------------------------
if [ ! -d "$INSTALL_ROOT/.git" ]; then
    git clone --depth 1 "$REPO_URL" "$INSTALL_ROOT" || {
        echo "SKIP: git clone of VulnHunter failed."
        echo "VULNHUNTER_RESULT=skipped"; exit 0
    }
else
    git -C "$INSTALL_ROOT" pull --ff-only || true
fi

# -- Install skills + vh launcher (harness-portable, non-interactive) --------
mkdir -p "$BIN_DIR"
export VULNHUNT_SKILLS_DIR="$SKILLS_DIR" VULNHUNT_BIN_DIR="$BIN_DIR"
[ -n "$AGENTS_DIR" ] && export VULNHUNT_AGENTS_DIR="$AGENTS_DIR"
# Non-interactive: install.sh resolves destinations from these env vars.
if bash "$INSTALL_ROOT/install.sh" </dev/null; then
    echo "VULNHUNTER_RESULT=ok"
else
    echo "WARN: VulnHunter install.sh exited non-zero (best-effort)." >&2
    echo "VULNHUNTER_RESULT=degraded"
fi
exit 0
