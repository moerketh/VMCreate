using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;

namespace VMCreate
{
    /// <summary>
    /// ViewModel for the gallery image selection page.
    /// Manages search filtering, item selection, and the loading indicator.
    /// </summary>
    public class SelectImagePageViewModel : ViewModelBase
    {
        private readonly WizardData _wizardData;
        private readonly ILogger _logger;
        private readonly ICollectionView _galleryView;

        private string _searchText = string.Empty;
        private GalleryItem _selectedItem;
        private bool _isLoading = true;
        private string _errorMessage;

        /// <summary>Raised when the wizard should complete (e.g. Cancel).</summary>
        public event Action<WizardResult> RequestWizardComplete;

        /// <summary>Raised when the user clicks Next and validation passes.</summary>
        public event Action RequestNavigateNext;

        public SelectImagePageViewModel(WizardData wizardData, ILogger logger)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _galleryView = CollectionViewSource.GetDefaultView(_wizardData.GalleryItems);
            if (_galleryView is ListCollectionView listView)
                listView.CustomSort = new GalleryItemComparer();
            else
                _galleryView.SortDescriptions.Add(
                    new SortDescription(nameof(GalleryItem.Name), ListSortDirection.Ascending));

            // If items are already loaded (e.g. wizard reset after VM creation),
            // don't show the loading overlay.
            if (_wizardData.GalleryItems.Count > 0)
                _isLoading = false;

            NextCommand = new RelayCommand(OnNext, () => SelectedItem != null);
            CancelCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Canceled));
            DismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        }

        /// <summary>Sorted and filtered view of the gallery items.</summary>
        public ICollectionView GalleryView => _galleryView;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    ApplyFilter();
            }
        }

        public GalleryItem SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetProperty(ref _selectedItem, value))
                    _logger.LogDebug("Selection changed to: {Name}", value?.Name ?? "(none)");
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        /// <summary>Error text shown in a dismissible banner at the top of the page.</summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        public ICommand NextCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand DismissErrorCommand { get; }

        private void ApplyFilter()
        {
            string filter = _searchText?.Trim().ToLower() ?? string.Empty;
            if (string.IsNullOrEmpty(filter))
            {
                _galleryView.Filter = null;
            }
            else
            {
                _galleryView.Filter = obj => obj is GalleryItem item &&
                    (item.Name?.ToLower().Contains(filter) == true ||
                     item.Publisher?.ToLower().Contains(filter) == true ||
                     item.Description?.ToLower().Contains(filter) == true);
            }
            _logger.LogDebug("Applied search filter: {Filter}", filter);
        }

        private void OnNext()
        {
            if (_selectedItem == null) return;
            _wizardData.SelectedItem = _selectedItem;
            RequestNavigateNext?.Invoke();
        }

        /// <summary>
        /// Sorts gallery items: recommended first, then security distros, then
        /// everything else — all tiers sorted alphabetically within themselves.
        /// </summary>
        private sealed class GalleryItemComparer : IComparer
        {
            public int Compare(object? x, object? y)
            {
                var a = x as GalleryItem;
                var b = y as GalleryItem;
                if (a == null && b == null) return 0;
                if (a == null) return 1;
                if (b == null) return -1;

                // Tier 1: Recommended (true sorts before false)
                int rec = b.IsRecommended.CompareTo(a.IsRecommended);
                if (rec != 0) return rec;

                // Tier 2: Security distros before general
                int sec = b.IsSecurity.CompareTo(a.IsSecurity);
                if (sec != 0) return sec;

                // Tier 3: Pre-installed images before ISO installers
                int pre = b.IsPreInstalled.CompareTo(a.IsPreInstalled);
                if (pre != 0) return pre;

                // Tier 4: Alphabetical by name
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
