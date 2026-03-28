using CleanroomValidator.Models;
using System.Collections.Generic;

namespace CleanroomValidator.Data
{
    public static class StandardsDatabase
    {
        private static readonly Dictionary<CleanlinessGrade, StandardRequirements> _requirements;

        static StandardsDatabase()
        {
            _requirements = new Dictionary<CleanlinessGrade, StandardRequirements>
            {
                // GMP Standards (EU Annex 1)
                [CleanlinessGrade.GMP_B] = new StandardRequirements(
                    grade: CleanlinessGrade.GMP_B,
                    minAch: 40,
                    maxAch: 60,
                    minPressureDifferential: 15.0,
                    recoveryTimeMinutes: 15,
                    filterClass: "HEPA H14"
                ),
                [CleanlinessGrade.GMP_C] = new StandardRequirements(
                    grade: CleanlinessGrade.GMP_C,
                    minAch: 20,
                    maxAch: 40,
                    minPressureDifferential: 10.0,
                    recoveryTimeMinutes: 20,
                    filterClass: "HEPA H14"
                ),
                [CleanlinessGrade.GMP_D] = new StandardRequirements(
                    grade: CleanlinessGrade.GMP_D,
                    minAch: 10,
                    maxAch: 20,
                    minPressureDifferential: 10.0,
                    recoveryTimeMinutes: 0, // Not specified
                    filterClass: "HEPA H13/H14"
                ),

                // ISO 14644 Standards
                [CleanlinessGrade.ISO_6] = new StandardRequirements(
                    grade: CleanlinessGrade.ISO_6,
                    minAch: 90,
                    maxAch: 180,
                    minPressureDifferential: 12.5,
                    recoveryTimeMinutes: 0, // Per validation
                    filterClass: "HEPA H14/ULPA"
                ),
                [CleanlinessGrade.ISO_7] = new StandardRequirements(
                    grade: CleanlinessGrade.ISO_7,
                    minAch: 30,
                    maxAch: 60,
                    minPressureDifferential: 12.5,
                    recoveryTimeMinutes: 0, // Per validation
                    filterClass: "HEPA H14"
                ),
                [CleanlinessGrade.ISO_8] = new StandardRequirements(
                    grade: CleanlinessGrade.ISO_8,
                    minAch: 10,
                    maxAch: 25,
                    minPressureDifferential: 10.0,
                    recoveryTimeMinutes: 0, // Per validation
                    filterClass: "HEPA H13"
                ),

                // Unclassified - no requirements
                [CleanlinessGrade.Unclassified] = new StandardRequirements(
                    grade: CleanlinessGrade.Unclassified,
                    minAch: 0,
                    maxAch: 0,
                    minPressureDifferential: 0,
                    recoveryTimeMinutes: 0,
                    filterClass: "N/A"
                )
            };
        }

        public static StandardRequirements GetRequirements(CleanlinessGrade grade)
        {
            return _requirements.TryGetValue(grade, out var req) ? req : _requirements[CleanlinessGrade.Unclassified];
        }

        public static StandardRequirements GetRequirements(CleanlinessClass cleanlinessClass)
        {
            return GetRequirements(cleanlinessClass.Grade);
        }

        public static IEnumerable<string> GetAvailableClasses()
        {
            return new[]
            {
                "GMP-B",
                "GMP-C",
                "GMP-D",
                "ISO-6",
                "ISO-7",
                "ISO-8",
                "Unclassified"
            };
        }
    }
}
