using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VMCreate.Cidata;

/// <summary>
/// Options for an Omarchy cidata (NoCloud-style) seed ISO. Mirrors the files the
/// upstream omarchy configurator (quattro / v4.0.4) writes for an unattended
/// full-disk install: <c>user_configuration.json</c>, <c>user_credentials.json</c>,
/// <c>user_encrypt_installation.txt</c> and the optional account/SSH files.
/// </summary>
public class CidataSeedOptions
{
    /// <summary>Login user created by the installer. Required (deferred provisioning is not used).</summary>
    public string Username { get; set; } = null!;

    /// <summary>Plaintext password for the user and root; hashed with SHA-512 crypt on the seed.</summary>
    public string Password { get; set; } = null!;

    /// <summary>Guest hostname. Usually the VM name.</summary>
    public string Hostname { get; set; } = null!;

    /// <summary>IANA timezone, e.g. "Europe/Copenhagen". Defaults to UTC.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>Console keyboard layout. Defaults to "us".</summary>
    public string KeyboardLayout { get; set; } = "us";

    /// <summary>Optional full name written to user_full_name.txt.</summary>
    public string? FullName { get; set; }

    /// <summary>Optional email written to user_email_address.txt.</summary>
    public string? EmailAddress { get; set; }

    /// <summary>
    /// Optional contents of authorized_keys (one public key per line). When present the
    /// installer writes the user's ~/.ssh/authorized_keys, enables sshd and opens the
    /// firewall for SSH — no post-boot SSH setup is required.
    /// </summary>
    public string? AuthorizedKeys { get; set; }

    /// <summary>Exact size in bytes of the target virtual disk. Drives the partition layout.</summary>
    public long DiskSizeBytes { get; set; }

    /// <summary>Guest device path of the target disk. A Gen2 VM with a single VHDX exposes /dev/sda.</summary>
    public string DiskDevice { get; set; } = "/dev/sda";

    /// <summary>Kernel package; "linux-omarchy" for standard hardware, "linux-t2" for T2 Macs.</summary>
    public string Kernel { get; set; } = "linux-omarchy";

    /// <summary>Omarchy settings package substituted into the package list (upstream default).</summary>
    public string OmarchySettingsPackage { get; set; } = "omarchy-settings";

    /// <summary>Omarchy runtime package substituted into the package list (upstream default).</summary>
    public string OmarchyRuntimePackage { get; set; } = "omarchy";

    /// <summary>
    /// Adds NoCloud <c>meta-data</c>/<c>user-data</c> that make the live installer
    /// listen on vsock:3389 until it reboots. Hyper-V only marks Enhanced Session
    /// available after it sees a listener there, and it stops looking once the
    /// guest resets without one — which the installer's reboot does, long
    /// before any RDP server is installed. Once marked available it stays so.
    /// </summary>
    public bool HoldEnhancedSessionPort { get; set; } = true;

    /// <summary>
    /// Optional static addressing. When <see cref="StaticNetworkConfig.StaticIp"/>
    /// is set, the seed also carries a NoCloud <c>network-config</c> (v2) that
    /// cloud-init applies to the live session's <c>eth0</c>; the installer's
    /// <c>network_config: {"type": "iso"}</c> then copies that configuration
    /// into the installed system, so the static address survives the install
    /// without any post-boot patching.
    /// </summary>
    public StaticNetworkConfig? StaticNetwork { get; set; }
}

/// <summary>
/// Partition layout for the Omarchy full-disk template, in bytes. Reproduces the
/// upstream configurator math exactly: the disk size is floored to a whole MiB,
/// the ESP starts at 1 MiB and spans 2 GiB, and 1 MiB is reserved for the GPT backup.
/// </summary>
public readonly record struct CidataPartitionLayout(long BootStart, long BootSize, long MainStart, long MainSize);

/// <summary>
/// Builds a <c>cidata</c> seed ISO for unattended Omarchy installs. The ISO volume is
/// labeled <c>cidata</c>; the installer's seed loader discovers it by that label and
/// reads the configuration/credentials pair without any interactive prompt.
/// </summary>
public static class CidataSeedBuilder
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    /// <summary>Salt alphabet for SHA-512 crypt (crypt(3) b64).</summary>
    private const string SaltAlphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Smallest disk considered installable: Omarchy downloads several GB of
    /// packages on top of the base system, comfortably exceeding 20 GB.
    /// </summary>
    public const long MinimumDiskSizeBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// Computes the partition layout the upstream configurator derives from the disk
    /// size. The virtual disk is sized in whole GiB by the host, so the floor to MiB is
    /// exact, but arbitrary byte sizes round the same way upstream does.
    /// </summary>
    public static CidataPartitionLayout ComputePartitionLayout(long diskSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diskSizeBytes);

        long diskSizeInMiB = diskSizeBytes / MiB * MiB; // floor to whole MiB
        long bootStart = MiB;                            // GPT primary + first MiB
        long bootSize = 2 * GiB;                         // ESP
        long mainStart = bootSize + bootStart;
        long mainSize = diskSizeInMiB - mainStart - MiB; // 1 MiB GPT backup reserve

        if (diskSizeBytes < MinimumDiskSizeBytes)
            throw new InvalidOperationException(
                $"Disk of {diskSizeBytes} bytes is below the Omarchy minimum of {MinimumDiskSizeBytes} bytes.");

        if (mainSize <= 0)
            throw new InvalidOperationException(
                $"Disk of {diskSizeBytes} bytes is too small for the Omarchy layout (needs > {mainStart + MiB} bytes).");

        return new CidataPartitionLayout(bootStart, bootSize, mainStart, mainSize);
    }

    /// <summary>
    /// Renders <c>user_configuration.json</c> — a byte-faithful transcription of the
    /// upstream full-disk template with our substitutions applied (no encryption,
    /// no deferred provisioning).
    /// </summary>
    public static string BuildUserConfigurationJson(CidataSeedOptions options)
    {
        Validate(options);
        var layout = ComputePartitionLayout(options.DiskSizeBytes);

        string json = $$"""
        {
            "app_config": null,
            "archinstall-language": "English",
            "auth_config": {},
            "audio_config": { "audio": "pipewire" },
            "bootloader_config": { "bootloader": "Limine", "uki": false, "removable": false },
            "custom_commands": [],
            "omarchy_install": {
                "mode": "full_disk",
                "defer_provisioning": false,
                "target_mount": "/mnt",
                "boot": {
                    "esp_mount": "/boot",
                    "esp_path": "/EFI/limine",
                    "efi_binary": "limine_x64.efi",
                    "enable_fallback": true
                },
                "storage": {
                    "kernel": "{{JsonEscape(options.Kernel)}}"
                }
            },
            "disk_config": {
                "config_type": "default_layout",
                "device_modifications": [
                    {
                        "device": "{{JsonEscape(options.DiskDevice)}}",
                        "partitions": [
                            {
                                "btrfs": [],
                                "dev_path": null,
                                "flags": [ "boot", "esp" ],
                                "fs_type": "fat32",
                                "mount_options": [],
                                "mountpoint": "/boot",
                                "obj_id": "ea21d3f2-82bb-49cc-ab5d-6f81ae94e18d",
                                "size": {
                                    "sector_size": { "unit": "B", "value": 512 },
                                    "unit": "B",
                                    "value": {{layout.BootSize}}
                                },
                                "start": {
                                    "sector_size": { "unit": "B", "value": 512 },
                                    "unit": "B",
                                    "value": {{layout.BootStart}}
                                },
                                "status": "create",
                                "type": "primary"
                            },
                            {
                                "btrfs": [
                                    { "mountpoint": "/", "name": "@" },
                                    { "mountpoint": "/home", "name": "@home" },
                                    { "mountpoint": "/var/log", "name": "@log" },
                                    { "mountpoint": "/var/cache/pacman/pkg", "name": "@pkg" }
                                ],
                                "dev_path": null,
                                "flags": [],
                                "fs_type": "btrfs",
                                "mount_options": [ "compress=zstd" ],
                                "mountpoint": null,
                                "obj_id": "8c2c2b92-1070-455d-b76a-56263bab24aa",
                                "size": {
                                    "sector_size": { "unit": "B", "value": 512 },
                                    "unit": "B",
                                    "value": {{layout.MainSize}}
                                },
                                "start": {
                                    "sector_size": { "unit": "B", "value": 512 },
                                    "unit": "B",
                                    "value": {{layout.MainStart}}
                                },
                                "status": "create",
                                "type": "primary"
                            }
                        ],
                        "wipe": true
                    }
                ]
            },
            "hostname": "{{JsonEscape(options.Hostname)}}",
            "kernels": [ "{{JsonEscape(options.Kernel)}}" ],
            "network_config": { "type": "iso" },
            "ntp": true,
            "parallel_downloads": 8,
            "script": null,
            "services": [],
            "swap": true,
            "timezone": "{{JsonEscape(options.Timezone)}}",
            "locale_config": {
                "kb_layout": "{{JsonEscape(options.KeyboardLayout)}}",
                "sys_enc": "UTF-8",
                "sys_lang": "en_US.UTF-8"
            },
            "mirror_config": {
                "custom_repositories": [],
                "custom_servers": [
                    {"url": "https://mirror.omarchy.org/$repo/os/$arch"},
                    {"url": "https://mirror.rackspace.com/archlinux/$repo/os/$arch"},
                    {"url": "https://geo.mirror.pkgbuild.com/$repo/os/$arch"}
                ],
                "mirror_regions": {},
                "optional_repositories": []
            },
            "packages": [
                "base-devel",
                "git",
                "omarchy-keyring",
                "{{JsonEscape(options.OmarchySettingsPackage)}}",
                "{{JsonEscape(options.OmarchyRuntimePackage)}}"
            ],
            "profile_config": {
                "gfx_driver": null,
                "greeter": null,
                "profile": {}
            },
            "version": "3.0.9"
        }
        """;

        return json.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    }

    /// <summary>
    /// Renders <c>user_credentials.json</c>: the same SHA-512 crypt hash for root and
    /// the sudo-enabled login user, exactly as the upstream configurator writes it
    /// for non-encrypted installs.
    /// </summary>
    public static string BuildUserCredentialsJson(CidataSeedOptions options, string? passwordHash = null)
    {
        Validate(options);
        passwordHash ??= HashPassword(options.Password);
        string hash = JsonEscape(passwordHash);
        string username = JsonEscape(options.Username);

        return $$"""
        {
            "root_enc_password": "{{hash}}",
            "users": [
                {
                    "enc_password": "{{hash}}",
                    "groups": [],
                    "sudo": true,
                    "username": "{{username}}"
                }
            ]
        }
        """.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    }

    /// <summary>
    /// Renders the NoCloud <c>meta-data</c>. The live ISO's cloud-init needs it
    /// (with <c>user-data</c>) to accept the cidata drive as its datasource.
    /// </summary>
    public static string BuildNoCloudMetaData(CidataSeedOptions options)
    {
        Validate(options);
        return $"instance-id: {options.Hostname}\n";
    }

    /// <summary>
    /// Renders the NoCloud <c>user-data</c> for the live installer: a transient
    /// unit that accepts and closes connections on vsock:3389 (vmms only probes
    /// for a listener), and the stock cloud.cfg side effects switched off — no
    /// default user, hostname or host-key changes in the live session. Nothing
    /// here reaches the installed system.
    /// </summary>
    public static string BuildNoCloudUserData()
    {
        return """
            #cloud-config
            # VMCreate: live installer only. Hold vsock:3389 until the installer
            # reboots so Hyper-V marks Enhanced Session available (see
            # CidataSeedOptions.HoldEnhancedSessionPort).
            users: []
            preserve_hostname: true
            manage_etc_hosts: false
            ssh_deletekeys: false
            ssh_genkeytypes: []
            bootcmd:
              - [sh, -c, "modprobe hv_sock || true"]
              - [systemd-run, --unit=vmcreate-esm-hold, --collect, python3, -c, "import socket as S\ns=S.socket(S.AF_VSOCK,S.SOCK_STREAM)\ns.bind((S.VMADDR_CID_ANY,3389))\ns.listen(8)\nwhile True: s.accept()[0].close()"]

            """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Builds the cidata seed ISO at <paramref name="outputPath"/>. Stages the seed
    /// files in a temp directory, burns them with the in-box IMAPI2 component
    /// (volume label <c>cidata</c>) and always removes the staging directory.
    /// </summary>
    /// <returns>The path to the created ISO (i.e. <paramref name="outputPath"/>).</returns>
    public static string BuildCidataIso(string outputPath, CidataSeedOptions options, ILogger logger)
    {
        string stagingDir = Path.Combine(Path.GetTempPath(), "vmcreate-cidata-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stagingDir);

        try
        {
            File.WriteAllText(
                Path.Combine(stagingDir, "user_configuration.json"),
                BuildUserConfigurationJson(options));
            File.WriteAllText(
                Path.Combine(stagingDir, "user_credentials.json"),
                BuildUserCredentialsJson(options));

            // echo semantics: value + trailing newline.
            File.WriteAllText(Path.Combine(stagingDir, "user_encrypt_installation.txt"), "false\n");

            if (!string.IsNullOrWhiteSpace(options.FullName))
                File.WriteAllText(Path.Combine(stagingDir, "user_full_name.txt"), options.FullName + "\n");
            if (!string.IsNullOrWhiteSpace(options.EmailAddress))
                File.WriteAllText(Path.Combine(stagingDir, "user_email_address.txt"), options.EmailAddress + "\n");

            if (!string.IsNullOrWhiteSpace(options.AuthorizedKeys))
            {
                // One key per line with exactly one trailing newline; the installer
                // hard-fails on malformed lines, so content is passed through verbatim.
                string keys = options.AuthorizedKeys.TrimEnd('\r', '\n') + "\n";
                File.WriteAllText(Path.Combine(stagingDir, "authorized_keys"), keys);
            }

            if (options.HoldEnhancedSessionPort)
            {
                File.WriteAllText(Path.Combine(stagingDir, "meta-data"), BuildNoCloudMetaData(options));
                File.WriteAllText(Path.Combine(stagingDir, "user-data"), BuildNoCloudUserData());
            }

            if (NoCloudNetworkConfig.IsRequested(options.StaticNetwork))
            {
                // Static addressing for the live session's eth0; the
                // installer copies it into the installed system (see
                // CidataSeedOptions.StaticNetwork). Requires meta-data +
                // user-data to be present: cloud-init only treats the drive
                // as a NoCloud datasource with that pair.
                if (!options.HoldEnhancedSessionPort)
                    throw new InvalidOperationException(
                        "Static network configuration requires the NoCloud meta-data/user-data pair on the seed.");

                File.WriteAllText(
                    Path.Combine(stagingDir, "network-config"),
                    NoCloudNetworkConfig.Render(options.StaticNetwork!));
            }

            logger.LogInformation("Staged Omarchy cidata seed files in {Path}", stagingDir);

            // ISO9660 + Joliet ONLY. With a UDF descriptor blkid reports
            // TYPE=udf and cloud-init's NoCloud datasource (which scans only
            // vfat/iso9660) never reads user-data, so the ESM hold never runs.
            // omarchy-cidata-load matches by label and would not notice.
            Imapi2Interop.CreateIsoFromDirectory(
                stagingDir, outputPath,
                volumeName: "cidata",
                FsiFileSystems.ISO9660 | FsiFileSystems.Joliet);

            logger.LogInformation("Created Omarchy cidata seed ISO at {Path}", outputPath);
            return outputPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, true);
            }
            catch { /* best effort */ }
        }
    }

    private static void Validate(CidataSeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Username))
            throw new ArgumentException("Username is required (deferred provisioning is not supported).", nameof(options));
        if (options.Password is null)
            throw new ArgumentException("Password is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Hostname))
            throw new ArgumentException("Hostname is required.", nameof(options));
    }

    /// <summary>Hashes the password with a fresh random 16-character salt ("openssl passwd -6").</summary>
    private static string HashPassword(string password)
    {
        byte[] saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = new StringBuilder(16);
        foreach (byte b in saltBytes)
            salt.Append(SaltAlphabet[b % SaltAlphabet.Length]);

        return CryptSha512.Hash(password, salt.ToString());
    }

    /// <summary>Minimal JSON string escaping, matching jq -Rsa output for the values we embed.</summary>
    private static string JsonEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
