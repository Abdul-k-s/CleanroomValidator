using Autodesk.Revit.UI;
using System;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace CleanroomValidator
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // Create ribbon tab
            string tabName = "Cleanroom";
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch
            {
                // Tab may already exist
            }

            // Create ribbon panel
            var panel = application.CreateRibbonPanel(tabName, "Compliance");

            // Get assembly path
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Add Set Class button
            var setClassButtonData = new PushButtonData(
                "SetCleanlinessClass",
                "Set\nClass",
                assemblyPath,
                "CleanroomValidator.Commands.SetCleanlinessClassCommand"
            )
            {
                ToolTip = "Set the cleanliness class for rooms/spaces",
                LongDescription = "Assigns a GMP grade (B/C/D) or ISO class (6/7/8) to the selected rooms or spaces."
            };

            var setClassButton = panel.AddItem(setClassButtonData) as PushButton;
            if (setClassButton != null)
            {
                try
                {
                    setClassButton.LargeImage = GetEmbeddedImage("CleanroomValidator.Resources.set_32.png");
                    setClassButton.Image = GetEmbeddedImage("CleanroomValidator.Resources.set_16.png");
                }
                catch
                {
                    // Icons not found
                }
            }

            // Add Create Spaces button
            var createSpacesButtonData = new PushButtonData(
                "CreateSpaces",
                "Create\nSpaces",
                assemblyPath,
                "CleanroomValidator.Commands.CreateSpacesCommand"
            )
            {
                ToolTip = "Create MEP spaces from rooms",
                LongDescription = "Creates MEP spaces from rooms with proper ceiling height handling. Classified rooms use ceiling height, unclassified use full height."
            };

            var createSpacesButton = panel.AddItem(createSpacesButtonData) as PushButton;
            if (createSpacesButton != null)
            {
                try
                {
                    createSpacesButton.LargeImage = GetEmbeddedImage("CleanroomValidator.Resources.space_32.png");
                    createSpacesButton.Image = GetEmbeddedImage("CleanroomValidator.Resources.space_16.png");
                }
                catch
                {
                    // Icons not found
                }
            }

            // Add Set Space Type button
            var setSpaceTypeButtonData = new PushButtonData(
                "SetSpaceType",
                "Set Space\nType",
                assemblyPath,
                "CleanroomValidator.Commands.SetSpaceTypeCommand"
            )
            {
                ToolTip = "Set Space Type for existing spaces",
                LongDescription = "Assigns Space Type to existing MEP spaces with fuzzy matching suggestions based on space names."
            };

            var setSpaceTypeButton = panel.AddItem(setSpaceTypeButtonData) as PushButton;
            if (setSpaceTypeButton != null)
            {
                try
                {
                    setSpaceTypeButton.LargeImage = GetEmbeddedImage("CleanroomValidator.Resources.type_32.png");
                    setSpaceTypeButton.Image = GetEmbeddedImage("CleanroomValidator.Resources.type_16.png");
                }
                catch
                {
                    // Icons not found
                }
            }

            // Add Check Compliance button
            var checkButtonData = new PushButtonData(
                "CheckCompliance",
                "Check\nCompliance",
                assemblyPath,
                "CleanroomValidator.Commands.CheckComplianceCommand"
            )
            {
                ToolTip = "Check spaces for cleanroom compliance",
                LongDescription = "Verifies that space airflow meets the requirements for the assigned cleanliness class (GMP or ISO standard). Shows ACH calculations and recovery times."
            };

            var checkButton = panel.AddItem(checkButtonData) as PushButton;
            if (checkButton != null)
            {
                try
                {
                    checkButton.LargeImage = GetEmbeddedImage("CleanroomValidator.Resources.check_32.png");
                    checkButton.Image = GetEmbeddedImage("CleanroomValidator.Resources.check_16.png");
                }
                catch
                {
                    // Icons not found, button will use default
                }
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private BitmapImage GetEmbeddedImage(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();

            return image;
        }
    }
}
