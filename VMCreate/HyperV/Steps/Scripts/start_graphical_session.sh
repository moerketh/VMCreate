#!/bin/bash
set -o pipefail

# Activates the graphical session on a freshly provisioned Lamco VM without
# a manual reboot.
#
# Why this exists: InstallLamcoRdpStep (235) and EnableGraphicalAutologinStep
# (238) arm everything for the NEXT session start — autologin only takes
# effect at the next DM start, and the lamco user units
# (WantedBy=graphical-session.target) only start with the session. On a fresh
# deployment the VM sits at the display-manager greeter: the units are armed
# but dead until someone reboots (observed on every TEST_20260910* run —
# "LAMCO deferred: listeners bind at session start" was the manual-reboot
# pain point).
#
# The fix: restart the display manager over SSH. The DM restart applies the
# autologin configuration written by step 238, the Wayland session starts,
# graphical-session.target activates, and the lamco units (server + consent
# grant + idle-inhibit) start with it. This is the in-deploy equivalent of
# the reboot, scoped to the DM.
#
# Result contract: SESSION_START_RESULT=ok|degraded|consent on the last line.
# Hard failures exit non-zero before the line. "consent" is the expected
# first-boot verdict: the one-time portal consent dialog appears on the VM
# console and one Allow click binds the listeners — reported as ok-with-
# notice by the host step (the deployment is complete and correct).

DEGRADED=0

USER="__AUTOLOGIN_USER__"

# Sentinels must be constructed at runtime from string fragments so a
# host-side string.Replace of __AUTOLOGIN_USER__ across the whole script
# cannot rewrite this comparison target (the self-clobber bug seen on
# TEST_20260910195849: "x = placeholder" became "kali = kali", always
# true). Only the assignment above is the substitution site.
_PLACEHOLDER='__AUTOLOGIN'
_PLACEHOLDER="${_PLACEHOLDER}"'_USER__'
if [ "$USER" = "$_PLACEHOLDER" ]; then
    USER=""
fi
if [ -z "$USER" ]; then
    USER=$(awk -F: '$3 >= 1000 && $3 < 65534 && $6 != "" {print $1; exit}' /etc/passwd 2>/dev/null || echo "")
fi
if [ -z "$USER" ]; then
    for cand in kali user parrot ubuntu; do
        if id "$cand" >/dev/null 2>&1; then USER="$cand"; break; fi
    done
fi
if [ -z "$USER" ]; then
    echo "ERROR: no desktop user could be resolved — cannot start a graphical session." >&2
    exit 1
fi
if ! id "$USER" >/dev/null 2>&1; then
    echo "ERROR: resolved desktop user '$USER' does not exist on this guest." >&2
    exit 1
fi
USER_UID=$(id -u "$USER")
echo "Desktop user resolved as '$USER' (uid $USER_UID)."

# -- Already in a session? Nothing to do ------------------------------------
# A live Wayland session means autologin already ran (or the operator is
# logged in): the lamco units are bound to graphical-session.target and are
# starting with it. Report ok — the poll below confirms the listeners.
SESSION_ACTIVE=$(sudo -u "$USER" XDG_RUNTIME_DIR=/run/user/$USER_UID \
    systemctl --user is-active graphical-session.target 2>/dev/null || true)
if [ "$SESSION_ACTIVE" = "active" ]; then
    echo "Graphical session already active for '$USER' — no DM restart needed."
else
    # -- Resolve the display manager ----------------------------------------
    # display-manager.service is the systemd alias the DM installs
    # (sddm/lightdm/gdm3); /etc/X11/default-display-manager is the
    # Debian-family file the package writes. Check both: the alias is what
    # systemctl restart accepts, the file is the fallback when the alias is
    # missing (e.g. a DM installed but not enabled).
    DM_UNIT=""
    if systemctl cat display-manager.service >/dev/null 2>&1; then
        DM_UNIT="display-manager.service"
    else
        dm_bin="$(cat /etc/X11/default-display-manager 2>/dev/null || true)"
        case "$dm_bin" in
            /usr/bin/sddm|/usr/sbin/sddm)   DM_UNIT="sddm.service" ;;
            /usr/sbin/lightdm|/usr/bin/lightdm) DM_UNIT="lightdm.service" ;;
            /usr/sbin/gdm3|/usr/bin/gdm3)  DM_UNIT="gdm3.service" ;;
            *) DM_UNIT="" ;;
        esac
    fi
    if [ -z "$DM_UNIT" ]; then
        echo "DEGRADED: no display manager found (no display-manager.service alias, /etc/X11/default-display-manager is '$(cat /etc/X11/default-display-manager 2>/dev/null || echo unset)') — cannot activate the session." >&2
        echo "SESSION_START_RESULT=degraded"
        exit 0
    fi
    echo "Restarting $DM_UNIT to apply autologin and start the session..."
    # -- Restart the DM ------------------------------------------------------
    # The restart kills the greeter and starts a fresh DM instance, which
    # reads the autologin config written by step 238 and logs the user
    # straight into the Wayland session. This is the DM-scoped equivalent
    # of the reboot the deploy previously required.
    if ! systemctl restart "$DM_UNIT" 2>&1; then
        echo "DEGRADED: systemctl restart $DM_UNIT failed." >&2
        echo "SESSION_START_RESULT=degraded"
        exit 0
    fi
    echo "$DM_UNIT restarted."
fi

# -- Wait for the session (and the lamco units) to come up ------------------
# Poll for the session + listener state, mirroring the install script's
# readiness gate. Budget: DM restart + Wayland session + portal grant
# attempt can take ~60-90s on a 2-vCPU guest; 12 x 10s covers it with room.
RDY_OUTCOME="timeout"
for r in $(seq 1 12); do
    # Session up?
    SESS=$(sudo -u "$USER" XDG_RUNTIME_DIR=/run/user/$USER_UID \
        systemctl --user is-active graphical-session.target 2>/dev/null || true)
    if [ "$SESS" = "active" ]; then
        # Server dispatcher line = listeners bound.
        if journalctl _UID=$USER_UID --since "-5 min" --no-pager 2>/dev/null \
            | grep -aq "Accept dispatcher started"; then
            RDY_OUTCOME="ready"
            break
        fi
        # Consent prompt surfaced by lamco-grant.service = deployment OK,
        # one console click pending. The fork emits several variants
        # ("Permission dialog will appear (one-time grant)" in server/mod.rs,
        # "permission dialog will appear" in portal.rs/libei) — match
        # case-insensitively so no capitalization drift re-breaks the gate.
        if journalctl _UID=$USER_UID --since "-5 min" --no-pager 2>/dev/null \
            | grep -aqi "permission dialog will appear"; then
            RDY_OUTCOME="consent"
            break
        fi
    fi
    sleep 10
done

case "$RDY_OUTCOME" in
    ready)
        echo "Graphical session active; lamco listeners bound (TCP/vsock)."
        ;;
    consent)
        echo "NOTICE: graphical session active; one-time portal consent dialog is on the VM console — click Allow once, then the listeners bind."
        ;;
    timeout)
        echo "DEGRADED: session/listener readiness gate timed out (120s) — graphical-session.target reports '$SESS'."
        echo "Diagnose with: journalctl _UID=$USER_UID -b --no-pager | tail -n 50"
        DEGRADED=1
        ;;
esac

if [ "$DEGRADED" = "1" ]; then
    echo "SESSION_START_RESULT=degraded"
    exit 0
fi
if [ "$RDY_OUTCOME" = "consent" ]; then
    echo "SESSION_START_RESULT=consent"
    exit 0
fi
echo "SESSION_START_RESULT=ok"
exit 0