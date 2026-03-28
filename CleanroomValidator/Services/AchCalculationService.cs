using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Data;
using CleanroomValidator.Models;
using System;

namespace CleanroomValidator.Services
{
    /// <summary>
    /// Service to calculate Air Changes per Hour and Recovery Time
    /// </summary>
    public class AchCalculationService
    {
        private readonly Document _doc;
        private readonly ParameterService _paramService;
        private readonly CeilingDetectionService _ceilingService;

        // Recovery time uses 100:1 contamination decay (99% reduction)
        private const double ContaminationRatio = 100.0;

        public AchCalculationService(Document doc)
        {
            _doc = doc;
            _paramService = new ParameterService();
            var linkedDocs = RoomDataExtractor.GetLinkedDocuments(doc);
            _ceilingService = new CeilingDetectionService(doc, linkedDocs);
        }

        /// <summary>
        /// Result of ACH calculation
        /// </summary>
        public class AchResult
        {
            public double Volume { get; set; } // cubic feet
            public double SupplyAirflowCfm { get; set; }
            public double AchCalculated { get; set; }
            public int AchRequired { get; set; }
            public bool MeetsRequirement { get; set; }
            public double RecoveryTimeMinutes { get; set; }
            public bool HasVolumeWarning { get; set; }
            public bool HasAirflowData { get; set; }
            public string Notes { get; set; }
        }

        /// <summary>
        /// Calculate ACH for a space
        /// </summary>
        public AchResult CalculateAch(Space space)
        {
            var result = new AchResult();

            // Get volume
            var volumeParam = space.get_Parameter(BuiltInParameter.ROOM_VOLUME);
            result.Volume = volumeParam?.AsDouble() ?? 0;

            if (result.Volume <= 0)
            {
                result.HasVolumeWarning = true;
                result.Notes = "Warning: Volume is zero or negative";
                return result;
            }

            // Get cleanliness class and requirements
            var cleanlinessClass = _paramService.GetCleanlinessClass(space);
            var classInfo = CleanlinessClass.Parse(cleanlinessClass);
            var requirements = StandardsDatabase.GetRequirements(classInfo.Grade);
            result.AchRequired = requirements.MinAch;

            // Get supply airflow
            var supplyParam = space.get_Parameter(BuiltInParameter.ROOM_DESIGN_SUPPLY_AIRFLOW_PARAM);
            double supplyAirflowCfs = supplyParam?.AsDouble() ?? 0;

            // Try other supply parameters if not found
            if (supplyAirflowCfs <= 0)
            {
                supplyParam = space.get_Parameter(BuiltInParameter.ROOM_ACTUAL_SUPPLY_AIRFLOW_PARAM);
                supplyAirflowCfs = supplyParam?.AsDouble() ?? 0;
            }

            if (supplyAirflowCfs <= 0)
            {
                supplyParam = space.get_Parameter(BuiltInParameter.ROOM_CALCULATED_SUPPLY_AIRFLOW_PARAM);
                supplyAirflowCfs = supplyParam?.AsDouble() ?? 0;
            }

            // Convert from cubic feet per second to CFM
            result.SupplyAirflowCfm = supplyAirflowCfs * 60.0;
            result.HasAirflowData = result.SupplyAirflowCfm > 0;

            if (result.HasAirflowData)
            {
                // Calculate ACH from airflow: ACH = (CFM × 60) / Volume
                result.AchCalculated = (result.SupplyAirflowCfm * 60.0) / result.Volume;
            }
            else
            {
                // No airflow data - use required ACH to suggest what airflow should be
                result.AchCalculated = 0;
                if (result.AchRequired > 0)
                {
                    double requiredCfm = (result.AchRequired * result.Volume) / 60.0;
                    result.Notes = $"No airflow data. Required CFM: {requiredCfm:F0}";
                }
            }

            // Check compliance
            result.MeetsRequirement = result.AchCalculated >= result.AchRequired;

            // Calculate recovery time using contamination decay formula
            // t = (V / Q) × ln(C0 / C1)
            // Where V = volume, Q = airflow rate, C0/C1 = contamination ratio
            if (result.HasAirflowData && result.SupplyAirflowCfm > 0)
            {
                result.RecoveryTimeMinutes = CalculateRecoveryTime(result.Volume, result.SupplyAirflowCfm);
            }

            return result;
        }

        /// <summary>
        /// Calculate ACH for a room (finds associated space or calculates from room data)
        /// </summary>
        public AchResult CalculateAchForRoom(Autodesk.Revit.DB.Architecture.Room room)
        {
            var result = new AchResult();

            // Get cleanliness class
            var cleanlinessClass = _paramService.GetCleanlinessClass(room);
            bool isClassified = cleanlinessClass != "Unclassified" && !string.IsNullOrEmpty(cleanlinessClass);

            // Calculate effective volume considering ceiling
            result.Volume = _ceilingService.CalculateEffectiveVolume(room, isClassified);

            if (result.Volume <= 0)
            {
                result.HasVolumeWarning = true;
                result.Notes = "Warning: Volume is zero or negative";
                return result;
            }

            // Get requirements
            var classInfo = CleanlinessClass.Parse(cleanlinessClass);
            var requirements = StandardsDatabase.GetRequirements(classInfo.Grade);
            result.AchRequired = requirements.MinAch;

            // Try to find associated space for airflow data
            var dataExtractor = new RoomDataExtractor(_doc);
            result.SupplyAirflowCfm = dataExtractor.GetSupplyCfm(room);
            result.HasAirflowData = result.SupplyAirflowCfm > 0;

            if (result.HasAirflowData)
            {
                // Calculate ACH: ACH = (CFM × 60) / Volume
                result.AchCalculated = (result.SupplyAirflowCfm * 60.0) / result.Volume;
            }
            else
            {
                result.AchCalculated = 0;
                if (result.AchRequired > 0)
                {
                    double requiredCfm = (result.AchRequired * result.Volume) / 60.0;
                    result.Notes = $"No airflow data. Required CFM: {requiredCfm:F0}";
                }
            }

            result.MeetsRequirement = result.AchCalculated >= result.AchRequired;

            // Calculate recovery time
            if (result.HasAirflowData && result.SupplyAirflowCfm > 0)
            {
                result.RecoveryTimeMinutes = CalculateRecoveryTime(result.Volume, result.SupplyAirflowCfm);
            }

            return result;
        }

        /// <summary>
        /// Calculate recovery time using contamination decay formula
        /// t = (V / Q) × ln(C0 / C1)
        /// Uses 100:1 contamination ratio (99% reduction)
        /// </summary>
        /// <param name="volumeCubicFeet">Room volume in cubic feet</param>
        /// <param name="airflowCfm">Supply airflow in CFM</param>
        /// <returns>Recovery time in minutes</returns>
        public double CalculateRecoveryTime(double volumeCubicFeet, double airflowCfm)
        {
            if (airflowCfm <= 0 || volumeCubicFeet <= 0)
                return 0;

            // ln(100) ≈ 4.605 for 100:1 ratio
            double lnRatio = Math.Log(ContaminationRatio);
            
            // Time = (Volume / Airflow) × ln(ratio)
            // Volume in cubic feet, Airflow in CFM
            double timeMinutes = (volumeCubicFeet / airflowCfm) * lnRatio;

            return timeMinutes;
        }

        /// <summary>
        /// Calculate required CFM based on ACH requirement
        /// </summary>
        public double CalculateRequiredCfm(double volumeCubicFeet, int requiredAch)
        {
            if (requiredAch <= 0 || volumeCubicFeet <= 0)
                return 0;

            // CFM = (ACH × Volume) / 60
            return (requiredAch * volumeCubicFeet) / 60.0;
        }
    }
}
