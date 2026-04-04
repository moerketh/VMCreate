using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VMCreate
{
    /// <summary>
    /// Display model for a single distribution-specific option rendered
    /// dynamically on the customization page via an <see cref="IConfigurableCustomizationStep"/>.
    /// </summary>
    public class DistributionOptionItem : INotifyPropertyChanged
    {
        private bool _isEnabled;

        public DistributionOptionItem(IConfigurableCustomizationStep step)
        {
            Name = step.Name;
            CardTitle = step.CardTitle;
            CardDescription = step.CardDescription;
            Label = step.Label;
            Tooltip = step.Tooltip;
            _isEnabled = step.DefaultEnabled;
        }

        /// <summary>Dictionary key matching <see cref="ICustomizationStep.Name"/>.</summary>
        public string Name { get; }

        public string CardTitle { get; }
        public string CardDescription { get; }
        public string Label { get; }
        public string Tooltip { get; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
