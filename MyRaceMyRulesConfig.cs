using System.Collections.Generic;

namespace MyRaceMyRules
{
    /// <summary>
    /// Root config, persisted to VintagestoryData/ModConfig/myracemyrules.json via
    /// ICoreAPI.LoadModConfig / StoreModConfig. The server operator edits this file to
    /// choose which races to override; the mod applies it at world load.
    /// </summary>
    public class MyRaceMyRulesConfig
    {
        /// <summary>
        /// Per-race overrides, keyed by fully-qualified model code "domain:modelcode"
        /// (e.g. "racialequality:orc"). The default race uses the plain key "seraph".
        /// Only races the operator has chosen to override need an entry; anything absent is
        /// left untouched. Run /myracemyrules in-game (as an admin) to list the exact race
        /// codes available.
        /// </summary>
        public Dictionary<string, RaceOverrideEntry> Overrides = new();
    }

    /// <summary>
    /// Override values for a single race. Every field is optional: a field that is present
    /// replaces the race mod's value, and a field left out keeps it. Field names mirror
    /// PlayerModelLib's custom model config.
    /// </summary>
    public class RaceOverrideEntry
    {
        // ----- Height / size -----

        /// <summary>Size slider limits, as [min, max]. PlayerModelLib default is [0.8, 1.2].</summary>
        public float[]? SizeRange;

        /// <summary>Eye/camera height. Vanilla default is 1.7.</summary>
        public float? EyeHeight;

        /// <summary>Collision box, as [width, height]. Vanilla default is [0.6, 1.85].</summary>
        public float[]? CollisionBox;

        // ----- Character-creation options -----

        /// <summary>Whether this race appears in the character-creation dialog.</summary>
        public bool? Enabled;

        /// <summary>
        /// Classes this race may pick. An empty list means "all classes available", which is
        /// distinct from leaving the field out (keep the race mod's own list).
        /// </summary>
        public List<string>? AvailableClasses;

        /// <summary>Traits granted on top of the class.</summary>
        public List<string>? ExtraTraits;

        // ----- Skinnable parts (hairstyles, facial hair, colors, ...) -----

        /// <summary>
        /// Enable EVERY skinnable part this race defines, overriding any parts the race mod
        /// author disabled, and keep all of their variants. Applied first, so entries in
        /// <see cref="SkinnableParts"/> can still restrict individual parts afterwards.
        /// </summary>
        public bool EnableAllSkinnableParts = false;

        /// <summary>
        /// Give EVERY skinnable part of this race the complete variant list from the default
        /// race (seraph) — i.e. "all hairstyles, all beards, all colors". Missing variants are
        /// added; the race's own extras are kept.
        ///
        /// The complete list comes from the default race, which is always loaded, so it always
        /// matches the current game version. Parts the race does not define are NOT added — a
        /// race that cannot wear hair removes the part, and that intent is respected.
        ///
        /// Applies on a player's first connect (live) as well as at load.
        /// </summary>
        public bool IncludeAllDefaultVariants = false;

        /// <summary>
        /// Per-skinnable-part overrides, keyed by part code (e.g. "hairbase", "hairextra",
        /// "mustache", "beard", "haircolor", "eyecolor", "baseskin"). For custom races these
        /// map to the model's "SkinnableParts" entries; for "seraph" they map to the vanilla
        /// player entity's skinnableParts. Run "/myracemyrules &lt;racecode&gt;" to list a
        /// race's part codes and variant codes.
        /// </summary>
        public Dictionary<string, SkinnablePartOverride> SkinnableParts = new();
    }

    /// <summary>
    /// Overrides for one skinnable part. All fields optional — only values that are present apply.
    /// </summary>
    public class SkinnablePartOverride
    {
        /// <summary>
        /// Enable this part and keep ALL of its variants (ignores AllowedVariants /
        /// RemoveVariants for this part). Use to undo a restriction a race mod baked in.
        /// </summary>
        public bool EnableAll = false;

        /// <summary>
        /// Include the complete variant list for this part from the default race (seraph) —
        /// e.g. all hairstyles for "hairbase", all colors for "haircolor". Missing variants
        /// are added; the race's own extras are kept.
        ///
        /// Has no effect if the race does not define this part (a race that cannot wear hair
        /// removes the part, and that intent is respected).
        /// </summary>
        public bool IncludeDefaultVariants = false;

        /// <summary>Show/hide the entire part (e.g. remove the facial-hair section).</summary>
        public bool? Enabled;

        /// <summary>
        /// Keep ONLY these variant codes (e.g. the allowed hairstyles / colors). Applied
        /// before RemoveVariants. Null = no whitelist filtering.
        /// </summary>
        public List<string>? AllowedVariants;

        /// <summary>Remove these variant codes. Null = nothing removed.</summary>
        public List<string>? RemoveVariants;

        /// <summary>
        /// Advanced: raw property overrides merged onto the part's JSON (any setting in the
        /// part's section, e.g. {"useDropDown": true}). Applied at load only (not live).
        ///
        /// SECURITY: the values are typed as <c>object</c>, which is safe only because this
        /// config is deserialized with Newtonsoft's default settings — without
        /// <c>TypeNameHandling</c>, JSON values become plain primitives/JObjects and cannot name
        /// a .NET type to instantiate. Never enable <c>TypeNameHandling</c> on this config: a
        /// config also arrives over the network from the server, so that would turn this field
        /// into a deserialization gadget.
        /// </summary>
        public Dictionary<string, object>? Set;
    }
}
