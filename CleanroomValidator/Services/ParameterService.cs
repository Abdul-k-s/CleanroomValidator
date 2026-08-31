using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Data;
using System;
using System.IO;
using System.Linq;

namespace CleanroomValidator.Services
{
    public class ParameterService
    {
        private const string CleanlinessClassParamName = "Cleanliness_Class";
        private const string CleanlinessClassGuid = "F7E8D9C0-B1A2-4E5F-8C7D-6A5B4C3D2E1F";

        private const string AchComputedParamName = "ACH_Computed";
        private const string AchComputedGuid = "A2B3C4D5-E6F7-8901-2345-6789ABCDEF01";

        private const string AchTargetParamName = "ACH_Target";
        private const string AchTargetGuid = "B3C4D5E6-F7A8-9012-3456-789ABCDEF012";

        private const string RoomPressureParamName = "Room_Pressure";
        private const string RoomPressureGuid = "C3D4E5F6-A7B8-4901-2345-6789ABCDEF02";

        /// <summary>
        /// Ensures all CleanroomValidator parameters exist on rooms and spaces.
        /// Must be called WITHOUT an active transaction.
        /// </summary>
        public bool EnsureParameterExists(Document doc, out string errorMessage)
        {
            errorMessage = null;

            try
            {
                bool cleanlinessExists = CheckParameterExists<Room>(doc, CleanlinessClassParamName);
                bool achExists = CheckParameterExists<Space>(doc, AchComputedParamName);
                bool pressureExists = CheckParameterExists<Room>(doc, RoomPressureParamName);

                if (!cleanlinessExists)
                {
                    if (!CreateSharedParameter(doc, CleanlinessClassParamName, CleanlinessClassGuid,
                        "Cleanliness classification (GMP-B/C/D or ISO-6/7/8)",
                        new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces },
                        SpecTypeId.String.Text,
                        out errorMessage))
                    {
                        return false;
                    }
                }

                if (!achExists)
                {
                    if (!CreateSharedParameter(doc, AchComputedParamName, AchComputedGuid,
                        "Computed Air Changes per Hour",
                        new[] { BuiltInCategory.OST_MEPSpaces },
                        SpecTypeId.String.Text,
                        out errorMessage))
                    {
                        return false;
                    }
                }

                bool achTargetExists = CheckParameterExists<Space>(doc, AchTargetParamName);
                if (!achTargetExists)
                {
                    if (!CreateSharedParameter(doc, AchTargetParamName, AchTargetGuid,
                        "Target Air Changes per Hour (design input, used when space has no cleanliness class)",
                        new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces },
                        SpecTypeId.Number,
                        out errorMessage))
                    {
                        return false;
                    }
                }

                if (!pressureExists)
                {
                    if (!CreateSharedParameter(doc, RoomPressureParamName, RoomPressureGuid,
                        "Room pressure differential in Pascals (Pa). Positive = pressurised.",
                        new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces },
                        SpecTypeId.Number,
                        out errorMessage))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Error checking parameter: {ex.Message}";
                return false;
            }
        }

        private bool CheckParameterExists<T>(Document doc, string paramName) where T : Element
        {
            var collector = new FilteredElementCollector(doc);
            var element = collector.OfClass(typeof(SpatialElement))
                                   .OfType<T>()
                                   .FirstOrDefault();

            if (element != null)
            {
                var existingParam = element.LookupParameter(paramName);
                if (existingParam != null)
                    return true;
            }

            return false;
        }

        private bool CreateSharedParameter(Document doc, string paramName, string guid,
            string description, BuiltInCategory[] categories, ForgeTypeId specType,
            out string errorMessage)
        {
            errorMessage = null;
            var app = doc.Application;

            try
            {
                string sharedParamFile = GetOrCreateSharedParameterFile(app);
                if (string.IsNullOrEmpty(sharedParamFile))
                {
                    errorMessage = "Could not create or access shared parameter file.";
                    return false;
                }

                app.SharedParametersFilename = sharedParamFile;
                var defFile = app.OpenSharedParameterFile();
                if (defFile == null)
                {
                    errorMessage = "Could not open shared parameter file.";
                    return false;
                }

                var group = defFile.Groups.get_Item("CleanroomValidator")
                            ?? defFile.Groups.Create("CleanroomValidator");

                var def = group.Definitions.get_Item(paramName);
                if (def == null)
                {
                    var options = new ExternalDefinitionCreationOptions(paramName, specType)
                    {
                        GUID = new Guid(guid),
                        Description = description,
                        UserModifiable = true,
                        Visible = true
                    };
                    def = group.Definitions.Create(options);
                }

                if (def == null)
                {
                    errorMessage = $"Could not create parameter definition for {paramName}.";
                    return false;
                }

                var categorySet = new CategorySet();
                foreach (var cat in categories)
                {
                    var category = doc.Settings.Categories.get_Item(cat);
                    if (category != null)
                        categorySet.Insert(category);
                }

                using (var trans = new Transaction(doc, $"Add {paramName} Parameter"))
                {
                    trans.Start();
                    try
                    {
                        var binding = new InstanceBinding(categorySet);
                        bool inserted = doc.ParameterBindings.Insert(def, binding, GroupTypeId.IdentityData);

                        if (!inserted)
                            inserted = doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.IdentityData);

                        trans.Commit();

                        if (!inserted)
                        {
                            errorMessage = $"Could not bind {paramName} to categories.";
                            return false;
                        }

                        return true;
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        errorMessage = $"Transaction failed: {ex.Message}";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = $"Error creating parameter: {ex.Message}";
                return false;
            }
        }

        private string GetOrCreateSharedParameterFile(Autodesk.Revit.ApplicationServices.Application app)
        {
            string existingFile = app.SharedParametersFilename;
            if (!string.IsNullOrEmpty(existingFile) && File.Exists(existingFile))
                return existingFile;

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CleanroomValidator"
            );
            Directory.CreateDirectory(folder);

            string filePath = Path.Combine(folder, "CleanroomValidator_SharedParams.txt");
            if (!File.Exists(filePath))
            {
                using (var fs = File.Create(filePath)) { }
            }

            return filePath;
        }

        // ── Cleanliness Class ────────────────────────────────────────────────

        public string GetCleanlinessClass(Room room)
        {
            var param = room.LookupParameter(CleanlinessClassParamName);
            if (param == null || !param.HasValue)
                return "Unclassified";
            return param.AsString() ?? "Unclassified";
        }

        public string GetCleanlinessClass(Space space)
        {
            var param = space.LookupParameter(CleanlinessClassParamName);
            if (param == null || !param.HasValue)
                return "Unclassified";
            return param.AsString() ?? "Unclassified";
        }

        public bool SetCleanlinessClass(Room room, string value, Document doc)
        {
            var param = room.LookupParameter(CleanlinessClassParamName);
            if (param == null || param.IsReadOnly)
                return false;
            param.Set(value);
            return true;
        }

        public bool SetCleanlinessClass(Space space, string value, Document doc)
        {
            var param = space.LookupParameter(CleanlinessClassParamName);
            if (param == null || param.IsReadOnly)
                return false;
            param.Set(value);
            return true;
        }

        // ── ACH Computed ─────────────────────────────────────────────────────

        public bool SetAchComputed(Space space, double achValue)
        {
            var param = space.LookupParameter(AchComputedParamName);
            if (param == null || param.IsReadOnly)
                return false;
            param.Set(achValue.ToString("F2"));
            return true;
        }

        public double GetAchComputed(Space space)
        {
            var param = space.LookupParameter(AchComputedParamName);
            if (param == null || !param.HasValue)
                return 0;
            if (double.TryParse(param.AsString(), out double result))
                return result;
            return 0;
        }

        // ── ACH Target (design input for unclassified spaces) ─────────────────

        public double GetAchTarget(Space space)
        {
            var param = space.LookupParameter(AchTargetParamName);
            if (param != null && param.HasValue)
                return param.AsDouble();
            return 0;
        }

        public double GetAchTarget(Room room)
        {
            var param = room.LookupParameter(AchTargetParamName);
            if (param != null && param.HasValue)
                return param.AsDouble();
            return 0;
        }

        /// <summary>
        /// Persists the engineer-entered ACH target to the Revit model.
        /// Must be called inside an active transaction.
        /// </summary>
        public bool SetAchTarget(Space space, double ach)
        {
            var param = space.LookupParameter(AchTargetParamName);
            if (param == null || param.IsReadOnly) return false;
            param.Set(ach);
            return true;
        }

        public bool SetAchTarget(Room room, double ach)
        {
            var param = room.LookupParameter(AchTargetParamName);
            if (param == null || param.IsReadOnly) return false;
            param.Set(ach);
            return true;
        }

        // ── Room Pressure ────────────────────────────────────────────────────

        /// <summary>
        /// Gets the pressure (Pa) from the Room_Pressure shared parameter.
        /// Returns 0 if not set.
        /// </summary>
        public double GetRoomPressure(Room room)
        {
            var param = room.LookupParameter(RoomPressureParamName);
            if (param != null && param.HasValue)
                return param.AsDouble();
            return 0;
        }

        public double GetRoomPressure(Space space)
        {
            var param = space.LookupParameter(RoomPressureParamName);
            if (param != null && param.HasValue)
                return param.AsDouble();
            return 0;
        }

        /// <summary>
        /// Sets the Room_Pressure shared parameter value (Pa).
        /// Must be called inside an active transaction.
        /// </summary>
        public bool SetRoomPressure(Room room, double pressurePa)
        {
            var param = room.LookupParameter(RoomPressureParamName);
            if (param == null || param.IsReadOnly)
                return false;
            param.Set(pressurePa);
            return true;
        }

        public bool SetRoomPressure(Space space, double pressurePa)
        {
            var param = space.LookupParameter(RoomPressureParamName);
            if (param == null || param.IsReadOnly)
                return false;
            param.Set(pressurePa);
            return true;
        }
    }
}
