using System.Collections.Generic;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

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
        /// (e.g. "racialequality:ork"). The default race uses the plain key "seraph".
        /// Only races the operator has chosen to override need an entry; anything absent is
        /// left untouched. Run /myracemyrules in-game (as an admin) to list the exact race
        /// codes available.
        /// </summary>
        public Dictionary<string, RaceOverrideEntry> Overrides = [];
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
        public float? MinEyeHeight;
        public float? MaxEyeHeight;
        public float? EyeHeight;

        /// <summary>Collision box, as [width, height]. Vanilla default is [0.6, 1.85].</summary>
        public float[]? MinCollisionBox;
        public float[]? MaxCollisionBox;
        public float[]? CollisionBox;

        // ----- Character-creation options -----

        /// <summary>
        /// Display name shown for this race in character creation. A literal string used
        /// verbatim (not a translation key); null keeps the race mod's name. Ignored for seraph,
        /// whose name comes from a game language entry rather than its model config.
        /// </summary>
        public string? Name;

        /// <summary>
        /// Description shown for this race in character creation (the blurb above the
        /// class/trait list). A literal string used verbatim; null keeps the race mod's
        /// description.
        ///
        /// Unlike <see cref="Name"/>, PlayerModelLib does NOT read the description from the
        /// model config — it looks up the language entry "&lt;domain&gt;:modeldesc-&lt;code&gt;"
        /// (for the default race, "game:modeldesc-seraph"). The mod applies this override by
        /// patching that loaded language entry, so unlike Name it IS supported for seraph.
        /// </summary>
        public string? Description;

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
        public Dictionary<string, SkinnablePartOverride> SkinnableParts = [];
    }

    /// <summary>
    /// Overrides for one skinnable part. All fields optional — only values that are present apply.
    /// </summary>
    public class SkinnablePartOverride
    {
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
        /// Add brand-new variants (options that are NOT on the default seraph and so cannot be
        /// pulled in with <see cref="IncludeDefaultVariants"/> / <see cref="AllowedVariants"/>).
        /// The classic use is adding voice types, whose variants carry a sound file.
        ///
        /// Two shapes are accepted, whichever is simplest for the case:
        ///
        /// 1) A MAP of "code" -&gt; value, for parts whose variants carry data:
        ///      "voicetype": { "AddVariants": { "frog": "koboldrdx:sounds/voice/treefrog" } }
        ///    The string value is used as the variant's primary asset, chosen by the part's
        ///    type — "sound" for voice parts, "texture" for texture parts, the "shape" base for
        ///    shape parts. For full control the value may instead be an object of raw variant
        ///    fields, e.g. { "sound": "domain:sounds/voice/x" }; the code is filled in from the key.
        ///
        /// 2) An ARRAY of bare codes, for code-only parts (e.g. "voicepitch"):
        ///      "voicepitch": { "AddVariants": ["verylow", "low"] }
        ///    Adding a bare code to a part that needs data (like a hair color, which needs a
        ///    texture) produces an option with nothing to show, so the mod logs a warning in
        ///    that case.
        ///
        /// Added before <see cref="AllowedVariants"/>/<see cref="RemoveVariants"/> run, so those
        /// filters still apply. Idempotent: re-adding an existing code updates it in place rather
        /// than duplicating it. Held as a raw JSON token because it accepts either a map or an
        /// array; it is interpreted when applied.
        /// </summary>
        public JToken? AddVariants;
    }
}
