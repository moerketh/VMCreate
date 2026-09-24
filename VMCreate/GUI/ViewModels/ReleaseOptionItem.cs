using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VMCreate
{
    /// <summary>
    /// Display model for one release radio option on the VM settings page,
    /// projected from a <see cref="ReleaseVariant"/> of the selected
    /// <see cref="GalleryItem"/>. Pre-computes the badge texts so the XAML
    /// template stays declarative.
    /// </summary>
    public class ReleaseOptionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ReleaseOptionItem(ReleaseVariant variant)
        {
            Id = variant.Id;
            DisplayName = variant.DisplayName;
            Description = variant.Description ?? "";
            IsDefault = variant.IsDefault;
            LastUpdated = variant.LastUpdated;
            DownloadSizeText = variant.DownloadSizeText;
        }

        /// <summary>Stable id stored in <see cref="WizardData.SelectedReleaseId"/>.</summary>
        public string Id { get; }

        public string DisplayName { get; }
        public string Description { get; }
        public bool IsDefault { get; }
        public string? LastUpdated { get; }
        public string? DownloadSizeText { get; }

        /// <summary>Release-date badge; empty when the scrape had no date.</summary>
        public string DateText => string.IsNullOrEmpty(LastUpdated) ? "" : $"released {LastUpdated}";

        /// <summary>Download-size badge; empty when the scrape had no size.</summary>
        public string SizeText => string.IsNullOrEmpty(DownloadSizeText) ? "" : DownloadSizeText;

        /// <summary>Set by the radio group; the default variant has a real id too.</summary>
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
