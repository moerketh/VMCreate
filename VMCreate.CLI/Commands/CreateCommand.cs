using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.CLI.Progress;
using VMCreate.Gallery;

namespace VMCreate.CLI.Commands
{
    public static class CreateCommand
    {
        public static Command Build(IServiceProvider services)
        {
            var cmd = new Command("create", "Create a new Hyper-V virtual machine.");

            // ── Image selection ────────────────────────────────────────────────
            var imageOpt = new Option<string>("--image")
            {
                Description = "Gallery image name to use (partial match). List names with 'vmcreate list'."
            };

            var imageUriOpt = new Option<string>("--image-uri")
            {
                Description = "URL or local path to a disk image (VMDK, QCOW2, VHDX, VHD, OVA, or archive)."
            };

            var checksumOpt = new Option<string>("--checksum")
            {
                Description = "Expected SHA-256 hash of the file at --image-uri."
            };

            var checksumUriOpt = new Option<string>("--checksum-uri")
            {
                Description = "URL to a checksum file for the file at --image-uri."
            };

            var nameOpt = new Option<string>("--name")
            {
                Description = "VM name. Defaults to the gallery image name."
            };

            // ── VM hardware ────────────────────────────────────────────────────
            var memoryOpt = new Option<int>("--memory")
            {
                Description = "Memory in MB (default: 4096).",
                DefaultValueFactory = _ => 4096
            };

            var cpuOpt = new Option<int>("--cpu")
            {
                Description = "Number of virtual CPUs (default: 2).",
                DefaultValueFactory = _ => 2
            };

            var diskSizeOpt = new Option<int>("--disk-size")
            {
                Description = "New disk size in GB. 0 = auto-detect from source image.",
                DefaultValueFactory = _ => 0
            };

            var noAutoDetectDiskOpt = new Option<bool>("--no-auto-detect-disk")
            {
                Description = "Disable auto-detect disk size. Requires --disk-size.",
                DefaultValueFactory = _ => false
            };

            var noNestedVirtOpt = new Option<bool>("--no-nested-virt")
            {
                Description = "Disable nested virtualization extensions.",
                DefaultValueFactory = _ => false
            };

            var replaceOpt = new Option<bool>("--replace")
            {
                Description = "Stop and remove an existing VM with the same name before creating.",
                DefaultValueFactory = _ => false
            };

            // ── Customization ──────────────────────────────────────────────────
            var noXrdpOpt = new Option<bool>("--no-xrdp")
            {
                Description = "Disable xRDP / Enhanced Session setup. (Deprecated alias for --rdp-backend none.)",
                DefaultValueFactory = _ => false
            };

            var rdpBackendOpt = new Option<string>("--rdp-backend")
            {
                Description = "RDP server backend: auto (default — detects Wayland vs X11 in the guest post-boot and picks the best backend), xrdp (disables Wayland), lamco (Wayland-native Lamco RDP Server), or none.",
                DefaultValueFactory = _ => "auto"
            };

            var noIntegrationSvcOpt = new Option<bool>("--no-integration-services")
            {
                Description = "Disable Hyper-V integration services (maximum isolation mode).",
                DefaultValueFactory = _ => false
            };

            var dnsModeOpt = new Option<string>("--dns-mode")
            {
                Description = "DNS mode: host (auto-detect) or custom.",
                DefaultValueFactory = _ => "host"
            };

            var nameserversOpt = new Option<string>("--nameservers")
            {
                Description = "Comma-separated DNS nameservers. Requires --dns-mode custom."
            };

            var sshKeyOpt = new Option<string>("--ssh-key")
            {
                Description = "Path to a custom SSH public key (.pub). Defaults to auto-generated key."
            };

            var noTimezoneOpt = new Option<bool>("--no-timezone-sync")
            {
                Description = "Disable guest timezone synchronization.",
                DefaultValueFactory = _ => false
            };

            // ── HTB VPN ────────────────────────────────────────────────────────
            var htbTokenOpt = new Option<string>("--htb-token")
            {
                Description = "Hack The Box API token for downloading VPN configuration."
            };

            var htbVpnOpt = new Option<string>("--htb-vpn")
            {
                Description = "Comma-separated HTB VPN endpoint types: labs, sp, academy."
            };

            var ovpnOpt = new Option<string>("--ovpn")
            {
                Description = "Path to a manually downloaded .ovpn file."
            };

            var noOpenVpnOpt = new Option<bool>("--no-openvpn")
            {
                Description = "Skip installing OpenVPN and the NetworkManager OpenVPN plugin.",
                DefaultValueFactory = _ => false
            };

            // ── Distribution-specific ──────────────────────────────────────────
            var optionOpt = new Option<string[]>("--option")
            {
                Description = "Distribution-specific option in key=value form (repeatable). " +
                             "E.g. --option pwncloudos-sync=true",
                AllowMultipleArgumentsPerToken = false,
                Arity = ArgumentArity.ZeroOrMore
            };

            // ── Output / behaviour ─────────────────────────────────────────────
            var formatOpt = new Option<string>("--format")
            {
                Description = "Output format: text or json.",
                DefaultValueFactory = _ => "text"
            };

            var quietOpt = new Option<bool>("--quiet")
            {
                Description = "Suppress progress output. Exit code still reflects success/failure.",
                DefaultValueFactory = _ => false
            };

            var nonInteractiveOpt = new Option<bool>("--non-interactive")
            {
                Description = "Fail with an error instead of prompting for missing information.",
                DefaultValueFactory = _ => false
            };

            // Register all options
            cmd.Options.Add(imageOpt);
            cmd.Options.Add(imageUriOpt);
            cmd.Options.Add(checksumOpt);
            cmd.Options.Add(checksumUriOpt);
            cmd.Options.Add(nameOpt);
            cmd.Options.Add(memoryOpt);
            cmd.Options.Add(cpuOpt);
            cmd.Options.Add(diskSizeOpt);
            cmd.Options.Add(noAutoDetectDiskOpt);
            cmd.Options.Add(noNestedVirtOpt);
            cmd.Options.Add(replaceOpt);
            cmd.Options.Add(noXrdpOpt);
            cmd.Options.Add(rdpBackendOpt);
            cmd.Options.Add(noIntegrationSvcOpt);
            cmd.Options.Add(dnsModeOpt);
            cmd.Options.Add(nameserversOpt);
            cmd.Options.Add(sshKeyOpt);
            cmd.Options.Add(noTimezoneOpt);
            cmd.Options.Add(htbTokenOpt);
            cmd.Options.Add(htbVpnOpt);
            cmd.Options.Add(ovpnOpt);
            cmd.Options.Add(noOpenVpnOpt);
            cmd.Options.Add(optionOpt);
            cmd.Options.Add(formatOpt);
            cmd.Options.Add(quietOpt);
            cmd.Options.Add(nonInteractiveOpt);

            cmd.SetAction(async (parseResult, ct) =>
            {
                var args = new CreateArgs
                {
                    Image = parseResult.GetValue(imageOpt),
                    ImageUri = parseResult.GetValue(imageUriOpt),
                    Checksum = parseResult.GetValue(checksumOpt),
                    ChecksumUri = parseResult.GetValue(checksumUriOpt),
                    Name = parseResult.GetValue(nameOpt),
                    MemoryMb = parseResult.GetValue(memoryOpt),
                    CpuCount = parseResult.GetValue(cpuOpt),
                    DiskSizeGb = parseResult.GetValue(diskSizeOpt),
                    NoAutoDetectDisk = parseResult.GetValue(noAutoDetectDiskOpt),
                    NoNestedVirt = parseResult.GetValue(noNestedVirtOpt),
                    Replace = parseResult.GetValue(replaceOpt),
                    NoXrdp = parseResult.GetValue(noXrdpOpt),
                    RdpBackend = parseResult.GetValue(rdpBackendOpt),
                    NoIntegrationServices = parseResult.GetValue(noIntegrationSvcOpt),
                    DnsMode = parseResult.GetValue(dnsModeOpt),
                    Nameservers = parseResult.GetValue(nameserversOpt),
                    SshKeyPath = parseResult.GetValue(sshKeyOpt),
                    NoTimezoneSync = parseResult.GetValue(noTimezoneOpt),
                    HtbToken = parseResult.GetValue(htbTokenOpt),
                    HtbVpn = parseResult.GetValue(htbVpnOpt),
                    OvpnPath = parseResult.GetValue(ovpnOpt),
                    NoOpenVpn = parseResult.GetValue(noOpenVpnOpt),
                    Options = parseResult.GetValue(optionOpt) ?? Array.Empty<string>(),
                    Format = parseResult.GetValue(formatOpt),
                    Quiet = parseResult.GetValue(quietOpt),
                    NonInteractive = parseResult.GetValue(nonInteractiveOpt),
                };

                return await RunAsync(services, args, ct);
            });

            return cmd;
        }

        /// <summary>
        /// Resolves the RDP backend from --rdp-backend, with --no-xrdp as a
        /// deprecated back-compat alias for --rdp-backend none. --no-xrdp only
        /// takes effect when --rdp-backend is left at its default (auto).
        /// An unrecognized value FAILS LOUDLY — silently mapping a typo
        /// (e.g. "lamc") to Xrdp would provision a completely different
        /// desktop stack than the user asked for.
        /// Returns false (and reports a validation error) on unrecognized values;
        /// TryResolveRdpBackend is a pure parse, checked before any VM work starts.
        /// (System.CommandLine 2.0 exposes no value-validation callbacks here;
        /// the CLI validates values itself and returns exit codes instead.)
        /// <para>
        /// Note: RdpBackend.Auto (the default) is resolved at runtime in the
        /// guest by AutoRdpBackendResolveStep — the CLI just passes it through.
        /// The CLI's Program.ScannableAssemblies covers the same assembly set
        /// as the GUI's App.xaml.cs scan (pinned by CliFrontEndParityTests),
        /// so the resolver and every other VMCreate-assembly step run for
        /// CLI deployments exactly as they do in the GUI.
        /// </para>
        /// </summary>
        private static RdpBackend ResolveRdpBackend(string? rdpBackend, bool noXrdp, bool jsonMode, out bool valid)
        {
            valid = true;
            if (!string.IsNullOrEmpty(rdpBackend)
                && !string.Equals(rdpBackend, "auto", StringComparison.OrdinalIgnoreCase))
            {
                switch (rdpBackend.ToLowerInvariant())
                {
                    case "xrdp":
                        return RdpBackend.Xrdp;
                    case "lamco":
                        return RdpBackend.Lamco;
                    case "none":
                        return RdpBackend.None;
                    default:
                        valid = false;
                        PrintError(jsonMode, "validation",
                            $"Unknown --rdp-backend value '{rdpBackend}'. Valid values: auto, xrdp, lamco, none.");
                        return RdpBackend.Auto;
                }
            }
            return noXrdp ? RdpBackend.None : RdpBackend.Auto;
        }

        private static async Task<int> RunAsync(IServiceProvider services, CreateArgs args, CancellationToken ct)
        {
            bool jsonMode = string.Equals(args.Format, "json", StringComparison.OrdinalIgnoreCase);

            // ── Resolve gallery item ─────────────────────────────────────────
            GalleryItem galleryItem;

            if (!string.IsNullOrEmpty(args.ImageUri))
            {
                // Custom image URI — build a synthetic GalleryItem
                galleryItem = new GalleryItem
                {
                    Name = args.Name ?? Path.GetFileNameWithoutExtension(args.ImageUri),
                    Publisher = "Custom",
                    DiskUri = args.ImageUri,
                    Checksum = args.Checksum,
                    ChecksumUri = args.ChecksumUri,
                    ChecksumAlgorithm = "sha256",
                };
            }
            else if (!string.IsNullOrEmpty(args.Image))
            {
                var resolved = await ResolveGalleryItemAsync(services, args.Image, jsonMode, ct);
                if (resolved == null)
                    return ExitCodes.ImageNotFound;
                galleryItem = resolved;
            }
            else
            {
                PrintError(jsonMode, "validation", "Either --image or --image-uri is required.");
                return ExitCodes.InvalidArguments;
            }

            // ── PowerShell warmup (overlaps download/extract) ────────────────
            // The first Hyper-V PowerShell call of the process pays ~600 ms
            // InitialSessionState build + ~2.3 s first-runspace-open (module
            // import + JIT). Deploys don't touch PowerShell until CreateVM —
            // after download and extraction — so warming in the background
            // now hides that cost entirely. Fire-and-forget: Warmup()
            // swallows failures (no Hyper-V role), and each Run* call opens
            // its own runspace, so this cannot affect deploy behavior.
            _ = Task.Run(() =>
            {
                try { services.GetRequiredService<VMCreate.HyperV.IPowerShellExecutor>().Warmup(); }
                catch { /* warmup is best-effort; deploys keep lazy semantics */ }
            });

            // ── Build VmSettings ─────────────────────────────────────────────
            var vmSettings = new VmSettings
            {
                VMName = args.Name ?? galleryItem.Name,
                MemoryInMB = args.MemoryMb,
                CPUCount = args.CpuCount,
                VirtualizationEnabled = !args.NoNestedVirt,
                AutoDetectDiskSize = !args.NoAutoDetectDisk && args.DiskSizeGb == 0,
                NewDriveSizeInGB = args.DiskSizeGb > 0 ? args.DiskSizeGb : 150,
                ReplacePreviousVm = args.Replace,
                EnhancedSessionTransportType = galleryItem.EnhancedSessionTransportType,
                // SecureBoot defaults to false; HyperVVmCreator applies SecureBootTemplate from the gallery item
                SecureBootTemplate = galleryItem.SecureBoot ?? "MicrosoftUEFICertificateAuthority",
            };

            // ── Parse --option key=value pairs ───────────────────────────────
            var distributionOptions = new List<DistributionOptionSelection>();
            foreach (var kv in args.Options)
            {
                var eq = kv.IndexOf('=');
                if (eq < 0)
                {
                    PrintError(jsonMode, "validation", $"Invalid --option format '{kv}'. Expected key=value.");
                    return ExitCodes.InvalidArguments;
                }
                var key = kv[..eq].Trim();
                var val = kv[(eq + 1)..].Trim();
                distributionOptions.Add(new DistributionOptionSelection
                {
                    Name = key,
                    IsEnabled = val.Equals("true", StringComparison.OrdinalIgnoreCase)
                                || val.Equals("1", StringComparison.OrdinalIgnoreCase)
                                || val.Equals("yes", StringComparison.OrdinalIgnoreCase),
                    Order = 0
                });
            }

            // ── Build VmCustomizations ───────────────────────────────────────
            bool hasHtbVpn = !string.IsNullOrEmpty(args.HtbToken) || !string.IsNullOrEmpty(args.OvpnPath);

            RdpBackend rdpBackend = ResolveRdpBackend(args.RdpBackend, args.NoXrdp, jsonMode, out bool rdpBackendValid);
            if (!rdpBackendValid)
                return ExitCodes.InvalidArguments;

            var vmCustomizations = new VmCustomizations
            {
                RdpBackend = rdpBackend,
                EnableIntegrationServices = !args.NoIntegrationServices,
                DnsMode = string.Equals(args.DnsMode, "custom", StringComparison.OrdinalIgnoreCase)
                    ? DnsMode.Custom
                    : DnsMode.Host,
                CustomNameservers = args.Nameservers,
                CustomSshPublicKeyPath = args.SshKeyPath,
                SyncTimezone = !args.NoTimezoneSync,
                InstallOpenVpn = !args.NoOpenVpn,
                ConfigureHtbVpn = hasHtbVpn,
                OvpnFilePath = args.OvpnPath,
                HtbVpnKeys = new List<HtbVpnKey>(),
                DistributionOptions = distributionOptions,
            };

            // Auto-populate FLARE VM optional distribution options if none were explicitly provided
            if (distributionOptions.Count == 0 &&
                string.Equals(galleryItem.Name, "FLARE VM", StringComparison.OrdinalIgnoreCase))
            {
                vmCustomizations.DistributionOptions.Add(new DistributionOptionSelection { Name = "Install FLARE VM", IsEnabled = true, Order = 200 });
            }

            // Auto-populate PwnCloudOS optional option if none were explicitly provided
            if (distributionOptions.Count == 0 &&
                string.Equals(galleryItem.Name, "PwnCloudOS", StringComparison.OrdinalIgnoreCase))
            {
                vmCustomizations.DistributionOptions.Add(new DistributionOptionSelection { Name = "PwnCloudOS Sync", IsEnabled = true, Order = 100 });
            }

            // ── Download HTB VPN keys if token is provided ───────────────────
            if (!string.IsNullOrEmpty(args.HtbToken) && !string.IsNullOrEmpty(args.HtbVpn))
            {
                var htbResult = await DownloadHtbKeysAsync(services, args.HtbToken, args.HtbVpn, jsonMode, ct);
                if (htbResult == null)
                    return ExitCodes.VmCreationFailed;
                vmCustomizations.HtbVpnKeys = htbResult;
            }

            // ── Validate custom DNS mode ─────────────────────────────────────
            if (vmCustomizations.DnsMode == DnsMode.Custom && string.IsNullOrWhiteSpace(args.Nameservers))
            {
                PrintError(jsonMode, "validation", "--nameservers is required when --dns-mode is custom.");
                return ExitCodes.InvalidArguments;
            }

            // ── Set up progress reporter ─────────────────────────────────────
            if (args.Quiet)
            {
                return await CreateVmAsync(services, vmSettings, vmCustomizations, galleryItem,
                    new NullProgressReporter(), jsonMode, ct);
            }

            if (jsonMode)
            {
                var jsonReporter = new JsonProgressReporter();
                return await CreateVmAsync(services, vmSettings, vmCustomizations, galleryItem,
                    jsonReporter, jsonMode, ct);
            }

            // Rich console output
            AnsiConsole.MarkupLine($"[bold]Creating VM:[/] {Markup.Escape(vmSettings.VMName)}");
            AnsiConsole.MarkupLine($"  Image    : {Markup.Escape(galleryItem.Name ?? string.Empty)}");
            AnsiConsole.MarkupLine($"  Memory   : {vmSettings.MemoryInMB} MB");
            AnsiConsole.MarkupLine($"  CPUs     : {vmSettings.CPUCount}");
            AnsiConsole.MarkupLine(string.Empty);

            int exitCode = ExitCodes.Success;
            ConsoleProgressReporter? consoleReporter = null;

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[bold]Phase[/]");
            table.AddColumn("[bold]Status[/]");

            await AnsiConsole.Live(table)
                .StartAsync(async liveCtx =>
                {
                    consoleReporter = new ConsoleProgressReporter(table, liveCtx);
                    exitCode = await CreateVmAsync(services, vmSettings, vmCustomizations, galleryItem,
                        consoleReporter, jsonMode, ct);
                });

            if (exitCode == ExitCodes.Success)
                AnsiConsole.MarkupLine($"\n[green bold]✓ VM '{Markup.Escape(vmSettings.VMName)}' created successfully.[/]");
            else
                AnsiConsole.MarkupLine($"\n[red bold]✗ VM creation failed.[/]");

            return exitCode;
        }

        private static async Task<int> CreateVmAsync(
            IServiceProvider services,
            VmSettings vmSettings,
            VmCustomizations vmCustomizations,
            GalleryItem galleryItem,
            IProgress<CreateVMProgressInfo> reporter,
            bool jsonMode,
            CancellationToken ct)
        {
            try
            {
                var creator = services.GetRequiredService<CreateVM>();
                await creator.StartCreateVMAsync(vmSettings, vmCustomizations, galleryItem, ct, reporter);
                return ExitCodes.Success;
            }
            catch (OperationCanceledException)
            {
                PrintError(jsonMode, "Cancelled", "VM creation was cancelled.");
                return ExitCodes.Cancelled;
            }
            catch (Exception ex) when (IsHyperVException(ex))
            {
                PrintError(jsonMode, "HyperV", ex.Message);
                return ExitCodes.HyperVError;
            }
            catch (Exception ex)
            {
                PrintError(jsonMode, "Error", ex.Message);
                return ExitCodes.VmCreationFailed;
            }
        }

        private static async Task<GalleryItem?> ResolveGalleryItemAsync(
            IServiceProvider services,
            string search,
            bool jsonMode,
            CancellationToken ct)
        {
            var galleryService = services.GetRequiredService<IGalleryService>();
            var items = galleryService.LoadFromCache();
            if (items.Count == 0)
            {
                var all = new List<GalleryItem>();
                var seen = new HashSet<(string, string)>();
                await galleryService.LoadFromSourcesStreamingAsync(seen, batch => all.AddRange(batch), ct);
                galleryService.SaveCache(all);
                items = all;
            }

            // Exact name match first, then partial
            var match = items.FirstOrDefault(i =>
                string.Equals(i.Name, search, StringComparison.OrdinalIgnoreCase));

            if (match == null)
                match = items.FirstOrDefault(i =>
                    i.Name != null && i.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match == null)
                PrintError(jsonMode, "ImageNotFound", $"No gallery image found matching '{search}'. Run 'vmcreate list' to see available images.");

            return match;
        }

        private static async Task<List<HtbVpnKey>?> DownloadHtbKeysAsync(
            IServiceProvider services,
            string token,
            string types,
            bool jsonMode,
            CancellationToken ct)
        {
            try
            {
                var htbClient = services.GetRequiredService<IHtbApiClient>();
                var requestedTypes = new HashSet<string>(
                    types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);

                var results = await htbClient.DownloadAllKeysAsync(token, ct);

                var keys = results
                    .Where(r => r.Success && r.Key != null &&
                                (requestedTypes.Count == 0 ||
                                 requestedTypes.Contains(r.EndpointName)))
                    .Select(r => r.Key!)
                    .ToList();

                return keys;
            }
            catch (Exception ex)
            {
                PrintError(jsonMode, "HtbApi", $"Failed to download HTB VPN keys: {ex.Message}");
                return null;
            }
        }

        private static bool IsHyperVException(Exception ex) =>
            ex.Message.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("virtual machine management", StringComparison.OrdinalIgnoreCase)
            || ex.GetType().FullName?.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) == true;

        private static void PrintError(bool jsonMode, string phase, string message)
        {
            if (jsonMode)
            {
                Console.Error.WriteLine(
                    System.Text.Json.JsonSerializer.Serialize(new { phase, error = message },
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
            }
        }

        private sealed class NullProgressReporter : IProgress<CreateVMProgressInfo>
        {
            public void Report(CreateVMProgressInfo value) { }
        }
    }

    internal sealed class CreateArgs
    {
        // All string members are optional CLI options — null means "not provided".
        public string? Image { get; set; }
        public string? ImageUri { get; set; }
        public string? Checksum { get; set; }
        public string? ChecksumUri { get; set; }
        public string? Name { get; set; }
        public int MemoryMb { get; set; }
        public int CpuCount { get; set; }
        public int DiskSizeGb { get; set; }
        public bool NoAutoDetectDisk { get; set; }
        public bool NoNestedVirt { get; set; }
        public bool Replace { get; set; }
        public bool NoXrdp { get; set; }
        public string? RdpBackend { get; set; }
        public bool NoIntegrationServices { get; set; }
        public string? DnsMode { get; set; }
        public string? Nameservers { get; set; }
        public string? SshKeyPath { get; set; }
        public bool NoTimezoneSync { get; set; }
        public string? HtbToken { get; set; }
        public string? HtbVpn { get; set; }
        public string? OvpnPath { get; set; }
        public bool NoOpenVpn { get; set; }
        public string[] Options { get; set; } = Array.Empty<string>();
        public string? Format { get; set; }
        public bool Quiet { get; set; }
        public bool NonInteractive { get; set; }
    }
}
