using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Services
{
    /// <summary>
    /// Calculates pressure differentials between adjacent rooms/spaces using:
    ///  - MEP airflow parameters (supply, return, exhaust)
    ///  - Door gap leakage area estimation
    ///  - Mass-balance pressure model
    /// Stores results in the Room_Pressure shared parameter.
    /// </summary>
    public class PressureCalculationService
    {
        private readonly Document _doc;
        private readonly ParameterService _paramService;

        // Door gap constants (EU GMP / ISPE guidance)
        private const double DoorGapHeight_m = 0.003;    // 3 mm undercut gap
        private const double AirDensity_kg_m3 = 1.2;
        private const double DischargeCoefficient = 0.65;

        /// <summary>
        /// Leakage area (m²) used when no doors are resolved from the model.
        /// Matches the user's selected leakage class:
        ///   Very tight     = 0.005 m²
        ///   Typical (default) = 0.008 m²
        ///   Ordinary door  = 0.012 m²
        /// </summary>
        public double FallbackLeakageArea { get; set; } = 0.008;

        public PressureCalculationService(Document doc)
        {
            _doc = doc;
            _paramService = new ParameterService();
        }

        // ── Public entry point ───────────────────────────────────────────────

        /// <summary>
        /// Calculates pressure for all spaces/rooms and writes to Room_Pressure parameter.
        /// Returns a summary of what was calculated.
        /// </summary>
        public PressureCalculationSummary CalculateAndStoreAll()
        {
            var summary = new PressureCalculationSummary();

            var spaces = new FilteredElementCollector(_doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .ToList();

            var rooms = new FilteredElementCollector(_doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            // Build door adjacency map once
            var doorMap = BuildDoorAdjacencyMap(spaces, rooms);

            using (var trans = new Transaction(_doc, "Calculate Room Pressures"))
            {
                trans.Start();
                try
                {
                    foreach (var space in spaces)
                    {
                        var result = CalculatePressureForSpace(space, doorMap);
                        _paramService.SetRoomPressure(space, result.PressurePa);
                        summary.Results.Add(result);
                    }

                    foreach (var room in rooms)
                    {
                        // Skip rooms that have a matching space already processed
                        bool hasSpace = spaces.Any(s =>
                            s.Level?.Id == room.Level?.Id &&
                            string.Equals(s.Number, room.Number, StringComparison.OrdinalIgnoreCase));

                        if (!hasSpace)
                        {
                            var result = CalculatePressureForRoom(room, doorMap);
                            _paramService.SetRoomPressure(room, result.PressurePa);
                            summary.Results.Add(result);
                        }
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    summary.Error = ex.Message;
                }
            }

            return summary;
        }

        // ── Pressure calculation for a space ────────────────────────────────

        public SpacePressureResult CalculatePressureForSpace(Space space,
            Dictionary<ElementId, List<DoorConnection>> doorMap)
        {
            var result = new SpacePressureResult
            {
                ElementId = space.Id,
                Name = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                Number = space.Number ?? ""
            };

            // ── Step 1: Read MEP airflow params (all in Revit internal units: ft³/s) ──
            double supplyFtS = GetParamDouble(space, BuiltInParameter.ROOM_ACTUAL_SUPPLY_AIRFLOW_PARAM)
                            ?? GetParamDouble(space, BuiltInParameter.ROOM_DESIGN_SUPPLY_AIRFLOW_PARAM)
                            ?? GetParamDouble(space, BuiltInParameter.ROOM_CALCULATED_SUPPLY_AIRFLOW_PARAM)
                            ?? 0;

            double returnFtS = GetParamDouble(space, BuiltInParameter.ROOM_ACTUAL_RETURN_AIRFLOW_PARAM)
                             ?? GetParamDouble(space, BuiltInParameter.ROOM_DESIGN_RETURN_AIRFLOW_PARAM)
                             ?? 0;

            double exhaustFtS = GetParamDouble(space, BuiltInParameter.ROOM_ACTUAL_EXHAUST_AIRFLOW_PARAM)
                              ?? GetParamDouble(space, BuiltInParameter.ROOM_DESIGN_EXHAUST_AIRFLOW_PARAM)
                              ?? 0;

            // Convert ft³/s → m³/s (1 ft³/s = 0.0283168 m³/s)
            double supply_m3s  = supplyFtS  * 0.0283168;
            double return_m3s  = returnFtS  * 0.0283168;
            double exhaust_m3s = exhaustFtS * 0.0283168;

            result.SupplyM3s  = supply_m3s;
            result.ReturnM3s  = return_m3s;
            result.ExhaustM3s = exhaust_m3s;

            // ── Step 2: Estimate door leakage area ───────────────────────────
            doorMap.TryGetValue(space.Id, out var doors);
            double totalLeakageArea_m2 = EstimateTotalLeakageArea(doors, FallbackLeakageArea);
            result.DoorCount = doors?.Count ?? 0;
            result.LeakageArea_m2 = totalLeakageArea_m2;

            // ── Step 3: Mass balance → pressure differential ─────────────────
            // Net airflow (positive = pressurised room, air wants to leak out)
            double netFlow_m3s = supply_m3s - return_m3s - exhaust_m3s;
            result.NetFlowM3s = netFlow_m3s;

            double pressurePa = 0;
            if (totalLeakageArea_m2 > 0 && Math.Abs(netFlow_m3s) > 1e-9)
            {
                // Orifice equation: Q = Cd × A × sqrt(2 × ΔP / ρ)
                // Solved for ΔP: ΔP = ρ/2 × (Q / (Cd × A))²
                double velocity = netFlow_m3s / (DischargeCoefficient * totalLeakageArea_m2);
                pressurePa = (AirDensity_kg_m3 / 2.0) * velocity * velocity;

                // Sign: positive supply surplus → positive pressure
                if (netFlow_m3s < 0) pressurePa = -pressurePa;
            }

            result.PressurePa = Math.Round(pressurePa, 2);
            return result;
        }

        // ── Pressure calculation for a room (architectural) ─────────────────

        public SpacePressureResult CalculatePressureForRoom(Room room,
            Dictionary<ElementId, List<DoorConnection>> doorMap)
        {
            var result = new SpacePressureResult
            {
                ElementId = room.Id,
                Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                Number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? ""
            };

            // Architectural rooms have no MEP airflow params — use existing Room_Pressure
            // or leave at 0 if no data available
            result.PressurePa = _paramService.GetRoomPressure(room);
            result.Notes = "No MEP space — pressure from parameter only";
            return result;
        }

        // ── Door adjacency & pressure differential ───────────────────────────

        /// <summary>
        /// Builds a map of ElementId → list of door connections.
        /// Tries every project phase for get_FromRoom/get_ToRoom because MEP spaces
        /// are often on a different phase than the architectural rooms, causing null
        /// returns when null (active phase) is passed.
        /// </summary>
        public Dictionary<ElementId, List<DoorConnection>> BuildDoorAdjacencyMap(
            IEnumerable<Space> spaces, IEnumerable<Room> rooms)
        {
            var map = new Dictionary<ElementId, List<DoorConnection>>();

            var allDoors = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            // Collect all project phases to probe — avoids phase-mismatch nulls
            var phases = new FilteredElementCollector(_doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .ToList();

            foreach (var door in allDoors)
            {
                Room fromRoom = null;
                Room toRoom   = null;

                // Try active phase first (fastest path), then all phases
                fromRoom = door.get_FromRoom(null);
                toRoom   = door.get_ToRoom(null);

                if (fromRoom == null || toRoom == null)
                {
                    foreach (var phase in phases)
                    {
                        fromRoom = fromRoom ?? door.get_FromRoom(phase);
                        toRoom   = toRoom   ?? door.get_ToRoom(phase);
                        if (fromRoom != null && toRoom != null) break;
                    }
                }

                if (fromRoom == null || toRoom == null) continue;

                double doorWidth_m  = GetDoorDimension(door, "Width",  BuiltInParameter.DOOR_WIDTH)  * 0.3048;
                double doorHeight_m = GetDoorDimension(door, "Height", BuiltInParameter.DOOR_HEIGHT) * 0.3048;

                ElementId fromId = FindSpaceOrRoomId(fromRoom, spaces, rooms);
                ElementId toId   = FindSpaceOrRoomId(toRoom,   spaces, rooms);

                if (fromId == null || toId == null) continue;

                var conn = new DoorConnection
                {
                    DoorId         = door.Id,
                    FromElementId  = fromId,
                    ToElementId    = toId,
                    DoorWidth_m    = doorWidth_m,
                    DoorHeight_m   = doorHeight_m
                };

                if (!map.ContainsKey(fromId)) map[fromId] = new List<DoorConnection>();
                if (!map.ContainsKey(toId))   map[toId]   = new List<DoorConnection>();

                map[fromId].Add(conn);
                map[toId].Add(conn);
            }

            return map;
        }

        /// <summary>
        /// Returns pressure differential across doors between this element and a specific neighbour.
        /// Positive means this element is at higher pressure.
        /// </summary>
        public double GetPressureDifferentialAcrossDoors(ElementId elementId,
            ElementId neighbourId,
            Dictionary<ElementId, List<DoorConnection>> doorMap)
        {
            if (!doorMap.TryGetValue(elementId, out var doors)) return 0;

            var connectingDoors = doors.Where(d =>
                d.FromElementId == neighbourId || d.ToElementId == neighbourId).ToList();

            if (!connectingDoors.Any()) return 0;

            double thisPressure      = GetStoredPressure(elementId);
            double neighbourPressure = GetStoredPressure(neighbourId);

            return thisPressure - neighbourPressure;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private double EstimateTotalLeakageArea(List<DoorConnection> doors, double fallbackArea = 0.008)
        {
            if (doors == null || !doors.Any())
                return fallbackArea;

            double total = 0;
            foreach (var door in doors)
            {
                double width = door.DoorWidth_m > 0 ? door.DoorWidth_m : 0.9;
                double undercut = width * DoorGapHeight_m;
                double sideGaps = 2 * (door.DoorHeight_m > 0 ? door.DoorHeight_m : 2.1) * 0.001;
                total += undercut + sideGaps;
            }
            return total;
        }

        private ElementId FindSpaceOrRoomId(Room room,
            IEnumerable<Space> spaces, IEnumerable<Room> rooms)
        {
            // Match by level + room number first
            var matchingSpace = spaces.FirstOrDefault(s =>
                s.Level?.Id == room.Level?.Id &&
                string.Equals(s.Number, room.Number, StringComparison.OrdinalIgnoreCase));

            if (matchingSpace != null) return matchingSpace.Id;

            // Fall back to matching room directly
            var matchingRoom = rooms.FirstOrDefault(r => r.Id == room.Id);
            return matchingRoom?.Id;
        }

        private double GetStoredPressure(ElementId id)
        {
            var element = _doc.GetElement(id);
            if (element is Space space) return _paramService.GetRoomPressure(space);
            if (element is Room room)   return _paramService.GetRoomPressure(room);
            return 0;
        }

        private double? GetParamDouble(Element element, BuiltInParameter param)
        {
            var p = element.get_Parameter(param);
            if (p == null || !p.HasValue) return null;
            double v = p.AsDouble();
            return v > 0 ? v : (double?)null;
        }

        private double GetDoorDimension(FamilyInstance door, string paramName, BuiltInParameter bip)
        {
            var p = door.get_Parameter(bip);
            if (p != null && p.HasValue) return p.AsDouble();

            p = door.LookupParameter(paramName);
            if (p != null && p.HasValue) return p.AsDouble();

            return 0;
        }
    }

    // ── Supporting types ─────────────────────────────────────────────────────

    public class DoorConnection
    {
        public ElementId DoorId        { get; set; }
        public ElementId FromElementId { get; set; }
        public ElementId ToElementId   { get; set; }
        public double DoorWidth_m      { get; set; }
        public double DoorHeight_m     { get; set; }
    }

    public class SpacePressureResult
    {
        public ElementId ElementId     { get; set; }
        public string Name             { get; set; }
        public string Number           { get; set; }
        public double SupplyM3s        { get; set; }
        public double ReturnM3s        { get; set; }
        public double ExhaustM3s       { get; set; }
        public double NetFlowM3s       { get; set; }
        public int DoorCount           { get; set; }
        public double LeakageArea_m2   { get; set; }
        public double PressurePa       { get; set; }
        public string Notes            { get; set; }
    }

    public class PressureCalculationSummary
    {
        public List<SpacePressureResult> Results { get; set; } = new List<SpacePressureResult>();
        public string Error { get; set; }
        public bool Success => string.IsNullOrEmpty(Error);
    }
}
