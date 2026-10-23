using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// <see cref="IGuestShell"/> decorator that transparently executes
    /// customization-step commands inside the ISO stage's chroot of the
    /// target filesystem (mounted at <c>/mnt/new</c>), so the existing
    /// step classes run pre-boot — behind the plymouth splash — instead of
    /// over post-boot TCP SSH.
    /// <para>
    /// Commands are wrapped as
    /// <c>sudo chroot /mnt/new /usr/bin/env -i PATH=… HOME=/root bash -c '…'</c>
    /// on the underlying vsock ISO shell (whose session user is the
    /// passwordless-sudo <c>vmcreate</c> account). Inside the wrapper a
    /// <c>sudo()</c> bash function is defined so the steps' existing
    /// <c>sudo …</c> commands keep working even though the chroot's target
    /// may not have sudo installed yet — and nothing sudo-shaped lands on
    /// the target disk (the function lives only in this bash process).
    /// <c>sudo -u user</c> maps to <c>runuser -u user -- env …</c>;
    /// password/stddin flags (<c>-S</c>, <c>-n</c>) are dropped — the
    /// chroot already runs as root.
    /// </para>
    /// <para>
    /// Liveness: execution routes through
    /// <see cref="SshGuestShell.RunStreamingCommandAsync"/> with the ISO
    /// cycle's idle budget. Package installs print continuously, but silent
    /// stretches (initramfs regeneration, dracut) would trip the idle
    /// watchdog, so the wrapper starts a guest-side heartbeat loop that
    /// emits <c>VMCREATE:MSG:&lt;label&gt;</c> lines — any stdout line
    /// satisfies the idle watchdog and keeps the session alive. These lines
    /// are host-side only: <see cref="ExecuteWrappedAsync"/> filters them
    /// from the step's returned stdout, and the plymouth splash is driven
    /// separately by the executor (see
    /// <see cref="PostBootCustomizationService.RunLinuxChrootAsync"/>),
    /// which relays each step name through the ISO shell.
    /// </para>
    /// <para>
    /// File transfers (<see cref="CopyContentAsync"/> etc.) prefix the
    /// guest path with the chroot root, so a step that writes
    /// <c>/tmp/tool.sh</c> lands in the TARGET's <c>/tmp</c>, and the
    /// command that consumes it finds it there.
    /// </para>
    /// </summary>
    public sealed class ChrootGuestShell : IGuestShell
    {
        private readonly SshGuestShell _iso;
        private readonly ILogger _logger;

        /// <summary>The mount point of the target filesystem inside the live ISO.</summary>
        public const string DefaultChrootRoot = "/mnt/new";

        // Chroot-safe PATH: env -i wipes the environment, and the target's
        // rootfs layout (merged-/usr or not) is unknown until probed. This
        // union covers Debian, Arch, Fedora and openSUSE images.
        private const string ChrootPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

        // Liveness budgets: total matches the ISO cycle's per-command cap;
        // idle matches the scenario scripts' budget (the heartbeat loop
        // resets it every Interval seconds while a command runs).
        private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

        // One-shot latch for the plymouth-death warning: the daemon stays
        // dead once it dies, so logging per splash update would spam.
        private bool _splashDeathLogged;

        /// <summary>
        /// The step name shown by the heartbeat lines. Set by the executor
        /// before each step's commands run; when null the heartbeat is
        /// suppressed (nothing to attribute the liveness to).
        /// </summary>
        public string? CurrentStepLabel { get; set; }

        public string VmName => _iso.VmName;
        public GuestExecutionContext Context => GuestExecutionContext.Chroot;

        public ChrootGuestShell(SshGuestShell isoShell, ILogger logger)
        {
            _iso = isoShell ?? throw new ArgumentNullException(nameof(isoShell));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> RunCommandAsync(string command, CancellationToken ct)
            => await ExecuteWrappedAsync(command, TotalTimeout, ct);

        public async Task<string> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
            => await ExecuteWrappedAsync(command, timeout, ct);

        public async Task<string> RunCommandWithStdinAsync(string command, string stdinContent, CancellationToken ct)
            // Forward through the wrapper (sudo() drops -S: no password is
            // needed as root); stdin rides ssh's stdin unchanged.
            => await _iso.RunCommandWithStdinAsync(BuildChrootCommand(command), stdinContent, ct);

        public Task CopyContentAsync(string content, string guestPath, CancellationToken ct)
            => _iso.CopyContentAsync(content, TargetPath(guestPath), ct);

        public Task CopySecretAsync(string content, string guestPath, CancellationToken ct)
            => _iso.CopySecretAsync(content, TargetPath(guestPath), ct);

        public Task CopyFileAsync(string hostPath, string guestPath, CancellationToken ct)
            => _iso.CopyFileAsync(hostPath, TargetPath(guestPath), ct);

        public Task WaitForReadyAsync(CancellationToken ct) => _iso.WaitForReadyAsync(ct);

        /// <summary>
        /// Best-effort splash update on the LIVE ISO (not the chroot — the
        /// chroot has no plymouth): <c>plymouth display-message</c> plus the
        /// determinate progress bar, mirroring common.sh's progress(). A
        /// null <paramref name="message"/> only advances the bar. Failures
        /// (no plymouth daemon under the verbose GRUB entry, transport
        /// hiccup) are logged and swallowed: the splash is cosmetic and must
        /// never fail a customization step.
        /// </summary>
        public async Task ShowOnSplashAsync(string? message, int percent, CancellationToken ct)
        {
            string text = message is null ? "" : " --text='" + SshTransport.EscapeSingleQuotes(message) + "'";
            // The leading --ping probe doubles as instrumentation: when the
            // daemon has died mid-run (the guest screen falls back to the
            // text console), every later display-message silently no-ops.
            // A one-time warning with the current step label pinpoints WHEN
            // the splash was lost instead of leaving only a visual symptom.
            string cmd = "sudo plymouth --ping >/dev/null 2>&1 && echo VMCREATE:SPLASH_ALIVE; "
                       + $"sudo plymouth display-message{text} >/dev/null 2>&1 || true; "
                       + $"sudo plymouth system-update --progress={Math.Clamp(percent, 0, 100)} >/dev/null 2>&1 || true";
            try
            {
                string stdout = await _iso.RunCommandAsync(cmd, TimeSpan.FromSeconds(15), ct);
                if (!stdout.Contains("VMCREATE:SPLASH_ALIVE", StringComparison.Ordinal) && !_splashDeathLogged)
                {
                    _splashDeathLogged = true;
                    _logger.LogWarning(
                        "Plymouth daemon stopped answering on VM {VMName} (during step '{Step}') — the guest screen falls back to the text console; splash updates will no-op until the daemon returns.",
                        VmName, CurrentStepLabel ?? "(between steps)");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Splash update skipped on VM {VMName} (plymouth absent or unreachable)", VmName);
            }
        }

        // ── Wrapping ─────────────────────────────────────────────────────

        /// <summary>
        /// Maps a step's target-guest path to its location inside the live
        /// ISO (<c>/etc/x</c> → <c>/mnt/new/etc/x</c>). Paths already rooted
        /// at the chroot root pass through unchanged.
        /// </summary>
        public string TargetPath(string guestPath)
        {
            if (string.IsNullOrWhiteSpace(guestPath))
                throw new ArgumentException("Guest path must not be empty.", nameof(guestPath));
            guestPath = guestPath.Replace('\\', '/').TrimEnd('/');
            if (guestPath == "")
                guestPath = "/";
            if (guestPath == DefaultChrootRoot || guestPath.StartsWith(DefaultChrootRoot + "/", StringComparison.Ordinal))
                return guestPath;
            return DefaultChrootRoot + (guestPath.StartsWith("/", StringComparison.Ordinal) ? guestPath : "/" + guestPath);
        }

        /// <summary>
        /// Builds the ISO-side command that runs <paramref name="command"/>
        /// inside the chroot. The inner bash defines the sudo() shim and the
        /// heartbeat loop; the step's command text is single-quoted POSIX-style
        /// so its own quoting survives verbatim.
        /// </summary>
        public string BuildChrootCommand(string command)
            => $"sudo chroot {DefaultChrootRoot} /usr/bin/env -i PATH='{ChrootPath}' HOME=/root "
               + "bash -c '" + SshTransport.EscapeSingleQuotes(InnerScript(command).Replace("\r\n", "\n")) + "'";

        private string InnerScript(string command)
        {
            // sudo() shim: in-chroot commands already run as root, so plain
            // sudo passthrough; -u maps to runuser (util-linux, present on
            // every supported target); -S/-n/-E and other root-meaningless
            // flags are dropped. 'env' absorbs both VAR=val prefixes ("sudo
            // VAR=1 cmd") and plain commands — "$@" alone would try to exec
            // a binary literally named "VAR=1".
            // Flag parsing mirrors sudo's own rules, because step command
            // trees include third-party scripts whose sudo forms we don't
            // control (VirtualBox's uninstall.sh in particular):
            //   - '--' ends option parsing and is consumed.
            //   - -u only takes a value that is non-empty and not
            //     option-shaped; a following '--' or flag means the switch
            //     itself was malformed and is skipped (root context needs
            //     no user switch).
            //   - --user=NAME is honored like -u NAME.
            const string shim = @"sudo() {
  local user=''
  while [ ""$#"" -gt 0 ]; do
    case ""$1"" in
      --user=*) user=""${1#--user=}""; shift ;;
      -u)
        if [ ""$#"" -ge 2 ] && [ -n ""$2"" ] && [ ""${2#-}"" = ""$2"" ]; then
          user=""$2""
        fi
        # Always consume the (possibly malformed/empty) value with the
        # flag — a leaked empty argument once made env exec ''.
        if [ ""$#"" -ge 2 ]; then shift 2; else shift; fi
        ;;
      --) shift; break ;;
      -*) shift ;;
      *)  break ;;
    esac
  done
  if [ -n ""$user"" ]; then
    runuser -u ""$user"" -- env ""$@""
  else
    env ""$@""
  fi
}";

            string heartbeat = "";
            if (!string.IsNullOrWhiteSpace(CurrentStepLabel))
            {
                // Self-terminating heartbeat: the loop watches the parent
                // bash ($$) and exits on its own when the wrapper dies, so
                // the loop can never outlive the session even if the step
                // command replaces the EXIT trap (EnablePasswordlessSudo
                // style) — an orphaned loop holding stdout open would hang
                // the SSH call until the total timeout instead of failing
                // fast. An explicit kill on wrapper exit remains as a
                // fast path (the loop's sleep can outlive $$ by one
                // interval otherwise).
                // The sleep's output is redirected to /dev/null: the sleep
                // process never writes anything, but an UNREDIRECTED sleep
                // inherits the session's stdout pipe, and the EXIT-trap
                // kill takes out the subshell only — the orphaned sleep
                // then held the pipe open and every step cost a full
                // heartbeat interval (60s) past its real work while sshd
                // waited for EOF. Redirected, the orphan holds nothing and
                // the session closes the moment the wrapper exits; the
                // loop's echo still relays to the host normally.
                // The label is single-quoted (POSIX-escaped) so $, " and `
                // in a step name can't expand.
                string label = SshTransport.EscapeSingleQuotes(CurrentStepLabel);
                heartbeat = "\n( while kill -0 $$ 2>/dev/null; do sleep "
                          + (int)HeartbeatInterval.TotalSeconds + " >/dev/null 2>&1; "
                          + $"echo VMCREATE:MSG:'{label}'; done ) &\n_hb=$!\ntrap 'kill $_hb 2>/dev/null' EXIT\n";
            }

            // The step command runs in a SUBSHELL on its own line: its exit
            // status propagates ($?), but any trap it installs (or resets)
            // stays inside the subshell and cannot clobber the wrapper's EXIT
            // trap — the heartbeat kill survives hostile commands. The
            // newline is load-bearing: 'fn(){…}( cmd )' with nothing between
            // the closing brace and the parenthesis is a bash syntax error,
            // and the no-label path (no heartbeat text) once generated
            // exactly that.
            return shim + heartbeat + "\n( " + command + " )";
        }

        // ── Streaming execution ──────────────────────────────────────────

        private async Task<string> ExecuteWrappedAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            var output = new StringBuilder();
            string wrapped = BuildChrootCommand(command);
            _logger.LogDebug("Chroot exec on VM {VMName} ({Length} chars, total {Total}s)",
                VmName, command.Length, timeout.TotalSeconds);

            await _iso.RunStreamingCommandAsync(wrapped, timeout, IdleTimeout, line =>
            {
                // Heartbeat lines exist for the idle watchdog, not for the
                // caller: steps parse terminal RESULT= prefixes from the
                // returned stdout and never expect VMCREATE: protocol noise.
                if (line.StartsWith("VMCREATE:MSG:", StringComparison.Ordinal))
                    return;
                lock (output) output.AppendLine(line);
            }, ct);

            lock (output) return output.ToString();
        }
    }
}
