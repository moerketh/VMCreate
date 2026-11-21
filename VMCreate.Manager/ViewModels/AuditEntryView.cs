using VMCreate.Metadata;

namespace VMCreate.Manager.ViewModels
{
    /// <summary>An audit entry formatted for the History list and the activity log window.</summary>
    public sealed class AuditEntryView
    {
        private static readonly Dictionary<string, string> Labels = new()
        {
            [AuditActions.VmCreate] = "Created",
            [AuditActions.VmStart] = "Started",
            [AuditActions.VmShutdown] = "Shut down",
            [AuditActions.VmTurnOff] = "Turned off",
            [AuditActions.VmSave] = "Saved state",
            [AuditActions.VmPause] = "Paused",
            [AuditActions.VmResume] = "Resumed",
            [AuditActions.VmRename] = "Renamed",
            [AuditActions.VmDelete] = "Deleted",
            [AuditActions.VmMove] = "Moved",
            [AuditActions.VmProtect] = "Protected",
            [AuditActions.VmUnprotect] = "Protection removed",
            [AuditActions.VmExpiry] = "Expiry set",
            [AuditActions.NetworkIsolate] = "Network isolated",
            [AuditActions.NetworkRestore] = "Network restored",
            [AuditActions.NetworkConnect] = "Network changed",
            [AuditActions.FolderCreate] = "Folder created",
            [AuditActions.FolderRename] = "Folder renamed",
            [AuditActions.FolderMove] = "Folder moved",
            [AuditActions.FolderDelete] = "Folder deleted",
            [AuditActions.FolderExpiry] = "Folder expiry set",
            [AuditActions.CheckpointRevert] = "Reverted to checkpoint",
            [AuditActions.CloneCreate] = "Linked clone created",
            [AuditActions.TemplateMark] = "Made a template",
            [AuditActions.TemplateUnmark] = "No longer a template",
            [AuditActions.Archive] = "Archived",
            [AuditActions.FileDelete] = "Unused file deleted",
            [AuditActions.MetadataExternalEdit] = "Notes edited outside",
            [AuditActions.MetadataConflict] = "Notes conflict",
            [AuditActions.MetadataRemove] = "Old metadata removed",
        };

        public AuditEntryView(AuditEntry entry) => Entry = entry;

        public AuditEntry Entry { get; }
        public string When => Entry.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        public string What => Labels.TryGetValue(Entry.Action, out var label) ? label : Entry.Action;
        public string? Detail => Entry.Detail;
        public string? Error => Entry.Error;
        public bool Failed => !Entry.Succeeded;
        public string Target => Entry.VmName
            ?? (Entry.Action.StartsWith("folder.", StringComparison.Ordinal) ? "(folder)"
                : Entry.Action.StartsWith("file.", StringComparison.Ordinal) ? "(storage)" : "");
        public string Tool => Entry.Tool;
        public string User => Entry.User;

        /// <summary>One line for the History list: "Moved · Labs → Archive".</summary>
        public string Summary => Detail == null ? What : $"{What} · {Detail}";

        /// <summary>Text matched by the activity-log search box.</summary>
        public bool Matches(string text) =>
            new[] { What, Detail, Error, Entry.VmName, Tool, User, Entry.Action }
                .Any(s => s?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }
}
