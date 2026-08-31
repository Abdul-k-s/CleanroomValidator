using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Data;
using CleanroomValidator.Models;
using CleanroomValidator.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfPoint      = System.Windows.Point;
using WpfPath       = System.Windows.Shapes.Path;
using WpfColor      = System.Windows.Media.Color;
using WpfVisibility = System.Windows.Visibility;

namespace CleanroomValidator.UI
{
    /// <summary>
    /// Wraps a linked Revit Document for binding to the checkable ComboBox.
    /// IsChecked is two-way bound so checking an item in the dropdown
    /// immediately updates the selection list.
    /// </summary>
    public class LinkedDocItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        public Document Document { get; }
        public string   Title    => Document.Title;

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
        }

        public LinkedDocItem(Document doc) => Document = doc;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class ComplianceSummaryWindow : Window
    {
        private readonly Document _doc;
        private readonly List<(Room room, Document sourceDoc, string sourceName)> _linkedRooms;
        private List<LinkedDocItem> _linkedDocItems = new();
        private ObservableCollection<SpaceComplianceResult> _results;
        private ICollectionView _collectionView;
        private bool _useSI = true;
        
        // Cache for all doors in the active host document to avoid slow FilteredElementCollector calls in loops
        private List<FamilyInstance> _cachedHostDoors;
        
        // Cache for doors in currently selected linked documents to avoid re-querying
        private List<(FamilyInstance door, Autodesk.Revit.DB.Transform transform, string sourceName)> _cachedLinkedDoors;

        // Fallback leakage area (m²) when doors cannot be resolved from model geometry.
        // Controlled by LeakageClassComboBox; default = Typical cleanroom.
        private double _leakageFallbackArea = 0.008;

        // Unit conversion constants
        // Revit internal: ft³/s for airflow, ft³ for volume
        private const double Ft3s_to_M3h  = 101.9406477; // ft³/s → m³/h
        private const double Ft3s_to_CFM  = 60.0;         // ft³/s → ft³/min
        private const double Ft3_to_M3    = 0.0283168;    // ft³  → m³
        private const double Pa_to_InWG   = 0.00401463;   // Pa   → inWG

        // Door gap physics (orifice model)
        private const double Cd  = 0.65;
        private const double Rho = 1.2;   // kg/m³

        public ComplianceSummaryWindow(Document doc)
        {
            InitializeComponent();
            _doc         = doc;
            _linkedRooms = new List<(Room, Document, string)>();
            LoadLinkedDocuments();
            RunComplianceCheck();
        }

        // ── Data loading ─────────────────────────────────────────────────────

        private void LoadLinkedDocuments()
        {
            var docs = RoomDataExtractor.GetLinkedDocuments(_doc);
            // Links are not checked by default
            _linkedDocItems = docs.Select(d => new LinkedDocItem(d) { IsChecked = false }).ToList();
            LinkedModelItemsControl.ItemsSource = _linkedDocItems;
            LinkedModelToggle.IsEnabled = _linkedDocItems.Any();

            // Set placeholder text when no linked docs are found
            if (!_linkedDocItems.Any())
                LinkedModelToggle.Content = "(No linked models) ▼";
            else
                LinkedModelToggle.Content = "Select Linked Models ▼";
        }

        private void RunComplianceCheck()
        {
            _results = new ObservableCollection<SpaceComplianceResult>();

            var achService   = new AchCalculationService(_doc);
            var paramService = new ParameterService();
            
            // Build door caches before iterating over spaces
            RefreshDoorCaches();

            // Always load ALL placed spaces directly from the document — never rely on
            // whatever filtered list CheckComplianceCommand passed in at construction,
            // which may exclude unclassified spaces depending on the project's command version.
            var allSpaces = new FilteredElementCollector(_doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .ToList();

            // Build effective linked room list from ticked items in the ComboBox
            var effectiveLinkedRooms = new List<(Room room, Document sourceDoc, string sourceName)>(_linkedRooms);
            foreach (var item in _linkedDocItems.Where(i => i.IsChecked))
            {
                if (effectiveLinkedRooms.Any(r => r.sourceDoc?.Title == item.Title))
                    continue;
                var rooms = new FilteredElementCollector(item.Document)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .OfType<Room>()
                    .Where(r => r.Area > 0)
                    .ToList();
                foreach (var room in rooms)
                    effectiveLinkedRooms.Add((room, item.Document, item.Title));
            }

            foreach (var space in allSpaces)
            {
                var achResult   = achService.CalculateAch(space);
                var cls         = CleanlinessClass.Parse(paramService.GetCleanlinessClass(space));
                var req         = StandardsDatabase.GetRequirements(cls);

                bool isUnclassified = cls.Grade == CleanlinessGrade.Unclassified;
                double achTargetOverride = isUnclassified ? paramService.GetAchTarget(space) : 0;

                // Read flows from actual terminals (bypasses Revit's supply-mirror artifact)
                GetTerminalFlows(space.Id, out double supTermFts, out double retTermFts, out double exhTermFts);
                double supplyFts = supTermFts > 0 ? supTermFts : achResult.SupplyAirflowCfm / 60.0;

                double returnFts  = 0;
                double exhaustFts = 0;
                if (retTermFts > 0 || exhTermFts > 0)
                {
                    returnFts  = retTermFts;
                    exhaustFts = exhTermFts;
                }
                else
                {
                    double rawReturn  = GetParam(space, BuiltInParameter.ROOM_ACTUAL_RETURN_AIRFLOW_PARAM)
                                    ?? GetParam(space, BuiltInParameter.ROOM_DESIGN_RETURN_AIRFLOW_PARAM)
                                    ?? 0;
                    double rawExhaust = GetParam(space, BuiltInParameter.ROOM_ACTUAL_EXHAUST_AIRFLOW_PARAM)
                                    ?? GetParam(space, BuiltInParameter.ROOM_DESIGN_EXHAUST_AIRFLOW_PARAM)
                                    ?? 0;
                    returnFts  = Math.Abs(rawReturn - supplyFts) < 0.01 ? 0 : rawReturn;
                    exhaustFts = rawExhaust;
                }

                double leakArea = EstimateLeakageArea(space.Id);

                // Compute pressure from the orifice model using the actual terminal flows.
                // This is always fresh — we never read the stale Room_Pressure param on load,
                // which was written by old code with a different (often wrong) leakage area.
                // ΔP = ρ/2 × (Q_net / (Cd × A))²,  signed by net flow direction.
                double netFt3s  = supplyFts - returnFts - exhaustFts;
                double netM3s   = netFt3s * 0.0283168;
                double pressure = 0;
                if (leakArea > 0 && Math.Abs(netM3s) > 1e-6)
                {
                    double velocity = netM3s / (Cd * leakArea);
                    pressure = Math.Sign(netM3s) * (Rho / 2.0) * velocity * velocity;
                }

                var row = new SpaceComplianceResult
                {
                    SpaceId            = space.Id,
                    IsSpace            = true,
                    RoomName           = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                    RoomNumber         = space.Number ?? "-",
                    Level              = space.Level?.Name ?? "No Level",
                    Source             = "Local",
                    CleanlinessClass   = cls.ToString(),
                    IsUnclassified     = isUnclassified,
                    VolumeFt3          = achResult.Volume,
                    SupplyFt3s         = supplyFts,
                    ReturnFt3s         = returnFts,
                    ExhaustFt3s        = exhaustFts,
                    MinAch             = isUnclassified ? (int)achTargetOverride : req.MinAch,
                    MaxAch             = isUnclassified ? (int)achTargetOverride * 2 : req.MaxAch,
                    RecoveryTimeMinutes= achResult.RecoveryTimeMinutes,
                    PressurePa         = pressure,
                    LeakageArea_m2     = leakArea,
                    HasVolumeWarning   = achResult.HasVolumeWarning,
                    Notes              = achResult.Notes,
                };

                // Pre-fill ACH target from the persisted parameter for unclassified spaces
                if (isUnclassified && achTargetOverride > 0)
                    row.AchTargetEdit = achTargetOverride.ToString("F1");

                row.RefreshStatus();
                row.RefreshCheckResults(_useSI);
                _results.Add(row);
            }

            foreach (var (room, sourceDoc, sourceName) in effectiveLinkedRooms)
            {
                var achResult = achService.CalculateAchForRoom(room);
                var cls       = CleanlinessClass.Parse(paramService.GetCleanlinessClass(room));
                var req       = StandardsDatabase.GetRequirements(cls);
                GetTerminalFlows(room.Id, out double rSupFts, out double rRetFts, out double rExhFts);
                double supplyFts  = rSupFts > 0 ? rSupFts : achResult.SupplyAirflowCfm / 60.0;
                double returnFts  = rRetFts;
                double exhaustFts = rExhFts;
                double leakAreaR  = EstimateLeakageArea(room.Id);
                double netFt3sR   = supplyFts - returnFts - exhaustFts;
                double netM3sR    = netFt3sR * 0.0283168;
                double pressureR  = 0;
                if (leakAreaR > 0 && Math.Abs(netM3sR) > 1e-6)
                {
                    double vel = netM3sR / (Cd * leakAreaR);
                    pressureR  = Math.Sign(netM3sR) * (Rho / 2.0) * vel * vel;
                }

                var row = new SpaceComplianceResult
                {
                    SpaceId            = room.Id,
                    IsSpace            = false,
                    RoomName           = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                    RoomNumber         = room.Number ?? "-",
                    Level              = room.Level?.Name ?? "No Level",
                    Source             = sourceName.Length > 10 ? sourceName[..10] + "…" : sourceName,
                    CleanlinessClass   = cls.ToString(),
                    VolumeFt3          = achResult.Volume,
                    SupplyFt3s         = supplyFts,
                    ReturnFt3s         = returnFts,
                    ExhaustFt3s        = exhaustFts,
                    MinAch             = req.MinAch,
                    MaxAch             = req.MaxAch,
                    RecoveryTimeMinutes= achResult.RecoveryTimeMinutes,
                    PressurePa         = pressureR,
                    LeakageArea_m2     = leakAreaR,
                    HasVolumeWarning   = achResult.HasVolumeWarning,
                    Notes              = achResult.Notes,
                };
                row.RefreshStatus();
                row.RefreshCheckResults(_useSI);
                _results.Add(row);
            }

            _results = new ObservableCollection<SpaceComplianceResult>(
                _results.OrderBy(r => r.Level == "No Level" ? "ZZZ" : r.Level)
                        .ThenBy(r => r.RoomNumber));

            ResultsGrid.ItemsSource = _results;
            _collectionView = CollectionViewSource.GetDefaultView(_results);
            _collectionView.GroupDescriptions.Clear();
            // Only group by Level if at least two distinct non-empty levels exist
            var distinctLevels = _results.Select(r => r.Level).Distinct().Where(l => l != "No Level").ToList();
            if (distinctLevels.Count > 1)
                _collectionView.GroupDescriptions.Add(new PropertyGroupDescription("Level"));

            ApplyUnitLabels();
            UpdateSummary();

            if (_results.Any())
                ResultsGrid.SelectedIndex = 0;

            // Mark the network as needing a rebuild. We don't call BuildPressureNetwork()
            // here because the Pressure Network tab may not be in the visual tree yet
            // (WPF defers layout of unselected tab content). The actual build runs the
            // first time the tab is selected, via MainTabControl_SelectionChanged.
            _networkNeedsRebuild = true;
        }

        private void WriteAchComputedToModel()
        {
            var paramService = new ParameterService();
            try
            {
                using (var trans = new Transaction(_doc, "Cleanroom HVAC Designer — Update ACH_Computed"))
                {
                    trans.Start();
                    foreach (var row in _results)
                    {
                        // ACH_Computed only applies to MEP Spaces (not linked architectural rooms)
                        if (_doc.GetElement(row.SpaceId) is Space space)
                        {
                            // supply_ft3s × 3600 / volume_ft3 — all Revit internal units (ft³/s, ft³)
                            double ach = row.VolumeFt3 > 0
                                ? row.SupplyFt3s * 3600.0 / row.VolumeFt3
                                : 0;
                            paramService.SetAchComputed(space, ach);
                        }
                    }
                    trans.Commit();
                }
            }
            catch { /* non-fatal — grid is already populated */ }
        }

        // ── Unit toggle ───────────────────────────────────────────────────────

        private void UnitToggle_Changed(object sender, RoutedEventArgs e)
        {
            _useSI = RadioSI.IsChecked == true;
            ApplyUnitLabels();
            foreach (var r in _results ?? new ObservableCollection<SpaceComplianceResult>())
            {
                r.UseSI = _useSI;
                r.RefreshDisplayProperties();
                r.RefreshCheckResults(_useSI);
            }
        }

        private void ApplyUnitLabels()
        {
            if (ColVolume   == null) return;
            if (_useSI)
            {
                ColVolume.Header   = "Volume (m³)";
                ColCfm.Header      = "Supply (m³/h)";
                ColReturn.Header   = "Return (m³/h)";
                ColExhaust.Header  = "Exhaust (m³/h)";
                ColPressure.Header = "Pressure (Pa)";
            }
            else
            {
                ColVolume.Header   = "Volume (CF)";
                ColCfm.Header      = "Supply (CFM)";
                ColReturn.Header   = "Return (CFM)";
                ColExhaust.Header  = "Exhaust (CFM)";
                ColPressure.Header = "Pressure (inWG)";
            }
        }

        // ── Editable pressure → recalculate ──────────────────────────────────
        // (recalculation is now driven by the PressureEditPa setter in SpaceComplianceResult)

        private void ResultsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row.Item is not SpaceComplianceResult row) return;

            string newText = null;
            if (e.EditingElement is TextBox tb) newText = tb.Text.Trim();
            if (newText == null) return;

            // Apply edit to all selected rows so a multi-select edit works in one step
            var targets = _results
                .Where(r => r.IsSelected || r == row)
                .ToList();

            bool anyChanges = false;

            foreach (var r in targets)
            {
                bool valueChanged = false;
                if (!double.TryParse(newText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedVal) &&
                    !double.TryParse(newText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out parsedVal))
                {
                    continue; // invalid number entered
                }

                if (e.Column == ColAch)
                {
                    valueChanged = true;
                }
                else if (e.Column == ColPressure)
                {
                    valueChanged = true;
                }
                else if (e.Column == ColCfm)
                {
                    double oldSupply = Math.Round(_useSI ? r.SupplyFt3s * Ft3s_to_M3h : r.SupplyFt3s * 60.0, 0);
                    if (Math.Abs(Math.Round(parsedVal, 0) - oldSupply) > 0.5) valueChanged = true;
                }
                else if (e.Column == ColReturn)
                {
                    double oldRet = Math.Round(_useSI ? r.ReturnFt3s * Ft3s_to_M3h : r.ReturnFt3s * 60.0, 0);
                    if (Math.Abs(Math.Round(parsedVal, 0) - oldRet) > 0.5) valueChanged = true;
                }
                else if (e.Column == ColExhaust)
                {
                    double oldExh = Math.Round(_useSI ? r.ExhaustFt3s * Ft3s_to_M3h : r.ExhaustFt3s * 60.0, 0);
                    if (Math.Abs(Math.Round(parsedVal, 0) - oldExh) > 0.5) valueChanged = true;
                }

                if (!valueChanged) continue; // No change made

                if (e.Column == ColCfm || e.Column == ColReturn || e.Column == ColExhaust)
                {
                    var sysType = e.Column == ColCfm ? DuctSystemType.SupplyAir :
                                  e.Column == ColReturn ? DuctSystemType.ReturnAir :
                                  DuctSystemType.ExhaustAir;
                                  
                    var element = _doc.GetElement(r.SpaceId);
                    if (element != null && CheckTerminalsExist(element, sysType) == 0)
                    {
                        string tName = sysType == DuctSystemType.SupplyAir ? "supply" : sysType == DuctSystemType.ReturnAir ? "return" : "exhaust";
                        MessageBox.Show($"Space '{r.RoomName}' has no {tName} air terminals to apply this value to. Please place a terminal first.",
                                        "No Terminals Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                        
                        r.RefreshDisplayProperties();
                        continue;
                    }
                }

                anyChanges = true;

                if      (e.Column == ColAch)     { r.AchTargetEdit      = newText; r.RecalculateCombined(_useSI, Ft3s_to_M3h, Ft3_to_M3, Pa_to_InWG, Cd, Rho); }
                else if (e.Column == ColPressure){ r.PressureEditPa     = newText; r.RecalculateCombined(_useSI, Ft3s_to_M3h, Ft3_to_M3, Pa_to_InWG, Cd, Rho); }
                else if (e.Column == ColCfm)     { r.SetSupplyRaw(newText);  r.RecalculateFromSupply(_useSI, Ft3s_to_M3h, Ft3_to_M3, Pa_to_InWG, Cd, Rho); }
                else if (e.Column == ColReturn)  { r.SetReturnRaw(newText);  r.RecalculateFromReturn(_useSI, Ft3s_to_M3h, Pa_to_InWG, Cd, Rho); }
                else if (e.Column == ColExhaust) { r.SetExhaustRaw(newText); r.RecalculateFromExhaust(_useSI, Ft3s_to_M3h, Pa_to_InWG, Cd, Rho); }

                r.RefreshStatus();
                r.RefreshCheckResults(_useSI);
                
                // Immediately write back if it's a direct airflow edit
                if (e.Column == ColCfm || e.Column == ColReturn || e.Column == ColExhaust)
                {
                    var element = _doc.GetElement(r.SpaceId);
                    if (element != null)
                    {
                        using (var trans = new Transaction(_doc, "Cleanroom HVAC Designer — Apply Airflow"))
                        {
                            trans.Start();
                            DistributeAirflowToTerminals(r, element);
                            trans.Commit();
                        }
                    }
                }
            }

            if (anyChanges)
            {
                UpdateSummary();
                if (ResultsGrid.SelectedItem == row)
                    DetailsGrid.ItemsSource = row.CheckResults;

                // Mark pressure network as stale so it re-renders the changed flow values/warnings
                _networkNeedsRebuild = true;
                
                // If the user happens to have the network canvas visible somehow during a cell edit, refresh it
                if (NetworkCanvas != null && NetworkCanvas.IsVisible)
                {
                    _networkNeedsRebuild = false;
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(() => { BuildPressureNetworkSafe(); }));
                }
            }
        }

        private void SelectAll_Checked(object sender, RoutedEventArgs e)
        {
            if (_results == null) return;
            foreach (var r in _results) r.IsSelected = true;
        }

        private void SelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_results == null) return;
            foreach (var r in _results) r.IsSelected = false;
        }

        // ── Summary & helpers ─────────────────────────────────────────────────

        private void UpdateSummary()
        {
            if (_results == null) return;
            // Summary counts only classified spaces — unclassified spaces have no standard to fail.
            var classified = _results.Where(r => !r.IsUnclassified).ToList();
            CompliantCount.Text = $"✓ {classified.Count(r => r.OverallStatus == ComplianceStatus.Compliant)} Compliant";
            PartialCount.Text   = $"⚠ {classified.Count(r => r.OverallStatus == ComplianceStatus.PartialCompliance)} Partial";
            FailCount.Text      = $"✗ {classified.Count(r => r.OverallStatus == ComplianceStatus.NonCompliant)} Fail";
        }

        private void HideUnclassified_Changed(object sender, RoutedEventArgs e)
        {
            // Re-run the combined filter logic in SearchBox_TextChanged so the
            // "cleanrooms only" checkbox and the search box always compose together
            // instead of one silently overwriting the other's filter.
            SearchBox_TextChanged(sender, null);
        }

        private void GetTerminalFlows(ElementId spaceId,
            out double supplyFt3s, out double returnFt3s, out double exhaustFt3s)
        {
            supplyFt3s = returnFt3s = exhaustFt3s = 0;
            try
            {
                var mechEquip = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>();

                var ductTerminals = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_DuctTerminal)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>();

                var terminals = mechEquip.Concat(ductTerminals)
                    .Where(t => IsTerminalInSpace(t, spaceId))
                    .ToList();

                // Sanity cap: 100 ft³/s ≈ 170,000 m³/h per terminal.
                // Revit sometimes stores stale/unconverted values in unconnected families
                // that produce wildly inflated readings — treat those as missing data.
                const double MaxReasonableFt3s = 100.0;

                foreach (var t in terminals)
                {
                    var flowParam = t.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM)
                                 ?? t.LookupParameter("Flow")
                                 ?? t.LookupParameter("Airflow");
                    if (flowParam == null || !flowParam.HasValue) continue;

                    double flow = flowParam.AsDouble(); // ft³/s
                    if (flow <= 0 || flow > MaxReasonableFt3s) continue;

                    if      (IsSystemType(t, DuctSystemType.SupplyAir))  supplyFt3s  += flow;
                    else if (IsSystemType(t, DuctSystemType.ReturnAir))  returnFt3s  += flow;
                    else if (IsSystemType(t, DuctSystemType.ExhaustAir)) exhaustFt3s += flow;
                    else
                    {
                        // Terminal has no classifiable system — treat as supply
                        // (visible overcount is safer than silently losing return data)
                        supplyFt3s += flow;
                    }
                }
            }
            catch { }
        }

        private void RefreshDoorCaches()
        {
            _cachedHostDoors = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            _cachedLinkedDoors = new List<(FamilyInstance, Autodesk.Revit.DB.Transform, string)>();

            var linkInstances = new FilteredElementCollector(_doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var item in _linkedDocItems.Where(i => i.IsChecked))
            {
                var linkInst = linkInstances.FirstOrDefault(l => l.GetLinkDocument()?.Title == item.Title);
                if (linkInst != null)
                {
                    var doors = new FilteredElementCollector(item.Document)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>();

                    foreach (var d in doors)
                        _cachedLinkedDoors.Add((d, linkInst.GetTotalTransform(), item.Title));
                }
            }
        }

        private (ElementId idA, ElementId idB) ResolveSpacesForDoor(FamilyInstance door, Autodesk.Revit.DB.Transform transform)
        {
            if (!(door.Location is LocationPoint lp)) return (null, null);

            // Get orientation and origin in host coordinates directly to handle mirrored/rotated links
            XYZ facing = transform != null ? transform.OfVector(door.FacingOrientation).Normalize() : door.FacingOrientation;
            XYZ hostLp = transform != null ? transform.OfPoint(lp.Point) : lp.Point;

            ElementId idA = null, idB = null;

            // Probe at multiple distances and Z offsets to handle varying door frame thicknesses
            // and MEP space boundary offsets.
            // We probe UP from the floor (dz > 0) since doors sit on the floor and spaces go up!
            foreach (double dist in new[] { 0.2, 0.5, 1.0, 1.5, 2.5, 4.0 })
            {
                foreach (double dz in new[] { 0.5, 1.0, 1.5, 2.5, 4.0 })
                {
                    // Point in host coordinates
                    var ptFront = hostLp + facing * dist + new XYZ(0, 0, dz);
                    var ptBack  = hostLp - facing * dist + new XYZ(0, 0, dz);

                    ElementId tempA = _doc.GetSpaceAtPoint(ptFront)?.Id ?? _doc.GetRoomAtPoint(ptFront)?.Id;
                    ElementId tempB = _doc.GetSpaceAtPoint(ptBack)?.Id  ?? _doc.GetRoomAtPoint(ptBack)?.Id;

                    // If both probes resolve to the exact same space, we are likely still inside 
                    // the wall/door frame thickness (where Revit assigns the volume to one of the rooms).
                    // We must ignore this and wait for the probe distance to increase.
                    if (tempA != null && tempB != null && tempA == tempB)
                    {
                        continue;
                    }

                    if (idA == null && tempA != null)
                    {
                        idA = tempA;
                    }
                    if (idB == null && tempB != null)
                    {
                        idB = tempB;
                    }

                    if (idA != null && idB != null) break;
                }
                if (idA != null && idB != null) break;
            }

            // Removed get_FromRoom() / get_ToRoom() fallback! 
            // Room centers are far away from the door and cause invalid long-distance connections.
            // If the local probes up to 4 feet away didn't hit a space, then the door
            // is legitimately an exterior door or not bordering a mapped space.

            return (idA, idB);
        }

        private double EstimateLeakageArea(ElementId spaceId)
        {
            try
            {
                var connectedDoors = new List<FamilyInstance>();

                // Use the cached host doors instead of re-collecting
                if (_cachedHostDoors != null)
                {
                    foreach (var door in _cachedHostDoors)
                    {
                        var (idA, idB) = ResolveSpacesForDoor(door, null);
                        if ((idA != null && idA == spaceId) || (idB != null && idB == spaceId))
                        {
                            connectedDoors.Add(door);
                        }
                    }
                }

                // Use the cached linked doors instead of re-collecting
                if (_cachedLinkedDoors != null)
                {
                    foreach (var (door, transform, _) in _cachedLinkedDoors)
                    {
                        var (idA, idB) = ResolveSpacesForDoor(door, transform);
                        if ((idA != null && idA == spaceId) || (idB != null && idB == spaceId))
                        {
                            connectedDoors.Add(door);
                        }
                    }
                }

                // Fallback: one standard 900 mm door gap (undercut + sides ≈ 69 cm²)
                if (!connectedDoors.Any()) return _leakageFallbackArea;

                double total = 0;
                foreach (var door in connectedDoors)
                {
                    double w = (door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()  ?? 0.9 / 0.3048) * 0.3048;
                    double h = (door.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 2.1 / 0.3048) * 0.3048;
                    total += w * 0.003 + 2 * h * 0.001; // 3 mm undercut + 1 mm sides
                }
                return total;
            }
            catch { return _leakageFallbackArea; }
        }

        private double? GetParam(Element e, BuiltInParameter p)
        {
            var param = e.get_Parameter(p);
            if (param == null || !param.HasValue) return null;
            double v = param.AsDouble();
            return v > 0 ? v : (double?)null;
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void UseLinkedModelCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = UseLinkedModelCheckbox.IsChecked == true;
            LinkedModelToggle.IsEnabled = enabled && _linkedDocItems.Any();

            // When enabled, default to checking all links to auto-include them
            // When disabled, uncheck all items
            foreach (var item in _linkedDocItems)
                item.IsChecked = enabled;

            RunComplianceCheck();
        }

        private void LinkedModelItem_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (UseLinkedModelCheckbox.IsChecked != true) return;
            RunComplianceCheck();
        }

        private void LeakageClass_Changed(object sender, SelectionChangedEventArgs e)
        {
            _leakageFallbackArea = LeakageClassComboBox.SelectedIndex switch
            {
                1 => 0.005,   // Very tight
                2 => 0.012,   // Ordinary door
                _ => 0.008    // Typical cleanroom (default, index 0)
            };

            // Re-run so all pressure values update immediately
            if (_results != null)
                RunComplianceCheck();
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is SpaceComplianceResult result)
                DetailsGrid.ItemsSource = result.CheckResults;
            else
                DetailsGrid.ItemsSource = null;
        }

        private void ResultsGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            // Auto-generated to fix XAML compilation error
        }

        private void ResultsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Left intentionally blank. By avoiding intercepting clicks, the default WPF
            // DataGrid interaction logic correctly establishes focus over columns set
            // to UpdateSourceTrigger=LostFocus.
        }

        private void SearchBox_TextChanged(object sender, RoutedEventArgs e)
        {
            if (_collectionView == null) return;
            string query = SearchBox.Text?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                // Reapply only the cleanrooms-only filter if it's active
                _collectionView.Filter = HideUnclassifiedCheckbox.IsChecked == true
                    ? (obj => obj is SpaceComplianceResult r && !r.IsUnclassified)
                    : null;
            }
            else
            {
                bool hideUnclassified = HideUnclassifiedCheckbox.IsChecked == true;
                _collectionView.Filter = obj =>
                {
                    if (obj is not SpaceComplianceResult r) return false;
                    if (hideUnclassified && r.IsUnclassified) return false;
                    return (r.RoomName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (r.RoomNumber?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                };
            }
            _collectionView.Refresh();
        }

        private void SizeAirflow_Click(object sender, RoutedEventArgs e)
        {
            Autodesk.Revit.UI.TaskDialog td = new Autodesk.Revit.UI.TaskDialog("Sizing Options")
            {
                MainInstruction = "Design & Size Terminals",
                MainContent = "Calculate required supply and return/exhaust airflow for classified spaces.\n\n" +
                              "For rooms without a manually typed ACH Target in the grid, which ACH requirement should be used for sizing?",
                CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Cancel
            };
            td.AddCommandLink(Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink1, "Highest (Maximum ACH)");
            td.AddCommandLink(Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink2, "Lowest (Minimum ACH)");
            td.AddCommandLink(Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink3, "Middle bound (Average of Min/Max)");

            var ans = td.Show();

            if (ans == Autodesk.Revit.UI.TaskDialogResult.Cancel || ans == Autodesk.Revit.UI.TaskDialogResult.Close) return;

            SizingTarget achChoice = SizingTarget.Minimum;
            if (ans == Autodesk.Revit.UI.TaskDialogResult.CommandLink1)
                achChoice = SizingTarget.Maximum;
            else if (ans == Autodesk.Revit.UI.TaskDialogResult.CommandLink3)
                achChoice = SizingTarget.Middle;

            // Build per-space ACH overrides from whatever the engineer has typed
            // into the ACH Target column. Rows where the user hasn't edited the
            // cell fall back to the standards-database minimum inside the sizer.
            var achOverrides = new Dictionary<ElementId, double>();
            foreach (var r in _results ?? new ObservableCollection<SpaceComplianceResult>())
            {
                if (r.HasUserAchTarget)
                {
                    achOverrides[r.SpaceId] = r.UserAchTargetValue;
                }
                else if (!r.IsUnclassified && r.MinAch > 0)
                {
                    // Provide a default derived from the chosen target strategy
                    double target = r.MinAch;
                    if (achChoice == SizingTarget.Middle && r.MaxAch > r.MinAch)
                    {
                        target = r.MinAch + (r.MaxAch - r.MinAch) / 2.0;
                    }
                    else if (achChoice == SizingTarget.Maximum && r.MaxAch > r.MinAch)
                    {
                        target = r.MaxAch;
                    }
                    achOverrides[r.SpaceId] = target;
                }
            }

            // Load all spaces directly — same as RunComplianceCheck, independent of command filter
            var allSpacesForSizing = new FilteredElementCollector(_doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .ToList();

            var linkedRoomsForSizing = new List<(Room room, Document sourceDoc, string sourceName)>(_linkedRooms);
            foreach (var item in _linkedDocItems.Where(i => i.IsChecked))
            {
                if (linkedRoomsForSizing.Any(r => r.sourceDoc?.Title == item.Title)) continue;
                new FilteredElementCollector(item.Document)
                    .OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>()
                    .Where(r => r.Area > 0)
                    .ToList()
                    .ForEach(r => linkedRoomsForSizing.Add((r, item.Document, item.Title)));
            }

            var sizer  = new AirflowSizerService(_doc);
            var report = sizer.SizeAll(allSpacesForSizing, linkedRoomsForSizing, achOverrides);

            if (!report.Success)
            {
                MessageBox.Show($"Sizing failed:\n{report.Error}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var warnings = report.Results
                .Where(r => r.Skipped || !string.IsNullOrEmpty(r.Warning))
                .Select(r => $"  • {r.SpaceNumber} {r.SpaceName}: {r.Warning}")
                .ToList();

            var massBalanceFails = report.Results
                .Where(r => !r.Skipped && !r.MassBalanceOk)
                .Select(r => $"  • {r.SpaceNumber} {r.SpaceName}: offset error {r.MassBalanceErrorFt3s * 1.699:F1} m³/h")
                .ToList();

            string msg =
                $"Sized {report.SpacesSized} space(s).\n" +
                $"Updated {report.TerminalsTotal} air terminal(s).\n";

            if (achOverrides.Count > 0)
                msg += $"Used {achOverrides.Count} engineer-specified ACH target(s) from the grid.\n";

            if (report.SpacesSkipped > 0)
                msg += $"Skipped {report.SpacesSkipped} unclassified space(s).\n";

            if (warnings.Any())
                msg += "\nWarnings:\n" + string.Join("\n", warnings);

            if (massBalanceFails.Any())
                msg += "\nMass balance check failed (Supply − Return/Exhaust ≠ expected leakage offset):\n"
                     + string.Join("\n", massBalanceFails);

            msg += "\n\nThe compliance grid will now refresh with the new values.";

            MessageBox.Show(msg, "Design & Size Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);

            RunComplianceCheck();
        }

        private void SavePressures_Click(object sender, RoutedEventArgs e)
        {
            var paramService = new ParameterService();
            int savedPressure = 0, savedTerminals = 0;

            using (var trans = new Transaction(_doc, "Cleanroom HVAC Designer — Apply to Model"))
            {
                trans.Start();
                try
                {
                    foreach (var row in _results)
                    {
                        var element = _doc.GetElement(row.SpaceId);

                        // 1 — Save Room_Pressure parameter (all spaces)
                        bool ok = false;
                        if (element is Space sp)
                        {
                            ok = paramService.SetRoomPressure(sp, row.PressurePa);

                            // For unclassified spaces, also persist the engineer-entered
                            // ACH target so it survives between sessions.
                            if (row.IsUnclassified && row.HasUserAchTarget)
                                paramService.SetAchTarget(sp, row.UserAchTargetValue);
                        }
                        else if (element is Room rm)
                        {
                            ok = paramService.SetRoomPressure(rm, row.PressurePa);
                            if (row.IsUnclassified && row.HasUserAchTarget)
                                paramService.SetAchTarget(rm, row.UserAchTargetValue);
                        }
                        if (ok) savedPressure++;

                        // 2 — Write back-calculated airflows to air terminals in the space
                        savedTerminals += DistributeAirflowToTerminals(row, element);
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    MessageBox.Show($"Save failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            string modeNote = "Supply, return/exhaust, and pressure values applied from grid.";

            MessageBox.Show(
                $"Saved pressure for {savedPressure} space(s).\n" +
                $"Updated airflow on {savedTerminals} air terminal(s).\n\n" +
                modeNote,
                "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private int CheckTerminalsExist(Element spaceElement, DuctSystemType targetType)
        {
            if (spaceElement == null) return 0;
            try
            {
                var mechEquip = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>();

                var ductTerminals = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_DuctTerminal)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>();

                var terminals = mechEquip.Concat(ductTerminals)
                    .Where(t => IsTerminalInSpace(t, spaceElement.Id));

                int count = 0;
                foreach (var t in terminals)
                {
                    if (IsSystemType(t, targetType))
                    {
                        count++;
                    }
                    else if (targetType == DuctSystemType.SupplyAir)
                    {
                        // Same fallback as GetTerminalFlows: if we're looking for supply,
                        // and it has no classifiable system (returns false for all three),
                        // count it as a supply terminal.
                        if (!IsSystemType(t, DuctSystemType.ReturnAir) && !IsSystemType(t, DuctSystemType.ExhaustAir))
                        {
                            count++;
                        }
                    }
                }
                return count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Distributes the row's supply/return/exhaust equally across the air terminals
        /// that belong to this space. Returns count of terminals updated.
        /// </summary>
        private int DistributeAirflowToTerminals(SpaceComplianceResult row, Element spaceElement)
        {
            int count = 0;
            if (spaceElement == null) return 0;

            try
            {
                // Find all air terminals whose space = this element
                // Run two collectors separately and merge (UnionWith needs a collector, not IDs)
                var mechEquip = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>();

                var ductTerminals = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_DuctTerminal)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>();

                var terminals = mechEquip.Concat(ductTerminals)
                    .Where(t => IsTerminalInSpace(t, spaceElement.Id))
                    .ToList();

                if (!terminals.Any()) return 0;

                // Separate by system type
                var supplyTerminals  = terminals.Where(t => IsSystemType(t, DuctSystemType.SupplyAir)).ToList();
                var returnTerminals  = terminals.Where(t => IsSystemType(t, DuctSystemType.ReturnAir)).ToList();
                var exhaustTerminals = terminals.Where(t => IsSystemType(t, DuctSystemType.ExhaustAir)).ToList();

                // If no system type assigned, fall back: assume all supply
                if (!supplyTerminals.Any() && !returnTerminals.Any() && !exhaustTerminals.Any())
                    supplyTerminals = terminals;

                // Supply is always fixed — never touched
                count += SetTerminalFlows(supplyTerminals, row.SupplyFt3s, "Supply");

                if (returnTerminals.Any())
                {
                    // Normal case: return grilles exist → adjust return to create net flow
                    // Exhaust stays fixed
                    count += SetTerminalFlows(returnTerminals,  row.ReturnFt3s,  "Return");
                    count += SetTerminalFlows(exhaustTerminals, row.ExhaustFt3s, "Exhaust");
                }
                else if (exhaustTerminals.Any())
                {
                    // 100% exhaust design (no return to AHU) → adjust exhaust instead
                    // ExhaustFt3s holds the current value from the grid
                    // but we need to recalculate it as: Exhaust = Supply − Q_leakage
                    // (ReturnFt3s is 0 in this case, so ExhaustFt3s is the correct residual)
                    count += SetTerminalFlows(exhaustTerminals, row.ExhaustFt3s, "Exhaust");
                }
            }
            catch { /* best-effort */ }

            return count;
        }

        private int SetTerminalFlows(List<FamilyInstance> terminals, double totalFt3s, string systemLabel = "")
        {
            if (!terminals.Any() || totalFt3s < 0) return 0;
            double perTerminal = totalFt3s / terminals.Count;
            int count = 0;

            foreach (var t in terminals)
            {
                var flowParam = t.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM)
                             ?? t.LookupParameter("Flow")
                             ?? t.LookupParameter("Airflow")
                             ?? t.LookupParameter("Air Flow");

                if (flowParam != null && !flowParam.IsReadOnly)
                {
                    flowParam.Set(perTerminal); // ft³/s — Revit internal unit
                    count++;
                }
            }

            return count;
        }

        private bool IsTerminalInSpace(FamilyInstance terminal, ElementId spaceId)
        {
            try
            {
                if (!(terminal.Location is LocationPoint lp)) return false;

                XYZ pt = lp.Point;

                // Probe at the terminal's own Z, then step down in 1-ft increments up to 3 ft.
                // Terminals mounted at ceiling level often sit above the Space boundary height
                // that GetSpaceAtPoint uses, so stepping down finds the space reliably.
                double[] zOffsets = { 0, -0.5, -1.0, -2.0, -3.0 };
                foreach (double dz in zOffsets)
                {
                    XYZ probe = dz == 0 ? pt : new XYZ(pt.X, pt.Y, pt.Z + dz);

                    // MEP Space first (fastest path for mechanical models)
                    var space = _doc.GetSpaceAtPoint(probe);
                    if (space?.Id == spaceId) return true;

                    // Architectural Room (for linked-room workflow)
                    var room = _doc.GetRoomAtPoint(probe);
                    if (room?.Id == spaceId) return true;
                }

                // Last resort: use the FamilyInstance.Space / Room property that
                // Revit sets when the terminal is placed inside a space or room.
                if (terminal.Space?.Id == spaceId) return true;
                if (terminal.Room?.Id  == spaceId) return true;
            }
            catch { }
            return false;
        }

        private bool IsSystemType(FamilyInstance terminal, DuctSystemType targetType)
        {
            try
            {
                // Primary: walk the connector's live duct system (most reliable when connected)
                var connectors = terminal.MEPModel?.ConnectorManager?.Connectors;
                if (connectors != null)
                {
                    foreach (Connector c in connectors)
                    {
                        if (c.Domain != Domain.DomainHvac) continue;

                        var sys = c.AllRefs?.Cast<Connector>()
                            .Select(r => r.Owner)
                            .OfType<MEPSystem>()
                            .FirstOrDefault();
                        if (sys is MechanicalSystem ms && ms.SystemType == targetType)
                            return true;
                    }
                }

                // Fallback A: RBS_SYSTEM_CLASSIFICATION_PARAM (set on unconnected terminals)
                var classParam = terminal.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM);
                if (classParam != null && classParam.HasValue)
                {
                    string cls = classParam.AsString() ?? "";
                    return targetType switch
                    {
                        DuctSystemType.SupplyAir  => cls.IndexOf("Supply",  StringComparison.OrdinalIgnoreCase) >= 0,
                        DuctSystemType.ReturnAir  => cls.IndexOf("Return",  StringComparison.OrdinalIgnoreCase) >= 0,
                        DuctSystemType.ExhaustAir => cls.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0,
                        _ => false
                    };
                }

            }
            catch { }
            return false;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RunComplianceCheck();
            if (NetworkCanvas != null && NetworkCanvas.IsVisible && _networkNeedsRebuild)
            {
                _networkNeedsRebuild = false;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => { BuildPressureNetworkSafe(); }));
            }
        }
        private void CloseButton_Click(object sender, RoutedEventArgs e)   => Close();

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter      = "CSV files (*.csv)|*.csv",
                DefaultExt  = ".csv",
                FileName    = $"CleanroomCompliance_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dialog.ShowDialog() == true) ExportToCsv(dialog.FileName);
        }

        private void ExportToCsv(string filePath)
        {
            string unit = _useSI ? "SI" : "Imperial";
            var sb = new StringBuilder();
            sb.AppendLine($"# Unit system: {unit}");
            sb.AppendLine($"Level,Space,Number,Source,Class,{ColVolume.Header},{ColCfm.Header},{ColReturn.Header},{ColExhaust.Header},ACH,ACH Range,Recovery (min),{ColPressure.Header},Status,Notes");

            foreach (var r in _results)
                sb.AppendLine($"\"{r.Level}\",\"{r.RoomName}\",\"{r.RoomNumber}\",\"{r.Source}\",{r.CleanlinessClass}," +
                              $"{r.VolumeDisplay:F1},{r.SupplyDisplay:F0},{r.ReturnDisplay:F0},{r.ExhaustDisplay:F0}," +
                              $"{r.ActualAch:F1},{r.RequiredAchRange},{r.RecoveryTimeMinutes:F1}," +
                              $"{r.PressureDisplay:F2},{r.OverallStatus},\"{r.Notes}\"");

            sb.AppendLine();
            sb.AppendLine("DETAILED CHECKS");
            sb.AppendLine("Space,Check,Required,Actual,Status");
            foreach (var r in _results)
                foreach (var c in r.CheckResults ?? new List<Models.ComplianceCheckResult>())
                    sb.AppendLine($"\"{r.RoomName}\",{c.CheckName},{c.Required},{c.Actual},{c.Status}");

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

        // ══════════════════════════════════════════════════════════════════
        // PRESSURE NETWORK — Phase 1
        //
        // Visualizes spaces as nodes and doors as edges on a pannable/zoomable
        // canvas. Reuses the door-adjacency map already built by
        // PressureCalculationService (same door detection, same phase-probing
        // fix). Arrow direction and labeled flow use the room-level net
        // leakage flow already computed in RunComplianceCheck (one arrow per
        // room pair — Phase 2 would split this per-door for multi-door rooms).
        //
        // Note: HierarchyLevel on CleanlinessClass already interleaves GMP
        // and ISO standards into one ordering (GMP-B=1, ISO-6=2, GMP-C/ISO-7=3,
        // GMP-D/ISO-8=4), so cascade-direction comparisons work for both
        // standards, not just GMP — using HierarchyLevel rather than Grade
        // directly is what makes that possible.
        // ══════════════════════════════════════════════════════════════════

        private class NetworkNode
        {
            public ElementId   SpaceId;
            public SpaceComplianceResult Row;
            public double      X, Y;          // canvas position
            public Border      Visual;
        }

        private class NetworkEdge
        {
            public NetworkNode From, To;
            public double      NetFlowM3s;     // signed: positive = From→To
            public bool        IsCascadeViolation;
            public WpfPath     Visual;
            public Polygon     ArrowHead;
            public FrameworkElement Label;
        }

        private readonly List<NetworkNode> _networkNodes = new();
        private readonly List<NetworkEdge> _networkEdges = new();
        private bool _networkNeedsRebuild = true; // set after every compliance check run
        private NetworkNode _networkSelectedNode;

        // Pan/zoom state
        private bool   _networkIsPanning;
        private WpfPoint _networkPanStart;
        private double _networkPanStartX, _networkPanStartY;

        private void BuildPressureNetwork()
        {
            if (NetworkCanvas == null) return;

            NetworkCanvas.Children.Clear();
            _networkNodes.Clear();
            _networkEdges.Clear();

            try
            {
                // Build nodes from the grid rows — these already have SpaceId set to
                // the MEP Space ElementId (or architectural Room Id for linked rooms).
                var rowById = _results.ToDictionary(r => r.SpaceId, r => r);
                if (!rowById.Any())
                {
                    NetworkEmptyStateText.Text = "No spaces loaded. Run a compliance check first.";
                    NetworkEmptyStateText.Visibility = System.Windows.Visibility.Visible;
                NetworkEmptyStateText.Visibility = System.Windows.Visibility.Visible;
                    return;
                }

                // Build door adjacency using MEP Space IDs directly.
                // BuildDoorAdjacencyMap returns architectural Room IDs, which don't match
                // MEP Space IDs — so we probe doors ourselves using GetSpaceAtPoint so
                // the IDs in the adjacency map match SpaceId in _results rows exactly.
                
                List<FamilyInstance> allDoors = new List<FamilyInstance>();
                try
                {
                    allDoors = new FilteredElementCollector(_doc)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .ToList();
                }
                catch { }

                var linkInstances = new FilteredElementCollector(_doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                var linkedDoors = new List<(FamilyInstance door, Autodesk.Revit.DB.Transform transform, string sourceName)>();

                foreach (var linkInst in linkInstances)
                {
                    var linkType = _doc.GetElement(linkInst.GetTypeId()) as RevitLinkType;
                    bool isLoaded = linkType != null && RevitLinkType.IsLoaded(_doc, linkType.Id);
                    
                    var linkDoc = linkInst.GetLinkDocument();
                    if (!isLoaded || linkDoc == null) continue;

                    string linkDocTitle = linkDoc.Title;

                    try
                    {
                        var doors = new FilteredElementCollector(linkDoc)
                            .OfCategory(BuiltInCategory.OST_Doors)
                            .OfClass(typeof(FamilyInstance))
                            .Cast<FamilyInstance>()
                            .ToList();

                        foreach (var d in doors)
                            linkedDoors.Add((d, linkInst.GetTotalTransform(), linkDocTitle));
                    }
                    catch { }
                }

                // Build a combined lookup: ElementId → row, covering both MEP spaces
                // AND architectural rooms (for linked-model rows whose IsSpace=false).
                // This is the key fix — door probing may find either type depending on
                // what's placed in the model.

                // Consolidate duplicate edges using a dictionary keyed by the ordered pair of IDs
                var pairAreas = new Dictionary<(long, long), (ElementId a, ElementId b, double totalArea)>();

                // Helper to add/merge a door gap area into the pair map
                void AddDoorToPairs(ElementId a, ElementId b, double w, double h)
                {
                    long keyA = a.Value < b.Value ? a.Value : b.Value;
                    long keyB = a.Value < b.Value ? b.Value : a.Value;
                    var key = (keyA, keyB);
                    
                    double gapArea = w > 0 && h > 0
                        ? w * 0.003 + 2 * h * 0.001   // 3mm undercut + 1mm side gaps
                        : _leakageFallbackArea;
                        
                    if (pairAreas.TryGetValue(key, out var existing))
                    {
                        pairAreas[key] = (existing.a, existing.b, existing.totalArea + gapArea);
                    }
                    else
                    {
                        pairAreas[key] = (a, b, gapArea);
                    }
                }

                // Process host doors
                foreach (var door in allDoors)
                {
                    if (door.Location == null || !(door.Location is LocationPoint)) continue;

                    var (idA, idB) = ResolveSpacesForDoor(door, null);

                    if (idA != null && !rowById.ContainsKey(idA)) idA = null;
                    if (idB != null && !rowById.ContainsKey(idB)) idB = null;

                    if (idA == null || idB == null || idA == idB) continue;

                    double w = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()  * 0.3048 ?? 0.9;
                    double h = door.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() * 0.3048 ?? 2.1;
                    AddDoorToPairs(idA, idB, w, h);
                }

                // Process linked doors
                foreach (var (door, transform, sourceName) in linkedDoors)
                {
                    if (transform == null || door.Location == null || !(door.Location is LocationPoint)) continue;

                    var (idA, idB) = ResolveSpacesForDoor(door, transform);

                    if (idA != null && !rowById.ContainsKey(idA)) idA = null;
                    if (idB != null && !rowById.ContainsKey(idB)) idB = null;

                    if (idA == null || idB == null || idA == idB) continue;

                    double w = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()  * 0.3048 ?? 0.9;
                    double h = door.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() * 0.3048 ?? 2.1;
                    AddDoorToPairs(idA, idB, w, h);
                }

                if (!pairAreas.Any())
                {
                    NetworkEmptyStateText.Text =
                        "No door connections found.\n" +
                        "Showing spaces as isolated nodes.\n" +
                        "Doors must be hosted between walled spaces with MEP spaces on both sides.";
                    NetworkEmptyStateText.Visibility = WpfVisibility.Visible;
                }

                // Determine which spaces participate in the layout.
                // When doors are found, only connected spaces are shown.
                // When no doors found, show ALL rows as isolated nodes.
                var participatingIds = pairAreas.Any()
                    ? pairAreas.Values.SelectMany(p => new[] { p.a, p.b }).Distinct().ToList()
                    : _results.Select(r => r.SpaceId).ToList();

                // Build a simple adjacency lookup (ElementId -> connected ElementIds) for ordering
                var adjacency = new Dictionary<ElementId, List<ElementId>>();
                foreach (var p in pairAreas.Values)
                {
                    if (!adjacency.ContainsKey(p.a)) adjacency[p.a] = new List<ElementId>();
                    if (!adjacency.ContainsKey(p.b)) adjacency[p.b] = new List<ElementId>();
                    adjacency[p.a].Add(p.b);
                    adjacency[p.b].Add(p.a);
                }

                // ── Layout: BFS / Hierarchical Layered Graph Layout
                // Groups connected spaces together and naturally places branches beneath/beside parents.
                
                const double nodeW = 180, nodeH = 100, hGap = 40, vGap = 120;
                var placedIds = new HashSet<ElementId>();
                var levels = new List<List<ElementId>>();
                
                var availableIds = participatingIds.Where(id => rowById.ContainsKey(id)).ToList();
                
                while (availableIds.Any())
                {
                    // Pick a root: unclassified space, or highest pressure
                    var rootId = availableIds.OrderByDescending(id => rowById[id].IsUnclassified ? 1 : 0)
                                             .ThenByDescending(id => rowById[id].PressurePa)
                                             .First();
                    
                    var queue = new Queue<ElementId>();
                    queue.Enqueue(rootId);
                    placedIds.Add(rootId);
                    availableIds.Remove(rootId);
                    
                    var currentLevel = new List<ElementId> { rootId };
                    levels.Add(currentLevel);
                    
                    while (queue.Any())
                    {
                        int count = queue.Count;
                        var nextLevel = new List<ElementId>();
                        
                        for (int i = 0; i < count; i++)
                        {
                            var current = queue.Dequeue();
                            if (adjacency.TryGetValue(current, out var neighbors))
                            {
                                // Sort neighbors by pressure descending to visually represent cascade
                                var sortedNeighbors = neighbors.Where(n => rowById.ContainsKey(n))
                                                               .OrderByDescending(n => rowById[n].PressurePa)
                                                               .ToList();
                                foreach (var neighbor in sortedNeighbors)
                                {
                                    if (!placedIds.Contains(neighbor))
                                    {
                                        placedIds.Add(neighbor);
                                        availableIds.Remove(neighbor);
                                        queue.Enqueue(neighbor);
                                        nextLevel.Add(neighbor);
                                    }
                                }
                            }
                        }
                        if (nextLevel.Any())
                        {
                            levels.Add(nextLevel);
                        }
                    }
                }
                
                double curY = 50;
                var nodeById = new Dictionary<ElementId, NetworkNode>();
                
                // To auto-center the BFS tree without scaling issues, we align each level to a center point
                double maxLevelWidth = levels.Any() ? levels.Max(l => l.Count) * (nodeW + hGap) - hGap : 0;
                double centerX = maxLevelWidth / 2 + 50;

                foreach (var level in levels)
                {
                    double levelWidth = level.Count * (nodeW + hGap) - hGap;
                    double startX = centerX - (levelWidth / 2);
                    double curX = startX;
                    
                    foreach (var id in level)
                    {
                        var row = rowById[id];
                        var node = new NetworkNode { SpaceId = row.SpaceId, Row = row, X = curX, Y = curY };
                        nodeById[id] = node;
                        _networkNodes.Add(node);
                        curX += nodeW + hGap;
                    }
                    curY += nodeH + vGap;
                }

                // ── Draw edges first (so nodes render on top) ──
                foreach (var p in pairAreas.Values)
                {
                    if (!nodeById.TryGetValue(p.a, out var nodeA) || !nodeById.TryGetValue(p.b, out var nodeB))
                        continue;

                    var rowA = nodeA.Row;
                    var rowB = nodeB.Row;

                    // Net leakage flow across the consolidated door area connecting these two rooms
                    double gapArea = p.totalArea;

                    double dPa = rowA.PressurePa - rowB.PressurePa;
                    double netM3s = 0;
                    if (gapArea > 0 && Math.Abs(dPa) > 0.01)
                    {
                        double vel = Math.Sqrt(2.0 * Math.Abs(dPa) / Rho);
                        netM3s = Math.Sign(dPa) * Cd * gapArea * vel;
                    }

                    bool cascadeViolation = IsCascadeViolation(rowA, rowB, netM3s);

                    var edge = new NetworkEdge { From = nodeA, To = nodeB, NetFlowM3s = netM3s, IsCascadeViolation = cascadeViolation };
                    _networkEdges.Add(edge);
                    DrawEdge(edge, nodeW, nodeH);
                }

                // ── Draw nodes ──
                foreach (var node in _networkNodes)
                    DrawNode(node, nodeW, nodeH);

                PopulateZoneFilter();
                ApplyNetworkFilter();
            }
            catch (Exception ex)
            {
                NetworkEmptyStateText.Text = $"Could not build pressure network:\n{ex.Message}";
                NetworkEmptyStateText.Visibility = WpfVisibility.Visible;
                throw; // let BuildPressureNetworkSafe surface this in a MessageBox
            }
        }

        /// <summary>
        /// Cascade rule: air should leak from the cleaner room toward the dirtier room.
        /// Uses HierarchyLevel (lower = cleaner) so this works across GMP and ISO alike,
        /// since HierarchyLevel already interleaves both standards into one ordering.
        /// Unclassified rooms (HierarchyLevel=99) are excluded — there's no requirement
        /// to validate against.
        /// </summary>
        private bool IsCascadeViolation(SpaceComplianceResult a, SpaceComplianceResult b, double netM3sAtoB)
        {
            var clsA = CleanlinessClass.Parse(a.CleanlinessClass);
            var clsB = CleanlinessClass.Parse(b.CleanlinessClass);
            if (clsA.HierarchyLevel >= 99 || clsB.HierarchyLevel >= 99) return false;
            if (clsA.HierarchyLevel == clsB.HierarchyLevel) return false; // same cleanliness — no hierarchy to violate

            bool aIsCleaner = clsA.HierarchyLevel < clsB.HierarchyLevel;
            // Flow should go cleaner → dirtier, i.e. positive netM3sAtoB when A is cleaner,
            // negative netM3sAtoB when B is cleaner (flow A→B is negative meaning B→A).
            if (aIsCleaner)  return netM3sAtoB < -1e-6;   // flowing dirtier(B)→cleaner(A) — wrong
            else             return netM3sAtoB >  1e-6;   // flowing dirtier(A)→cleaner(B) — wrong
        }

        private void DrawNode(NetworkNode node, double w, double h)
        {
            var row = node.Row;
            
            // Subtle professional colors based on node type
            bool isAirLock = row.RoomName.IndexOf("air lock", StringComparison.OrdinalIgnoreCase) >= 0 || 
                             row.RoomName.IndexOf("airlock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             row.RoomName.IndexOf("AL", StringComparison.OrdinalIgnoreCase) == 0;
                             
            var baseColor = row.IsUnclassified ? WpfColor.FromRgb(0xE0, 0xE0, 0xE0) : // Grey for Unclassified
                            isAirLock ? WpfColor.FromRgb(0xFF, 0xEC, 0xB3) : // Light Amber for Air Locks
                            WpfColor.FromRgb(0xBB, 0xDE, 0xFB); // Light Blue for GMP/Classified
                            
            var borderColor = row.IsUnclassified ? WpfColor.FromRgb(0x9E, 0x9E, 0x9E) :
                              isAirLock ? WpfColor.FromRgb(0xFF, 0xC1, 0x07) :
                              WpfColor.FromRgb(0x21, 0x96, 0xF3);

            bool fails = !row.IsUnclassified && row.OverallStatus == ComplianceStatus.NonCompliant;

            var border = new Border
            {
                Width = w, Height = h,
                Background = new SolidColorBrush(baseColor),
                BorderBrush = new SolidColorBrush(fails ? WpfColor.FromRgb(0xC6, 0x28, 0x28) : borderColor),
                BorderThickness = new Thickness(fails ? 3 : 2),
                CornerRadius = new CornerRadius(8),
                Tag = node
            };

            var stack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            stack.Children.Add(new TextBlock
            {
                Text = $"{row.RoomNumber} - {row.RoomName}",
                FontWeight = FontWeights.Bold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.Black
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{row.CleanlinessClass}",
                FontSize = 10, Foreground = Brushes.DarkSlateGray
            });
            
            var pressureColor = row.PressurePa > 1.0 ? WpfColor.FromRgb(0x2E, 0x7D, 0x32) : // Dark Green
                                row.PressurePa < -1.0 ? WpfColor.FromRgb(0xC6, 0x28, 0x28) : // Dark Red
                                WpfColor.FromRgb(0x15, 0x65, 0xC0); // Dark Blue (neutral)
                                
            stack.Children.Add(new TextBlock
            {
                Text = $"Pressure: {row.PressurePa:+0.0;-0.0;0.0} Pa", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(pressureColor)
            });
            stack.Children.Add(new TextBlock { 
                Text = $"Sup: {row.SupplyDisplay:F0} | Ret: {row.ReturnDisplay:F0} | Exh: {row.ExhaustDisplay:F0}", 
                FontSize = 10, Foreground = Brushes.Black 
            });

            if (fails)
                stack.Children.Add(new TextBlock { Text = "⚠ Fails requirement", FontSize = 10, Foreground = Brushes.DarkRed, FontWeight = FontWeights.Bold });

            border.Child = stack;
            border.MouseLeftButtonDown += (s, e) => { NetworkNode_Click(node); e.Handled = true; };
            border.Cursor = System.Windows.Input.Cursors.Hand;
            border.ToolTip = $"{row.RoomName} — click to highlight connected rooms";

            Canvas.SetLeft(border, node.X);
            Canvas.SetTop(border, node.Y);
            Canvas.SetZIndex(border, 10);
            NetworkCanvas.Children.Add(border);
            node.Visual = border;
        }

        private void DrawEdge(NetworkEdge edge, double nodeW, double nodeH)
        {
            var lineColor = edge.IsCascadeViolation ? System.Windows.Media.Colors.Red : WpfColor.FromRgb(0x90, 0x90, 0x90);

            // Clean orthogonal edge routing
            double startX, startY, endX, endY;
            bool isVertical = Math.Abs(edge.From.Y - edge.To.Y) > 10;
            
            double midX, midY;
            
            if (isVertical)
            {
                bool isDown = edge.To.Y > edge.From.Y;
                double deltaX = (edge.To.X + nodeW/2) - (edge.From.X + nodeW/2);
                
                // Shift startX to avoid merging edges coming out of the same parent
                startX = edge.From.X + nodeW / 2 + Math.Clamp(deltaX * 0.15, -nodeW * 0.4, nodeW * 0.4);
                startY = isDown ? edge.From.Y + nodeH : edge.From.Y;
                endX = edge.To.X + nodeW / 2;
                endY = isDown ? edge.To.Y : edge.To.Y + nodeH;
                
                midX = startX + (endX - startX) / 2;
                // Shift midY slightly so horizontal segments don't perfectly overlap
                midY = startY + (endY - startY) / 2 + Math.Clamp(deltaX * 0.05, -25, 25);
            }
            else
            {
                bool isRight = edge.To.X > edge.From.X;
                double deltaY = (edge.To.Y + nodeH/2) - (edge.From.Y + nodeH/2);
                
                startX = isRight ? edge.From.X + nodeW : edge.From.X;
                startY = edge.From.Y + nodeH / 2 + Math.Clamp(deltaY * 0.15, -nodeH * 0.4, nodeH * 0.4);
                endX = isRight ? edge.To.X : edge.To.X + nodeW;
                endY = edge.To.Y + nodeH / 2;
                
                midX = startX + (endX - startX) / 2 + Math.Clamp(deltaY * 0.05, -25, 25);
                midY = startY + (endY - startY) / 2;
            }

            var figure = new PathFigure { StartPoint = new WpfPoint(startX, startY) };
            
            if (isVertical)
            {
                // Go vertically halfway, then horizontally, then vertically
                figure.Segments.Add(new System.Windows.Media.LineSegment(new WpfPoint(startX, midY), true));
                figure.Segments.Add(new System.Windows.Media.LineSegment(new WpfPoint(endX, midY), true));
                figure.Segments.Add(new System.Windows.Media.LineSegment(new WpfPoint(endX, endY), true));
            }
            else
            {
                // Go horizontally halfway, then vertically, then horizontally
                figure.Segments.Add(new System.Windows.Media.LineSegment(new WpfPoint(midX, startY), true));
                figure.Segments.Add(new System.Windows.Media.LineSegment(new WpfPoint(midX, endY), true));
                figure.Segments.Add(new System.Windows.Media.LineSegment(new WpfPoint(endX, endY), true));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            var path = new WpfPath
            {
                Data = geometry,
                Stroke = new SolidColorBrush(lineColor),
                StrokeThickness = edge.IsCascadeViolation ? 2.5 : 2.0,
                StrokeDashArray = edge.IsCascadeViolation ? new DoubleCollection { 4, 2 } : null,
                Fill = null
            };
            Canvas.SetZIndex(path, 1);
            NetworkCanvas.Children.Add(path);
            edge.Visual = path;

            // Arrow head + label at the midpoint
            if (Math.Abs(edge.NetFlowM3s) > 1e-6)
            {
                bool forward = edge.NetFlowM3s > 0; // From→To
                double arrowAngle = 0;
                
                if (isVertical) {
                    arrowAngle = (edge.To.Y > edge.From.Y) ? 90 : -90;
                } else {
                    arrowAngle = (edge.To.X > edge.From.X) ? 0 : 180;
                }
                
                if (!forward) arrowAngle += 180;

                var arrow = new Polygon
                {
                    Fill = new SolidColorBrush(lineColor),
                    Points = new PointCollection(new[]
                    {
                        new WpfPoint(0, -6), new WpfPoint(12, 0), new WpfPoint(0, 6)
                    })
                };
                var rt = new RotateTransform(arrowAngle);
                var tt = new TranslateTransform(midX, midY);
                var tg = new TransformGroup();
                tg.Children.Add(rt);
                tg.Children.Add(tt);
                arrow.RenderTransform = tg;
                Canvas.SetZIndex(arrow, 2);
                NetworkCanvas.Children.Add(arrow);
                edge.ArrowHead = arrow;

                // Flow label (CFM and L/s)
                double absFlowM3s = Math.Abs(edge.NetFlowM3s);
                double cfm = absFlowM3s * 2118.88;
                double ls  = absFlowM3s * 1000.0;
                var label = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(230, 255, 255, 255)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(2),
                    Child = new TextBlock
                    {
                        Text = $"{cfm:F0} CFM ({ls:F1} L/s)",
                        FontSize = 9, Foreground = new SolidColorBrush(lineColor), FontWeight = FontWeights.SemiBold
                    }
                };
                
                Canvas.SetLeft(label, midX - 35);
                Canvas.SetTop(label, midY + 8);
                Canvas.SetZIndex(label, 3);
                NetworkCanvas.Children.Add(label);
                edge.Label = label;
                
                if (edge.IsCascadeViolation)
                {
                    var warn = new TextBlock { Text = "⚠", FontSize = 14, Foreground = Brushes.Red, FontWeight = FontWeights.Bold };
                    Canvas.SetLeft(warn, midX - 28);
                    Canvas.SetTop(warn, midY - 14);
                    Canvas.SetZIndex(warn, 4);
                    warn.ToolTip = "Pressure cascade violation: airflow direction conflicts with cleanliness hierarchy";
                    NetworkCanvas.Children.Add(warn);
                }
            }
        }

        private void NetworkNode_Click(NetworkNode node)
        {
            _networkSelectedNode = (_networkSelectedNode == node) ? null : node;
            RefreshNetworkHighlight();
        }

        private void RefreshNetworkHighlight()
        {
            foreach (var n in _networkNodes)
            {
                if (n.Visual == null) continue;
                bool connected = _networkSelectedNode == null
                    || n == _networkSelectedNode
                    || _networkEdges.Any(e => (e.From == _networkSelectedNode && e.To == n) || (e.To == _networkSelectedNode && e.From == n));
                n.Visual.Opacity = connected ? 1.0 : 0.25;
            }
            foreach (var edge in _networkEdges)
            {
                bool connected = _networkSelectedNode == null
                    || edge.From == _networkSelectedNode || edge.To == _networkSelectedNode;
                double op = connected ? 1.0 : 0.15;
                if (edge.Visual != null) edge.Visual.Opacity = op;
                if (edge.ArrowHead != null) edge.ArrowHead.Opacity = op;
                if (edge.Label != null) edge.Label.Opacity = op;
            }
        }

        private void PopulateZoneFilter()
        {
            if (NetworkZoneFilterCombo == null) return;
            var zones = _results.Select(r => r.CleanlinessClass).Distinct().OrderBy(z => z).ToList();
            NetworkZoneFilterCombo.Items.Clear();
            NetworkZoneFilterCombo.Items.Add("(All zones)");
            foreach (var z in zones) NetworkZoneFilterCombo.Items.Add(z);
            NetworkZoneFilterCombo.SelectedIndex = 0;
        }

        private void ApplyNetworkFilter()
        {
            if (NetworkZoneFilterCombo == null) return;
            string zone = NetworkZoneFilterCombo.SelectedItem as string;
            bool selectedOnly = NetworkSelectedOnlyCheckbox.IsChecked == true;

            bool NodeVisible(NetworkNode n)
            {
                if (zone != null && zone != "(All zones)" && n.Row.CleanlinessClass != zone) return false;
                if (selectedOnly && _networkSelectedNode != null)
                {
                    return n == _networkSelectedNode
                        || _networkEdges.Any(e => (e.From == _networkSelectedNode && e.To == n) || (e.To == _networkSelectedNode && e.From == n));
                }
                return true;
            }

            foreach (var n in _networkNodes)
            {
                if (n.Visual != null)
                    n.Visual.Visibility = NodeVisible(n) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
            foreach (var edge in _networkEdges)
            {
                bool vis = NodeVisible(edge.From) && NodeVisible(edge.To);
                var v = vis ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (edge.Visual != null) edge.Visual.Visibility = v;
                if (edge.ArrowHead != null) edge.ArrowHead.Visibility = v;
                if (edge.Label != null) edge.Label.Visibility = v;
            }
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Keep this wired (XAML references it) but do nothing here —
            // NetworkCanvas_IsVisibleChanged is the reliable trigger now.
        }

        private void NetworkCanvas_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Fires when the Pressure Network tab is selected and the canvas becomes
            // visible in the rendered tree. This is the correct moment to add children —
            // the canvas is guaranteed to be laid out and have its actual size.
            if ((bool)e.NewValue && _networkNeedsRebuild)
            {
                _networkNeedsRebuild = false;
                // Defer execution to avoid modifying the Children collection while the layout engine is enumerating it
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        BuildPressureNetworkSafe();
                    })
                );
            }
        }

        private void AutoSizeAndCenterGraph()
        {
            if (!_networkNodes.Any()) return;
            
            // Allow layout to process before calculating actual width
            double minX = _networkNodes.Min(n => n.X);
            double maxX = _networkNodes.Max(n => n.X + 180); // nodeW
            double minY = _networkNodes.Min(n => n.Y);
            double maxY = _networkNodes.Max(n => n.Y + 100);  // nodeH
            
            double graphW = maxX - minX + 100; // padding
            double graphH = maxY - minY + 100;
            
            // Set canvas size so scroll viewer bounds it correctly
            NetworkCanvas.Width = graphW;
            NetworkCanvas.Height = graphH;
            
            // Center viewport
            NetworkTranslateTransform.X = -minX + 50;
            NetworkTranslateTransform.Y = -minY + 50;
        }

        private void BuildPressureNetworkSafe()
        {
            try
            {
                BuildPressureNetwork();
                AutoSizeAndCenterGraph();
            }
            catch (Exception ex)
            {
                // Write error to canvas — safe during layout, unlike MessageBox
                if (NetworkCanvas != null)
                {
                    var err = new TextBlock
                    {
                        Text = $"Error: {ex.Message}",
                        FontSize = 11,
                        Foreground = System.Windows.Media.Brushes.Red,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 600
                    };
                    Canvas.SetLeft(err, 10);
                    Canvas.SetTop(err, 10);
                    NetworkCanvas.Children.Add(err);
                }
            }
        }

        private void NetworkZoneFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyNetworkFilter();
        private void NetworkSelectedOnly_Changed(object sender, RoutedEventArgs e) => ApplyNetworkFilter();

        // ── Zoom / pan ────────────────────────────────────────────────────────

        private void NetworkZoomIn_Click(object sender, RoutedEventArgs e)  => NetworkZoom(1.15);
        private void NetworkZoomOut_Click(object sender, RoutedEventArgs e) => NetworkZoom(1 / 1.15);

        private void NetworkZoom(double factor)
        {
            double newScale = Math.Clamp(NetworkScaleTransform.ScaleX * factor, 0.2, 3.0);
            NetworkScaleTransform.ScaleX = newScale;
            NetworkScaleTransform.ScaleY = newScale;
        }

        private void NetworkResetView_Click(object sender, RoutedEventArgs e)
        {
            NetworkScaleTransform.ScaleX = 1;
            NetworkScaleTransform.ScaleY = 1;
            NetworkTranslateTransform.X = 0;
            NetworkTranslateTransform.Y = 0;
        }

        private void NetworkViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            NetworkZoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        }

        private void NetworkViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only start panning if the click wasn't on a node (nodes handle their own click)
            if (e.OriginalSource is Border) return;
            _networkIsPanning = true;
            _networkPanStart = e.GetPosition(NetworkViewport);
            _networkPanStartX = NetworkTranslateTransform.X;
            _networkPanStartY = NetworkTranslateTransform.Y;
            NetworkViewport.CaptureMouse();
        }

        private void NetworkViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _networkIsPanning = false;
            NetworkViewport.ReleaseMouseCapture();
        }

        private void NetworkViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_networkIsPanning) return;
            var pos = e.GetPosition(NetworkViewport);
            NetworkTranslateTransform.X = _networkPanStartX + (pos.X - _networkPanStart.X);
            NetworkTranslateTransform.Y = _networkPanStartY + (pos.Y - _networkPanStart.Y);
        }

    }

    public enum SizingTarget { Minimum, Middle, Maximum }

        // ── View model ────────────────────────────────────────────────────────────

    public class SpaceComplianceResult : INotifyPropertyChanged
    {
        // Conversion constants (duplicated here for self-contained VM)
        private const double Ft3s_M3h = 101.9406477;
        private const double Ft3_M3   = 0.0283168;
        private const double Pa_InWG  = 0.00401463;

        // ── Raw data (Revit internal units) ──────────────────────────────────
        public ElementId SpaceId             { get; set; }
        public bool      IsSpace             { get; set; }
        public bool      IsUnclassified      { get; set; }
        public string    RoomName            { get; set; }
        public string    RoomNumber          { get; set; }
        public string    Level               { get; set; }
        public string    Source              { get; set; }
        public string    CleanlinessClass    { get; set; }
        public double    VolumeFt3           { get; set; }   // ft³
        public int       MinAch              { get; set; }
        public int       MaxAch              { get; set; }
        public double    RecoveryTimeMinutes { get; set; }
        public double    PressurePa          { get; set; }

        /// <summary>
        /// Numeric sort key for RoomNumber. Extracts leading digits so "101A" sorts
        /// after "99" and before "102" — matches how engineers expect room numbers
        /// to order, rather than plain string comparison ("10" before "2").
        /// Falls back to 0 if the number has no leading digits.
        /// </summary>
        public double RoomNumberSort
        {
            get
            {
                if (string.IsNullOrEmpty(RoomNumber)) return 0;
                var digits = new string(RoomNumber.TakeWhile(char.IsDigit).ToArray());
                return double.TryParse(digits, out double n) ? n : 0;
            }
        }
        public double    LeakageArea_m2      { get; set; }
        public bool      HasVolumeWarning    { get; set; }
        public string    Notes               { get; set; }
        public bool      UseSI               { get; set; } = true;

        // ── Flows (ft³/s) — setters auto-update ActualAch ────────────────────
        private double _supplyFt3s;
        public double SupplyFt3s
        {
            get => _supplyFt3s;
            set
            {
                _supplyFt3s = value;
                // Always derive ACH directly from the flow so it stays in sync
                // regardless of whether the value came from AchCalculationService
                // (which reads Revit space params) or from terminal reads.
                ActualAch = VolumeFt3 > 0 ? _supplyFt3s * 3600.0 / VolumeFt3 : 0;
                RecoveryTimeMinutes = _supplyFt3s > 0
                    ? (VolumeFt3 * Ft3_to_M3 / (_supplyFt3s * Ft3s_to_M3s)) * Math.Log(100.0) / 60.0
                    : 0;
            }
        }
        public double ReturnFt3s  { get; set; }
        public double ExhaustFt3s { get; set; }

        // ActualAch is set by SupplyFt3s setter — never set directly from outside
        public double ActualAch { get; private set; }

        // ── Row selection (checkbox column) ──────────────────────────────────
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        // ── Editable display properties ───────────────────────────────────────
        private const double Ft3s_to_M3h = 101.9406477;
        private const double Ft3s_to_M3s = 0.0283168;
        private const double Ft3_to_M3   = 0.0283168;
        private const double Cd  = 0.65;
        private const double Rho = 1.2;

        public string SupplyEditDisplay
        {
            get => UseSI ? (_supplyFt3s * Ft3s_to_M3h).ToString("N0") : (_supplyFt3s * 60.0).ToString("N0");
            set 
            { 
                SetSupplyRaw(value);
                RecalculateFromSupply(UseSI, Ft3s_to_M3h, Ft3_to_M3, Pa_InWG, Cd, Rho);
                RefreshDisplayProperties();
            }
        }
        public string ReturnEditDisplay
        {
            get => UseSI ? (ReturnFt3s * Ft3s_to_M3h).ToString("N0") : (ReturnFt3s * 60.0).ToString("N0");
            set 
            { 
                SetReturnRaw(value);
                RecalculateFromReturn(UseSI, Ft3s_to_M3h, Pa_InWG, Cd, Rho);
                RefreshDisplayProperties();
            }
        }
        public string ExhaustEditDisplay
        {
            get => UseSI ? (ExhaustFt3s * Ft3s_to_M3h).ToString("N0") : (ExhaustFt3s * 60.0).ToString("N0");
            set 
            { 
                SetExhaustRaw(value);
                RecalculateFromExhaust(UseSI, Ft3s_to_M3h, Pa_InWG, Cd, Rho);
                RefreshDisplayProperties();
            }
        }

        // ACH Target edit string
        private string _achTargetEdit;
        public string AchTargetEdit
        {
            get => _achTargetEdit ?? ActualAch.ToString("F1");
            set
            {
                _achTargetEdit = value;
                OnPropertyChanged(nameof(AchTargetEdit));
            }
        }

        /// <summary>
        /// True only when the engineer has actually typed an ACH Target for this row
        /// (as opposed to AchTargetEdit's getter falling back to the displayed ActualAch).
        /// Used by the sizer to decide whether to honor a per-row ACH override.
        /// </summary>
        public bool HasUserAchTarget =>
            !string.IsNullOrEmpty(_achTargetEdit) && double.TryParse(_achTargetEdit, out double v) && v > 0;

        public double UserAchTargetValue =>
            HasUserAchTarget ? double.Parse(_achTargetEdit) : 0;

        // ── Derived/status ────────────────────────────────────────────────────
        public ComplianceStatus OverallStatus { get; set; }
        public string AchStatus               { get; set; }  // "OK", "Under", "Over", "Partial"
        public string RequiredAchRange        => MinAch == 0 ? "N/A" : $"{MinAch}–{MaxAch}";

        public List<Models.ComplianceCheckResult> CheckResults { get; set; } = new();

        // ── Display properties (unit-aware) ───────────────────────────────────
        public double VolumeDisplay   => UseSI ? VolumeFt3   * Ft3_M3    : VolumeFt3;
        public double SupplyDisplay   => UseSI ? SupplyFt3s  * Ft3s_M3h  : SupplyFt3s  * 60.0;
        public double ReturnDisplay   => UseSI ? ReturnFt3s  * Ft3s_M3h  : ReturnFt3s  * 60.0;
        public double ExhaustDisplay  => UseSI ? ExhaustFt3s * Ft3s_M3h  : ExhaustFt3s * 60.0;
        public double PressureDisplay => UseSI ? PressurePa               : PressurePa  * Pa_InWG;

        // Editable pressure — the grid binds to this string; setter stores in Pa
        private string _pressureEditPa;
        public string PressureEditPa
        {
            get => _pressureEditPa ?? PressureDisplay.ToString(UseSI ? "F2" : "F3");
            set
            {
                _pressureEditPa = value;
                OnPropertyChanged(nameof(PressureEditPa));
            }
        }

        public void SetSupplyRaw(string v)  => _supplyEditRaw  = v;
        public void SetReturnRaw(string v)  => _returnEditRaw  = v;
        public void SetExhaustRaw(string v) => _exhaustEditRaw = v;

        /// <summary>Supply edited directly → re-derive ACH and pressure.</summary>
        public void RecalculateFromSupply(bool useSI, double ft3sM3h, double ft3M3, double paInwg, double cd, double rho)
        {
            // Parse the entered value (in the current display unit) and convert to ft³/s
            if (!double.TryParse(_supplyEditRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double entered) && 
                !double.TryParse(_supplyEditRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out entered)) return;
            if (entered < 0) return;
            double newFt3s = useSI ? entered / ft3sM3h : entered / 60.0;
            SupplyFt3s = newFt3s; // setter auto-updates ActualAch + RecoveryTime
            RederivePressureFromFlows(newFt3s * Ft3s_to_M3s, ReturnFt3s * Ft3s_to_M3s, ExhaustFt3s * Ft3s_to_M3s, cd, rho);
            _supplyEditRaw = null;
        }
        private string _supplyEditRaw;

        /// <summary>Return edited directly → re-derive pressure. Supply and ACH unchanged.</summary>
        public void RecalculateFromReturn(bool useSI, double ft3sM3h, double paInwg, double cd, double rho)
        {
            if (!double.TryParse(_returnEditRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double entered) && 
                !double.TryParse(_returnEditRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out entered)) return;
            if (entered < 0) return;
            ReturnFt3s = useSI ? entered / ft3sM3h : entered / 60.0;
            RederivePressureFromFlows(SupplyFt3s * Ft3s_to_M3s, ReturnFt3s * Ft3s_to_M3s, ExhaustFt3s * Ft3s_to_M3s, cd, rho);
            _returnEditRaw = null;
        }
        private string _returnEditRaw;

        /// <summary>Exhaust edited directly → re-derive pressure. Supply and ACH unchanged.</summary>
        public void RecalculateFromExhaust(bool useSI, double ft3sM3h, double paInwg, double cd, double rho)
        {
            if (!double.TryParse(_exhaustEditRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double entered) && 
                !double.TryParse(_exhaustEditRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out entered)) return;
            if (entered < 0) return;
            ExhaustFt3s = useSI ? entered / ft3sM3h : entered / 60.0;
            RederivePressureFromFlows(SupplyFt3s * Ft3s_to_M3s, ReturnFt3s * Ft3s_to_M3s, ExhaustFt3s * Ft3s_to_M3s, cd, rho);
            _exhaustEditRaw = null;
        }
        private string _exhaustEditRaw;


        // Called after the grid commits a PressureEditPa edit (Pressure Priority mode)
        /// <summary>
        /// ACH Priority mode: ACH Target is the only input. Supply comes from ACH;
        /// return/exhaust are adjusted to hold whatever pressure was already set, and
        /// PressurePa is re-derived from the resulting net flow so the displayed value
        /// never goes stale relative to the terminals.
        /// </summary>
        public void RecalculateFromAchTarget(bool useSI,
            double ft3sM3h, double ft3M3, double paInwg, double cd, double rho)
        {
            if (!double.TryParse(_achTargetEdit, out double targetAch) || targetAch <= 0) return;

            double volFt3 = VolumeFt3;
            double volM3  = volFt3 * ft3M3;

            double newSupplyFt3s = targetAch * volFt3 / 3600.0;
            SupplyFt3s = newSupplyFt3s; // setter auto-computes ActualAch

            double supplyM3s = newSupplyFt3s * ft3sM3h / 3600.0;
            RecoveryTimeMinutes = supplyM3s > 0
                ? (volM3 / supplyM3s) * Math.Log(100.0) / 60.0 : 0;

            // Adjust return/exhaust to hold the existing target pressure, then
            // re-derive PressurePa from the actual resulting flows so the grid
            // never shows a pressure value inconsistent with the terminals.
            ApplyPressureOffsetToReturnExhaust(supplyM3s, PressurePa, cd, rho, ft3sM3h,
                out double newReturnM3s, out double newExhaustM3s);

            RederivePressureFromFlows(supplyM3s, newReturnM3s, newExhaustM3s, cd, rho);

            _achTargetEdit  = targetAch.ToString("F1");
            _pressureEditPa = null; // force the Pressure cell to refresh from the new PressurePa
        }

        /// <summary>
        /// Combined mode: both ACH Target and Pressure are live inputs. Whichever one the
        /// user just edited has already been written into _achTargetEdit / _pressureEditPa
        /// by the caller — this re-derives supply from the current ACH Target and
        /// return/exhaust from the current Pressure, every time either changes.
        ///
        ///   Supply           = ACH Target × Volume / 3600
        ///   Net leakage flow = Cd × A × √(2|ΔP| / ρ), signed by pressure sign
        ///   Return/Exhaust   = Supply − Net leakage flow   (positive pressure → return < supply)
        /// </summary>
        public void RecalculateCombined(bool useSI,
            double ft3sM3h, double ft3M3, double paInwg, double cd, double rho)
        {
            // ACH Target: use whatever is currently in the edit field, falling back to
            // the displayed ActualAch if the field hasn't been touched yet (e.g. the user
            // only just edited Pressure for the first time in this row).
            double targetAch = double.TryParse(_achTargetEdit, out double achVal) && achVal > 0
                ? achVal : ActualAch;

            // Pressure: same pattern — use the live edit value, converting from the
            // displayed unit back to Pa, falling back to the stored PressurePa.
            double targetPressurePa = PressurePa;
            if (double.TryParse(_pressureEditPa, out double pEntered))
                targetPressurePa = useSI ? pEntered : pEntered / paInwg;

            double volFt3 = VolumeFt3;
            double volM3  = volFt3 * ft3M3;

            // Supply from ACH — independent of pressure.
            double newSupplyFt3s = targetAch * volFt3 / 3600.0;
            SupplyFt3s = newSupplyFt3s; // setter auto-computes ActualAch

            double supplyM3s = newSupplyFt3s * ft3sM3h / 3600.0;
            RecoveryTimeMinutes = supplyM3s > 0
                ? (volM3 / supplyM3s) * Math.Log(100.0) / 60.0 : 0;

            // Return/exhaust from pressure — independent of how supply was derived.
            ApplyPressureOffsetToReturnExhaust(supplyM3s, targetPressurePa, cd, rho, ft3sM3h,
                out double newReturnM3s, out double newExhaustM3s);

            // PressurePa is exactly the target the user typed — no re-derivation needed
            // here, since both supply and return/exhaust were just solved to hit it.
            PressurePa = targetPressurePa;

            _achTargetEdit  = targetAch.ToString("F1");
            _pressureEditPa = null; // refresh from PressurePa, which now equals the target exactly
        }

        /// <summary>
        /// Shared core: given supply and a target pressure, derives return/exhaust via the
        /// orifice leakage model and writes them onto this row. Used by both ACH Priority
        /// (pressure fixed, supply varies) and Combined (both vary together) paths.
        /// </summary>
        private void ApplyPressureOffsetToReturnExhaust(
            double supplyM3s, double targetPressurePa, double cd, double rho, double ft3sM3h,
            out double newReturnM3s, out double newExhaustM3s)
        {
            bool hasReturn = ReturnFt3s > 0 || !(ExhaustFt3s > 0);
            double exhaustM3s = ExhaustFt3s * ft3sM3h / 3600.0;

            if (LeakageArea_m2 <= 0)
            {
                // No leakage path known — leave return/exhaust unchanged.
                newReturnM3s  = ReturnFt3s  * ft3sM3h / 3600.0;
                newExhaustM3s = exhaustM3s;
                return;
            }

            double absPa  = Math.Abs(targetPressurePa);
            double netM3s = Math.Abs(targetPressurePa) < 0.01
                ? 0
                : Math.Sign(targetPressurePa) * cd * LeakageArea_m2 * Math.Sqrt(2.0 * absPa / rho);

            if (hasReturn)
            {
                newReturnM3s  = Math.Max(0, supplyM3s - exhaustM3s - netM3s);
                newExhaustM3s = exhaustM3s;
                ReturnFt3s    = newReturnM3s / ft3sM3h * 3600.0;
            }
            else
            {
                newReturnM3s  = 0;
                newExhaustM3s = Math.Max(0, supplyM3s - netM3s);
                ExhaustFt3s   = newExhaustM3s / ft3sM3h * 3600.0;
            }
        }

        /// <summary>
        /// Re-derives PressurePa from the actual net flow (supply − return − exhaust) via
        /// the orifice equation in reverse. Used after ACH-only edits so the displayed
        /// pressure always matches what the terminals would actually produce.
        /// </summary>
        private void RederivePressureFromFlows(
            double supplyM3s, double returnM3s, double exhaustM3s, double cd, double rho)
        {
            if (LeakageArea_m2 <= 0) return;

            double netM3s = supplyM3s - returnM3s - exhaustM3s;
            if (Math.Abs(netM3s) < 1e-9)
            {
                PressurePa = 0;
                return;
            }

            double velocity = netM3s / (cd * LeakageArea_m2);
            PressurePa = Math.Sign(netM3s) * (rho / 2.0) * velocity * velocity;
        }


        public void RefreshDisplayProperties()
        {
            _pressureEditPa = null;
            _achTargetEdit  = null;
            OnPropertyChanged(nameof(VolumeDisplay));
            OnPropertyChanged(nameof(SupplyDisplay));
            OnPropertyChanged(nameof(ReturnDisplay));
            OnPropertyChanged(nameof(ExhaustDisplay));
            OnPropertyChanged(nameof(SupplyEditDisplay));
            OnPropertyChanged(nameof(ReturnEditDisplay));
            OnPropertyChanged(nameof(ExhaustEditDisplay));
            OnPropertyChanged(nameof(ActualAch));
            OnPropertyChanged(nameof(PressureDisplay));
            OnPropertyChanged(nameof(PressureEditPa));
            OnPropertyChanged(nameof(AchTargetEdit));
        }

        public string StatusIcon => OverallStatus switch
        {
            ComplianceStatus.Compliant        => "✓",
            ComplianceStatus.PartialCompliance=> "⚠",
            ComplianceStatus.NonCompliant     => "✗",
            _                                 => "?"
        };

        // ── Methods ───────────────────────────────────────────────────────────

        public void RefreshStatus()
        {
            // ACH status
            if (MinAch == 0)
            {
                AchStatus = "OK";
            }
            // Allow 0.1 tolerance for matching minimum ACH (e.g. 19.9 >= 20.0)
            else if (ActualAch < MinAch - 0.15)
            {
                AchStatus = "Under";
            }
            else if (MaxAch > 0 && ActualAch > MaxAch)
            {
                // Partial if within 2× the max (over-ventilated but justifiable per risk assessment)
                // Hard fail only if more than 2× the max (e.g. >80 ACH for GMP-C, >40 for GMP-D)
                AchStatus = ActualAch <= MaxAch * 2.0 ? "Partial" : "Over";
            }
            else
            {
                AchStatus = "OK";
            }

            // Overall status
            bool achFail    = AchStatus == "Under" || AchStatus == "Over";
            bool achPartial = AchStatus == "Partial";
            bool volFail    = HasVolumeWarning;

            if (achFail || volFail)
                OverallStatus = ComplianceStatus.NonCompliant;
            else if (achPartial)
                OverallStatus = ComplianceStatus.PartialCompliance;
            else
                OverallStatus = ComplianceStatus.Compliant;

            OnPropertyChanged(nameof(AchStatus));
            OnPropertyChanged(nameof(OverallStatus));
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(ActualAch));
            OnPropertyChanged(nameof(RecoveryTimeMinutes));
            _pressureEditPa = null;
            _achTargetEdit  = null;
            OnPropertyChanged(nameof(VolumeDisplay));
            OnPropertyChanged(nameof(SupplyDisplay));
            OnPropertyChanged(nameof(ReturnDisplay));
            OnPropertyChanged(nameof(ExhaustDisplay));
            OnPropertyChanged(nameof(SupplyEditDisplay));
            OnPropertyChanged(nameof(ReturnEditDisplay));
            OnPropertyChanged(nameof(ExhaustEditDisplay));
            OnPropertyChanged(nameof(PressureDisplay));
            OnPropertyChanged(nameof(PressureEditPa));
            OnPropertyChanged(nameof(AchTargetEdit));
        }

        public void RefreshCheckResults(bool useSI)
        {
            UseSI = useSI;
            var checks = new List<Models.ComplianceCheckResult>();
            string flowUnit   = useSI ? "m³/h" : "CFM";
            string pressUnit  = useSI ? "Pa"   : "inWG";
            double supplyDisp = useSI ? SupplyFt3s * Ft3s_M3h : SupplyFt3s * 60.0;
            double pressDisp  = useSI ? PressurePa             : PressurePa * Pa_InWG;

            // ACH check
            string achReq, achAct;
            ComplianceStatus achStatus;
            if (MinAch == 0)
            {
                achReq    = "N/A";
                achAct    = $"{ActualAch:F1} ACH";
                achStatus = ComplianceStatus.NotApplicable;
            }
            else
            {
                achReq = $"{MinAch}–{MaxAch} ACH";
                string note = AchStatus is "Over" or "Partial" ? $" (max {MaxAch})" : "";
                achAct    = $"{ActualAch:F1} ACH{note}";
                achStatus = AchStatus == "OK"      ? ComplianceStatus.Compliant
                          : AchStatus == "Partial" ? ComplianceStatus.PartialCompliance
                                                   : ComplianceStatus.NonCompliant;
            }
            checks.Add(new Models.ComplianceCheckResult("Air Changes per Hour (ACH)", achReq, achAct, achStatus));

            // Recovery time
            var reqObj = StandardsDatabase.GetRequirements(Models.CleanlinessClass.Parse(CleanlinessClass));
            if (reqObj.RecoveryTimeMinutes > 0)
            {
                bool ok = RecoveryTimeMinutes <= reqObj.RecoveryTimeMinutes;
                checks.Add(new Models.ComplianceCheckResult(
                    "Recovery Time (100:1)",
                    $"≤ {reqObj.RecoveryTimeMinutes} min",
                    $"{RecoveryTimeMinutes:F1} min",
                    ok ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant));
            }

            // Volume
            double volDisp = useSI ? VolumeFt3 * Ft3_M3 : VolumeFt3;
            string volUnit = useSI ? "m³" : "CF";
            checks.Add(new Models.ComplianceCheckResult(
                "Volume",
                $"> 0 {volUnit}",
                $"{volDisp:F1} {volUnit}",
                HasVolumeWarning ? ComplianceStatus.NonCompliant : ComplianceStatus.Compliant));

            // Supply airflow
            string supplySource = $"ACH {ActualAch:F1} | Pressure {PressurePa:F1} Pa";
            checks.Add(new Models.ComplianceCheckResult(
                $"Supply Airflow",
                supplySource,
                $"{supplyDisp:F0} {flowUnit}",
                ComplianceStatus.NotApplicable));

            // Pressure
            string pressFormat = useSI ? "F2" : "F3";
            checks.Add(new Models.ComplianceCheckResult(
                "Room Pressure (calculated)",
                $"≥ {(useSI ? reqObj.MinPressureDifferential : reqObj.MinPressureDifferential * Pa_InWG).ToString(pressFormat)} {pressUnit}",
                $"{pressDisp.ToString(pressFormat)} {pressUnit}",
                PressurePa >= reqObj.MinPressureDifferential
                    ? ComplianceStatus.Compliant
                    : ComplianceStatus.PartialCompliance));

            CheckResults = checks;
            OnPropertyChanged(nameof(CheckResults));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
