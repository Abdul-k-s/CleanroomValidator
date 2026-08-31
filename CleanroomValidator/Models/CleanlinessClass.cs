namespace CleanroomValidator.Models
{
    public enum CleanlinessStandard
    {
        GMP,
        ISO
    }

    public enum CleanlinessGrade
    {
        // GMP Grades
        GMP_B,
        GMP_C,
        GMP_D,

        // ISO Classes
        ISO_6,
        ISO_7,
        ISO_8,

        Unclassified
    }

    public class CleanlinessClass
    {
        public CleanlinessStandard Standard { get; }
        public CleanlinessGrade Grade { get; }
        public string DisplayName { get; }
        public int HierarchyLevel { get; }

        public CleanlinessClass(CleanlinessStandard standard, CleanlinessGrade grade, string displayName, int hierarchyLevel)
        {
            Standard = standard;
            Grade = grade;
            DisplayName = displayName;
            HierarchyLevel = hierarchyLevel;
        }

        public static CleanlinessClass Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new CleanlinessClass(CleanlinessStandard.GMP, CleanlinessGrade.Unclassified, "Unclassified", 99);

            var normalized = value.ToUpperInvariant().Replace(" ", "").Replace("_", "-");

            return normalized switch
            {
                "GMP-B" or "GMPB" => new CleanlinessClass(CleanlinessStandard.GMP, CleanlinessGrade.GMP_B, "GMP-B", 1),
                "GMP-C" or "GMPC" => new CleanlinessClass(CleanlinessStandard.GMP, CleanlinessGrade.GMP_C, "GMP-C", 3),
                "GMP-D" or "GMPD" => new CleanlinessClass(CleanlinessStandard.GMP, CleanlinessGrade.GMP_D, "GMP-D", 4),
                "ISO-6" or "ISO6" => new CleanlinessClass(CleanlinessStandard.ISO, CleanlinessGrade.ISO_6, "ISO-6", 2),
                "ISO-7" or "ISO7" => new CleanlinessClass(CleanlinessStandard.ISO, CleanlinessGrade.ISO_7, "ISO-7", 3),
                "ISO-8" or "ISO8" => new CleanlinessClass(CleanlinessStandard.ISO, CleanlinessGrade.ISO_8, "ISO-8", 4),
                _ => new CleanlinessClass(CleanlinessStandard.GMP, CleanlinessGrade.Unclassified, "Unclassified", 99)
            };
        }

        public override string ToString() => DisplayName;
    }
}
