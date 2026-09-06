using System.Linq;
using ScaleTrigger.Models;

namespace ScaleTrigger
{
    /// <summary>Reads LoadConfig's seed values from appsettings.json's "Load" section; SettingNames and MaxAllowedValues are also the source of truth for LoadConfigApiController's validation, and Validate/EnsureValid apply those same rules to the seed values themselves.</summary>
    public static class LoadConfigDefaults
    {
        public static readonly string[] SettingNames =
        {
            "CpuIterationsPerVote",
            "MemoryKilobytesPerVote",
            "DiskWriteKilobytesPerVote",
            "NetworkLatencyMillisecondsPerVote",
            "PayloadBytesPerVote",
            "DbCpuIterationsPerVote",
            "ConfigRefresh",
            "CacheEnabled",
            "LoadEnabled"
        };

        /// <summary>Per-setting ceiling on Max, scaled to what each unit can safely allocate/block on
        /// per vote (memory/disk bytes, latency ms), not just an arbitrary shared number. Shared by
        /// LoadConfigApiController (API-side validation) and Validate/EnsureValid below (seed-side
        /// validation), so appsettings.json/Bicep can't seed a value the API itself would reject.</summary>
        public static readonly IReadOnlyDictionary<string, int> MaxAllowedValues = new Dictionary<string, int>
        {
            ["CpuIterationsPerVote"] = 100_000_000,
            ["MemoryKilobytesPerVote"] = 4_194_304,
            ["DiskWriteKilobytesPerVote"] = 1_048_576,
            ["NetworkLatencyMillisecondsPerVote"] = 60_000,
            ["PayloadBytesPerVote"] = 10_485_760,
            ["DbCpuIterationsPerVote"] = 100_000_000,
            ["ConfigRefresh"] = 3600,
            ["CacheEnabled"] = 1,
            ["LoadEnabled"] = 1
        };

        public static List<LoadConfigSetting> ReadFrom(IConfiguration configuration)
        {
            var defaults = new List<LoadConfigSetting>();

            foreach (var settingName in SettingNames)
            {
                if (!int.TryParse(configuration[$"Load:{settingName}:Min"], out int min))
                {
                    min = 0;
                }

                if (!int.TryParse(configuration[$"Load:{settingName}:Max"], out int max))
                {
                    max = 0;
                }

                defaults.Add(new LoadConfigSetting { SettingName = settingName, Min = min, Max = max });
            }

            return defaults;
        }

        /// <summary>Null if valid, otherwise a human-readable reason - same rules LoadConfigApiController.Update enforces on a live edit.</summary>
        public static string? Validate(LoadConfigSetting setting)
        {
            if (!SettingNames.Contains(setting.SettingName))
            {
                return $"Unknown setting name: '{setting.SettingName}'.";
            }

            if (setting.Min < 0 || setting.Max < 0)
            {
                return $"'{setting.SettingName}': Min and Max must not be negative.";
            }

            if (setting.Min > setting.Max)
            {
                return $"'{setting.SettingName}': Min must not be greater than Max.";
            }

            if (setting.Max > MaxAllowedValues[setting.SettingName])
            {
                return $"'{setting.SettingName}': Max must not exceed {MaxAllowedValues[setting.SettingName]}.";
            }

            return null;
        }

        /// <summary>Throws if any seed default fails Validate, so a bad Load:* value in
        /// appsettings.json/Bicep fails startup instead of silently seeding LoadConfig with
        /// something the dashboard's own API would refuse to save.</summary>
        public static void EnsureValid(IEnumerable<LoadConfigSetting> settings)
        {
            foreach (var setting in settings)
            {
                string? error = Validate(setting);
                if (error != null)
                {
                    throw new InvalidOperationException($"Invalid Load:{setting.SettingName} default: {error}");
                }
            }
        }
    }
}
