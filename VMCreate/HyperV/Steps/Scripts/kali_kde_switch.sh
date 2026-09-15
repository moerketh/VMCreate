#!/bin/bash
set -o pipefail

# Switches a Kali Linux deployment from XFCE to the KDE Plasma desktop.
#
# Triggered by the "Kali ... (KDE)" gallery twins (tag: kali-kde) and runs
# post-boot over SSH BEFORE the Auto RDP backend detection
# (AutoRdpBackendResolveStep, order 232) so the detector sees a
# Wayland-default Plasma session and selects the Lamco RDP Server.
#
# Procedure mirrors Kali's official "Switching Desktop Environments" doc
# (https://www.kali.org/docs/general-use/switching-desktop-environments/):
#   1. apt-get update
#   2. apt-get install -y kali-desktop-kde         (KDE desktop meta)
#      — preseed the login-manager choice to sddm (the doc's recommendation:
#        "We will select 'sddm' as we will have to replace KDE due to how it
#        interacts with Xfce"). Kali's XFCE image ships lightdm; without the
#        preseed the debconf prompt is skipped non-interactively and lightdm
#        survives as the display manager, leaving SDDM installed but never
#        activated (observed on TEST_20260910165003: lightdm kept the
#        display-manager.service symlink; "dpkg-reconfigure sddm" required).
#   3. default x-session-manager -> startplasma-wayland (non-interactive)
#      — REGISTER the alternative first: kali-desktop-kde does NOT register
#        startplasma-wayland in the x-session-manager alternatives group
#        (verified on TEST_20260910135942: `update-alternatives --list
#        x-session-manager` shows startplasma-x11/startxfce4/xfce4-session
#        while /usr/bin/startplasma-wayland EXISTS). A grep for it in the
#        alternatives therefore always failed and the old --auto fallback
#        picked startxfce4 — an X11 session that routed the Auto RDP
#        backend to xrdp instead of Lamco.
#   4. apt-get purge --autoremove --allow-remove-essential kali-desktop-xfce
#      (kali-desktop-* are system-protected per the doc; --allow-remove-essential
#      is the documented removal flag) — with dpkg recovery: lightdm's prerm
#      can fail with "Please be sure to run dpkg-reconfigure sddm" (exit 10)
#      under --autoremove, wedging dpkg ("too many errors, stopping"). Recover
#      by handing the DM over to sddm and running `dpkg --configure -a` so
#      the purge lands and sddm takes over.
#
# Output contract: the machine-readable terminal line
#     KALI_KDE_RESULT=ok|degraded
# "degraded" = the desktop switched but a side task failed (e.g. the
# session-manager alternative was not settable). Hard failures (apt install
# failing) exit non-zero and fail the deployment — shipping a half-installed
# desktop silently is the fabricated-success bug class.

DEGRADED=0

echo "Updating package indexes..."
if ! apt-get update 2>&1; then
    echo "ERROR: apt-get update failed."
    exit 1
fi

# -- Preseed sddm as the login manager (Kali doc: "select sddm") ----------
# The XFCE image ships lightdm; kali-desktop-kde's debconf prompt offers
# sddm. A non-interactive apt run skips the prompt and keeps lightdm —
# exactly the state that wedged TEST_20260910165003. Preseed BEFORE the
# install so the package's config script reads the choice at install time.
echo "Preseeding sddm as the display manager..."
echo "shared/default-x-display-manager select sddm" \
    | debconf-set-selections 2>/dev/null || true

echo "Installing KDE Plasma desktop (kali-desktop-kde)..."
if ! DEBIAN_FRONTEND=noninteractive apt-get install -y kali-desktop-kde 2>&1; then
    echo "ERROR: apt-get install kali-desktop-kde failed."
    exit 1
fi

# -- Make sddm the active display manager ---------------------------------
# Belt-and-braces for the debconf preseed: if the lightdm->sddm handover
# did not happen at install time, force it now so SDDM is the DM before
# the purge below can wedge lightdm's prerm.
if command -v systemctl >/dev/null 2>&1 && [ -x /usr/bin/sddm ]; then
    current_dm="$(cat /etc/X11/default-display-manager 2>/dev/null || true)"
    if [ "$current_dm" != "/usr/bin/sddm" ]; then
        echo "/usr/bin/sddm" > /etc/X11/default-display-manager
        echo "Display manager set to sddm (was: ${current_dm:-unset})."
        systemctl enable sddm.service 2>/dev/null || true
        systemctl disable lightdm.service 2>/dev/null || true
        systemctl disable gdm3.service 2>/dev/null || true
    else
        echo "Display manager already sddm."
    fi
fi

# -- Make KDE Plasma the default session (Kali doc step) ------------------
# REGISTER startplasma-wayland into the alternatives group FIRST —
# kali-desktop-kde leaves it unregistered (verified on TEST_20260910135942:
# the binary exists but `update-alternatives --list x-session-manager` never
# listed it). A grep in the alternatives therefore always failed and the old
# --auto fallback picked startxfce4 — an X11 session. Wayland is Kali's KDE
# default since 2023.1 and the Lamco backend needs a Wayland session.
# --config prompts interactively (impossible over an automation shell), so
# registration + --set is the non-interactive equivalent of the doc's step.
if command -v update-alternatives >/dev/null 2>&1; then
    if [ -x /usr/bin/startplasma-wayland ]; then
        update-alternatives --install /usr/bin/x-session-manager x-session-manager \
            /usr/bin/startplasma-wayland 50 2>&1
        if update-alternatives --set x-session-manager /usr/bin/startplasma-wayland 2>&1; then
            echo "Default session manager set to /usr/bin/startplasma-wayland"
        else
            DEGRADED=1
            echo "WARNING: update-alternatives --set failed for startplasma-wayland."
        fi
    else
        DEGRADED=1
        echo "WARNING: /usr/bin/startplasma-wayland not found — the session default cannot be set to KDE Wayland."
    fi
else
    DEGRADED=1
    echo "WARNING: update-alternatives not available — session manager default not set."
fi

echo "Removing the XFCE desktop meta-package (kali-desktop-xfce)..."
if ! DEBIAN_FRONTEND=noninteractive apt-get purge -y --autoremove --allow-remove-essential kali-desktop-xfce 2>&1; then
    # TEST_20260910165003: lightdm's prerm fails under --autoremove
    # ("Please be sure to run dpkg-reconfigure sddm", exit 10) and wedges
    # dpkg ("too many errors, stopping") — lightdm SURVIVES as the active
    # DM. Recover: hand the DM over inside dpkg's expected flow, then
    # configure pending packages so the purge completes.
    echo "WARNING: purge of kali-desktop-xfce failed — attempting sddm handover + dpkg recovery."
    echo "shared/default-x-display-manager select sddm" \
        | debconf-set-selections 2>/dev/null || true
    if command -v dpkg-reconfigure >/dev/null 2>&1; then
        DEBIAN_FRONTEND=noninteractive dpkg-reconfigure sddm 2>&1 || true
    fi
    if [ -x /usr/bin/sddm ]; then
        echo "/usr/bin/sddm" > /etc/X11/default-display-manager
    fi
    if DEBIAN_FRONTEND=noninteractive dpkg --configure -a 2>&1; then
        echo "dpkg recovered after purge failure."
    else
        DEGRADED=1
        echo "WARNING: dpkg --configure -a failed after the purge failure — dpkg may be in a broken state."
    fi
fi

# -- Final consistency check ------------------------------------------------
# The doc's verification step is "reboot and make sure all our changes were
# made properly". We cannot reboot inside this step (the deploy continues
# over SSH), but we CAN verify the two facts the DM reads at startup: the
# display-manager default and the session default. A wrong pair means the
# first boot lands on XFCE/X11 instead of KDE Wayland — report degraded,
# never a silent ok.
final_dm="$(cat /etc/X11/default-display-manager 2>/dev/null || true)"
final_sm="$(update-alternatives --query x-session-manager 2>/dev/null | grep '^Value:' | awk '{print $2}')"
if [ -x /usr/bin/sddm ] && [ "$final_dm" != "/usr/bin/sddm" ]; then
    DEGRADED=1
    echo "WARNING: sddm is installed but /etc/X11/default-display-manager is '$final_dm' — first boot may start the wrong DM."
fi
if [ "$final_sm" != "/usr/bin/startplasma-wayland" ]; then
    DEGRADED=1
    echo "WARNING: default session manager is '$final_sm', expected /usr/bin/startplasma-wayland."
fi

if [ "$DEGRADED" = "1" ]; then
    echo "KALI_KDE_RESULT=degraded"
else
    echo "KALI_KDE_RESULT=ok"
fi
exit 0