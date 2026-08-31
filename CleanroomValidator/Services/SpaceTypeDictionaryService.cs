using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CleanroomValidator.Services
{
    public class SpaceTypeMappingEntry
    {
        public string normalizedName { get; set; }
        public string originalName { get; set; }
        public string spaceType { get; set; }
    }

    public class SpaceTypeDictionaryData
    {
        public int schemaVersion { get; set; } = 1;
        public string exportedAt { get; set; }
        public List<SpaceTypeMappingEntry> mappings { get; set; } = new List<SpaceTypeMappingEntry>();
    }

    public class SpaceTypeDictionaryService
    {
        private Dictionary<string, SpaceTypeMappingEntry> _dictionary = new Dictionary<string, SpaceTypeMappingEntry>(StringComparer.OrdinalIgnoreCase);
        
        private string GetDefaultPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "CleanroomValidator");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return Path.Combine(folder, "space-type-dictionary.json");
        }

        public string Normalize(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return string.Empty;
            
            // lowercase and trim
            string normalized = roomName.Trim().ToLowerInvariant();
            
            // strip trailing digits and punctuation
            // This regex matches any trailing non-word characters and digits
            normalized = Regex.Replace(normalized, @"[\d\W_]+$", "");
            
            return normalized;
        }

        public void Load()
        {
            string path = GetDefaultPath();
            LoadFromFile(path);
        }

        private void LoadFromFile(string path)
        {
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<SpaceTypeDictionaryData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data?.mappings != null)
                {
                    _dictionary.Clear();
                    foreach (var entry in data.mappings)
                    {
                        if (!string.IsNullOrEmpty(entry.normalizedName))
                        {
                            _dictionary[entry.normalizedName] = entry;
                        }
                    }
                }
            }
            catch
            {
                // Ignore load errors for missing/corrupt default file
            }
        }

        public void Save()
        {
            string path = GetDefaultPath();
            SaveToFile(path);
        }

        private void SaveToFile(string path)
        {
            var data = new SpaceTypeDictionaryData
            {
                schemaVersion = 1,
                exportedAt = DateTime.UtcNow.ToString("O"),
                mappings = _dictionary.Values.OrderBy(x => x.normalizedName).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(path, json);
        }

        public void ExportToFile(string path)
        {
            SaveToFile(path);
        }

        public void ImportFromFile(string path, out int imported, out int overwritten)
        {
            imported = 0;
            overwritten = 0;

            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<SpaceTypeDictionaryData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data?.mappings != null)
                {
                    foreach (var entry in data.mappings)
                    {
                        if (string.IsNullOrEmpty(entry.normalizedName)) continue;

                        if (_dictionary.ContainsKey(entry.normalizedName))
                        {
                            // Overwrite only if the space type is different
                            if (_dictionary[entry.normalizedName].spaceType != entry.spaceType)
                            {
                                _dictionary[entry.normalizedName] = entry;
                                overwritten++;
                            }
                        }
                        else
                        {
                            _dictionary[entry.normalizedName] = entry;
                            imported++;
                        }
                    }
                }
            }
            catch
            {
                // Handle parsing exceptions or file access exceptions if needed, but signature expects counts
                imported = 0;
                overwritten = 0;
            }
        }

        public void Record(string roomName, string spaceType)
        {
            if (string.IsNullOrWhiteSpace(roomName) || string.IsNullOrWhiteSpace(spaceType)) return;

            string normalized = Normalize(roomName);
            if (string.IsNullOrEmpty(normalized)) return;

            _dictionary[normalized] = new SpaceTypeMappingEntry
            {
                normalizedName = normalized,
                originalName = roomName,
                spaceType = spaceType
            };
        }

        public string GetMapping(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return null;
            string normalized = Normalize(roomName);
            
            if (_dictionary.TryGetValue(normalized, out var entry))
            {
                return entry.spaceType;
            }
            return null;
        }
        
        public int Count => _dictionary.Count;
    }
}
