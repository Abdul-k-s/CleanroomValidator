using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Data;
using CleanroomValidator.Models;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Services
{
    public class ComplianceCalculator
    {
        private readonly Document _doc;
        private readonly RoomDataExtractor _dataExtractor;
        private readonly AdjacencyDetector _adjacencyDetector;
        private readonly ParameterService _parameterService;
        private readonly AchCalculationService _achService;
        private readonly CeilingDetectionService _ceilingService;
        private readonly PressureCalculationService _pressureService;

        public ComplianceCalculator(Document doc, Document linkedMechanicalDoc = null)
        {
            _doc = doc;
            _dataExtractor = new RoomDataExtractor(doc, linkedMechanicalDoc);
            _adjacencyDetector = new AdjacencyDetector(doc, _dataExtractor);
            _parameterService = new ParameterService();
            _achService = new AchCalculationService(doc);
            var linkedDocs = RoomDataExtractor.GetLinkedDocuments(doc);
            _ceilingService = new CeilingDetectionService(doc, linkedDocs);
            _pressureService = new PressureCalculationService(doc);
        }

        public RoomComplianceResult CheckCompliance(Room room)
        {
            var cleanlinessValue = _parameterService.GetCleanlinessClass(room);
            var cleanlinessClass = CleanlinessClass.Parse(cleanlinessValue);
            var requirements = StandardsDatabase.GetRequirements(cleanlinessClass);

            // Use ACH service for calculations
            var achResult = _achService.CalculateAchForRoom(room);
            
            var pressure = _dataExtractor.GetRoomPressure(room);
            var adjacentRooms = _adjacencyDetector.GetAdjacentRooms(room);

            var result = new RoomComplianceResult
            {
                RoomId = room.Id,
                RoomName = GetRoomDisplayName(room),
                RoomNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "",
                Level = room.Level?.Name ?? "No Level",
                Source = "Local",
                CleanlinessClass = cleanlinessClass,
                VolumeCubicFeet = achResult.Volume,
                ActualSupplyCfm = achResult.SupplyAirflowCfm,
                ActualAch = achResult.AchCalculated,
                RequiredAch = achResult.AchRequired,
                AchMeetsRequirement = achResult.MeetsRequirement,
                RecoveryTimeMinutes = achResult.RecoveryTimeMinutes,
                ActualPressure = pressure,
                AdjacentRooms = adjacentRooms,
                HasVolumeWarning = achResult.HasVolumeWarning,
                Notes = achResult.Notes
            };

            // Perform compliance checks
            var checks = new List<ComplianceCheckResult>();

            // 1. ACH Check
            checks.Add(CheckAch(achResult, requirements));

            // 2. Recovery Time Check
            if (requirements.RecoveryTimeMinutes > 0)
            {
                checks.Add(CheckRecoveryTime(achResult, requirements));
            }

            // 3. Volume Check
            checks.Add(CheckVolume(achResult));

            // 4. Pressure Check against adjacent rooms
            checks.AddRange(CheckPressureCascade(pressure, cleanlinessClass, adjacentRooms, requirements, room.Id));

            result.CheckResults = checks;
            result.OverallStatus = DetermineOverallStatus(checks);

            return result;
        }

        public RoomComplianceResult CheckCompliance(Space space)
        {
            var cleanlinessValue = _parameterService.GetCleanlinessClass(space);
            var cleanlinessClass = CleanlinessClass.Parse(cleanlinessValue);
            var requirements = StandardsDatabase.GetRequirements(cleanlinessClass);

            var achResult = _achService.CalculateAch(space);

            // Use ParameterService consistently — same logic as rooms
            var pressure = _parameterService.GetRoomPressure(space);

            // Get adjacent rooms via the room that owns this space (best-effort)
            var adjacentRooms = _adjacencyDetector.GetAdjacentSpaces(space);

            var result = new RoomComplianceResult
            {
                RoomId = space.Id,
                RoomName = GetSpaceDisplayName(space),
                RoomNumber = space.Number ?? "",
                Level = space.Level?.Name ?? "No Level",
                Source = "Local",
                CleanlinessClass = cleanlinessClass,
                VolumeCubicFeet = achResult.Volume,
                ActualSupplyCfm = achResult.SupplyAirflowCfm,
                ActualAch = achResult.AchCalculated,
                RequiredAch = achResult.AchRequired,
                AchMeetsRequirement = achResult.MeetsRequirement,
                RecoveryTimeMinutes = achResult.RecoveryTimeMinutes,
                ActualPressure = pressure,
                AdjacentRooms = adjacentRooms,
                HasVolumeWarning = achResult.HasVolumeWarning,
                Notes = achResult.Notes
            };

            var checks = new List<ComplianceCheckResult>();
            checks.Add(CheckAch(achResult, requirements));

            if (requirements.RecoveryTimeMinutes > 0)
            {
                checks.Add(CheckRecoveryTime(achResult, requirements));
            }

            checks.Add(CheckVolume(achResult));

            // Pressure cascade — same as room path
            checks.AddRange(CheckPressureCascade(pressure, cleanlinessClass, adjacentRooms, requirements, space.Id));

            result.CheckResults = checks;
            result.OverallStatus = DetermineOverallStatus(checks);

            return result;
        }

        private ComplianceCheckResult CheckAch(AchCalculationService.AchResult achResult, StandardRequirements requirements)
        {
            if (requirements.Grade == CleanlinessGrade.Unclassified)
            {
                return new ComplianceCheckResult(
                    "Air Changes per Hour (ACH)",
                    "N/A",
                    achResult.HasAirflowData ? $"{achResult.AchCalculated:F1} ACH" : "No data",
                    ComplianceStatus.NotApplicable
                );
            }

            if (!achResult.HasAirflowData)
            {
                return new ComplianceCheckResult(
                    "Air Changes per Hour (ACH)",
                    $"{requirements.MinAch}–{requirements.MaxAch} ACH",
                    "No airflow data",
                    ComplianceStatus.NonCompliant
                );
            }

            ComplianceStatus status;
            string note = "";

            if (achResult.AchCalculated < requirements.MinAch)
            {
                // Below minimum — hard fail
                status = ComplianceStatus.NonCompliant;
            }
            else if (requirements.MaxAch > 0 && achResult.AchCalculated > requirements.MaxAch)
            {
                // Partial if within 2× max — justifiable with risk assessment per EU GMP Annex 1 §4.23
                // Hard fail only beyond 2× max
                bool hardFail = achResult.AchCalculated > requirements.MaxAch * 2.0;
                status = hardFail ? ComplianceStatus.NonCompliant : ComplianceStatus.PartialCompliance;
                note = $" (exceeds max {requirements.MaxAch})";
            }
            else
            {
                status = ComplianceStatus.Compliant;
            }

            return new ComplianceCheckResult(
                "Air Changes per Hour (ACH)",
                $"{requirements.MinAch}–{requirements.MaxAch} ACH",
                $"{achResult.AchCalculated:F1} ACH{note}",
                status
            );
        }

        private ComplianceCheckResult CheckRecoveryTime(AchCalculationService.AchResult achResult, StandardRequirements requirements)
        {
            if (!achResult.HasAirflowData || achResult.RecoveryTimeMinutes <= 0)
            {
                return new ComplianceCheckResult(
                    "Recovery Time (100:1)",
                    $"≤ {requirements.RecoveryTimeMinutes} min",
                    "No data",
                    ComplianceStatus.NonCompliant
                );
            }

            var status = achResult.RecoveryTimeMinutes <= requirements.RecoveryTimeMinutes
                ? ComplianceStatus.Compliant
                : ComplianceStatus.NonCompliant;

            return new ComplianceCheckResult(
                "Recovery Time (100:1)",
                $"≤ {requirements.RecoveryTimeMinutes} min",
                $"{achResult.RecoveryTimeMinutes:F1} min",
                status
            );
        }

        private ComplianceCheckResult CheckVolume(AchCalculationService.AchResult achResult)
        {
            return new ComplianceCheckResult(
                "Volume",
                "> 0 CF",
                $"{achResult.Volume:F0} CF",
                achResult.HasVolumeWarning ? ComplianceStatus.NonCompliant : ComplianceStatus.Compliant
            );
        }

        private List<ComplianceCheckResult> CheckPressureCascade(
            double roomPressure,
            CleanlinessClass roomClass,
            List<AdjacentRoomInfo> adjacentRooms,
            StandardRequirements requirements,
            ElementId elementId = null)
        {
            var results = new List<ComplianceCheckResult>();

            if (requirements.Grade == CleanlinessGrade.Unclassified || !adjacentRooms.Any())
            {
                results.Add(new ComplianceCheckResult(
                    "Pressure Cascade",
                    "N/A",
                    roomPressure != 0 ? $"{roomPressure:F1} Pa" : "—",
                    ComplianceStatus.NotApplicable
                ));
                return results;
            }

            // Build door map once to filter only door-connected neighbours
            Dictionary<ElementId, List<DoorConnection>> doorMap = null;
            if (elementId != null)
            {
                var allSpaces = new FilteredElementCollector(_doc)
                    .OfClass(typeof(SpatialElement)).OfType<Space>().Where(s => s.Area > 0);
                var allRooms = new FilteredElementCollector(_doc)
                    .OfClass(typeof(SpatialElement)).OfType<Room>().Where(r => r.Area > 0);
                doorMap = _pressureService.BuildDoorAdjacencyMap(allSpaces, allRooms);
            }

            foreach (var adjacent in adjacentRooms)
            {
                // Only evaluate pairs connected through a door
                if (doorMap != null && elementId != null)
                {
                    bool hasDoor = doorMap.TryGetValue(elementId, out var doors) &&
                                   doors.Any(d => d.FromElementId == adjacent.RoomId ||
                                                  d.ToElementId   == adjacent.RoomId);
                    if (!hasDoor) continue;
                }

                // Always read live pressure from the parameter (not stale snapshot)
                double neighbourPressure = GetLivePressure(adjacent.RoomId);
                double pressureDiff = roomPressure - neighbourPressure;

                if (adjacent.CleanlinessClass.HierarchyLevel < roomClass.HierarchyLevel)
                {
                    // This room is higher class → must be at HIGHER pressure
                    var requiredDiff = requirements.MinPressureDifferential;
                    var status = pressureDiff >= requiredDiff
                        ? ComplianceStatus.Compliant
                        : ComplianceStatus.NonCompliant;

                    results.Add(new ComplianceCheckResult(
                        $"Pressure vs {adjacent.RoomName}",
                        $"≥ +{requiredDiff:F0} Pa",
                        $"{pressureDiff:+0.0;-0.0} Pa",
                        status
                    ));
                }
                else if (adjacent.CleanlinessClass.HierarchyLevel > roomClass.HierarchyLevel)
                {
                    // This room is lower class → must be at LOWER pressure
                    var adjacentReq = StandardsDatabase.GetRequirements(adjacent.CleanlinessClass);
                    var requiredDiff = adjacentReq.MinPressureDifferential;
                    var status = (-pressureDiff) >= requiredDiff
                        ? ComplianceStatus.Compliant
                        : ComplianceStatus.NonCompliant;

                    results.Add(new ComplianceCheckResult(
                        $"Pressure vs {adjacent.RoomName} (higher class)",
                        $"Lower by ≥ {requiredDiff:F0} Pa",
                        $"{-pressureDiff:+0.0;-0.0} Pa diff",
                        status
                    ));
                }
                else
                {
                    // Same class — informational only
                    results.Add(new ComplianceCheckResult(
                        $"Pressure vs {adjacent.RoomName} (same class)",
                        "Same class",
                        $"{pressureDiff:+0.0;-0.0} Pa",
                        ComplianceStatus.Compliant
                    ));
                }
            }

            if (!results.Any())
            {
                results.Add(new ComplianceCheckResult(
                    "Pressure Cascade",
                    $"≥ +{requirements.MinPressureDifferential:F0} Pa vs lower",
                    $"{roomPressure:F1} Pa",
                    ComplianceStatus.Compliant
                ));
            }

            return results;
        }

        private double GetLivePressure(ElementId id)
        {
            var element = _doc.GetElement(id);
            if (element is Space space) return _parameterService.GetRoomPressure(space);
            if (element is Room room)   return _parameterService.GetRoomPressure(room);
            return 0;
        }

        private ComplianceStatus DetermineOverallStatus(List<ComplianceCheckResult> checks)
        {
            var applicableChecks = checks.Where(c => c.Status != ComplianceStatus.NotApplicable).ToList();

            if (!applicableChecks.Any())
                return ComplianceStatus.NotApplicable;

            if (applicableChecks.Any(c => c.Status == ComplianceStatus.NonCompliant))
                return ComplianceStatus.NonCompliant;

            if (applicableChecks.Any(c => c.Status == ComplianceStatus.PartialCompliance))
                return ComplianceStatus.PartialCompliance;

            return ComplianceStatus.Compliant;
        }

        private string GetRoomDisplayName(Room room)
        {
            var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            var number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";

            if (!string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(name))
                return $"{number} - {name}";

            return !string.IsNullOrEmpty(number) ? number : name;
        }

        private string GetSpaceDisplayName(Space space)
        {
            var name = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            var number = space.Number ?? "";

            if (!string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(name))
                return $"{number} - {name}";

            return !string.IsNullOrEmpty(number) ? number : name;
        }

        public List<RoomComplianceResult> CheckAllRooms(IEnumerable<Room> rooms)
        {
            return rooms.Select(CheckCompliance).ToList();
        }

        public List<RoomComplianceResult> CheckAllSpaces(IEnumerable<Space> spaces)
        {
            return spaces.Select(CheckCompliance).ToList();
        }
    }
}
