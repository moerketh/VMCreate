using System.Collections.ObjectModel;
using VMCreate.Metadata;

namespace VMCreate.Manager.ViewModels
{
    public enum NavNodeKind
    {
        /// <summary>Every VM; dropping a VM here moves it out of any folder.</summary>
        All,
        Folder,
        Unfiled,
        Smart,
    }

    /// <summary>A node in the left-hand navigation tree.</summary>
    public sealed class NavNodeViewModel : ObservableObject
    {
        private bool _isSelected;
        private bool _isExpanded = true;
        private int _count;

        public NavNodeViewModel(NavNodeKind kind, string name, string icon, Guid? folderId = null, SmartFolderFilter? filter = null, string? color = null)
        {
            Kind = kind;
            Color = color;
            Name = name;
            Icon = icon;
            FolderId = folderId;
            Filter = filter;
        }

        public NavNodeKind Kind { get; }
        public string Name { get; }

        /// <summary>WPF-UI SymbolRegular name.</summary>
        public string Icon { get; }

        public Guid? FolderId { get; }

        /// <summary>Folder icon colour ("#RRGGBB", own or inherited); null = default.</summary>
        public string? Color { get; }
        public SmartFolderFilter? Filter { get; }
        public ObservableCollection<NavNodeViewModel> Children { get; } = new();

        /// <summary>Stable identity used to restore the selection after the tree is rebuilt.</summary>
        public string Key => Kind switch
        {
            NavNodeKind.Folder => "folder:" + FolderId,
            NavNodeKind.Smart => "smart:" + Name,
            _ => Kind.ToString(),
        };

        public bool IsFolderDropTarget => Kind is NavNodeKind.All or NavNodeKind.Folder or NavNodeKind.Unfiled;

        public int Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public IEnumerable<NavNodeViewModel> SelfAndDescendants()
        {
            yield return this;
            foreach (var child in Children)
                foreach (var d in child.SelfAndDescendants())
                    yield return d;
        }
    }
}
