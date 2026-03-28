using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using CleanroomValidator.Services;
using CleanroomValidator.UI;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CheckComplianceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // Ensure parameters exist
            var paramService = new ParameterService();
            if (!paramService.EnsureParameterExists(doc, out string paramError))
            {
                TaskDialog.Show("Cleanroom Validator", 
                    $"Could not create required parameters:\n\n{paramError}");
                return Result.Failed;
            }

            // Get spaces to check (prefer spaces over rooms)
            var spaces = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .Where(s => paramService.GetCleanlinessClass(s) != "Unclassified")
                .ToList();

            // Also get rooms from linked files
            var linkedRooms = GetLinkedRooms(doc, paramService);

            if (!spaces.Any() && !linkedRooms.Any())
            {
                TaskDialog.Show("Cleanroom Validator",
                    "No classified spaces or rooms found.\n\n" +
                    "Please assign cleanliness classes to spaces (GMP-B/C/D or ISO-6/7/8) " +
                    "and run the check again.");
                return Result.Cancelled;
            }

            // Show compliance window
            var window = new ComplianceSummaryWindow(doc, spaces, linkedRooms);
            window.ShowDialog();

            return Result.Succeeded;
        }

        private List<(Room room, Document sourceDoc, string sourceName)> GetLinkedRooms(Document doc, ParameterService paramService)
        {
            var result = new List<(Room, Document, string)>();
            var linkedDocs = RoomDataExtractor.GetLinkedDocuments(doc);

            foreach (var linkedDoc in linkedDocs)
            {
                var rooms = new FilteredElementCollector(linkedDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .OfType<Room>()
                    .Where(r => r.Area > 0)
                    .Where(r => paramService.GetCleanlinessClass(r) != "Unclassified")
                    .ToList();

                foreach (var room in rooms)
                {
                    result.Add((room, linkedDoc, linkedDoc.Title));
                }
            }

            return result;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SetCleanlinessClassCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // Ensure parameters exist
            var paramService = new ParameterService();
            if (!paramService.EnsureParameterExists(doc, out string paramError))
            {
                TaskDialog.Show("Cleanroom Validator", 
                    $"Could not create required parameters:\n\n{paramError}");
                return Result.Failed;
            }

            // Get ALL spaces in the project
            var spaces = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .ToList();

            // Get spaces from linked files (read-only display)
            var linkedDocs = RoomDataExtractor.GetLinkedDocuments(doc);
            var linkedSpaces = new List<(Space space, string sourceName)>();
            foreach (var linkedDoc in linkedDocs)
            {
                var linkedSpacesList = new FilteredElementCollector(linkedDoc)
                    .OfClass(typeof(SpatialElement))
                    .OfType<Space>()
                    .Where(s => s.Area > 0)
                    .ToList();

                foreach (var space in linkedSpacesList)
                {
                    linkedSpaces.Add((space, linkedDoc.Title));
                }
            }

            if (!spaces.Any() && !linkedSpaces.Any())
            {
                TaskDialog.Show("Cleanroom Validator", 
                    "No spaces found in the project or linked files.\n\n" +
                    "Please use 'Create Spaces' to create MEP spaces from rooms first.");
                return Result.Cancelled;
            }

            // Show the Space Classification window
            var window = new RoomClassificationWindow(doc, spaces, linkedSpaces);
            var result = window.ShowDialog();

            if (result == true && window.ChangesApplied)
            {
                var changes = window.GetChanges();
                
                if (changes.Any())
                {
                    int successCount = 0;
                    int failCount = 0;
                    var failedSpaces = new List<string>();

                    using (var trans = new Transaction(doc, "Set Space Classifications"))
                    {
                        trans.Start();
                        
                        foreach (var change in changes)
                        {
                            var space = doc.GetElement(change.Key) as Space;
                            if (space != null)
                            {
                                var param = space.LookupParameter("Cleanliness_Class");
                                if (param != null && !param.IsReadOnly)
                                {
                                    param.Set(change.Value);
                                    successCount++;
                                }
                                else
                                {
                                    failCount++;
                                    failedSpaces.Add(space.Number ?? space.Id.ToString());
                                }
                            }
                        }
                        
                        trans.Commit();
                    }

                    string resultMsg = $"Successfully updated {successCount} space(s).";
                    if (failCount > 0)
                    {
                        resultMsg += $"\n\nFailed to update {failCount} space(s) - parameter not found or read-only:";
                        resultMsg += $"\n{string.Join(", ", failedSpaces.Take(10))}";
                        if (failedSpaces.Count > 10)
                            resultMsg += $"... and {failedSpaces.Count - 10} more";
                    }
                    
                    TaskDialog.Show("Cleanroom Validator", resultMsg);
                }
                else
                {
                    TaskDialog.Show("Cleanroom Validator", "No changes were detected.");
                }
            }

            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateSpacesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // Ensure parameters exist
            var paramService = new ParameterService();
            if (!paramService.EnsureParameterExists(doc, out string paramError))
            {
                TaskDialog.Show("Cleanroom Validator", 
                    $"Could not create required parameters:\n\n{paramError}");
                return Result.Failed;
            }

            // Get local rooms
            var localRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            // Get linked documents with rooms
            var linkedDocs = RoomDataExtractor.GetLinkedDocuments(doc);
            var linkedRoomsByDoc = new Dictionary<Document, List<Room>>();
            
            foreach (var linkedDoc in linkedDocs)
            {
                var linkedRooms = new FilteredElementCollector(linkedDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .OfType<Room>()
                    .Where(r => r.Area > 0)
                    .ToList();
                
                if (linkedRooms.Any())
                {
                    linkedRoomsByDoc[linkedDoc] = linkedRooms;
                }
            }

            if (!localRooms.Any() && !linkedRoomsByDoc.Any())
            {
                TaskDialog.Show("Cleanroom Validator", "No rooms found in the project or linked files.");
                return Result.Cancelled;
            }

            // Show selection dialog with linked model options
            var dialog = new TaskDialog("Create Spaces from Rooms")
            {
                MainInstruction = "Select room source for creating MEP spaces",
                MainContent = "Choose where to read rooms from:\n\n" +
                              "• Classified rooms: Space height limited to ceiling\n" +
                              "• Unclassified rooms: Space uses full room height\n\n" +
                              "Next step: Map rooms to Space Types.",
                AllowCancellation = true
            };

            // Add command links for each option
            if (localRooms.Any())
            {
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, 
                    $"Local Rooms ({localRooms.Count})",
                    "Create spaces from rooms in the current project");
            }

            int linkIndex = 2;
            var linkIdMap = new Dictionary<TaskDialogCommandLinkId, Document>();
            
            foreach (var kvp in linkedRoomsByDoc)
            {
                var linkId = (TaskDialogCommandLinkId)linkIndex;
                dialog.AddCommandLink(linkId, 
                    $"{kvp.Key.Title} ({kvp.Value.Count} rooms)",
                    "Create spaces from rooms in this linked model");
                linkIdMap[linkId] = kvp.Key;
                linkIndex++;
            }

            // Add "All" option if there are multiple sources
            if (localRooms.Any() && linkedRoomsByDoc.Any())
            {
                int totalRooms = localRooms.Count + linkedRoomsByDoc.Values.Sum(l => l.Count);
                dialog.AddCommandLink((TaskDialogCommandLinkId)linkIndex,
                    $"All Sources ({totalRooms} rooms)",
                    "Create spaces from all available rooms");
            }

            var dialogResult = dialog.Show();

            if (dialogResult == TaskDialogResult.Cancel)
                return Result.Cancelled;

            // Determine which rooms to process and their source name
            List<Room> roomsToProcess = new List<Room>();
            string sourceName = "Local";
            
            if (dialogResult == TaskDialogResult.CommandLink1 && localRooms.Any())
            {
                roomsToProcess = localRooms;
                sourceName = "Local";
            }
            else if (linkIdMap.ContainsKey((TaskDialogCommandLinkId)dialogResult))
            {
                var selectedDoc = linkIdMap[(TaskDialogCommandLinkId)dialogResult];
                roomsToProcess = linkedRoomsByDoc[selectedDoc];
                sourceName = selectedDoc.Title;
            }
            else if ((int)dialogResult == linkIndex) // "All" option
            {
                roomsToProcess.AddRange(localRooms);
                foreach (var kvp in linkedRoomsByDoc)
                {
                    roomsToProcess.AddRange(kvp.Value);
                }
                sourceName = "All Sources";
            }
            else
            {
                return Result.Cancelled;
            }

            if (!roomsToProcess.Any())
            {
                TaskDialog.Show("Cleanroom Validator", "No rooms selected for space creation.");
                return Result.Cancelled;
            }

            // Show Space Type Mapping window
            var mappingWindow = new SpaceTypeMappingWindow(doc, roomsToProcess, sourceName);
            var mappingResult = mappingWindow.ShowDialog();

            if (mappingResult != true || !mappingWindow.DialogConfirmed)
            {
                return Result.Cancelled;
            }

            var mappings = mappingWindow.Mappings;
            var mappingService = mappingWindow.MappingService;

            // Create spaces with Space Type assignments
            var spaceService = new SpaceCreationService(doc);
            int created = 0;
            int skipped = 0;
            int failed = 0;
            int typesApplied = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "Create Spaces from Rooms"))
            {
                trans.Start();

                // Create spaces and apply types
                foreach (var mapping in mappings)
                {
                    var room = roomsToProcess.FirstOrDefault(r => r.Id == mapping.RoomId);
                    if (room == null)
                        continue;

                    var result = spaceService.CreateSpaceFromRoom(room);
                    
                    if (result.Success)
                    {
                        if (result.ErrorMessage == "Space already exists")
                        {
                            skipped++;
                        }
                        else
                        {
                            created++;
                        }

                        // Apply Space Type and cleanroom parameters if specified
                        if (result.CreatedSpace != null)
                        {
                            var spaceTypeName = mappingWindow.GetSpaceTypeNameForMapping(mapping);
                            bool applyCleanroom = mapping.ApplyCleanroomParams && mapping.IsClassified;
                            
                            if (mappingService.ApplySpaceTypeAndParameters(
                                result.CreatedSpace, 
                                spaceTypeName, 
                                applyCleanroom ? mapping.CleanlinessClass : null))
                            {
                                typesApplied++;
                            }
                        }
                    }
                    else
                    {
                        failed++;
                        errors.Add($"{result.RoomNumber}: {result.ErrorMessage}");
                    }
                }

                trans.Commit();
            }

            // Show results
            string resultMsg = $"Space creation complete:\n\n" +
                               $"• Created: {created}\n" +
                               $"• Skipped (already exist): {skipped}\n" +
                               $"• Failed: {failed}\n" +
                               $"• Space Types/Parameters applied: {typesApplied}";

            if (errors.Any())
            {
                resultMsg += "\n\nErrors:\n" + string.Join("\n", errors.Take(5));
                if (errors.Count > 5)
                    resultMsg += $"\n... and {errors.Count - 5} more";
            }

            TaskDialog.Show("Cleanroom Validator", resultMsg);

            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SetSpaceTypeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // Get all spaces in the project
            var spaces = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .ToList();

            if (!spaces.Any())
            {
                TaskDialog.Show("Cleanroom Validator", 
                    "No spaces found in the project.\n\n" +
                    "Please use 'Create Spaces' to create MEP spaces from rooms first.");
                return Result.Cancelled;
            }

            // Show Space Type window
            var window = new SetSpaceTypeWindow(doc, spaces);
            var result = window.ShowDialog();

            if (result != true || !window.DialogConfirmed)
            {
                return Result.Cancelled;
            }

            var mappings = window.Mappings;
            var mappingService = window.MappingService;

            int updated = 0;
            int failed = 0;
            var failureReasons = new List<string>();

            using (var trans = new Transaction(doc, "Set Space Types"))
            {
                trans.Start();

                foreach (var mapping in mappings)
                {
                    var space = doc.GetElement(mapping.SpaceId) as Space;
                    if (space == null)
                        continue;

                    bool applyCleanroom = mapping.ApplyCleanroomParams && mapping.IsClassified;
                    string failureReason;
                    
                    if (mappingService.ApplySpaceTypeAndParameters(
                        space, 
                        mapping.SelectedSpaceTypeName, 
                        applyCleanroom ? mapping.CleanlinessClass : null,
                        out failureReason))
                    {
                        updated++;
                    }
                    else
                    {
                        failed++;
                        if (!string.IsNullOrEmpty(failureReason) && !failureReasons.Contains(failureReason))
                        {
                            failureReasons.Add(failureReason);
                        }
                    }
                }

                trans.Commit();
            }

            string resultMessage = $"Space Type update complete:\n\n" +
                $"• Updated: {updated}\n" +
                $"• Failed: {failed}";

            if (failureReasons.Count > 0)
            {
                resultMessage += "\n\nFailure reasons:\n";
                foreach (var reason in failureReasons.Take(5)) // Limit to first 5 unique reasons
                {
                    resultMessage += $"• {reason}\n";
                }
                if (failureReasons.Count > 5)
                {
                    resultMessage += $"... and {failureReasons.Count - 5} more";
                }
            }

            TaskDialog.Show("Cleanroom Validator", resultMessage);


            return Result.Succeeded;
        }
    }
}
