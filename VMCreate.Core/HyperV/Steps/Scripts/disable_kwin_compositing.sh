#!/bin/bash
set -o pipefail

# -- Disable KWin compositing for all users -------------------------------
# KWin's OpenGL compositing does not work through xrdp's software renderer
# or on Hyper-V's limited framebuffer.  Disable compositing and force the
# XRender backend for all users.  This is belt-and-suspenders with the
# startwm.sh written by FixXrdpStep -- that handles per-session kwinrc for
# xrdp logins, while this handles existing user home dirs for LightDM logins.
for user_home in /home/* /root; do
    [ -d "$user_home" ] || continue
    kwinrc="${user_home}/.config/kwinrc"
    mkdir -p "$(dirname "$kwinrc")" 2>/dev/null || true

    if [ -f "$kwinrc" ]; then
        # File exists -- update or append compositing settings
        if grep -q '^\[Compositing\]' "$kwinrc" 2>/dev/null; then
            sed -i '/^\[Compositing\]/,/^\[/{ s/^Enabled=.*/Enabled=false/; s/^Backend=.*/Backend=XRender/; s/^OpenGLIsUnsafe=.*/OpenGLIsUnsafe=true/; }' "$kwinrc" 2>/dev/null || true
            # Ensure all three keys exist under [Compositing]
            if ! grep -A100 '^\[Compositing\]' "$kwinrc" 2>/dev/null | grep -q '^Enabled='; then
                sed -i '/^\[Compositing\]/a Enabled=false' "$kwinrc" 2>/dev/null || true
            fi
            if ! grep -A100 '^\[Compositing\]' "$kwinrc" 2>/dev/null | grep -q '^Backend='; then
                sed -i '/^\[Compositing\]/a Backend=XRender' "$kwinrc" 2>/dev/null || true
            fi
            if ! grep -A100 '^\[Compositing\]' "$kwinrc" 2>/dev/null | grep -q '^OpenGLIsUnsafe='; then
                sed -i '/^\[Compositing\]/a OpenGLIsUnsafe=true' "$kwinrc" 2>/dev/null || true
            fi
        else
            # No [Compositing] section -- append it
            printf '\n[Compositing]\nEnabled=false\nBackend=XRender\nOpenGLIsUnsafe=true\n' >> "$kwinrc"
        fi
    else
        # File doesn't exist -- create it
        cat > "$kwinrc" << KWINRC_EOF
[Compositing]
Enabled=false
Backend=XRender
OpenGLIsUnsafe=true
KWINRC_EOF
    fi
    # Remove cached KWin output config that may reference invalid modes
    rm -f "${user_home}/.config/kwinoutputconfig.json" 2>/dev/null || true
    # Fix ownership for regular users (not root)
    username=$(basename "$user_home")
    if [ "$username" != "root" ]; then
        chown "$username":"$username" "$kwinrc" 2>/dev/null || true
        chown "$username":"$username" "$(dirname "$kwinrc")" 2>/dev/null || true
    fi
    echo "KWin compositing disabled for: $username"
done

echo "=== KWin compositing disable complete ==="
exit 0
