using System.Collections.ObjectModel;

namespace VMCreate
{
    public enum WizardResult
    {
        Finished,
        Canceled
    }

    public class WizardData
    {
        public ObservableCollection<GalleryItem>? GalleryItems { get; set; }
        public GalleryItem? SelectedItem { get; set; }

        /// <summary>
        /// Id of the non-default release variant chosen on the settings page
        /// (see <see cref="GalleryItem.ReleaseVariants"/>); null = the item's
        /// default release. Resolved to an effective item only when deployment
        /// starts.
        /// </summary>
        public string? SelectedReleaseId { get; set; }
        // Settings/Customizations are always present: initialized at construction
        // and only ever replaced with fresh instances by ResetForNewWizard.
        public VmSettings Settings { get; set; } = new VmSettings();
        public VmCustomizations Customizations { get; set; } = new VmCustomizations();
        public bool DemoMode { get; set; }

        /// <summary>
        /// Machine-level preferences backing this wizard run (pre-fills, stored
        /// credentials). Always non-null; defaults when no settings file exists.
        /// Owned by <see cref="MainWindow"/> — loaded from
        /// <see cref="IUserPreferencesStore"/> and re-applied after the Settings
        /// window saves. Never holds per-VM state.
        /// </summary>
        public UserPreferences Preferences { get; set; } = new UserPreferences();

        /// <summary>
        /// The HTB API token actually used during this run (typed on the
        /// customization page). Deliberately <b>not</b> part of
        /// <see cref="VmCustomizations"/> — the guest never needs the token —
        /// but DeployPage persists it into the preferences store on success so
        /// the user types it exactly once. Transient; never serialized to the guest.
        /// </summary>
        public string? LastUsedHtbApiToken { get; set; }

        /// <summary>
        /// Seeds <see cref="Settings"/> and <see cref="Customizations"/> from
        /// <see cref="Preferences"/>. Called when the wizard starts (and after a
        /// Settings-window save) so every run starts from the user's habitual
        /// values while remaining fully editable per-run.
        /// </summary>
        public void ApplyPreferences()
        {
            Settings.MemoryInMB = Preferences.DefaultMemoryInMB;
            Settings.CPUCount = Preferences.DefaultCpuCount;
            Settings.NewDriveSizeInGB = Preferences.DefaultDiskSizeInGB;

            Customizations.RdpBackend = Preferences.RdpBackend;
            Customizations.DnsMode = Preferences.DnsMode;
            Customizations.CustomNameservers = Preferences.CustomNameservers;
        }

        /// <summary>Resets user-modifiable state for a fresh wizard run.</summary>
        public void ResetForNewWizard()
        {
            Settings = new VmSettings();
            Customizations = new VmCustomizations();
            SelectedReleaseId = null;
            LastUsedHtbApiToken = null;
            // Preferences survive a reset (machine-level, not per-wizard); the
            // fresh Settings/Customizations instances re-seed from them.
            ApplyPreferences();
        }
    }
}
