using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace CleanroomValidator.Models
{
    public enum ComplianceStatus
    {
        Compliant,
        PartialCompliance,
        NonCompliant,
        NotApplicable
    }

    public class ComplianceCheckResult
    {
        public string CheckName { get; set; }
        public string Required { get; set; }
        public string Actual { get; set; }
        public ComplianceStatus Status { get; set; }

        public ComplianceCheckResult() { }

        public ComplianceCheckResult(string checkName, string required, string actual, ComplianceStatus status)
        {
            CheckName = checkName;
            Required = required;
            Actual = actual;
            Status = status;
        }
    }

    public class RoomComplianceResult
    {
        public ElementId RoomId { get; set; }
        public string RoomName { get; set; }
        public string RoomNumber { get; set; }
        public string Level { get; set; }
        public string Source { get; set; }
        public CleanlinessClass CleanlinessClass { get; set; }
        public double VolumeCubicFeet { get; set; }
        public double ActualSupplyCfm { get; set; }
        public double ActualAch { get; set; }
        public int RequiredAch { get; set; }
        public bool AchMeetsRequirement { get; set; }
        public double RecoveryTimeMinutes { get; set; }
        public double ActualPressure { get; set; }
        public ComplianceStatus OverallStatus { get; set; }
        public List<ComplianceCheckResult> CheckResults { get; set; } = new List<ComplianceCheckResult>();
        public List<AdjacentRoomInfo> AdjacentRooms { get; set; } = new List<AdjacentRoomInfo>();
        public bool HasVolumeWarning { get; set; }
        public string Notes { get; set; }

        public string StatusIcon => OverallStatus switch
        {
            ComplianceStatus.Compliant => "✓",
            ComplianceStatus.PartialCompliance => "⚠",
            ComplianceStatus.NonCompliant => "✗",
            _ => "—"
        };
    }

    public class AdjacentRoomInfo
    {
        public ElementId RoomId { get; set; }
        public string RoomName { get; set; }
        public CleanlinessClass CleanlinessClass { get; set; }
        public double Pressure { get; set; }
    }
}
