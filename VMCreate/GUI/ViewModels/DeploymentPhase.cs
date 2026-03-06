using Wpf.Ui.Controls;

namespace VMCreate
{
    public enum DeploymentPhaseStatus
    {
        Pending,
        Active,
        Completed,
        Failed,
        Skipped
    }

    /// <summary>
    /// Represents a single phase card on the Deploy page.
    /// Each phase tracks its own status, progress, and display text.
    /// </summary>
    public class DeploymentPhase : ViewModelBase
    {
        private DeploymentPhaseStatus _status = DeploymentPhaseStatus.Pending;
        private int _progressPercentage;
        private string _progressText;
        private bool _isIndeterminate;

        public DeploymentPhase(string id, string name, string description, SymbolRegular icon)
        {
            Id = id;
            Name = name;
            Description = description;
            Icon = icon;
        }

        /// <summary>Machine-readable identifier used to match progress reports to cards.</summary>
        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public SymbolRegular Icon { get; }

        public DeploymentPhaseStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                    OnPropertyChanged(nameof(DisplayIcon));
            }
        }

        /// <summary>
        /// The icon to display, which changes based on status:
        /// Completed → checkmark, Failed → error, otherwise the phase's own icon.
        /// </summary>
        public SymbolRegular DisplayIcon => _status switch
        {
            DeploymentPhaseStatus.Completed => SymbolRegular.CheckmarkCircle24,
            DeploymentPhaseStatus.Failed    => SymbolRegular.ErrorCircle24,
            DeploymentPhaseStatus.Skipped   => SymbolRegular.SubtractCircle24,
            _                               => Icon
        };

        /// <summary>0–100 progress within this phase. Ignored when <see cref="IsIndeterminate"/> is true.</summary>
        public int ProgressPercentage
        {
            get => _progressPercentage;
            set => SetProperty(ref _progressPercentage, value);
        }

        /// <summary>Secondary text shown below the phase name (speed, URI, error message, etc.).</summary>
        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value);
        }

        /// <summary>When true the card shows an indeterminate spinner instead of a progress bar.</summary>
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => SetProperty(ref _isIndeterminate, value);
        }
    }
}
