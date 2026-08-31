using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using CleanroomValidator.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace CleanroomValidator.UI
{
    public partial class SetPressureWindow : Window
    {
        private readonly Document _doc;
        private readonly List<Space> _spaces;
        private readonly List<Room> _rooms;
        private readonly ParameterService _paramService;
        private readonly ObservableCollection<PressureRowViewModel> _rows;

        public SetPressureWindow(Document doc, List<Space> spaces, List<Room> rooms, ParameterService paramService)
        {
            InitializeComponent();
            _doc = doc;
            _spaces = spaces;
            _rooms = rooms;
            _paramService = paramService;
            _rows = new ObservableCollection<PressureRowViewModel>();
            LoadRows();
            PressureGrid.ItemsSource = _rows;
        }

        private void LoadRows()
        {
            // Spaces take priority
            foreach (var space in _spaces.OrderBy(s => s.Level?.Name).ThenBy(s => s.Number))
            {
                _rows.Add(new PressureRowViewModel
                {
                    ElementId = space.Id,
                    ElementType = "Space",
                    Number = space.Number ?? "",
                    Name = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                    Level = space.Level?.Name ?? "",
                    CleanlinessClass = _paramService.GetCleanlinessClass(space),
                    Pressure = _paramService.GetRoomPressure(space)
                });
            }

            // Rooms that don't have a matching space
            foreach (var room in _rooms.OrderBy(r => r.Level?.Name).ThenBy(r => r.Number))
            {
                bool hasMatchingSpace = _spaces.Any(s =>
                    s.Level?.Id == room.Level?.Id &&
                    string.Equals(s.Number, room.Number, StringComparison.OrdinalIgnoreCase));

                if (!hasMatchingSpace)
                {
                    _rows.Add(new PressureRowViewModel
                    {
                        ElementId = room.Id,
                        ElementType = "Room",
                        Number = room.Number ?? "",
                        Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                        Level = room.Level?.Name ?? "",
                        CleanlinessClass = _paramService.GetCleanlinessClass(room),
                        Pressure = _paramService.GetRoomPressure(room)
                    });
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Commit any in-progress cell edit
            PressureGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            int saved = 0;
            int failed = 0;

            using (var trans = new Transaction(_doc, "Set Room Pressure"))
            {
                trans.Start();
                try
                {
                    foreach (var row in _rows)
                    {
                        var element = _doc.GetElement(row.ElementId);
                        bool ok = false;

                        if (element is Space space)
                            ok = _paramService.SetRoomPressure(space, row.Pressure);
                        else if (element is Room room)
                            ok = _paramService.SetRoomPressure(room, row.Pressure);

                        if (ok) saved++; else failed++;
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    TaskDialog.Show("Error", $"Failed to save pressures:\n{ex.Message}");
                    return;
                }
            }

            string msg = $"Saved pressure values for {saved} room(s)/space(s).";
            if (failed > 0) msg += $"\n{failed} could not be updated (read-only parameter).";
            TaskDialog.Show("Cleanroom Validator", msg);
            Close();
        }

        private void ApplyToSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(QuickFillValue.Text, out double value))
            {
                TaskDialog.Show("Cleanroom Validator", "Please enter a valid number in the quick fill box.");
                return;
            }

            var selected = PressureGrid.SelectedItems.Cast<PressureRowViewModel>().ToList();
            if (!selected.Any())
            {
                TaskDialog.Show("Cleanroom Validator", "Select one or more rows first, then click Apply to All Selected.");
                return;
            }

            foreach (var row in selected)
                row.Pressure = value;

            PressureGrid.Items.Refresh();
        }
    }

    public class PressureRowViewModel : INotifyPropertyChanged
    {
        public ElementId ElementId { get; set; }
        public string ElementType { get; set; }
        public string Number { get; set; }
        public string Name { get; set; }
        public string Level { get; set; }
        public string CleanlinessClass { get; set; }

        private double _pressure;
        public double Pressure
        {
            get => _pressure;
            set { _pressure = value; OnPropertyChanged(nameof(Pressure)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
