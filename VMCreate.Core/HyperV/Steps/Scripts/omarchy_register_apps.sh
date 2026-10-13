#!/bin/bash
# Registers installed security tools and curated web apps in Omarchy's launcher
# (quickshell) by dropping XDG .desktop entries into the deploy user's
# ~/.local/share/applications/ so they surface as applications.
#
#  - Refreshes the desktop database so pacman-installed BlackArch GUI tools index.
#  - Web apps launch as focused browser app-windows and work from local cache
#    (CyberChef is fully static; revshells persists to Local Storage by design).
#  - CLI security tools get Terminal=true wrappers so they're discoverable.
#
# To add more sites/tools, append a line to WEBAPPS / CLI_TOOLS below.
#
# Contract: the terminal line is APPS_RESULT=ok|skipped|degraded.

set -o pipefail

APPS_DIR="$HOME/.local/share/applications"
ICONS_DIR="$HOME/.local/share/icons"
mkdir -p "$APPS_DIR" "$ICONS_DIR"

# Browser used for app-window web apps (Omarchy default is Chromium).
BROWSER="$(command -v chromium || command -v chromium-browser || command -v google-chrome || command -v firefox || true)"
GENERIC_ICON="utilities-terminal"

write_desktop() {
    local id="$1" name="$2" exec_line="$3" icon="$4" terminal="$5" comment="$6"
    cat > "$APPS_DIR/$id.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$name
Comment=$comment
Exec=$exec_line
Icon=$icon
Terminal=$terminal
Categories=Security;
StartupNotify=true
EOF
    echo "registered: $name"
}

fetch_favicon() {
    local id="$1" domain="$2"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --max-time 10 "https://$domain/favicon.ico" -o "$ICONS_DIR/$id.ico" 2>/dev/null \
            && { echo "$ICONS_DIR/$id.ico"; return 0; }
    fi
    rm -f "$ICONS_DIR/$id.ico"
    echo "$GENERIC_ICON"
}

# ── Web apps (name|id|url|domain) — edit this list to add more ──────────────
WEBAPPS=$(cat <<'LIST'
CyberChef|cyberchef|https://gchq.github.io/CyberChef/|gchq.github.io
RevShells|revshells|https://www.revshells.com/|www.revshells.com
JWT.io|jwt-io|https://jwt.io/|jwt.io
GTFOBins|gtfobins|https://gtfobins.org/|gtfobins.org
LOLBAS|lolbas|https://lolbas-project.github.io/|lolbas-project.github.io
MITRE ATT&CK|mitre-attack|https://attack.mitre.org/|attack.mitre.org
LIST
)

if [ -n "$BROWSER" ]; then
    case "$BROWSER" in
        *firefox) APPFLAG="--new-window" ;;
        *)        APPFLAG="--app=" ;;
    esac
    while IFS='|' read -r name id url domain; do
        [ -z "$id" ] && continue
        icon="$(fetch_favicon "$id" "$domain")"
        if [ "$APPFLAG" = "--app=" ]; then
            exec_line="$BROWSER --app=$url"
        else
            exec_line="$BROWSER $APPFLAG $url"
        fi
        write_desktop "$id" "$name" "$exec_line" "$icon" "false" "$name (web app)"
    done <<< "$WEBAPPS"
    WEB_STATUS=ok
else
    echo "WARN: no browser found — skipping web app launchers." >&2
    WEB_STATUS=degraded
fi

# ── CLI tools as Terminal=true wrappers (id|name|binary) ────────────────────
CLI_TOOLS=$(cat <<'LIST'
nmap|Nmap|nmap
hashcat|Hashcat|hashcat
john|John the Ripper|john
wireshark|Wireshark|wireshark
LIST
)
CLI_STATUS=ok
while IFS='|' read -r id name bin; do
    [ -z "$id" ] && continue
    if command -v "$bin" >/dev/null 2>&1; then
        write_desktop "$id" "$name" "$bin" "$GENERIC_ICON" "true" "$name (security tool)"
    fi
done <<< "$CLI_TOOLS"

# Re-index so Omarchy's launcher (and pacman-installed BlackArch .desktop files)
# all surface.
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
    [ -d /usr/share/applications ] && update-desktop-database /usr/share/applications 2>/dev/null || true
fi

if [ "$WEB_STATUS" = "degraded" ] || [ "$CLI_STATUS" = "degraded" ]; then
    echo "APPS_RESULT=degraded"
else
    echo "APPS_RESULT=ok"
fi
exit 0
