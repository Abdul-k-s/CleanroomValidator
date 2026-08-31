using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Services
{
    /// <summary>
    /// Service to create MEP Spaces from Rooms with proper ceiling height handling
    /// </summary>
    public class SpaceCreationService
    {
        private readonly Document _doc;
        private readonly CeilingDetectionService _ceilingService;
        private readonly ParameterService _paramService;

        public SpaceCreationService(Document doc)
        {
            _doc = doc;
            var linkedDocs = RoomDataExtractor.GetLinkedDocuments(doc);
            _ceilingService = new CeilingDetectionService(doc, linkedDocs);
            _paramService = new ParameterService();
        }

        /// <summary>
        /// Result of space creation operation
        /// </summary>
        public class SpaceCreationResult
        {
            public bool Success { get; set; }
            public Space CreatedSpace { get; set; }
            public string RoomNumber { get; set; }
            public string RoomName { get; set; }
            public string ErrorMessage { get; set; }
            public double EffectiveHeight { get; set; }
            public bool UsedCeilingHeight { get; set; }
        }

        /// <summary>
        /// Create a space from a room, considering ceiling height for classified rooms
        /// </summary>
        public SpaceCreationResult CreateSpaceFromRoom(Room room)
        {
            var result = new SpaceCreationResult
            {
                RoomNumber = room.Number ?? "-",
                RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed"
            };

            try
            {
                // Check if space already exists
                var existingSpace = FindExistingSpace(room);
                if (existingSpace != null)
                {
                    result.Success = true;
                    result.CreatedSpace = existingSpace;
                    result.ErrorMessage = "Space already exists";
                    return result;
                }

                // Get room location
                var location = room.Location as LocationPoint;
                if (location == null)
                {
                    result.ErrorMessage = "Room has no valid location";
                    return result;
                }

                var point = location.Point;
                var level = room.Level;
                if (level == null)
                {
                    result.ErrorMessage = "Room has no level";
                    return result;
                }

                // Determine if room is classified
                var cleanlinessClass = _paramService.GetCleanlinessClass(room);
                bool isClassified = cleanlinessClass != "Unclassified" && !string.IsNullOrEmpty(cleanlinessClass);

                // Calculate effective height
                double effectiveHeight;
                bool usedCeilingHeight = false;

                if (isClassified)
                {
                    // For classified rooms, use ceiling height (volume stops at ceiling)
                    var ceilingResult = _ceilingService.DetectCeiling(room);
                    if (ceilingResult.HasCeiling)
                    {
                        effectiveHeight = ceilingResult.AverageHeight;
                        usedCeilingHeight = true;
                    }
                    else
                    {
                        // No ceiling found - use room's upper limit or default
                        var upperLimit = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                        effectiveHeight = upperLimit?.AsDouble() ?? 10.0; // Default 10 feet
                    }
                }
                else
                {
                    // Unclassified rooms - use full room height (ignore ceiling)
                    var unboundedHeight = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                    effectiveHeight = unboundedHeight?.AsDouble() ?? 10.0;
                    
                    // Try to get actual room height from volume/area
                    var volumeParam = room.get_Parameter(BuiltInParameter.ROOM_VOLUME);
                    var areaParam = room.get_Parameter(BuiltInParameter.ROOM_AREA);
                    if (volumeParam != null && areaParam != null)
                    {
                        double volume = volumeParam.AsDouble();
                        double area = areaParam.AsDouble();
                        if (area > 0)
                        {
                            effectiveHeight = volume / area;
                        }
                    }
                }

                result.EffectiveHeight = effectiveHeight;
                result.UsedCeilingHeight = usedCeilingHeight;

                // Create the space
                var uv = new UV(point.X, point.Y);
                var space = _doc.Create.NewSpace(level, uv);

                if (space == null)
                {
                    result.ErrorMessage = "Failed to create space";
                    return result;
                }

                // Set space properties
                SetSpaceProperties(space, room, effectiveHeight, cleanlinessClass);

                result.Success = true;
                result.CreatedSpace = space;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error: {ex.Message}";
            }

            return result;
        }

        private void SetSpaceProperties(Space space, Room room, double height, string cleanlinessClass)
        {
            // Set name
            var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            if (!string.IsNullOrEmpty(roomName))
            {
                var spaceNameParam = space.get_Parameter(BuiltInParameter.ROOM_NAME);
                if (spaceNameParam != null && !spaceNameParam.IsReadOnly)
                {
                    spaceNameParam.Set(roomName);
                }
            }

            // Set number
            var roomNumber = room.Number;
            if (!string.IsNullOrEmpty(roomNumber))
            {
                var spaceNumberParam = space.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                if (spaceNumberParam != null && !spaceNumberParam.IsReadOnly)
                {
                    spaceNumberParam.Set(roomNumber);
                }
            }

            // Set upper limit/offset for height
            var upperOffset = space.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
            if (upperOffset != null && !upperOffset.IsReadOnly)
            {
                upperOffset.Set(height);
            }

            // Copy cleanliness class if parameter exists on space
            var cleanlinessParam = space.LookupParameter("Cleanliness_Class");
            if (cleanlinessParam != null && !cleanlinessParam.IsReadOnly)
            {
                cleanlinessParam.Set(cleanlinessClass);
            }
        }

        private Space FindExistingSpace(Room room)
        {
            var roomNumber = room.Number;
            var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            var roomLocation = room.Location as LocationPoint;

            var spaces = new FilteredElementCollector(_doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0);

            foreach (var space in spaces)
            {
                // Match by number
                if (!string.IsNullOrEmpty(roomNumber) && space.Number == roomNumber)
                    return space;

                // Match by location
                if (roomLocation != null)
                {
                    var spaceLocation = space.Location as LocationPoint;
                    if (spaceLocation != null && spaceLocation.Point.DistanceTo(roomLocation.Point) < 1.0)
                        return space;
                }
            }

            return null;
        }

        /// <summary>
        /// Create spaces from multiple rooms
        /// </summary>
        public List<SpaceCreationResult> CreateSpacesFromRooms(IEnumerable<Room> rooms)
        {
            var results = new List<SpaceCreationResult>();

            foreach (var room in rooms)
            {
                results.Add(CreateSpaceFromRoom(room));
            }

            return results;
        }
    }
}
