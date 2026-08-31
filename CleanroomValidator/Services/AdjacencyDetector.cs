using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Models;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Services
{
    public class AdjacencyDetector
    {
        private readonly Document _doc;
        private readonly ParameterService _parameterService;
        private readonly RoomDataExtractor _dataExtractor;

        public AdjacencyDetector(Document doc, RoomDataExtractor dataExtractor)
        {
            _doc = doc;
            _parameterService = new ParameterService();
            _dataExtractor = dataExtractor;
        }

        public List<AdjacentRoomInfo> GetAdjacentRooms(Room room)
        {
            var adjacentRooms = new List<AdjacentRoomInfo>();
            var processedRoomIds = new HashSet<ElementId>();

            // Get all doors that touch this room
            var doors = GetDoorsInRoom(room);

            foreach (var door in doors)
            {
                // Get rooms on both sides of the door
                var fromRoom = GetRoomFromDoor(door, door.FromRoom);
                var toRoom = GetRoomFromDoor(door, door.ToRoom);

                // Find the adjacent room (the one that isn't our current room)
                Room adjacentRoom = null;
                if (fromRoom != null && fromRoom.Id != room.Id && !processedRoomIds.Contains(fromRoom.Id))
                {
                    adjacentRoom = fromRoom;
                }
                else if (toRoom != null && toRoom.Id != room.Id && !processedRoomIds.Contains(toRoom.Id))
                {
                    adjacentRoom = toRoom;
                }

                if (adjacentRoom != null && adjacentRoom.Area > 0)
                {
                    processedRoomIds.Add(adjacentRoom.Id);

                    var cleanlinessValue = _parameterService.GetCleanlinessClass(adjacentRoom);
                    var cleanlinessClass = CleanlinessClass.Parse(cleanlinessValue);

                    adjacentRooms.Add(new AdjacentRoomInfo
                    {
                        RoomId = adjacentRoom.Id,
                        RoomName = GetRoomDisplayName(adjacentRoom),
                        CleanlinessClass = cleanlinessClass,
                        Pressure = _dataExtractor.GetRoomPressure(adjacentRoom)
                    });
                }
            }

            return adjacentRooms;
        }

        private List<FamilyInstance> GetDoorsInRoom(Room room)
        {
            var doors = new List<FamilyInstance>();
            var collector = new FilteredElementCollector(_doc);
            var allDoors = collector.OfCategory(BuiltInCategory.OST_Doors)
                                    .OfClass(typeof(FamilyInstance))
                                    .Cast<FamilyInstance>();

            foreach (var door in allDoors)
            {
                var fromRoom = GetRoomFromDoor(door, door.FromRoom);
                var toRoom = GetRoomFromDoor(door, door.ToRoom);

                if ((fromRoom != null && fromRoom.Id == room.Id) ||
                    (toRoom != null && toRoom.Id == room.Id))
                {
                    doors.Add(door);
                }
            }

            return doors;
        }

        private Room GetRoomFromDoor(FamilyInstance door, Room phaseRoom)
        {
            if (phaseRoom != null)
                return phaseRoom;

            // Try to get room using phases
            var phases = new FilteredElementCollector(_doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .ToList();

            foreach (var phase in phases)
            {
                try
                {
                    var fromRoom = door.get_FromRoom(phase);
                    var toRoom = door.get_ToRoom(phase);

                    if (fromRoom != null || toRoom != null)
                    {
                        return fromRoom ?? toRoom;
                    }
                }
                catch
                {
                    // Phase might not be valid for this door
                }
            }

            return null;
        }

        private string GetRoomDisplayName(Room room)
        {
            var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            var number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";

            if (!string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(name))
                return $"{number} - {name}";

            return !string.IsNullOrEmpty(number) ? number : name;
        }

        public Dictionary<ElementId, List<AdjacentRoomInfo>> BuildAdjacencyMap(IEnumerable<Room> rooms)
        {
            var map = new Dictionary<ElementId, List<AdjacentRoomInfo>>();

            foreach (var room in rooms)
            {
                map[room.Id] = GetAdjacentRooms(room);
            }

            return map;
        }

        /// <summary>
        /// Returns adjacent spaces for a given MEP Space, reading pressure via ParameterService.
        /// </summary>
        public List<AdjacentRoomInfo> GetAdjacentSpaces(Space space)
        {
            var result = new List<AdjacentRoomInfo>();
            var paramService = new ParameterService();

            try
            {
                // Collect all spaces on the same level
                var allSpaces = new FilteredElementCollector(_doc)
                    .OfClass(typeof(SpatialElement))
                    .OfType<Space>()
                    .Where(s => s.Id != space.Id && s.Level?.Id == space.Level?.Id)
                    .ToList();

                var thisLocation = (space.Location as LocationPoint)?.Point;
                if (thisLocation == null) return result;

                // Use bounding box proximity as adjacency heuristic
                var thisBB = space.get_BoundingBox(null);
                if (thisBB == null) return result;

                // Expand the bounding box slightly to detect touching spaces
                double tolerance = 1.0; // 1 foot
                var expandedMin = new XYZ(thisBB.Min.X - tolerance, thisBB.Min.Y - tolerance, thisBB.Min.Z - tolerance);
                var expandedMax = new XYZ(thisBB.Max.X + tolerance, thisBB.Max.Y + tolerance, thisBB.Max.Z + tolerance);

                foreach (var candidate in allSpaces)
                {
                    var candidateBB = candidate.get_BoundingBox(null);
                    if (candidateBB == null) continue;

                    // Check if bounding boxes overlap after expansion
                    bool overlaps = candidateBB.Min.X <= expandedMax.X && candidateBB.Max.X >= expandedMin.X
                                 && candidateBB.Min.Y <= expandedMax.Y && candidateBB.Max.Y >= expandedMin.Y
                                 && candidateBB.Min.Z <= expandedMax.Z && candidateBB.Max.Z >= expandedMin.Z;

                    if (!overlaps) continue;

                    var candidateCleanlinessValue = paramService.GetCleanlinessClass(candidate);
                    var candidateClass = CleanlinessClass.Parse(candidateCleanlinessValue);
                    var candidatePressure = paramService.GetRoomPressure(candidate);

                    var name = candidate.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                    var number = candidate.Number ?? "";
                    var displayName = (!string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(name))
                        ? $"{number} - {name}"
                        : (!string.IsNullOrEmpty(number) ? number : name);

                    result.Add(new AdjacentRoomInfo
                    {
                        RoomId = candidate.Id,
                        RoomName = displayName,
                        CleanlinessClass = candidateClass,
                        Pressure = candidatePressure
                    });
                }
            }
            catch { /* Adjacency detection is best-effort */ }

            return result;
        }
    }
}
