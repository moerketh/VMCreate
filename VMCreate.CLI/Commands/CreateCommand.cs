using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
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
            var imageOpt = new Option<string>(
                "--image",
                "Gallery image name to use (partial match). List names with 'vmcreate list'.");

            var imageUriOpt = new Option<string>(
                "--image-uri",
                "URL or local path to a disk image (VMDK, QCOW2, VHDX, VHD, OVA, or archive).");

            var checksumOpt = new Option<string>(
                "--checksum",
                "Expected SHA-256 hash of the file at --image-uri.");

            var checksumUriOpt = new Option<string>(
                "--checksum-uri",
                "URL to a checksum file for the file at --image-uri.");

            var nameOpt = new Option<string>(
                "--name",
                "VM name. Defaults to the gallery image name.");

            // ── VM hardware ────────────────────────────────────────────────────
            var memoryOpt = new Option<int>(
                "--memory",
                getDefaultValue: () => 4096,
                description: "Memory in MB (default: 4096).");

            var cpuOpt = new Option<int>(
                "--cpu",
                getDefaultValue: () => 2,
                description: "Number of virtual CPUs (default: 2).");

            var diskSizeOpt = new Option<int>(
                "--disk-size",
                getDefaultValue: () => 0,
                description: "New disk size in GB. 0 = auto-detect from source image.");

            var noAutoDetectDiskOpt = new Option<bool>(
                "--no-auto-detect-disk",
                getDefaultValue: () => false,
                description: "Disable auto-detect disk size. Requires --disk-size.");

            var noNestedVirtOpt = new Option<bool>(
                "--no-nested-virt",
                getDefaultValue: () => false,
                description: "Disable nested virtualization extensions.");

            var replaceOpt = new Option<bool>(
                "--replace",
                getDefaultValue: () => false,
                description: "Stop and remove an existing VM with the same name before creating.");

            // ── Customization ──────────────────────────────────────────────────
            var noXrdpOpt = new Option<bool>(
                "--no-xrdp",
                getDefaultValue: () => false,
                description: "Disable xRDP / Enhanced Session setup.");

            var noIntegrationSvcOpt = new Option<bool>(
                "--no-integration-services",
                getDefaultValue: () => false,
                description: "Disable Hyper-V integration services (maximum isolation mode).");

            var dnsModeOpt = new Option<string>(
                "--dns-mode",
                getDefaultValue: () => "host",
                description: "DNS mode: host (auto-detect) or custom.");

            var nameserversOpt = new Option<string>(
                "--nameservers",
                description: "Comma-separated DNS nameservers. Requires --dns-mode custom.");

            var sshKeyOpt = new Option<string>(
                "--ssh-key",
                description: "Path to a custom SSH public key (.pub). Defaults to auto-generated key.");

            var noTimezoneOpt = new Option<bool>(
                "--no-timezone-sync",
                getDefaultValue: () => false,
                description: "Disable guest timezone synchronization.");

            // ── HTB VPN ────────────────────────────────────────────────────────
            var htbTokenOpt = new Option<string>(
                "--htb-token",
                description: "Hack The Box API token for downloading VPN configuration.");

            var htbVpnOpt = new Option<string>(
                "--htb-vpn",
                description: "Comma-separated HTB VPN endpoint types: labs, sp, academy.");

            var ovpnOpt = new Option<string>(
                "--ovpn",
                description: "Path to a manually downloaded .ovpn file.");

            var noOpenVpnOpt = new Option<bool>(
                "--no-openvpn",
                getDefaultValue: () => false,
                description: "Skip installing OpenVPN and the NetworkManager OpenVPN plugin.");

            // ── Distribution-specific ──────────────────────────────────────────
            var optionOpt = new Option<string[]>(
                "--option",
                description: "Distribution-specific option in key=value form (repeatable). " +
                             "E.g. --option pwncloudos-sync=true")
            {
                AllowMultipleArgumentsPerToken = false,
                Arity = ArgumentArity.ZeroOrMore,
            };
            optionOpt.AllowMultipleArgumentsPerToken = false;

            // ── Output / behaviour ─────────────────────────────────────────────
            var formatOpt = new Option<string>(
                "--format",
                getDefaultValue: () => "text",
                description: "Output format: text or json.");

            var quietOpt = new Option<bool>(
                "--quiet",
                getDefaultValue: () => false,
                description: "Suppress progress output. Exit code still reflects success/failure.");

            var nonInteractiveOpt = new Option<bool>(
                "--non-interactive",
                getDefaultValue: () => false,
                description: "Fail with an error instead of prompting for missing information.");

            // Register all options
            cmd.AddOption(imageOpt);
            cmd.AddOption(imageUriOpt);
            cmd.AddOption(checksumOpt);
            cmd.AddOption(checksumUriOpt);
            cmd.AddOption(nameOpt);
            cmd.AddOption(memoryOpt);
            cmd.AddOption(cpuOpt);
            cmd.AddOption(diskSizeOpt);
            cmd.AddOption(noAutoDetectDiskOpt);
            cmd.AddOption(noNestedVirtOpt);
            cmd.AddOption(replaceOpt);
            cmd.AddOption(noXrdpOpt);
            cmd.AddOption(noIntegrationSvcOpt);
            cmd.AddOption(dnsModeOpt);
            cmd.AddOption(nameserversOpt);
            cmd.AddOption(sshKeyOpt);
            cmd.AddOption(noTimezoneOpt);
            cmd.AddOption(htbTokenOpt);
            cmd.AddOption(htbVpnOpt);
            cmd.AddOption(ovpnOpt);
            cmd.AddOption(noOpenVpnOpt);
            cmd.AddOption(optionOpt);
            cmd.AddOption(formatOpt);
            cmd.AddOption(quietOpt);
            cmd.AddOption(nonInteractiveOpt);

            cmd.SetHandler(async (InvocationContext ctx) =>
            {
                var r = ctx.ParseResult;
                var ct = ctx.GetCancellationToken();

                var args = new CreateArgs
                {
                    Image = r.GetValueForOption(imageOpt),
                    ImageUri = r.GetValueForOption(imageUriOpt),
                    Checksum = r.GetValueForOption(checksumOpt),
                    ChecksumUri = r.GetValueForOption(checksumUriOpt),
                    Name = r.GetValueForOption(nameOpt),
                    MemoryMb = r.GetValueForOption(memoryOpt),
                    CpuCount = r.GetValueForOption(cpuOpt),
                    DiskSizeGb = r.GetValueForOption(diskSizeOpt),
                    NoAutoDetectDisk = r.GetValueForOption(noAutoDetectDiskOpt),
                    NoNestedVirt = r.GetValueForOption(noNestedVirtOpt),
                    Replace = r.GetValueForOption(replaceOpt),
                    NoXrdp = r.GetValueForOption(noXrdpOpt),
                    NoIntegrationServices = r.GetValueForOption(noIntegrationSvcOpt),
                    DnsMode = r.GetValueForOption(dnsModeOpt),
                    Nameservers = r.GetValueForOption(nameserversOpt),
                    SshKeyPath = r.GetValueForOption(sshKeyOpt),
                    NoTimezoneSync = r.GetValueForOption(noTimezoneOpt),
                    HtbToken = r.GetValueForOption(htbTokenOpt),
                    HtbVpn = r.GetValueForOption(htbVpnOpt),
                    OvpnPath = r.GetValueForOption(ovpnOpt),
                    NoOpenVpn = r.GetValueForOption(noOpenVpnOpt),
                    Options = r.GetValueForOption(optionOpt) ?? Array.Empty<string>(),
                    Format = r.GetValueForOption(formatOpt),
                    Quiet = r.GetValueForOption(quietOpt),
                    NonInteractive = r.GetValueForOption(nonInteractiveOpt),
                };

                ctx.ExitCode = await RunAsync(services, args, ct);
            });

            return cmd;
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

            var vmCustomizations = new VmCustomizations
            {
                ConfigureXrdp = !args.NoXrdp,
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
            ConsoleProgressReporter consoleReporter = null;

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

        private static async Task<GalleryItem> ResolveGalleryItemAsync(
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

        private static async Task<List<HtbVpnKey>> DownloadHtbKeysAsync(
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
                    .Select(r => r.Key)
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
        public string Image { get; set; }
        public string ImageUri { get; set; }
        public string Checksum { get; set; }
        public string ChecksumUri { get; set; }
        public string Name { get; set; }
        public int MemoryMb { get; set; }
        public int CpuCount { get; set; }
        public int DiskSizeGb { get; set; }
        public bool NoAutoDetectDisk { get; set; }
        public bool NoNestedVirt { get; set; }
        public bool Replace { get; set; }
        public bool NoXrdp { get; set; }
        public bool NoIntegrationServices { get; set; }
        public string DnsMode { get; set; }
        public string Nameservers { get; set; }
        public string SshKeyPath { get; set; }
        public bool NoTimezoneSync { get; set; }
        public string HtbToken { get; set; }
        public string HtbVpn { get; set; }
        public string OvpnPath { get; set; }
        public bool NoOpenVpn { get; set; }
        public string[] Options { get; set; }
        public string Format { get; set; }
        public bool Quiet { get; set; }
        public bool NonInteractive { get; set; }
    }
}
