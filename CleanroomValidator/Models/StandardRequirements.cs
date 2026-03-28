namespace CleanroomValidator.Models
{
    public class StandardRequirements
    {
        public CleanlinessGrade Grade { get; set; }
        public int MinAch { get; set; }
        public int MaxAch { get; set; }
        public double MinPressureDifferential { get; set; } // Pa
        public int RecoveryTimeMinutes { get; set; }
        public string FilterClass { get; set; }

        public StandardRequirements(
            CleanlinessGrade grade,
            int minAch,
            int maxAch,
            double minPressureDifferential,
            int recoveryTimeMinutes,
            string filterClass)
        {
            Grade = grade;
            MinAch = minAch;
            MaxAch = maxAch;
            MinPressureDifferential = minPressureDifferential;
            RecoveryTimeMinutes = recoveryTimeMinutes;
            FilterClass = filterClass;
        }

        public double CalculateRequiredCfm(double volumeCubicFeet)
        {
            return volumeCubicFeet * MinAch / 60.0;
        }
    }
}
