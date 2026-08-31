using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Data;
using CleanroomValidator.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Services
{
    /// <summary>
    /// Sizes supply and return/exhaust airflow for each cleanroom space from first principles:
    ///
    ///   1. Read room volume + target ACH  → total supply (ft³/s)
    ///   2. Read target pressure + door leakage area → net leakage flow (m³/s)
    ///   3. Derive return/exhaust = supply − leakage   (or supply + |leakage| for negative pressure)
    ///   4. Find every supply / return / exhaust air terminal in the space
    ///   5. Distribute the totals equally across each terminal group
    ///   6. Write RBS_DUCT_FLOW_PARAM on every terminal (ft³/s — Revit internal unit)
    ///   7. Write the resulting aggregate values back to the MEP space parameters
    ///   8. Return a report so the compliance grid can re-validate immediately
    ///
    /// All calculations use Revit internal units (ft³/s, ft³) and convert only for physics
    /// that require SI (orifice equation uses m³/s, m², Pa, kg/m³).
    /// </summary>
    public class AirflowSizerService
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const double Cd  = 0.65;          // door gap discharge coefficient (EU GMP)
        private const double Rho = 1.2;            // air density kg/m³

        // Door gap geometry
        private const double DoorGapHeight_m = 0.003;   // 3 mm undercut (EU GMP / ISPE)
        private const double DoorSideGap_m   = 0.001;   // 1 mm side gap per side

        // Conversion
        private const double Ft3s_to_M3s = 0.0283168;   // ft³/s → m³/s
        private const double M3s_to_Ft3s = 1.0 / Ft3s_to_M3s;
        private const double Ft3_to_M3   = 0.0283168;   // ft³  → m³

        // Minimum leakage area used when no doors are found (10 cm² wall infiltration fallback)
        private const double MinLeakageArea_m2 = 0.001;

        private readonly Document _doc;
        private readonly ParameterService _paramService;

        public AirflowSizerService(Document doc)
        {
            _doc          = doc;
            _paramService = new ParameterService();
        }

        // ── Public result types ───────────────────────────────────────────────

        public class SizingResult
        {
            /// <summary>Revit ElementId of the space/room that was sized.</summary>
            public ElementId SpaceId             { get; set; }
            public string    SpaceName           { get; set; }
            public string    SpaceNumber         { get; set; }

            // Inputs used
            public double VolumeFt3              { get; set; }
            public int    TargetAch              { get; set; }
            public double TargetPressurePa       { get; set; }
            public double LeakageArea_m2         { get; set; }

            // Calculated totals (ft³/s — Revit internal)
            public double SupplyFt3s             { get; set; }
            public double ReturnFt3s             { get; set; }
            public double ExhaustFt3s            { get; set; }

            // Terminal counts written
            public int SupplyTerminalsUpdated    { get; set; }
            public int ReturnTerminalsUpdated    { get; set; }
            public int ExhaustTerminalsUpdated   { get; set; }

            // Mass balance check: Supply − Return − Exhaust should equal the
            // leakage offset implied by the target pressure, within tolerance.
            // This confirms internal self-consistency of the calculation —
            // not that the physical ductwork can actually deliver it.
            public double ExpectedOffsetFt3s     { get; set; }
            public double ActualOffsetFt3s       => SupplyFt3s - ReturnFt3s - ExhaustFt3s;
            public double MassBalanceErrorFt3s   => Math.Abs(ActualOffsetFt3s - ExpectedOffsetFt3s);
            public bool   MassBalanceOk          => MassBalanceErrorFt3s < 0.01; // ~17 m³/h tolerance

            // Warnings / skips
            public string Warning                { get; set; }
            public bool   Skipped                { get; set; }
        }

        public class SizingReport
        {
            public List<SizingResult> Results { get; set; } = new();
            public string             Error   { get; set; }
            public bool               Success => string.IsNullOrEmpty(Error);

            public int SpacesSized    => Results.Count(r => !r.Skipped);
            public int SpacesSkipped  => Results.Count(r =>  r.Skipped);
            public int TerminalsTotal => Results.Sum(r =>
                r.SupplyTerminalsUpdated + r.ReturnTerminalsUpdated + r.ExhaustTerminalsUpdated);
        }

        // ── Entry point ───────────────────────────────────────────────────────

        /// <summary>
        /// Sizes all supplied spaces and linked rooms. Wraps everything in a single
        /// Revit transaction so it can be rolled back atomically on any error.
        ///
        /// <paramref name="achTargetOverrides"/> lets the caller supply a per-space ACH
        /// target — typically whatever the engineer has typed into the grid's ACH Target
        /// column. If a space's ElementId is not present in the dictionary (or the value
        /// is &lt;= 0), the standards-database minimum ACH for its cleanliness class is
        /// used as the fallback target, exactly as before.
        /// </summary>
        public SizingReport SizeAll(
            IEnumerable<Space>                                         spaces,
            IEnumerable<(Room room, Document sourceDoc, string name)>  linkedRooms,
            Dictionary<ElementId, double>                              achTargetOverrides = null)
        {
            achTargetOverrides ??= new Dictionary<ElementId, double>();
            var report = new SizingReport();

            // Build door map once (covers both spaces and rooms)
            var allSpaces = new FilteredElementCollector(_doc)
                .OfClass(typeof(SpatialElement)).OfType<Space>()
                .Where(s => s.Area > 0).ToList();
            var allRooms = new FilteredElementCollector(_doc)
                .OfClass(typeof(SpatialElement)).OfType<Room>()
                .Where(r => r.Area > 0).ToList();
            var doorMap = BuildDoorAdjacencyMap(allSpaces, allRooms);

            using (var trans = new Transaction(_doc, "CleanroomValidator — Size Airflow Terminals"))
            {
                trans.Start();
                try
                {
                    foreach (var space in spaces)
                    {
                        double? overrideAch = achTargetOverrides.TryGetValue(space.Id, out double v) && v > 0
                            ? v : (double?)null;
                        report.Results.Add(SizeSpace(space, doorMap, overrideAch));
                    }

                    foreach (var (room, _, _) in linkedRooms)
                    {
                        double? overrideAch = achTargetOverrides.TryGetValue(room.Id, out double v) && v > 0
                            ? v : (double?)null;
                        report.Results.Add(SizeRoom(room, doorMap, overrideAch));
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    report.Error = ex.Message;
                }
            }

            return report;
        }

        // ── Space sizing ──────────────────────────────────────────────────────

        private SizingResult SizeSpace(Space space, Dictionary<ElementId, List<DoorConnection>> doorMap,
            double? overrideAch = null)
        {
            var result = new SizingResult
            {
                SpaceId     = space.Id,
                SpaceName   = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                SpaceNumber = space.Number ?? "-"
            };

            // ── Step 1: Volume ────────────────────────────────────────────────
            double volFt3 = space.get_Parameter(BuiltInParameter.ROOM_VOLUME)?.AsDouble() ?? 0;
            result.VolumeFt3 = volFt3;
            if (volFt3 <= 0)
            {
                result.Skipped = true;
                result.Warning = "Volume is zero — space may be unplaced.";
                return result;
            }

            // ── Step 2: Target ACH ─────────────────────────────────────────────
            // Prefer the engineer's ACH Target (grid override) if one was supplied;
            // otherwise fall back to the standards-database minimum for the
            // assigned cleanliness class.
            var cls = CleanlinessClass.Parse(_paramService.GetCleanlinessClass(space));
            var req = StandardsDatabase.GetRequirements(cls);
            int targetAch = overrideAch.HasValue ? (int)Math.Round(overrideAch.Value) : req.MinAch;
            result.TargetAch = targetAch;

            if (targetAch <= 0)
            {
                result.Skipped = true;
                result.Warning = "No ACH requirement (Unclassified). Assign a cleanliness class first.";
                return result;
            }

            // ── Step 3: Target pressure ───────────────────────────────────────
            double targetPressurePa = _paramService.GetRoomPressure(space);
            result.TargetPressurePa = targetPressurePa;

            // ── Step 4: Calculate supply (ft³/s) ──────────────────────────────
            //   ACH = supply_ft3s × 3600 / volFt3  →  supply = ACH × vol / 3600
            double supplyFt3s = targetAch * volFt3 / 3600.0;
            result.SupplyFt3s = supplyFt3s;

            // ── Step 5: Calculate leakage and derive return/exhaust ───────────
            doorMap.TryGetValue(space.Id, out var doors);
            double leakArea = EstimateLeakageArea(doors);
            result.LeakageArea_m2 = leakArea;

            CalculateReturnExhaust(
                space.Id, supplyFt3s, targetPressurePa, leakArea,
                out double returnFt3s, out double exhaustFt3s, out bool exhaustOnly,
                out double netOffsetFt3s);

            result.ReturnFt3s        = returnFt3s;
            result.ExhaustFt3s       = exhaustFt3s;
            result.ExpectedOffsetFt3s = netOffsetFt3s;

            // ── Step 6: Write to MEP space parameters ─────────────────────────
            WriteSpaceAirflowParams(space, supplyFt3s, returnFt3s, exhaustFt3s);

            // ── Step 7: Distribute across terminals ───────────────────────────
            DistributeToTerminals(space.Id, supplyFt3s, returnFt3s, exhaustFt3s, exhaustOnly,
                out int supCnt, out int retCnt, out int exhCnt);

            result.SupplyTerminalsUpdated  = supCnt;
            result.ReturnTerminalsUpdated  = retCnt;
            result.ExhaustTerminalsUpdated = exhCnt;

            if (supCnt == 0)
                result.Warning = "No supply terminals found in this space — manual assignment needed.";

            return result;
        }

        // ── Room sizing (architectural) ───────────────────────────────────────

        private SizingResult SizeRoom(Room room, Dictionary<ElementId, List<DoorConnection>> doorMap,
            double? overrideAch = null)
        {
            var result = new SizingResult
            {
                SpaceId     = room.Id,
                SpaceName   = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed",
                SpaceNumber = room.Number ?? "-"
            };

            // Volume — use ceiling-clipped volume from CeilingDetectionService if available,
            // otherwise fall back to raw room volume (ft³ from Revit internal units)
            double volFt3 = room.get_Parameter(BuiltInParameter.ROOM_VOLUME)?.AsDouble() ?? 0;
            result.VolumeFt3 = volFt3;
            if (volFt3 <= 0)
            {
                result.Skipped = true;
                result.Warning = "Volume is zero — room may be unplaced.";
                return result;
            }

            // Prefer the engineer's ACH Target (grid override) if supplied;
            // otherwise fall back to the standards-database minimum.
            var cls = CleanlinessClass.Parse(_paramService.GetCleanlinessClass(room));
            var req = StandardsDatabase.GetRequirements(cls);
            int targetAch = overrideAch.HasValue ? (int)Math.Round(overrideAch.Value) : req.MinAch;
            result.TargetAch = targetAch;

            if (targetAch <= 0)
            {
                result.Skipped = true;
                result.Warning = "No ACH requirement (Unclassified). Assign a cleanliness class first.";
                return result;
            }

            double targetPressurePa = _paramService.GetRoomPressure(room);
            result.TargetPressurePa = targetPressurePa;

            double supplyFt3s = targetAch * volFt3 / 3600.0;
            result.SupplyFt3s = supplyFt3s;

            doorMap.TryGetValue(room.Id, out var doors);
            double leakArea = EstimateLeakageArea(doors);
            result.LeakageArea_m2 = leakArea;

            CalculateReturnExhaust(
                room.Id, supplyFt3s, targetPressurePa, leakArea,
                out double returnFt3s, out double exhaustFt3s, out bool exhaustOnly,
                out double netOffsetFt3s);

            result.ReturnFt3s        = returnFt3s;
            result.ExhaustFt3s       = exhaustFt3s;
            result.ExpectedOffsetFt3s = netOffsetFt3s;

            // Distribute across terminals — rooms share the same terminal collector path
            DistributeToTerminals(room.Id, supplyFt3s, returnFt3s, exhaustFt3s, exhaustOnly,
                out int supCnt, out int retCnt, out int exhCnt);

            result.SupplyTerminalsUpdated  = supCnt;
            result.ReturnTerminalsUpdated  = retCnt;
            result.ExhaustTerminalsUpdated = exhCnt;

            if (supCnt == 0)
                result.Warning = "No supply terminals found in this room — manual assignment needed.";

            return result;
        }

        // ── Core physics: derive return/exhaust from supply and target pressure ──

        /// <summary>
        /// Given supply and the target pressure, computes return and exhaust needed to
        /// achieve that pressure through the door leakage path.
        ///
        /// Mass balance:  Q_net = Q_supply − Q_return − Q_exhaust
        /// Orifice model: Q_net = Cd × A × sqrt(2 × |ΔP| / ρ)  [signed by pressure sign]
        ///
        /// Strategy:
        ///   • exhaustOnly = true if no return terminals exist (exhaust-only design)
        ///   • Positive target pressure → supply > return + exhaust (net exfiltration)
        ///   • Negative target pressure → supply < return + exhaust (net infiltration — uncommon
        ///     but supported, e.g. containment rooms)
        /// </summary>
        private void CalculateReturnExhaust(
            ElementId spaceId,
            double    supplyFt3s,
            double    targetPressurePa,
            double    leakageArea_m2,
            out double returnFt3s,
            out double exhaustFt3s,
            out bool   exhaustOnly,
            out double netOffsetFt3s)
        {
            // Detect whether this space has return or exhaust terminals
            exhaustOnly = !SpaceHasReturnTerminals(spaceId);

            double supplyM3s = supplyFt3s * Ft3s_to_M3s;

            // Net leakage flow through door gap(s) to achieve target pressure:
            //   Q_net  = sign(ΔP) × Cd × A × sqrt(2 × |ΔP| / ρ)
            double netM3s = 0;
            if (leakageArea_m2 > 0 && Math.Abs(targetPressurePa) > 0.01)
            {
                double absPa = Math.Abs(targetPressurePa);
                netM3s = Math.Sign(targetPressurePa)
                         * Cd * leakageArea_m2
                         * Math.Sqrt(2.0 * absPa / Rho);
            }

            netOffsetFt3s = netM3s * M3s_to_Ft3s;

            // Q_return_or_exhaust = Q_supply − Q_net
            //   Positive pressure → net > 0 → return < supply  ✓
            //   Zero target pressure → return == supply (perfectly balanced)
            //   Negative pressure (containment) → net < 0 → return > supply
            double extractM3s = Math.Max(0, supplyM3s - netM3s);

            if (exhaustOnly)
            {
                returnFt3s  = 0;
                exhaustFt3s = extractM3s * M3s_to_Ft3s;
            }
            else
            {
                // Split: return carries extraction, exhaust stays at zero unless separately
                // assigned; cleaner to leave exhaust at 0 and let engineers add it explicitly.
                returnFt3s  = extractM3s * M3s_to_Ft3s;
                exhaustFt3s = 0;
            }
        }

        // ── Terminal distribution ─────────────────────────────────────────────

        private void DistributeToTerminals(
            ElementId spaceId,
            double    supplyFt3s,
            double    returnFt3s,
            double    exhaustFt3s,
            bool      exhaustOnly,
            out int   supCnt,
            out int   retCnt,
            out int   exhCnt)
        {
            supCnt = retCnt = exhCnt = 0;

            var terminals = GetTerminalsInSpace(spaceId);
            if (!terminals.Any()) return;

            var supplyTerminals  = terminals.Where(t => IsSystemType(t, DuctSystemType.SupplyAir)).ToList();
            var returnTerminals  = terminals.Where(t => IsSystemType(t, DuctSystemType.ReturnAir)).ToList();
            var exhaustTerminals = terminals.Where(t => IsSystemType(t, DuctSystemType.ExhaustAir)).ToList();

            // Fallback: if nothing has a system type, treat all as supply
            if (!supplyTerminals.Any() && !returnTerminals.Any() && !exhaustTerminals.Any())
                supplyTerminals = terminals;

            supCnt = SetTerminalFlows(supplyTerminals,  supplyFt3s);
            retCnt = SetTerminalFlows(returnTerminals,  returnFt3s);
            exhCnt = SetTerminalFlows(exhaustTerminals, exhaustFt3s);
        }

        /// <summary>
        /// Splits <paramref name="totalFt3s"/> equally across the terminal list and sets
        /// RBS_DUCT_FLOW_PARAM (or the first writeable flow param found) on each terminal.
        /// Returns the number of terminals successfully updated.
        /// </summary>
        private int SetTerminalFlows(List<FamilyInstance> terminals, double totalFt3s)
        {
            if (!terminals.Any() || totalFt3s < 0) return 0;

            double perTerminal = totalFt3s / terminals.Count;
            int count = 0;

            foreach (var t in terminals)
            {
                var p = t.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM)
                     ?? t.LookupParameter("Flow")
                     ?? t.LookupParameter("Airflow")
                     ?? t.LookupParameter("Air Flow");

                if (p != null && !p.IsReadOnly)
                {
                    p.Set(perTerminal);   // ft³/s — Revit internal unit
                    count++;
                }
            }

            return count;
        }

        // ── MEP Space parameter write-back ────────────────────────────────────

        /// <summary>
        /// Writes the sized totals back to the space's DESIGN supply/return/exhaust airflow
        /// parameters so that Revit's built-in schedule and load calculations see the new values.
        /// Must be called inside an active transaction.
        /// </summary>
        private void WriteSpaceAirflowParams(Space space, double supplyFt3s, double returnFt3s, double exhaustFt3s)
        {
            SetSpaceParam(space, BuiltInParameter.ROOM_DESIGN_SUPPLY_AIRFLOW_PARAM,  supplyFt3s);
            SetSpaceParam(space, BuiltInParameter.ROOM_DESIGN_RETURN_AIRFLOW_PARAM,  returnFt3s);
            SetSpaceParam(space, BuiltInParameter.ROOM_DESIGN_EXHAUST_AIRFLOW_PARAM, exhaustFt3s);
        }

        private static void SetSpaceParam(Space space, BuiltInParameter bip, double valueFt3s)
        {
            var p = space.get_Parameter(bip);
            if (p != null && !p.IsReadOnly)
                p.Set(valueFt3s);
        }

        // ── Terminal discovery ────────────────────────────────────────────────

        private List<FamilyInstance> GetTerminalsInSpace(ElementId spaceId)
        {
            var mechEquip = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            var ductTerminals = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_DuctTerminal)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            return mechEquip.Concat(ductTerminals)
                .Where(t => IsTerminalInElement(t, spaceId))
                .ToList();
        }

        private bool IsTerminalInElement(FamilyInstance terminal, ElementId spaceId)
        {
            try
            {
                if (!(terminal.Location is LocationPoint lp)) return false;

                XYZ pt = lp.Point;

                // Probe at the terminal's own Z, then step down in case the terminal
                // is mounted at ceiling level above the Space boundary.
                double[] zOffsets = { 0, -0.5, -1.0, -2.0, -3.0 };
                foreach (double dz in zOffsets)
                {
                    XYZ probe = dz == 0 ? pt : new XYZ(pt.X, pt.Y, pt.Z + dz);

                    var space = _doc.GetSpaceAtPoint(probe);
                    if (space?.Id == spaceId) return true;

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

        private bool SpaceHasReturnTerminals(ElementId spaceId)
        {
            return GetTerminalsInSpace(spaceId)
                .Any(t => IsSystemType(t, DuctSystemType.ReturnAir));
        }

        private bool IsSystemType(FamilyInstance terminal, DuctSystemType targetType)
        {
            try
            {
                // Primary: live duct system via connector
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

                // Fallback A: RBS_SYSTEM_CLASSIFICATION_PARAM (unconnected terminals)
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

        // ── Door leakage area ─────────────────────────────────────────────────

        private static double EstimateLeakageArea(List<DoorConnection> doors)
        {
            if (doors == null || !doors.Any())
                return MinLeakageArea_m2;

            double total = 0;
            foreach (var door in doors)
            {
                double width  = door.DoorWidth_m  > 0 ? door.DoorWidth_m  : 0.9;  // 900 mm default
                double height = door.DoorHeight_m > 0 ? door.DoorHeight_m : 2.1;  // 2100 mm default

                double undercut  = width  * DoorGapHeight_m;                // bottom gap
                double sideGaps  = 2.0 * height * DoorSideGap_m;           // two side gaps
                total += undercut + sideGaps;
            }
            return total;
        }

        // ── Door adjacency map ────────────────────────────────────────────────
        // (Mirrors PressureCalculationService.BuildDoorAdjacencyMap — kept here so
        //  AirflowSizerService is self-contained and doesn't need a running pressure
        //  service instance.)

        private Dictionary<ElementId, List<DoorConnection>> BuildDoorAdjacencyMap(
            IEnumerable<Space> spaces, IEnumerable<Room> rooms)
        {
            var map = new Dictionary<ElementId, List<DoorConnection>>();

            var allDoors = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            var spaceList = spaces.ToList();
            var roomList  = rooms.ToList();

            foreach (var door in allDoors)
            {
                var fromRoom = door.get_FromRoom(null);
                var toRoom   = door.get_ToRoom(null);
                if (fromRoom == null || toRoom == null) continue;

                double w = GetDoorParam(door, BuiltInParameter.DOOR_WIDTH)  * 0.3048;
                double h = GetDoorParam(door, BuiltInParameter.DOOR_HEIGHT) * 0.3048;

                ElementId fromId = FindElementId(fromRoom, spaceList, roomList);
                ElementId toId   = FindElementId(toRoom,   spaceList, roomList);
                if (fromId == null || toId == null) continue;

                var conn = new DoorConnection
                {
                    DoorId        = door.Id,
                    FromElementId = fromId,
                    ToElementId   = toId,
                    DoorWidth_m   = w,
                    DoorHeight_m  = h
                };

                AddToMap(map, fromId, conn);
                AddToMap(map, toId,   conn);
            }

            return map;
        }

        private static void AddToMap(Dictionary<ElementId, List<DoorConnection>> map,
            ElementId id, DoorConnection conn)
        {
            if (!map.ContainsKey(id)) map[id] = new List<DoorConnection>();
            map[id].Add(conn);
        }

        private static ElementId FindElementId(Room room,
            IEnumerable<Space> spaces, IEnumerable<Room> rooms)
        {
            var s = spaces.FirstOrDefault(sp =>
                sp.Level?.Id == room.Level?.Id &&
                string.Equals(sp.Number, room.Number, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Id;

            return rooms.FirstOrDefault(r => r.Id == room.Id)?.Id;
        }

        private static double GetDoorParam(FamilyInstance door, BuiltInParameter bip)
        {
            var p = door.get_Parameter(bip);
            return (p != null && p.HasValue) ? p.AsDouble() : 0;
        }
    }
}
