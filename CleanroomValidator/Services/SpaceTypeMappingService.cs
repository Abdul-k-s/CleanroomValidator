using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using CleanroomValidator.Data;
using CleanroomValidator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CleanroomValidator.Services
{
    /// <summary>
    /// Service for mapping room names to Space Types with fuzzy matching,
    /// phonetic matching (Metaphone), and synonym groups
    /// </summary>
    public class SpaceTypeMappingService
    {
        private readonly Document _doc;

        // Synonym groups: each tuple contains a set of related keywords and the target space type name
        // Adding new synonyms here will automatically enable both exact and phonetic matching
        private static readonly List<(HashSet<string> Synonyms, string SpaceTypeName)> SynonymGroups = new List<(HashSet<string>, string)>
        {
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "office", "ofc" }, "Office Enclosed"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "storage", "stor", "warehouse" }, "Active Storage"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "corridor", "corr", "hallway", "passage" }, "Corridor / Transition"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lobby", "foyer", "entrance" }, "Lobby Hotel"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "restroom", "wc", "toilet", "bathroom", "washroom", "lavatory" }, "Restrooms"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stair", "stairway", "stairwell" }, "Stairway"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lab", "laboratory" }, "Laboratory"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "conference", "meeting", "multipurpose", "boardroom" }, "Conference / Meeting / Multipurpose"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dining", "cafeteria", "canteen", "lunchroom" }, "Dining Area"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manufacturing", "factory", "production", "assembly", "fabrication" }, "General Low Bay Manufacturing Facility"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "reception", "waiting" }, "Reception / Waiting"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kitchen", "pantry" }, "Food Preparation"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mechanical", "mech", "hvac", "boiler" }, "Mechanical / Electrical Room"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "electrical", "elec", "electric" }, "Mechanical / Electrical Room"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cleanroom" }, "Laboratory"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "break", "lunch" }, "Dining Area"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "janitor", "jc", "janitorial" }, "Janitor / Utility"),
            (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "utility", "util" }, "Janitor / Utility")
        };

        // Pre-computed Metaphone codes for synonym lookup (built once at static initialization)
        private static readonly Dictionary<string, List<string>> MetaphoneToSpaceTypes;

        static SpaceTypeMappingService()
        {
            MetaphoneToSpaceTypes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in SynonymGroups)
            {
                foreach (var synonym in group.Synonyms)
                {
                    string code = Metaphone(synonym);
                    if (!string.IsNullOrEmpty(code))
                    {
                        if (!MetaphoneToSpaceTypes.ContainsKey(code))
                        {
                            MetaphoneToSpaceTypes[code] = new List<string>();
                        }
                        if (!MetaphoneToSpaceTypes[code].Contains(group.SpaceTypeName))
                        {
                            MetaphoneToSpaceTypes[code].Add(group.SpaceTypeName);
                        }
                    }
                }
            }
        }

        private readonly SpaceTypeDictionaryService _dictionaryService;

        public SpaceTypeMappingService(Document doc, SpaceTypeDictionaryService dictionaryService = null)
        {
            _doc = doc;
            _dictionaryService = dictionaryService;
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
        /// Find the best matching Space Type name for a room name using:
        /// 1. Exact synonym match
        /// 2. Phonetic (Metaphone) match for typos
        /// 3. Fuzzy matching as fallback
        /// </summary>
        public string FindMatchingSpaceTypeName(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName))
                return "(None)";

            if (_dictionaryService != null)
            {
                string dictMatch = _dictionaryService.GetMapping(roomName);
                if (!string.IsNullOrEmpty(dictMatch))
                {
                    return dictMatch;
                }
            }

            var availableNames = GetAvailableSpaceTypeNames();
            var roomWords = ExtractWords(roomName);

            // First, check exact synonym matches
            foreach (var group in SynonymGroups)
            {
                foreach (var word in roomWords)
                {
                    if (group.Synonyms.Contains(word))
                    {
                        string preferred = FindInAvailableNames(group.SpaceTypeName, availableNames);
                        if (preferred != null)
                            return preferred;
                    }
                }
            }

            // Second, try phonetic (Metaphone) matching for typos
            // This catches misspellings like "reciption" -> "reception"
            foreach (var word in roomWords)
            {
                string wordCode = Metaphone(word);
                if (!string.IsNullOrEmpty(wordCode) && MetaphoneToSpaceTypes.TryGetValue(wordCode, out var spaceTypeNames))
                {
                    foreach (var spaceTypeName in spaceTypeNames)
                    {
                        string preferred = FindInAvailableNames(spaceTypeName, availableNames);
                        if (preferred != null)
                            return preferred;
                    }
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
        /// Find a space type name in available names, handling format variations
        /// </summary>
        private string FindInAvailableNames(string targetName, List<string> availableNames)
        {
            return availableNames.FirstOrDefault(n =>
                n.Replace(" / ", " Or ").Equals(targetName.Replace(" / ", " Or "), StringComparison.OrdinalIgnoreCase) ||
                n.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                NormalizeName(n).Contains(NormalizeName(targetName)));
        }

        /// <summary>
        /// Extract individual words from a room name
        /// </summary>
        private static List<string> ExtractWords(string roomName)
        {
            if (string.IsNullOrEmpty(roomName))
                return new List<string>();

            // Remove special characters and split by whitespace
            string cleaned = Regex.Replace(roomName, @"[^\w\s]", " ");
            return cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => w.ToLowerInvariant())
                         .Where(w => w.Length > 1)
                         .ToList();
        }

        /// <summary>
        /// Generate a Metaphone phonetic code for a word.
        /// This handles common English phonetic patterns and catches typos automatically.
        /// Examples: "reception" and "reciption" produce the same code "RSPSN"
        /// </summary>
        private static string Metaphone(string word)
        {
            if (string.IsNullOrEmpty(word))
                return string.Empty;

            word = word.ToUpperInvariant();
            
            // Remove non-alphabetic characters
            word = Regex.Replace(word, @"[^A-Z]", "");
            
            if (word.Length == 0)
                return string.Empty;

            var result = new StringBuilder();

            // Handle initial letter patterns
            if (word.StartsWith("KN") || word.StartsWith("GN") || word.StartsWith("PN") || word.StartsWith("WR"))
                word = word.Substring(1);
            else if (word.StartsWith("X"))
                word = "S" + word.Substring(1);
            else if (word.StartsWith("WH"))
                word = "W" + word.Substring(2);

            int i = 0;
            while (i < word.Length)
            {
                char c = word[i];
                char? next = i + 1 < word.Length ? word[i + 1] : (char?)null;
                char? prev = i > 0 ? word[i - 1] : (char?)null;

                // Skip duplicate adjacent letters
                if (prev == c && c != 'C')
                {
                    i++;
                    continue;
                }

                switch (c)
                {
                    case 'A': case 'E': case 'I': case 'O': case 'U':
                        // Only keep vowels at the start
                        if (i == 0) result.Append(c);
                        break;
                    case 'B':
                        // B is silent after M at end
                        if (!(prev == 'M' && i == word.Length - 1))
                            result.Append('P');
                        break;
                    case 'C':
                        if (next == 'H') { result.Append('X'); i++; }
                        else if (next == 'I' || next == 'E' || next == 'Y') result.Append('S');
                        else result.Append('K');
                        break;
                    case 'D':
                        if (next == 'G' && (i + 2 < word.Length && "IEY".Contains(word[i + 2])))
                        { result.Append('J'); i++; }
                        else result.Append('T');
                        break;
                    case 'F': case 'J': case 'L': case 'M': case 'N': case 'R':
                        result.Append(c);
                        break;
                    case 'G':
                        if (next == 'H') { i++; }
                        else if (next == 'N' && i + 1 == word.Length - 1) { }
                        else if (next == 'I' || next == 'E' || next == 'Y') result.Append('J');
                        else result.Append('K');
                        break;
                    case 'H':
                        // H is often silent
                        if (i == 0 || (prev.HasValue && !"AEIOU".Contains(prev.Value)))
                            result.Append('H');
                        break;
                    case 'K':
                        if (prev != 'C') result.Append('K');
                        break;
                    case 'P':
                        if (next == 'H') { result.Append('F'); i++; }
                        else result.Append('P');
                        break;
                    case 'Q': result.Append('K'); break;
                    case 'S':
                        if (next == 'H') { result.Append('X'); i++; }
                        else result.Append('S');
                        break;
                    case 'T':
                        if (next == 'H') { result.Append('0'); i++; } // 0 represents TH
                        else if (next == 'I' && (i + 2 < word.Length && "OA".Contains(word[i + 2])))
                        { result.Append('X'); i++; }
                        else result.Append('T');
                        break;
                    case 'V': result.Append('F'); break;
                    case 'W': case 'Y':
                        if (next.HasValue && "AEIOU".Contains(next.Value)) result.Append(c);
                        break;
                    case 'X': result.Append("KS"); break;
                    case 'Z': result.Append('S'); break;
                }
                i++;
            }

            return result.ToString();
        }

        /// <summary>
        /// Get match score for a room name against a space type name
        /// </summary>
        public double GetMatchScore(string roomName, string spaceTypeName)
        {
            if (string.IsNullOrEmpty(spaceTypeName) || spaceTypeName == "(None)")
                return 0;

            if (_dictionaryService != null)
            {
                string dictMatch = _dictionaryService.GetMapping(roomName);
                if (!string.IsNullOrEmpty(dictMatch) && dictMatch.Equals(spaceTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    return 1.0;
                }
            }

            return CalculateSimilarity(NormalizeName(roomName), NormalizeName(spaceTypeName));
        }

        /// <summary>
        /// Normalize a name for comparison (used in fuzzy matching fallback)
        /// </summary>
        private string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            string normalized = name.ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"[\d\-_\.\,\(\)\[\]]+", " ");

            // Only keep semantic abbreviation expansions that aren't covered by synonym groups
            var replacements = new Dictionary<string, string>
            {
                { "rm", "room" },
                { "vest", "vestibule" },
                { "exec", "executive" },
                { "admin", "administration" }
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
