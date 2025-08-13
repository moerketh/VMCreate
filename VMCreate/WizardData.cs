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
        public ObservableCollection<GalleryItem> GalleryItems { get; set; }
        public GalleryItem SelectedItem { get; set; }
        public VmSettings Settings { get; set; } = new VmSettings();
        public VmCustomizations Customizations { get; set; } = new VmCustomizations();
    }
}