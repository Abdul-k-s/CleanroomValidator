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

            // Open the window — it loads all spaces itself and runs compliance check internally.
            // Parameters are created lazily on first "Apply to Model" click, not at startup.
            var window = new ComplianceSummaryWindow(doc);
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
                TaskDialog.Show("Cleanroom HVAC Designer", 
                    $"Could not create required parameters:\n\n{paramError}");
                return Result.Failed;
            }

            // Auto-calculate pressures from MEP airflow + door leakage before checking
            var pressureService = new PressureCalculationService(doc);
            var pressureSummary = pressureService.CalculateAndStoreAll();
            if (!pressureSummary.Success)
            {
                TaskDialog.Show("Cleanroom HVAC Designer",
                    $"Warning: Pressure calculation failed:\n{pressureSummary.Error}\n\nCompliance check will proceed with existing pressure values.");
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
                TaskDialog.Show("Cleanroom HVAC Designer", 
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
                    
                    TaskDialog.Show("Cleanroom HVAC Designer", resultMsg);
                }
                else
                {
                    TaskDialog.Show("Cleanroom HVAC Designer", "No changes were detected.");
                }
            }

            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SetSpaceTypeCommand
 : IExternalCommand
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
                TaskDialog.Show("Cleanroom HVAC Designer", 
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

            TaskDialog.Show("Cleanroom HVAC Designer", resultMessage);


            return Result.Succeeded;
        }
    }
}
