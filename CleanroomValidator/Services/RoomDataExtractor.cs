using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Services
{
    public class RoomDataExtractor
    {
        private readonly Document _doc;
        private readonly Document _linkedMechanicalDoc;

        public RoomDataExtractor(Document doc, Document linkedMechanicalDoc = null)
        {
            _doc = doc;
            _linkedMechanicalDoc = linkedMechanicalDoc;
        }

        public double GetRoomVolumeCubicFeet(Room room)
        {
            var volumeParam = room.get_Parameter(BuiltInParameter.ROOM_VOLUME);
            if (volumeParam == null || !volumeParam.HasValue)
                return 0;

            // Volume is returned in cubic feet by Revit internal units
            return volumeParam.AsDouble();
        }

        public double GetSupplyCfm(Room room)
        {
            // Try to get from MEP space in main document
            var space = FindAssociatedSpace(room, _doc);
            if (space != null)
            {
                var cfm = GetSpaceSupplyAirflow(space);
                if (cfm > 0)
                    return cfm;
            }

            // Try linked mechanical document if provided
            if (_linkedMechanicalDoc != null)
            {
                space = FindAssociatedSpaceInLinkedDoc(room);
                if (space != null)
                {
                    return GetSpaceSupplyAirflow(space);
                }
            }

            return 0;
        }

        private Space FindAssociatedSpace(Room room, Document doc)
        {
            // Get the room's location point
            var location = room.Location as LocationPoint;
            if (location == null)
                return null;

            var point = location.Point;

            // Find space at the same location
            var collector = new FilteredElementCollector(doc);
            var spaces = collector.OfClass(typeof(SpatialElement))
                                  .OfType<Space>()
                                  .Where(s => s.Area > 0);

            foreach (var space in spaces)
            {
                var spaceLocation = space.Location as LocationPoint;
                if (spaceLocation == null)
                    continue;

                // Check if points are close enough (within tolerance)
                if (spaceLocation.Point.DistanceTo(point) < 1.0) // 1 foot tolerance
                    return space;
            }

            // Alternative: match by room name/number
            var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            var roomNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();

            foreach (var space in new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0))
            {
                var spaceName = space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                var spaceNumber = space.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();

                if (!string.IsNullOrEmpty(roomNumber) && roomNumber == spaceNumber)
                    return space;

                if (!string.IsNullOrEmpty(roomName) && roomName == spaceName)
                    return space;
            }

            return null;
        }

        private Space FindAssociatedSpaceInLinkedDoc(Room room)
        {
            if (_linkedMechanicalDoc == null)
                return null;

            return FindAssociatedSpace(room, _linkedMechanicalDoc);
        }

        private double GetSpaceSupplyAirflow(Space space)
        {
            // Try different parameters for supply airflow
            var paramNames = new[]
            {
                BuiltInParameter.ROOM_DESIGN_SUPPLY_AIRFLOW_PARAM,
                BuiltInParameter.ROOM_ACTUAL_SUPPLY_AIRFLOW_PARAM,
                BuiltInParameter.ROOM_CALCULATED_SUPPLY_AIRFLOW_PARAM
            };

            foreach (var paramId in paramNames)
            {
                var param = space.get_Parameter(paramId);
                if (param != null && param.HasValue)
                {
                    var value = param.AsDouble();
                    if (value > 0)
                    {
                        // Convert from internal units (cubic feet per second) to CFM
                        return value * 60.0;
                    }
                }
            }

            // Try custom parameter
            var customParam = space.LookupParameter("Supply_Airflow") 
                              ?? space.LookupParameter("Supply Air Flow")
                              ?? space.LookupParameter("SupplyAirflow");

            if (customParam != null && customParam.HasValue)
            {
                return customParam.AsDouble() * 60.0; // Convert to CFM if in cfs
            }

            return 0;
        }

        public double GetRoomPressure(Room room)
        {
            // Try to get pressure from associated MEP space
            var space = FindAssociatedSpace(room, _doc) 
                        ?? (_linkedMechanicalDoc != null ? FindAssociatedSpaceInLinkedDoc(room) : null);

            if (space != null)
            {
                var pressureParam = space.LookupParameter("Design_Pressure")
                                    ?? space.LookupParameter("Room_Pressure")
                                    ?? space.LookupParameter("Pressure");

                if (pressureParam != null && pressureParam.HasValue)
                {
                    return pressureParam.AsDouble(); // Assuming Pa
                }
            }

            // Try room parameter directly
            var roomPressure = room.LookupParameter("Design_Pressure")
                               ?? room.LookupParameter("Room_Pressure");

            if (roomPressure != null && roomPressure.HasValue)
            {
                return roomPressure.AsDouble();
            }

            return 0;
        }

        public List<Room> GetAllRoomsWithCleanlinessClass()
        {
            var paramService = new ParameterService();
            var collector = new FilteredElementCollector(_doc);

            return collector.OfCategory(BuiltInCategory.OST_Rooms)
                           .OfType<Room>()
                           .Where(r => r.Area > 0)
                           .Where(r => paramService.GetCleanlinessClass(r) != "Unclassified")
                           .ToList();
        }

        public static List<Document> GetLinkedDocuments(Document doc)
        {
            var linkedDocs = new List<Document>();
            var collector = new FilteredElementCollector(doc);
            var linkInstances = collector.OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>();

            foreach (var link in linkInstances)
            {
                var linkedDoc = link.GetLinkDocument();
                if (linkedDoc != null)
                {
                    linkedDocs.Add(linkedDoc);
                }
            }

            return linkedDocs;
        }
    }
}
