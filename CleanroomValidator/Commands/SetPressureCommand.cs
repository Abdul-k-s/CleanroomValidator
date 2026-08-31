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
    public class SetPressureCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            var paramService = new ParameterService();
            if (!paramService.EnsureParameterExists(doc, out string paramError))
            {
                TaskDialog.Show("Cleanroom Validator",
                    $"Could not create required parameters:\n\n{paramError}");
                return Result.Failed;
            }

            // Collect spaces first, fall back to rooms
            var spaces = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .ToList();

            var rooms = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            if (!spaces.Any() && !rooms.Any())
            {
                TaskDialog.Show("Cleanroom Validator", "No rooms or spaces found in the model.");
                return Result.Cancelled;
            }

            var window = new SetPressureWindow(doc, spaces, rooms, paramService);
            window.ShowDialog();

            return Result.Succeeded;
        }
    }
}
