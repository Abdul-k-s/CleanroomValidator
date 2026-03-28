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

        /// <summary>
        /// Ensures the Cleanliness_Class parameter exists on rooms and spaces.
        /// Must be called WITHOUT an active transaction.
        /// </summary>
        public bool EnsureParameterExists(Document doc, out string errorMessage)
        {
            errorMessage = null;
            
            try
            {
                bool cleanlinessExists = CheckParameterExists<Room>(doc, CleanlinessClassParamName);
                bool achExists = CheckParameterExists<Space>(doc, AchComputedParamName);

                if (!cleanlinessExists)
                {
                    if (!CreateSharedParameter(doc, CleanlinessClassParamName, CleanlinessClassGuid, 
                        "Cleanliness classification (GMP-B/C/D or ISO-6/7/8)", 
                        new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces }, 
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
            string description, BuiltInCategory[] categories, out string errorMessage)
        {
            errorMessage = null;
            var app = doc.Application;
            
            try
            {
                // Create or get shared parameter file
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

                // Get or create group
                var group = defFile.Groups.get_Item("CleanroomValidator") 
                            ?? defFile.Groups.Create("CleanroomValidator");

                // Check if definition exists
                var def = group.Definitions.get_Item(paramName);
                if (def == null)
                {
                    var options = new ExternalDefinitionCreationOptions(paramName, SpecTypeId.String.Text)
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

                // Add parameter to categories
                var categorySet = new CategorySet();
                foreach (var cat in categories)
                {
                    var category = doc.Settings.Categories.get_Item(cat);
                    if (category != null)
                        categorySet.Insert(category);
                }

                // Single transaction for binding the parameter
                using (var trans = new Transaction(doc, $"Add {paramName} Parameter"))
                {
                    trans.Start();
                    try
                    {
                        var binding = new InstanceBinding(categorySet);
                        bool inserted = doc.ParameterBindings.Insert(def, binding, GroupTypeId.IdentityData);
                        
                        if (!inserted)
                        {
                            // Parameter might already be bound, try to rebind
                            inserted = doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.IdentityData);
                        }
                        
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

            // Create a new shared parameter file
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CleanroomValidator"
            );
            Directory.CreateDirectory(folder);

            string filePath = Path.Combine(folder, "CleanroomValidator_SharedParams.txt");
            if (!File.Exists(filePath))
            {
                using (var fs = File.Create(filePath))
                {
                    // Empty file is valid for shared parameters
                }
            }

            return filePath;
        }

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
    }
}
