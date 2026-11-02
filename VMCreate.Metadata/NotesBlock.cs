using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VMCreate.Metadata
{
    /// <summary>
    /// The portable copy of a VM's metadata kept inside its Hyper-V Notes
    /// field, so folder/tags/description travel with export/import and
    /// survive losing the local metadata file. Credentials are never included.
    /// Folders are stored as a path of names: folder IDs are local to one
    /// metadata file and mean nothing on another host.
    /// </summary>
    public sealed class NotesPayload
    {
        public int V { get; set; } = 1;
        public string? Folder { get; set; }
        public List<string>? Tags { get; set; }
        public string? Purpose { get; set; }
        public string? Description { get; set; }
        public bool? Protected { get; set; }
        public bool? Template { get; set; }
        public Guid? TemplateId { get; set; }
        public DateOnly? ExpiresOn { get; set; }
        public VmProvenance? Provenance { get; set; }
    }

    /// <summary>
    /// Reads and writes the <c>[vmcreate]{json}[/vmcreate]</c> block in VM
    /// Notes while leaving the user's own free text untouched.
    /// </summary>
    public static class NotesBlock
    {
        public const string StartMarker = "[vmcreate]";
        public const string EndMarker = "[/vmcreate]";

        private static readonly Regex BlockPattern = new(
            @"\r?\n?\[vmcreate\](?<json>.*?)\[/vmcreate\]\r?\n?",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        /// <summary>
        /// Splits Notes into the user's free text and the parsed payload.
        /// A malformed block is kept in the free text (never silently dropped)
        /// and the payload is null.
        /// </summary>
        public static (string FreeText, NotesPayload? Payload) Parse(string? notes)
        {
            if (string.IsNullOrEmpty(notes))
                return ("", null);

            NotesPayload? payload = null;
            string freeText = BlockPattern.Replace(notes, match =>
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<NotesPayload>(match.Groups["json"].Value, JsonOptions);
                    if (parsed == null)
                        return match.Value;
                    payload = parsed; // last well-formed block wins
                    return "";
                }
                catch (JsonException)
                {
                    return match.Value;
                }
            });
            return (freeText, payload);
        }

        /// <summary>
        /// Rebuilds Notes from free text plus payload. The block goes at the
        /// end so the old MMC's Notes pane still shows the user's text first.
        /// A null payload removes the block.
        /// </summary>
        public static string Compose(string? freeText, NotesPayload? payload)
        {
            string text = (freeText ?? "").TrimEnd();
            if (payload == null)
                return text;

            string block = StartMarker + JsonSerializer.Serialize(payload, JsonOptions) + EndMarker;
            return text.Length == 0 ? block : text + "\r\n" + block;
        }

        /// <summary>The block's JSON as written; null when there is no block.</summary>
        public static string? Serialize(NotesPayload? payload)
            => payload == null ? null : JsonSerializer.Serialize(payload, JsonOptions);

        /// <summary>
        /// Short hash of the block's content ("none" without a block). Equal
        /// fingerprints mean equal blocks, whatever free text surrounds them.
        /// </summary>
        public static string Fingerprint(NotesPayload? payload)
            => Serialize(payload) is string json
                ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16]
                : "none";

        /// <summary>Replaces (or adds) the block in existing Notes, preserving the free text.</summary>
        public static string Update(string? existingNotes, NotesPayload? payload)
            => Compose(Parse(existingNotes).FreeText, payload);
    }
}
