namespace VMCreate.Metadata
{
    public enum NotesSyncAction
    {
        /// <summary>Store and Notes agree.</summary>
        InSync,

        /// <summary>No entry yet: created from the Notes block (imported VM, lost metadata file).</summary>
        Adopted,

        /// <summary>The store changed and Notes has not caught up: write the store's block to Notes.</summary>
        PushToNotes,

        /// <summary>Notes was edited outside VMCreate tools: the store took the edit.</summary>
        TookFromNotes,

        /// <summary>The block was removed outside VMCreate tools: it is written back from the store.</summary>
        RestoreBlock,

        /// <summary>Both sides changed since the last sync: the store wins, the overwritten block is reported.</summary>
        Conflict,
    }

    /// <param name="ChangedFields">For <see cref="NotesSyncAction.TookFromNotes"/>: what the outside edit changed.</param>
    /// <param name="NeedsPush">Write the store's block to Notes afterwards (then record its fingerprint).</param>
    /// <param name="OverwrittenBlock">For a conflict: the Notes block that is being replaced, so it is not lost.</param>
    public sealed record NotesReconcileResult(
        NotesSyncAction Action,
        IReadOnlyList<string> ChangedFields,
        bool NeedsPush,
        string? OverwrittenBlock = null);

    /// <summary>
    /// Decides which side of a VM's metadata is newer, the local store or the
    /// [vmcreate] block in its Notes, from the fingerprint of the block as a
    /// VMCreate tool last wrote or read it:
    /// <list type="bullet">
    /// <item>Notes still as last written, store different → the store is newer: push.</item>
    /// <item>Store still as last written, Notes different → edited outside: take it.</item>
    /// <item>Both different → conflict: the store wins, the old block is reported.</item>
    /// </list>
    /// Applies the store side to <paramref name="doc"/>; the caller writes Notes.
    /// </summary>
    public static class NotesReconciler
    {
        public static NotesReconcileResult Reconcile(MetadataDocument doc, Guid vmId, NotesPayload? notes)
        {
            var entry = doc.FindVm(vmId);
            string notesPrint = NotesBlock.Fingerprint(notes);
            if (entry == null)
            {
                if (notes == null)
                    return new(NotesSyncAction.InSync, Array.Empty<string>(), false);
                doc.AdoptFromNotes(vmId, notes);
                return new(NotesSyncAction.Adopted, Array.Empty<string>(), false);
            }

            string storePrint = NotesBlock.Fingerprint(doc.ToNotesPayload(entry));
            if (storePrint == notesPrint)
            {
                entry.NotesFingerprint = notesPrint;
                return new(NotesSyncAction.InSync, Array.Empty<string>(), false);
            }

            // Never synced with a fingerprint (older builds): an entry with nothing to mirror
            // (credentials only) must not wipe a real block; otherwise the store wins, as before.
            if (entry.NotesFingerprint == null && notes != null && doc.ToNotesPayload(entry) == null)
            {
                var taken = doc.ApplyNotes(entry, notes);
                entry.NotesFingerprint = notesPrint;
                return new(NotesSyncAction.TookFromNotes, taken, NotesBlock.Fingerprint(doc.ToNotesPayload(entry)) != notesPrint);
            }

            // Never synced with a fingerprint, or Notes untouched since the last write: the store is newer.
            if (entry.NotesFingerprint == null || entry.NotesFingerprint == notesPrint)
                return new(NotesSyncAction.PushToNotes, Array.Empty<string>(), true);

            if (entry.NotesFingerprint == storePrint)
            {
                // A cleared Notes field is far more likely an accident than a wish to drop
                // folder, tags and description: put the block back.
                if (notes == null)
                    return new(NotesSyncAction.RestoreBlock, Array.Empty<string>(), true);

                var changed = doc.ApplyNotes(entry, notes);
                entry.NotesFingerprint = notesPrint;
                // Protection is kept even if the edit removed it: then Notes needs the store's block again.
                bool push = NotesBlock.Fingerprint(doc.ToNotesPayload(entry)) != notesPrint;
                return new(NotesSyncAction.TookFromNotes, changed, push);
            }

            return new(NotesSyncAction.Conflict, Array.Empty<string>(), true, NotesBlock.Serialize(notes));
        }

        /// <summary>Records the name and day a VM was seen; true when the entry changed.</summary>
        public static bool MarkSeen(VmMetadata entry, string name, DateOnly today)
        {
            if (entry.LastKnownName == name && entry.LastSeenOn == today)
                return false;
            entry.LastKnownName = name;
            entry.LastSeenOn = today;
            return true;
        }
    }
}
