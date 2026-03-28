using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Data;
using CleanroomValidator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CleanroomValidator.Services
{
    /// <summary>
    /// Service for mapping room names to Space Types with fuzzy matching
    /// and setting cleanroom-specific parameters based on standards
    /// </summary>
    public class SpaceTypeMappingService
    {
        private readonly Document _doc;

        // Explicit preferred mappings for common room name keywords
        // These take priority over fuzzy matching
        private static readonly Dictionary<string, string> PreferredMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "office", "Office Enclosed" },
            { "storage", "Active Storage" },
            { "stor", "Active Storage" },
            { "corridor", "Corridor / Transition" },
            { "corr", "Corridor / Transition" },
            { "lobby", "Lobby Hotel" },
            { "restroom", "Restrooms" },
            { "wc", "Restrooms" },
            { "toilet", "Restrooms" },
            { "bathroom", "Restrooms" },
            { "stair", "Stairway" },
            { "lab", "Laboratory" },
            { "conference", "Conference / Meeting / Multipurpose" },
            { "meeting", "Conference / Meeting / Multipurpose" },
            { "dining", "Dining Area" },
            { "cafeteria", "Dining Area" }
        };

        public SpaceTypeMappingService(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Get all available Space Type names (friendly display names from the SpaceType enum)
        /// </summary>
        public List<string> GetAvailableSpaceTypeNames()
        {
            var names = new List<string> { "(None)" };

            // Get all SpaceType enum values dynamically
            var allTypes = Enum.GetValues(typeof(SpaceType)).Cast<SpaceType>().ToList();

            foreach (var spaceType in allTypes)
            {
                string enumName = spaceType.ToString();
                
                // Skip kNoSpaceType if it exists
                if (enumName.Equals("kNoSpaceType", StringComparison.OrdinalIgnoreCase))
                    continue;

                string displayName = GetDisplayNameFromEnumName(enumName);
                if (!names.Contains(displayName))
                {
                    names.Add(displayName);
                }
            }

            return names;
        }

        /// <summary>
        /// Convert enum name (e.g., "kDiningArea") to friendly display name (e.g., "Dining Area")
        /// </summary>
        private string GetDisplayNameFromEnumName(string enumName)
        {
            // Remove the 'k' prefix
            string name = enumName;
            if (name.StartsWith("k"))
                name = name.Substring(1);

            // Insert spaces before capitals
            name = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
            name = Regex.Replace(name, "([A-Z]+)([A-Z][a-z])", "$1 $2");

            // Replace "Or" with "/"
            name = name.Replace(" Or ", " / ");

            return name;
        }

        /// <summary>
        /// Get the SpaceType enum value for a display name
        /// </summary>
        private SpaceType? GetSpaceTypeFromDisplayName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName) || displayName == "(None)")
                return null;

            // Get all enum values
            var allTypes = Enum.GetValues(typeof(SpaceType)).Cast<SpaceType>().ToList();

            foreach (var spaceType in allTypes)
            {
                string enumName = spaceType.ToString();
                string friendlyName = GetDisplayNameFromEnumName(enumName);

                // Direct match with display name
                if (string.Equals(friendlyName, displayName, StringComparison.OrdinalIgnoreCase))
                    return spaceType;

                // Try matching without spaces
                string normalizedInput = displayName.Replace(" ", "").Replace("/", "Or");
                string normalizedEnum = enumName.StartsWith("k") ? enumName.Substring(1) : enumName;

                if (string.Equals(normalizedInput, normalizedEnum, StringComparison.OrdinalIgnoreCase))
                    return spaceType;
            }

            // Fuzzy match - find best match
            SpaceType? bestMatch = null;
            double bestScore = 0;

            foreach (var spaceType in allTypes)
            {
                string enumName = spaceType.ToString();
                string friendlyName = GetDisplayNameFromEnumName(enumName);

                double score = CalculateSimilarity(
                    NormalizeName(displayName),
                    NormalizeName(friendlyName));

                if (score > bestScore && score >= 0.7)
                {
                    bestScore = score;
                    bestMatch = spaceType;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Find the best matching Space Type name for a room name using fuzzy matching
        /// </summary>
        public string FindMatchingSpaceTypeName(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName))
                return "(None)";

            string lowerRoomName = roomName.ToLowerInvariant();
            var availableNames = GetAvailableSpaceTypeNames();

            // First, check explicit preferred mappings
            foreach (var kvp in PreferredMappings)
            {
                // Check if room name contains the keyword
                if (lowerRoomName.Contains(kvp.Key.ToLowerInvariant()))
                {
                    // Verify the preferred mapping exists in available names
                    string preferred = availableNames.FirstOrDefault(n => 
                        n.Replace(" / ", " Or ").Equals(kvp.Value.Replace(" / ", " Or "), StringComparison.OrdinalIgnoreCase) ||
                        n.Equals(kvp.Value, StringComparison.OrdinalIgnoreCase) ||
                        NormalizeName(n).Contains(NormalizeName(kvp.Value)));
                    
                    if (preferred != null)
                        return preferred;
                }
            }

            // Fuzzy matching with preference for shorter names
            string normalizedRoomName = NormalizeName(roomName);
            string bestMatch = "(None)";
            double bestScore = 0;
            int bestLength = int.MaxValue;

            foreach (var typeName in availableNames)
            {
                if (typeName == "(None)")
                    continue;

                string normalizedTypeName = NormalizeName(typeName);
                double score = CalculateSimilarity(normalizedRoomName, normalizedTypeName);

                if (score >= 0.5)
                {
                    // Prefer higher score, or same score but shorter name
                    if (score > bestScore || (score == bestScore && typeName.Length < bestLength))
                    {
                        bestScore = score;
                        bestMatch = typeName;
                        bestLength = typeName.Length;
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Get match score for a room name against a space type name
        /// </summary>
        public double GetMatchScore(string roomName, string spaceTypeName)
        {
            if (string.IsNullOrEmpty(spaceTypeName) || spaceTypeName == "(None)")
                return 0;

            return CalculateSimilarity(NormalizeName(roomName), NormalizeName(spaceTypeName));
        }

        /// <summary>
        /// Normalize a name for comparison
        /// </summary>
        private string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            string normalized = name.ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"[\d\-_\.\,\(\)\[\]]+", " ");

            var replacements = new Dictionary<string, string>
            {
                { "conf", "conference" },
                { "rm", "room" },
                { "lab", "laboratory" },
                { "stor", "storage" },
                { "elec", "electrical" },
                { "mech", "mechanical" },
                { "corr", "corridor" },
                { "vest", "vestibule" },
                { "recep", "reception" },
                { "exec", "executive" },
                { "admin", "administration" },
                { "util", "utility" },
                { "jc", "janitor closet" },
                { "wc", "restroom" },
                { "bathroom", "restroom" },
                { "washroom", "restroom" },
                { "toilet", "restroom" },
                { "break", "cafeteria" },
                { "lunch", "cafeteria" },
                { "kitchen", "food preparation" },
                { "pantry", "cafeteria" },
                { "clean", "cleanroom" }
            };

            foreach (var kvp in replacements)
            {
                normalized = Regex.Replace(normalized, $@"\b{kvp.Key}\b", kvp.Value);
            }

            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        /// <summary>
        /// Calculate similarity between two strings
        /// </summary>
        public double CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0;

            if (s1 == s2)
                return 1.0;

            if (s1.Contains(s2) || s2.Contains(s1))
                return 0.9;

            var words1 = s1.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var words2 = s2.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            int commonWords = words1.Intersect(words2).Count();
            int totalWords = Math.Max(words1.Length, words2.Length);

            if (totalWords > 0 && commonWords > 0)
            {
                double wordScore = (double)commonWords / totalWords;
                if (wordScore >= 0.5)
                    return 0.5 + (wordScore * 0.4);
            }

            double levenshteinScore = 1.0 - ((double)LevenshteinDistance(s1, s2) / Math.Max(s1.Length, s2.Length));
            return levenshteinScore;
        }

        private int LevenshteinDistance(string s1, string s2)
        {
            int[,] d = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                d[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++)
                d[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[s1.Length, s2.Length];
        }

        /// <summary>
        /// Apply space type name and cleanroom parameters to a Space
        /// </summary>
        public bool ApplySpaceTypeAndParameters(Space space, string spaceTypeName, string cleanlinessClass)
        {
            string failureReason;
            return ApplySpaceTypeAndParameters(space, spaceTypeName, cleanlinessClass, out failureReason);
        }

        /// <summary>
        /// Apply space type name and cleanroom parameters to a Space
        /// </summary>
        public bool ApplySpaceTypeAndParameters(Space space, string spaceTypeName, string cleanlinessClass, out string failureReason)
        {
            failureReason = null;

            if (space == null)
            {
                failureReason = "Space is null";
                return false;
            }

            try
            {
                bool anyApplied = false;

                // Try to set Space Type using the SpaceType enum
                if (!string.IsNullOrEmpty(spaceTypeName) && spaceTypeName != "(None)")
                {
                    var spaceTypeEnum = GetSpaceTypeFromDisplayName(spaceTypeName);

                    if (spaceTypeEnum.HasValue)
                    {
                        space.SpaceType = spaceTypeEnum.Value;
                        anyApplied = true;
                    }
                    else
                    {
                        failureReason = $"Could not find SpaceType enum value matching '{spaceTypeName}'.";
                    }
                }

                // Apply cleanroom parameters if classified
                if (!string.IsNullOrEmpty(cleanlinessClass) && cleanlinessClass != "Unclassified")
                {
                    ApplyCleanroomParameters(space, cleanlinessClass);
                    anyApplied = true;
                }

                return anyApplied;
            }
            catch (Exception ex)
            {
                failureReason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Apply cleanroom-specific parameters to a Space based on classification
        /// </summary>
        private void ApplyCleanroomParameters(Space space, string cleanlinessClass)
        {
            var cleanClass = CleanlinessClass.Parse(cleanlinessClass);
            var requirements = StandardsDatabase.GetRequirements(cleanClass);

            // Set Cleanliness_Class parameter
            var cleanlinessParam = space.LookupParameter("Cleanliness_Class");
            if (cleanlinessParam != null && !cleanlinessParam.IsReadOnly)
            {
                cleanlinessParam.Set(cleanlinessClass);
            }

            // Try to set design/specified parameters based on requirements
            // Calculate required CFM based on space volume
            var volumeParam = space.get_Parameter(BuiltInParameter.ROOM_VOLUME);
            if (volumeParam != null)
            {
                double volumeCuFt = volumeParam.AsDouble();
                double requiredCfm = requirements.CalculateRequiredCfm(volumeCuFt);

                // Try various supply airflow parameter names
                TrySetParameter(space, BuiltInParameter.ROOM_DESIGN_SUPPLY_AIRFLOW_PARAM, requiredCfm);
            }

            // Try setting custom parameters if they exist
            TrySetParameter(space, "Required_ACH", requirements.MinAch);
            TrySetParameter(space, "Min_Pressure_Differential", requirements.MinPressureDifferential);
            TrySetParameter(space, "Filter_Class", requirements.FilterClass);
        }

        private void TrySetParameter(Space space, BuiltInParameter bip, double value)
        {
            try
            {
                var param = space.get_Parameter(bip);
                if (param != null && !param.IsReadOnly)
                {
                    param.Set(value);
                }
            }
            catch
            {
                // Parameter doesn't exist or can't be set
            }
        }

        private void TrySetParameter(Space space, string paramName, object value)
        {
            var param = space.LookupParameter(paramName);
            if (param == null || param.IsReadOnly)
                return;

            try
            {
                if (value is int intVal)
                    param.Set(intVal);
                else if (value is double doubleVal)
                    param.Set(doubleVal);
                else if (value is string strVal)
                    param.Set(strVal);
            }
            catch
            {
                // Parameter type mismatch, ignore
            }
        }
    }
}
