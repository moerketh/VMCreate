using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VMCreate
{
    /// <summary>
    /// Display model for one desktop-environment radio option on the
    /// customization page, projected from a <see cref="DesktopEnvironmentOption"/>
    /// in the distribution's <see cref="IDesktopEnvironmentCatalog"/>.
    /// Pre-computes the badge texts so the XAML template stays declarative.
    /// </summary>
    public class DesktopOptionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public DesktopOptionItem(DesktopEnvironmentOption option)
        {
            Id = option.Id;
            DisplayName = option.DisplayName;
            Description = option.Description;
            IsDefault = option.IsDefault;
            LamcoSupported = option.LamcoSupported;
            DisplayServer = option.DisplayServer;
            EstimatedDownloadMb = option.EstimatedDownloadMb;
        }

        /// <summary>Stable id stored in <see cref="VmCustomizations.DesktopEnvironmentId"/>.</summary>
        public string Id { get; }

        public string DisplayName { get; }
        public string Description { get; }
        public bool IsDefault { get; }
        public bool LamcoSupported { get; }
        public DesktopDisplayServer DisplayServer { get; }
        public int EstimatedDownloadMb { get; }

        /// <summary>
        /// "Wayland by default", "X11 only", or "Wayland (experimental)" —
        /// the display-server badge text.
        /// </summary>
        public string DisplayServerText => DisplayServer switch
        {
            DesktopDisplayServer.WaylandDefault => "Wayland by default",
            DesktopDisplayServer.X11Only => "X11 only",
            _ => "Wayland (experimental)"
        };

        /// <summary>
        /// Download-size badge; empty for the stock desktop (no download).
        /// </summary>
        public string SizeText => EstimatedDownloadMb <= 0
            ? ""
            : $"~{EstimatedDownloadMb:N0} MB download";

        /// <summary>Set by the radio group; the keep-default pseudo option uses null id.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
