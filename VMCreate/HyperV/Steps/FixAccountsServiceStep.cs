using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Fixes AccountsService per-user session overrides on the guest VM.
    /// <para>
    /// AccountsService stores per-user session preferences in
    /// <c>/var/lib/AccountsService/users/&lt;username&gt;</c>.  The
    /// <c>XSession=</c> key takes priority over LightDM's
    /// <c>user-session=</c> and can reference a Wayland session
    /// (e.g. <c>XSession=plasma</c>) that no longer exists after Wayland
    /// is disabled.  This step rewrites or removes any <c>XSession=</c>
    /// that points to a Wayland session, and creates missing entries for
    /// users who haven't logged in yet.
    /// </para>
    /// <para>
    /// Note: Plasma X11 sessions have different names across distros:
    /// Parrot OS / KDE Neon use <c>plasmax11</c> (no hyphen),
    /// Kubuntu / openSUSE use <c>plasma-x11</c> (with hyphen).
    /// This step detects which variant exists at runtime.
    /// </para>
    /// <para>
    /// Must run before <see cref="DisableWaylandSessionsStep"/> so that
    /// LightDM can resolve session names from .desktop files that are
    /// still present when AccountsService is read.
    /// </para>
    /// <para>
    /// Runs at Order 255, after <see cref="DisableKwinCompositingStep"/> (250)
    /// and before <see cref="DisableWaylandSessionsStep"/> (270).
    /// </para>
    /// </summary>
    public class FixAccountsServiceStep : ICustomizationStep
    {
        public string Name => "Fix AccountsService Sessions";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 255;
        public string? ProgressPhaseId => "Sub_FixAccountsService";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Fixing AccountsService session overrides on VM {VMName}", shell.VmName);

            string script = AccountsScript.Replace("\r\n", "\n");
            await shell.CopyContentAsync(script, "/tmp/fix_accounts.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/fix_accounts.sh; sudo rm -f /tmp/fix_accounts.sh", ct);

            logger.LogInformation("AccountsService fix result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }

        private const string AccountsScript = @"#!/bin/bash
set -o pipefail

# -- Detect available X11 sessions -----------------------------------------
x11_session=""
for xs in /usr/share/xsessions/*.desktop; do
    [ -f ""$xs"" ] || continue
    x11_session=$(basename ""$xs"" .desktop)
    break
done

# -- Detect Plasma X11 session name (plasmax11 vs plasma-x11) ------------
plasma_x11_name=""plasmax11""
if [ -f /usr/share/xsessions/plasmax11.desktop ]; then
    plasma_x11_name=""plasmax11""
elif [ -f /usr/share/xsessions/plasma-x11.desktop ]; then
    plasma_x11_name=""plasma-x11""
fi

# -- Fix AccountsService per-user session overrides ------------------------
# AccountsService stores per-user session preferences in
# /var/lib/AccountsService/users/<username>.  The XSession= key takes
# priority over LightDM's user-session= and can reference a Wayland session
# (e.g. XSession=plasma) that no longer exists after we disable Wayland.
# We must rewrite or remove any XSession= that points to a Wayland session.
# If the file doesn't exist, we create it with the correct X11 session.
for acct_user in /var/lib/AccountsService/users/*; do
    [ -f ""$acct_user"" ] || continue
    username=$(basename ""$acct_user"")

    # If an X11 session was detected, rewrite any Wayland XSession to the
    # X11 equivalent (e.g. plasma -> plasmax11, gnome -> gnome-x11).
    if [ -n ""$x11_session"" ]; then
        # Replace known Wayland session names with their X11 counterparts
        # plasma -> plasmax11 or plasma-x11 (detected above)
        sed -i ""s/^XSession=plasma$/XSession=$plasma_x11_name/"" ""$acct_user"" 2>/dev/null || true
        # gnome -> gnome-x11
        sed -i 's/^XSession=gnome$/XSession=gnome-x11/' ""$acct_user"" 2>/dev/null || true
        # cinnamon -> cinnamon-x11
        sed -i 's/^XSession=cinnamon$/XSession=cinnamon-x11/' ""$acct_user"" 2>/dev/null || true
        # mate -> mate-x11
        sed -i 's/^XSession=mate$/XSession=mate-x11/' ""$acct_user"" 2>/dev/null || true
        # xfce -> xfce (already X11, no change needed)

        # If XSession still references a Wayland-only session (not rewritten
        # above), replace it with the detected X11 session name.
        if grep -q '^XSession=' ""$acct_user"" 2>/dev/null; then
            current_session=$(grep '^XSession=' ""$acct_user"" | head -1 | cut -d= -f2)
            # Check if the current session has a matching X11 .desktop file
            if [ ! -f ""/usr/share/xsessions/${current_session}.desktop"" ] && [ ! -f ""/usr/share/xsessions/${current_session}-x11.desktop"" ]; then
                # The referenced session doesn't exist as X11 -- override it
                sed -i ""s/^XSession=.*/XSession=$x11_session/"" ""$acct_user"" 2>/dev/null || true
            fi
        fi
    else
        # No X11 session detected -- just remove any XSession line so the
        # display manager falls back to its configured default.
        sed -i '/^XSession=/d' ""$acct_user"" 2>/dev/null || true
    fi
    echo ""Fixed AccountsService for user: $username""
done

# Also ensure AccountsService entries exist for users who haven't logged in yet.
# LightDM reads XSession= from these files, and if the file is missing it may
# fall back to a Wayland session from the greeter dropdown.
if [ -n ""$x11_session"" ]; then
    for user_home in /home/* /root; do
        [ -d ""$user_home"" ] || continue
        username=$(basename ""$user_home"")
        acct_file=""/var/lib/AccountsService/users/$username""
        if [ ! -f ""$acct_file"" ]; then
            mkdir -p /var/lib/AccountsService/users 2>/dev/null || true
            cat > ""$acct_file"" << ACCT_EOF
[User]
XSession=$x11_session
ACCT_EOF
            echo ""Created AccountsService entry for: $username""
        fi
    done
fi

echo ""=== AccountsService fix complete ===""
exit 0
";
    }
}
