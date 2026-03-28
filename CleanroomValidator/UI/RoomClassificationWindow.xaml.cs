using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WpfGrid = System.Windows.Controls.Grid;

namespace CleanroomValidator.UI
{
    public partial class RoomClassificationWindow : Window
    {
        private readonly Document _doc;
        private readonly List<Space> _localSpaces;
        private readonly List<(Space space, string sourceName)> _linkedSpaces;
        private ObservableCollection<SpaceClassificationItem> _spaceItems;
        private ICollectionView _collectionView;
        private bool _isUpdatingComboBoxes = false;
        
        // Store changes before dialog closes
        private Dictionary<ElementId, string> _storedChanges = new Dictionary<ElementId, string>();

        public bool ChangesApplied { get; private set; } = false;

        public RoomClassificationWindow(Document doc, List<Space> localSpaces, List<(Space space, string sourceName)> linkedSpaces = null)
        {
            InitializeComponent();
            _doc = doc;
            _localSpaces = localSpaces;
            _linkedSpaces = linkedSpaces ?? new List<(Space, string)>();

            LoadSpaces();
            SetupGrouping();
        }

        private void LoadSpaces()
        {
            _spaceItems = new ObservableCollection<SpaceClassificationItem>();

            // Add local spaces (editable)
            foreach (var space in _localSpaces.OrderBy(s => s.Level?.Name ?? "").ThenBy(s => s.Number))
            {
                var currentClass = space.LookupParameter("Cleanliness_Class")?.AsString();
                var displayClass = string.IsNullOrEmpty(currentClass) ? "Unclassified" : currentClass;
                
                var initialClasses = new List<string> { "Unclassified" };
                if (displayClass != "Unclassified" && !initialClasses.Contains(displayClass))
                {
                    initialClasses.Add(displayClass);
                }
                
                _spaceItems.Add(new SpaceClassificationItem
                {
                    SpaceId = space.Id,
                    Number = space.Number ?? "-",
                    Name = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                    Level = space.Level?.Name ?? "No Level",
                    Area = space.Area * 0.092903,
                    CurrentClass = displayClass,
                    AvailableClasses = initialClasses,
                    NewClass = displayClass,
                    IsSelected = false,
                    IsEditable = true,
                    Source = "Local"
                });
            }

            // Add linked spaces (read-only)
            foreach (var (space, sourceName) in _linkedSpaces.OrderBy(s => s.space.Level?.Name ?? "").ThenBy(s => s.space.Number))
            {
                var currentClass = space.LookupParameter("Cleanliness_Class")?.AsString();
                var displayClass = string.IsNullOrEmpty(currentClass) ? "Unclassified" : currentClass;
                
                _spaceItems.Add(new SpaceClassificationItem
                {
                    SpaceId = space.Id,
                    Number = space.Number ?? "-",
                    Name = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                    Level = space.Level?.Name ?? "No Level",
                    Area = space.Area * 0.092903,
                    CurrentClass = displayClass,
                    AvailableClasses = new List<string> { displayClass },
                    NewClass = displayClass,
                    IsSelected = false,
                    IsEditable = false,
                    Source = sourceName.Length > 12 ? sourceName.Substring(0, 12) + "..." : sourceName
                });
            }

            SpacesGrid.ItemsSource = _spaceItems;
        }

        private void SetupGrouping()
        {
            _collectionView = CollectionViewSource.GetDefaultView(_spaceItems);
            _collectionView.GroupDescriptions.Add(new PropertyGroupDescription("Level"));
        }

        private void StandardRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (GmpRadio == null || IsoRadio == null) return;

            List<string> classes;
            
            if (GmpRadio.IsChecked == true)
            {
                classes = new List<string> { "Unclassified", "GMP-B", "GMP-C", "GMP-D" };
            }
            else if (IsoRadio.IsChecked == true)
            {
                classes = new List<string> { "Unclassified", "ISO-6", "ISO-7", "ISO-8" };
            }
            else
            {
                return;
            }

            _isUpdatingComboBoxes = true;
            
            try
            {
                foreach (var item in _spaceItems.Where(s => s.IsEditable))
                {
                    item.AvailableClasses = classes;
                    
                    if (!classes.Contains(item.NewClass))
                    {
                        item.NewClass = classes.Contains(item.CurrentClass) ? item.CurrentClass : "Unclassified";
                    }
                }

                SpacesGrid.Items.Refresh();
            }
            finally
            {
                _isUpdatingComboBoxes = false;
            }
        }

        private void ClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingComboBoxes) return;
            
            var comboBox = sender as ComboBox;
            if (comboBox == null) return;
            
            var selectedClass = comboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedClass)) return;
            
            var currentItem = comboBox.DataContext as SpaceClassificationItem;
            if (currentItem == null || !currentItem.IsEditable) return;
            
            // Only apply to other checked items if the current item is checked
            if (currentItem.IsSelected)
            {
                _isUpdatingComboBoxes = true;
                
                try
                {
                    foreach (var item in _spaceItems.Where(s => s.IsSelected && s.IsEditable && s != currentItem))
                    {
                        item.NewClass = selectedClass;
                    }
                    // No need to call Items.Refresh() - INotifyPropertyChanged handles updates
                }
                finally
                {
                    _isUpdatingComboBoxes = false;
                }
            }
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            _isUpdatingComboBoxes = true;
            
            try
            {
                foreach (var item in _spaceItems.Where(s => s.IsEditable))
                {
                    item.NewClass = "Unclassified";
                }
            }
            finally
            {
                _isUpdatingComboBoxes = false;
            }
        }

        private void CheckAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _spaceItems.Where(s => s.IsEditable))
            {
                item.IsSelected = true;
            }
        }

        private void UncheckAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _spaceItems)
            {
                item.IsSelected = false;
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            _storedChanges = _spaceItems
                .Where(s => s.IsEditable && s.NewClass != s.CurrentClass)
                .ToDictionary(s => s.SpaceId, s => s.NewClass);

            if (!_storedChanges.Any())
            {
                MessageBox.Show("No changes to apply.", "No Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var classifiedCount = _storedChanges.Count(c => c.Value != "Unclassified");
            var clearedCount = _storedChanges.Count(c => c.Value == "Unclassified");

            var msg = $"Apply changes to {_storedChanges.Count} space(s)?";
            if (classifiedCount > 0)
                msg += $"\n• {classifiedCount} space(s) will be classified";
            if (clearedCount > 0)
                msg += $"\n• {clearedCount} space(s) will be set to Unclassified";

            var result = MessageBox.Show(msg, "Confirm Changes", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                ChangesApplied = true;
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public Dictionary<ElementId, string> GetChanges()
        {
            return _storedChanges;
        }
    }

    public class SpaceClassificationItem : INotifyPropertyChanged
    {
        private string _newClass;
        private List<string> _availableClasses;
        private bool _isSelected;

        public ElementId SpaceId { get; set; }
        public string Number { get; set; }
        public string Name { get; set; }
        public string Level { get; set; }
        public double Area { get; set; }
        public string CurrentClass { get; set; }
        public bool IsEditable { get; set; }
        public string Source { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public string NewClass
        {
            get => _newClass;
            set
            {
                if (_newClass != value)
                {
                    _newClass = value;
                    OnPropertyChanged(nameof(NewClass));
                }
            }
        }

        public List<string> AvailableClasses
        {
            get => _availableClasses;
            set
            {
                _availableClasses = value;
                OnPropertyChanged(nameof(AvailableClasses));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
