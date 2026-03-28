using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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
    }
}
