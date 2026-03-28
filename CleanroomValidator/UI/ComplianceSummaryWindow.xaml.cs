using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Models;
using CleanroomValidator.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CleanroomValidator.UI
{
    public partial class ComplianceSummaryWindow : Window
    {
        private readonly Document _doc;
        private readonly List<Space> _spaces;
        private readonly List<(Room room, Document sourceDoc, string sourceName)> _linkedRooms;
        private List<Document> _linkedDocuments;
        private Document _selectedLinkedDoc;
        private List<SpaceComplianceResult> _results;
        private ICollectionView _collectionView;

        public ComplianceSummaryWindow(Document doc, List<Space> spaces, 
            List<(Room room, Document sourceDoc, string sourceName)> linkedRooms = null)
        {
            InitializeComponent();
            _doc = doc;
            _spaces = spaces;
            _linkedRooms = linkedRooms ?? new List<(Room, Document, string)>();

            LoadLinkedDocuments();
            RunComplianceCheck();
        }

        // Legacy constructor for rooms only
        public ComplianceSummaryWindow(Document doc, List<Room> rooms) 
            : this(doc, new List<Space>(), new List<(Room, Document, string)>())
        {
            // Convert rooms to linked room format for display
            foreach (var room in rooms)
            {
                _linkedRooms.Add((room, doc, "Local"));
            }
            RunComplianceCheck();
        }

        private void LoadLinkedDocuments()
        {
            _linkedDocuments = RoomDataExtractor.GetLinkedDocuments(_doc);

            LinkedModelComboBox.Items.Clear();
            LinkedModelComboBox.Items.Add("(None)");

            foreach (var linkedDoc in _linkedDocuments)
            {
                LinkedModelComboBox.Items.Add(linkedDoc.Title);
            }

            LinkedModelComboBox.SelectedIndex = 0;
        }

        private void RunComplianceCheck()
        {
            _results = new List<SpaceComplianceResult>();

            var achService = new AchCalculationService(_doc);
            var paramService = new ParameterService();

            // Process spaces
            foreach (var space in _spaces)
            {
                var achResult = achService.CalculateAch(space);
                var cleanlinessClass = paramService.GetCleanlinessClass(space);
                var classInfo = CleanlinessClass.Parse(cleanlinessClass);

                _results.Add(new SpaceComplianceResult
                {
                    SpaceId = space.Id,
                    RoomName = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                    RoomNumber = space.Number ?? "-",
                    Level = space.Level?.Name ?? "No Level",
                    Source = "Local",
                    CleanlinessClass = cleanlinessClass,
                    VolumeCubicFeet = achResult.Volume,
                    ActualSupplyCfm = achResult.SupplyAirflowCfm,
                    ActualAch = achResult.AchCalculated,
                    RequiredAch = achResult.AchRequired,
                    AchMeetsRequirement = achResult.MeetsRequirement,
                    RecoveryTimeMinutes = achResult.RecoveryTimeMinutes,
                    ActualPressure = GetSpacePressure(space),
                    OverallStatus = DetermineOverallStatus(achResult, classInfo),
                    CheckResults = BuildCheckResults(achResult, classInfo),
                    HasVolumeWarning = achResult.HasVolumeWarning,
                    Notes = achResult.Notes
                });
            }

            // Process linked rooms
            foreach (var (room, sourceDoc, sourceName) in _linkedRooms)
            {
                var achResult = achService.CalculateAchForRoom(room);
                var cleanlinessClass = paramService.GetCleanlinessClass(room);
                var classInfo = CleanlinessClass.Parse(cleanlinessClass);

                _results.Add(new SpaceComplianceResult
                {
                    SpaceId = room.Id,
                    RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                    RoomNumber = room.Number ?? "-",
                    Level = room.Level?.Name ?? "No Level",
                    Source = sourceName.Length > 10 ? sourceName.Substring(0, 10) + "..." : sourceName,
                    CleanlinessClass = cleanlinessClass,
                    VolumeCubicFeet = achResult.Volume,
                    ActualSupplyCfm = achResult.SupplyAirflowCfm,
                    ActualAch = achResult.AchCalculated,
                    RequiredAch = achResult.AchRequired,
                    AchMeetsRequirement = achResult.MeetsRequirement,
                    RecoveryTimeMinutes = achResult.RecoveryTimeMinutes,
                    ActualPressure = 0, // Can't get pressure from linked rooms easily
                    OverallStatus = DetermineOverallStatus(achResult, classInfo),
                    CheckResults = BuildCheckResults(achResult, classInfo),
                    HasVolumeWarning = achResult.HasVolumeWarning,
                    Notes = achResult.Notes
                });
            }

            // Sort and group by level
            _results = _results.OrderBy(r => r.Level).ThenBy(r => r.RoomNumber).ToList();
            
            ResultsGrid.ItemsSource = _results;
            
            // Setup grouping
            _collectionView = CollectionViewSource.GetDefaultView(_results);
            _collectionView.GroupDescriptions.Clear();
            _collectionView.GroupDescriptions.Add(new PropertyGroupDescription("Level"));

            UpdateSummary();

            if (_results.Any())
            {
                ResultsGrid.SelectedIndex = 0;
            }
        }

        private double GetSpacePressure(Space space)
        {
            var pressureParam = space.LookupParameter("Design_Pressure")
                                ?? space.LookupParameter("Room_Pressure")
                                ?? space.LookupParameter("Pressure");

            return pressureParam?.AsDouble() ?? 0;
        }

        private ComplianceStatus DetermineOverallStatus(AchCalculationService.AchResult achResult, CleanlinessClass classInfo)
        {
            if (classInfo.Grade == CleanlinessGrade.Unclassified)
                return ComplianceStatus.Compliant;

            if (achResult.HasVolumeWarning)
                return ComplianceStatus.NonCompliant;

            // No airflow data means we can't verify compliance - treat as fail
            if (!achResult.HasAirflowData)
                return ComplianceStatus.NonCompliant;

            return achResult.MeetsRequirement ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant;
        }

        private List<Models.ComplianceCheckResult> BuildCheckResults(AchCalculationService.AchResult achResult, CleanlinessClass classInfo)
        {
            var results = new List<Models.ComplianceCheckResult>();
            var requirements = Data.StandardsDatabase.GetRequirements(classInfo.Grade);

            // ACH Check
            results.Add(new Models.ComplianceCheckResult
            {
                CheckName = "Air Changes per Hour (ACH)",
                Required = $"≥ {requirements.MinAch} ACH",
                Actual = achResult.HasAirflowData ? $"{achResult.AchCalculated:F1} ACH" : "No data",
                Status = achResult.MeetsRequirement ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant
            });

            // Recovery Time Check
            if (requirements.RecoveryTimeMinutes > 0)
            {
                bool recoveryOk = achResult.RecoveryTimeMinutes <= requirements.RecoveryTimeMinutes;
                results.Add(new Models.ComplianceCheckResult
                {
                    CheckName = "Recovery Time (100:1)",
                    Required = $"≤ {requirements.RecoveryTimeMinutes} min",
                    Actual = achResult.HasAirflowData ? $"{achResult.RecoveryTimeMinutes:F1} min" : "No data",
                    Status = recoveryOk ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant
                });
            }

            // Volume Check
            results.Add(new Models.ComplianceCheckResult
            {
                CheckName = "Volume",
                Required = "> 0 CF",
                Actual = $"{achResult.Volume:F0} CF",
                Status = achResult.HasVolumeWarning ? ComplianceStatus.NonCompliant : ComplianceStatus.Compliant
            });

            // Supply Airflow Info - if not modeled, it's a fail
            if (achResult.AchRequired > 0 && !achResult.HasAirflowData)
            {
                double requiredCfm = (achResult.AchRequired * achResult.Volume) / 60.0;
                results.Add(new Models.ComplianceCheckResult
                {
                    CheckName = "Required Supply Airflow",
                    Required = $"{requiredCfm:F0} CFM",
                    Actual = "Not modeled",
                    Status = ComplianceStatus.NonCompliant
                });
            }

            return results;
        }

        private void UpdateSummary()
        {
            var compliant = _results.Count(r => r.OverallStatus == ComplianceStatus.Compliant);
            var partial = _results.Count(r => r.OverallStatus == ComplianceStatus.PartialCompliance);
            var fail = _results.Count(r => r.OverallStatus == ComplianceStatus.NonCompliant);

            CompliantCount.Text = $"✓ {compliant} Compliant";
            PartialCount.Text = $"⚠ {partial} Partial";
            FailCount.Text = $"✗ {fail} Fail";
        }

        private void UseLinkedModelCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            LinkedModelComboBox.IsEnabled = UseLinkedModelCheckbox.IsChecked == true;

            if (UseLinkedModelCheckbox.IsChecked != true)
            {
                LinkedModelComboBox.SelectedIndex = 0;
                _selectedLinkedDoc = null;
                RunComplianceCheck();
            }
        }

        private void LinkedModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LinkedModelComboBox.SelectedIndex <= 0)
            {
                _selectedLinkedDoc = null;
            }
            else
            {
                var selectedTitle = LinkedModelComboBox.SelectedItem as string;
                _selectedLinkedDoc = _linkedDocuments.FirstOrDefault(d => d.Title == selectedTitle);
            }

            if (UseLinkedModelCheckbox.IsChecked == true)
            {
                RunComplianceCheck();
            }
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is SpaceComplianceResult result)
            {
                DetailsGrid.ItemsSource = result.CheckResults;
            }
            else
            {
                DetailsGrid.ItemsSource = null;
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                DefaultExt = ".csv",
                FileName = $"CleanroomCompliance_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                ExportToCsv(dialog.FileName);
            }
        }

        private void ExportToCsv(string filePath)
        {
            var sb = new StringBuilder();

            // Header
            sb.AppendLine("Level,Space,Number,Source,Class,Volume (CF),Supply CFM,ACH,ACH Required,Recovery Time (min),Pressure (Pa),Status,Notes");

            // Data rows
            foreach (var result in _results)
            {
                sb.AppendLine($"\"{result.Level}\",\"{result.RoomName}\",\"{result.RoomNumber}\",\"{result.Source}\",{result.CleanlinessClass},{result.VolumeCubicFeet:F0},{result.ActualSupplyCfm:F0},{result.ActualAch:F1},{result.RequiredAch},{result.RecoveryTimeMinutes:F1},{result.ActualPressure:F1},{result.OverallStatus},\"{result.Notes}\"");
            }

            // Blank line and details
            sb.AppendLine();
            sb.AppendLine("DETAILED CHECKS");
            sb.AppendLine("Space,Check,Required,Actual,Status");

            foreach (var result in _results)
            {
                foreach (var check in result.CheckResults)
                {
                    sb.AppendLine($"\"{result.RoomName}\",{check.CheckName},{check.Required},{check.Actual},{check.Status}");
                }
            }

            try
            {
                File.WriteAllText(filePath, sb.ToString());
                MessageBox.Show($"Exported to {filePath}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RunComplianceCheck();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>
    /// Result model for space compliance
    /// </summary>
    public class SpaceComplianceResult
    {
        public ElementId SpaceId { get; set; }
        public string RoomName { get; set; }
        public string RoomNumber { get; set; }
        public string Level { get; set; }
        public string Source { get; set; }
        public string CleanlinessClass { get; set; }
        public double VolumeCubicFeet { get; set; }
        public double ActualSupplyCfm { get; set; }
        public double ActualAch { get; set; }
        public int RequiredAch { get; set; }
        public bool AchMeetsRequirement { get; set; }
        public double RecoveryTimeMinutes { get; set; }
        public double ActualPressure { get; set; }
        public ComplianceStatus OverallStatus { get; set; }
        public List<Models.ComplianceCheckResult> CheckResults { get; set; }
        public bool HasVolumeWarning { get; set; }
        public string Notes { get; set; }

        public string StatusIcon => OverallStatus switch
        {
            ComplianceStatus.Compliant => "✓",
            ComplianceStatus.PartialCompliance => "⚠",
            ComplianceStatus.NonCompliant => "✗",
            _ => "?"
        };
    }
}
