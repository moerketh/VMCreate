namespace VMCreate
{
    /// <summary>
    /// Declares whether a customization step should be visible for a given gallery item.
    /// Separated from execution metadata so UI filtering does not depend on the
    /// broader <see cref="ICustomizationStep"/> contract.
    /// </summary>
    public interface IStepVisibility
    {
        /// <summary>
        /// Returns true when the step is relevant for the supplied gallery item.
        /// Used both by customization-page card filtering and by deploy-time
        /// applicability checks.
        /// </summary>
        bool IsVisibleFor(GalleryItem item);
    }
}
