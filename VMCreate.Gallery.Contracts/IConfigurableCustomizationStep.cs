namespace VMCreate
{
    /// <summary>
    /// A customization step that also carries UI metadata so the main app can
    /// dynamically render a checkbox card on the customization page.
    /// <para>
    /// Implementations live in gallery assemblies (e.g. <c>VMCreate.Gallery.Security</c>)
    /// and are auto-discovered via the same assembly scanning that finds
    /// <see cref="ICustomizationStep"/> implementations — no registration in the
    /// main app required.
    /// </para>
    /// </summary>
    public interface IConfigurableCustomizationStep : ICustomizationStep
    {
        /// <summary>Card header shown above the checkbox (e.g. "PwnCloudOS Tools").</summary>
        string CardTitle { get; }

        /// <summary>Card description shown below the header.</summary>
        string CardDescription { get; }

        /// <summary>Checkbox label text (e.g. "Update all tools (pwncloudos-sync)").</summary>
        string Label { get; }

        /// <summary>Tooltip displayed when hovering over the checkbox.</summary>
        string Tooltip { get; }

        /// <summary>Default checkbox state when the card first appears.</summary>
        bool DefaultEnabled { get; }

        /// <summary>
        /// Returns true if this step's UI card should be shown for the given gallery item.
        /// Called once when the customization page loads to filter visible options.
        /// </summary>
        bool IsVisibleFor(GalleryItem item);
    }
}
