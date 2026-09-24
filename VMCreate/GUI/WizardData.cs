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

        /// <summary>Resets user-modifiable state for a fresh wizard run.</summary>
        public void ResetForNewWizard()
        {
            Settings = new VmSettings();
            Customizations = new VmCustomizations();
            SelectedReleaseId = null;
        }
    }
}
