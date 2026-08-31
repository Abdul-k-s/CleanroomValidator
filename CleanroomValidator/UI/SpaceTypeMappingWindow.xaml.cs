using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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
    public partial class SpaceTypeMappingWindow : Window
    {
        private readonly Document _doc;
        private readonly List<Room> _rooms;
        private readonly SpaceTypeMappingService _mappingService;
        private readonly ParameterService _paramService;
        private ObservableCollection<RoomMappingItem> _mappingItems;

        public bool DialogConfirmed { get; private set; }
        public List<RoomMappingItem> Mappings => _mappingItems?.ToList() ?? new List<RoomMappingItem>();
        public SpaceTypeMappingService MappingService => _mappingService;

        public SpaceTypeMappingWindow(Document doc, List<Room> rooms, string sourceName = "Local")
        {
            InitializeComponent();
            
            _doc = doc;
            _rooms = rooms;
            _mappingService = new SpaceTypeMappingService(doc);
            _paramService = new ParameterService();

            InitializeMappings(sourceName);
            InitializeBulkComboBox();
            UpdateStatus();
        }

        private void InitializeMappings(string sourceName)
        {
            _mappingItems = new ObservableCollection<RoomMappingItem>();

            var spaceTypeNames = _mappingService.GetAvailableSpaceTypeNames();

            foreach (var room in _rooms)
            {
                var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                var cleanlinessClass = _paramService.GetCleanlinessClass(room);
                var suggestedTypeName = _mappingService.FindMatchingSpaceTypeName(roomName);

                var item = new RoomMappingItem
                {
                    RoomId = room.Id,
                    RoomNumber = room.Number ?? "-",
                    RoomName = roomName,
                    CleanlinessClass = cleanlinessClass,
                    SourceName = sourceName,
                    AvailableSpaceTypes = spaceTypeNames,
                    SelectedSpaceTypeName = suggestedTypeName,
                    MatchScore = _mappingService.GetMatchScore(roomName, suggestedTypeName)
                };

                // Auto-enable cleanroom parameters for classified rooms
                item.ApplyCleanroomParams = item.IsClassified;

                item.PropertyChanged += Item_PropertyChanged;
                _mappingItems.Add(item);
            }

            MappingDataGrid.ItemsSource = _mappingItems;
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

            StatusText.Text = $"{mapped}/{total} rooms with Space Type • {cleanroom} with cleanroom parameters";
        }

        private void AutoMatchAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _mappingItems)
            {
                var suggestedTypeName = _mappingService.FindMatchingSpaceTypeName(item.RoomName);
                item.SelectedSpaceTypeName = suggestedTypeName;
                item.MatchScore = _mappingService.GetMatchScore(item.RoomName, suggestedTypeName);
                item.ApplyCleanroomParams = item.IsClassified;
            }

            MappingDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void ClearAllMappings_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _mappingItems)
            {
                item.SelectedSpaceTypeName = "(None)";
                item.ApplyCleanroomParams = false;
                item.MatchScore = 0;
            }

            MappingDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void SelectCleanroomTypes_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _mappingItems.Where(i => i.IsClassified))
            {
                item.ApplyCleanroomParams = true;
                // Set Cleanroom or Laboratory type for classified spaces
                if (item.SelectedSpaceTypeName == "(None)")
                {
                    item.SelectedSpaceTypeName = "Cleanroom";
                }
            }

            MappingDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void ApplyBulkSpaceType_Click(object sender, RoutedEventArgs e)
        {
            var selectedTypeName = BulkSpaceTypeCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedTypeName))
                return;

            var selectedItems = MappingDataGrid.SelectedItems.Cast<RoomMappingItem>().ToList();
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

            MappingDataGrid.Items.Refresh();
            UpdateStatus();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogConfirmed = false;
            DialogResult = false;
            Close();
        }

        private void CreateSpaces_Click(object sender, RoutedEventArgs e)
        {
            DialogConfirmed = true;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Get the Space Type name for a mapping item
        /// </summary>
        public string GetSpaceTypeNameForMapping(RoomMappingItem item)
        {
            return item.SelectedSpaceTypeName;
        }
    }

    /// <summary>
    /// View model for room-to-space-type mapping
    /// </summary>
    public class RoomMappingItem : INotifyPropertyChanged
    {
        private string _selectedSpaceTypeName;
        private bool _applyCleanroomParams;

        public ElementId RoomId { get; set; }
        public string RoomNumber { get; set; }
        public string RoomName { get; set; }
        public string CleanlinessClass { get; set; }
        public string SourceName { get; set; }
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
