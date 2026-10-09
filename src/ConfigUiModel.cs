using System.Collections.Generic;
using System.ComponentModel;

namespace MyRaceMyRules
{
    /// <summary>
    /// Library-agnostic VIEW model for the in-game settings screen (Design 1).
    ///
    /// This is NOT the authoritative config — <see cref="MyRaceMyRulesConfig"/> is. ConfigKit or
    /// configlib (whichever is installed) renders this object and reports edits; the bridge
    /// (<see cref="ConfigUiBridge"/>) maps those edits onto the real config via the existing
    /// server-authoritative setters, and refreshes this object from the real config on sync.
    ///
    /// It deliberately uses ONLY stock attributes (System.ComponentModel /
    /// System.ComponentModel.DataAnnotations), so it compiles and runs with neither library
    /// installed. There is no reference to either library anywhere in this file.
    ///
    /// Shape: a dictionary keyed by race code renders as its own drill-down screen in both
    /// libraries (dictionary-of-classes), and each race's fields become the right control
    /// (bool -> switch, list -> add/edit screen, nested dictionary -> another drill-down).
    /// </summary>
    public class ConfigUiModel
    {
        /// <summary>Per-race settings, keyed by race code ("domain:code", or "seraph").</summary>
        [Description("Each detected race and the settings this mod overrides for it.")]
        public Dictionary<string, RaceUiEntry> Races = new();
    }

    /// <summary>
    /// One race's editable surface. Field types are chosen for how the libraries render them;
    /// the bridge translates between these and the real override. Empty string / null means
    /// "leave the race's default" (mirrors the real config's null-means-default contract);
    /// numeric fields are free text so "min max" style values and "default" both round-trip.
    /// </summary>
    public class RaceUiEntry
    {
        [Description("Whether this race appears in character creation. Leave unset to keep the race's default.")]
        public bool? Enabled;

        [Description("Display name shown in character creation. Blank keeps the race mod's name.")]
        public string Name = "";

        [Description("Description blurb shown in character creation. Blank keeps the race mod's description.")]
        public string Description = "";

        [Description("Character size range as \"min max\" (e.g. \"0.5 2.0\"). Blank keeps default; \"default\" resets. Not used for seraph.")]
        public string SizeRange = "";

        [Description("Base eye height (a number). Blank keeps default; \"default\" resets. Not used for seraph.")]
        public string EyeHeight = "";

        [Description("Base collision box as \"width height\". Blank keeps default; \"default\" resets. Not used for seraph.")]
        public string Collision = "";

        [Description("Classes this race may pick. Empty list = all classes (unrestricted).")]
        public List<string> AvailableClasses = new();

        [Description("Extra traits granted on top of the class. Empty list = no extra traits.")]
        public List<string> ExtraTraits = new();

        [Description("Give every appearance section this race has the full default variant set.")]
        public bool IncludeAllDefaultVariants;

        [Description("Per-appearance-section overrides (hairstyles, colors, etc.).")]
        public Dictionary<string, PartUiEntry> Sections = new();
    }

    /// <summary>One appearance section's filters, as the UI exposes them.</summary>
    public class PartUiEntry
    {
        [Description("Show or hide this whole section. Leave unset to keep the race's default.")]
        public bool? Enabled;

        [Description("Keep ONLY these variant codes. Empty = no whitelist (keep all the race has).")]
        public List<string> AllowedVariants = new();

        [Description("Remove these variant codes. Empty = remove none.")]
        public List<string> RemoveVariants = new();
    }
}
