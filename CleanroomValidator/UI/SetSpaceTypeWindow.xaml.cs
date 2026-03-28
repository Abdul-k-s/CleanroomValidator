using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CleanroomValidator.UI
{
    public partial class SetSpaceTypeWindow : Window
    {
        private readonly Document _doc;
        private readonly List<Space> _spaces;
        private readonly SpaceTypeMappingService _mappingService;
        private readonly ParameterService _paramService;
        private ObservableCollection<SpaceMappingItem> _mappingItems;

        public bool DialogConfirmed { get; private set; }
        public List<SpaceMappingItem> Mappings => _mappingItems?.ToList() ?? new List<SpaceMappingItem>();
        public SpaceTypeMappingService MappingService => _mappingService;

        public SetSpaceTypeWindow(Document doc, List<Space> spaces)
        {
            InitializeComponent();
            
            _doc = doc;
            _spaces = spaces;
            _mappingService = new SpaceTypeMappingService(doc);
            _paramService = new ParameterService();

            InitializeMappings();
            InitializeBulkComboBox();
            UpdateStatus();
        }

        private void InitializeMappings()
        {
            _mappingItems = new ObservableCollection<SpaceMappingItem>();

            var spaceTypeNames = _mappingService.GetAvailableSpaceTypeNames();

            foreach (var space in _spaces)
            {
                var spaceName = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                var cleanlinessClass = _paramService.GetCleanlinessClass(space);
                var suggestedTypeName = _mappingService.FindMatchingSpaceTypeName(spaceName);

                var item = new SpaceMappingItem
                {
                    SpaceId = space.Id,
                    SpaceNumber = space.Number ?? "-",
                    SpaceName = spaceName,
                    CleanlinessClass = cleanlinessClass,
                    AvailableSpaceTypes = spaceTypeNames,
                    SelectedSpaceTypeName = suggestedTypeName,
                    MatchScore = _mappingService.GetMatchScore(spaceName, suggestedTypeName)
                };

                // Auto-enable cleanroom parameters for classified spaces
                item.ApplyCleanroomParams = item.IsClassified;

                item.PropertyChanged += Item_PropertyChanged;
                _mappingItems.Add(item);
            }

            SpaceDataGrid.ItemsSource = _mappingItems;
        }

        private void InitializeBulkComboBox()
        {
            BulkSpaceTypeCombo.ItemsSource = _mappingService.GetAvailableSpaceTypeNames();
            BulkSpaceTypeCombo.SelectedIndex = 0;
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var total = _mappingItems.Count;
            var mapped = _mappingItems.Count(i => i.HasSpaceType);
            var cleanroom = _mappingItems.Count(i => i.ApplyCleanroomParams);

            StatusText.Text = $"{mapped}/{total} spaces with Space Type • {cleanroom} with cleanroom parameters";
        }

        private void AutoMatchAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _mappingItems)
            {
                var suggestedTypeName = _mappingService.FindMatchingSpaceTypeName(item.SpaceName);
                item.SelectedSpaceTypeName = suggestedTypeName;
                item.MatchScore = _mappingService.GetMatchScore(item.SpaceName, suggestedTypeName);
                item.ApplyCleanroomParams = item.IsClassified;
            }

            SpaceDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _mappingItems)
            {
                item.SelectedSpaceTypeName = "(None)";
                item.ApplyCleanroomParams = false;
                item.MatchScore = 0;
            }

            SpaceDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void SetCleanroomForClassified_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _mappingItems.Where(i => i.IsClassified))
            {
                item.ApplyCleanroomParams = true;
                item.SelectedSpaceTypeName = "Cleanroom";
            }

            SpaceDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void ApplyBulkSpaceType_Click(object sender, RoutedEventArgs e)
        {
            var selectedTypeName = BulkSpaceTypeCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedTypeName))
                return;

            var selectedItems = SpaceDataGrid.SelectedItems.Cast<SpaceMappingItem>().ToList();
            if (!selectedItems.Any())
            {
                MessageBox.Show("Please select rows in the grid first.", "No Selection", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var item in selectedItems)
            {
                item.SelectedSpaceTypeName = selectedTypeName;
            }

            SpaceDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogConfirmed = false;
            DialogResult = false;
            Close();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            DialogConfirmed = true;
            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// View model for space-to-type mapping
    /// </summary>
    public class SpaceMappingItem : INotifyPropertyChanged
    {
        private string _selectedSpaceTypeName;
        private bool _applyCleanroomParams;

        public ElementId SpaceId { get; set; }
        public string SpaceNumber { get; set; }
        public string SpaceName { get; set; }
        public string CleanlinessClass { get; set; }
        public List<string> AvailableSpaceTypes { get; set; }
        public double MatchScore { get; set; }

        public string SelectedSpaceTypeName
        {
            get => _selectedSpaceTypeName;
            set
            {
                if (_selectedSpaceTypeName != value)
                {
                    _selectedSpaceTypeName = value;
                    OnPropertyChanged(nameof(SelectedSpaceTypeName));
                    OnPropertyChanged(nameof(HasSpaceType));
                }
            }
        }

        public bool ApplyCleanroomParams
        {
            get => _applyCleanroomParams;
            set
            {
                if (_applyCleanroomParams != value)
                {
                    _applyCleanroomParams = value;
                    OnPropertyChanged(nameof(ApplyCleanroomParams));
                }
            }
        }

        public bool IsClassified => !string.IsNullOrEmpty(CleanlinessClass) && CleanlinessClass != "Unclassified";

        public bool HasSpaceType => !string.IsNullOrEmpty(SelectedSpaceTypeName) && SelectedSpaceTypeName != "(None)";

        public Brush MatchIndicatorColor
        {
            get
            {
                if (IsClassified)
                    return new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 150, 243)); // Blue

                if (MatchScore >= 0.8)
                    return new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                if (MatchScore >= 0.5)
                    return new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)); // Yellow
                return new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158)); // Gray
            }
        }

        public string MatchTooltip
        {
            get
            {
                if (IsClassified)
                    return $"Classified: {CleanlinessClass}";
                if (MatchScore >= 0.8)
                    return $"Good match ({MatchScore:P0})";
                if (MatchScore >= 0.5)
                    return $"Partial match ({MatchScore:P0})";
                return "No match found";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
