using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace MyRaceMyRules
{
    /// <summary>
    /// A race discovered in a mod's assets, with the values currently declared by that mod.
    /// </summary>
    public class DetectedRace
    {
        public string Domain = "";          // e.g. "racialequality" ("" for the default seraph)
        public string ModelCode = "";       // e.g. "ork" / "seraph"
        public string AssetPath = "";        // asset holding the model settings
        public string FullCode => Domain.Length == 0 ? ModelCode : $"{Domain}:{ModelCode}";

        /// <summary>
        /// True for the default seraph model. Its skin parts live in the vanilla player
        /// entity JSON (SkinPartsAssetPath) rather than in the model-settings asset.
        /// </summary>
        public bool IsSeraph;
        public string? SkinPartsAssetPath;

        // Current values as declared by the owning mod (null = not specified).
        public float[]? SizeRange;           // [min, max]
        public float? EyeHeight;
        public float[]? CollisionBox;        // [width, height]
        public float? MinEyeHeight;
        public float? MaxEyeHeight;
        public float[]? MinCollisionBox;     // [width, height]
        public float[]? MaxCollisionBox;     // [width, height]
        public bool? Enabled;
        public List<string>? AvailableClasses;
        public List<string>? ExtraTraits;

        /// <summary>Skinnable parts: part code -> variant codes (hairstyles, colors, ...).</summary>
        public Dictionary<string, List<string>> SkinParts = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans loaded mods for PlayerModelLib custom-model configs and extracts the races they
    /// define. Custom models: assets/<domain>/config/customplayermodels/*.json, top-level
    /// keys = model codes. The default "seraph" model is special (per PlayerModelLib's
    /// LoadDefault): its model settings live in playermodellib:config/default-model-config.json
    /// under the key "seraph", while its skin parts (hairstyles, facial hair,
    /// colors) come from the vanilla player entity: game:entities/humanoid/player.json →
    /// attributes.skinnableParts. Eye-height and collision ranges come from the model config.
    /// </summary>
    public static class RaceDetector
    {
        private const string CategoryPath = "config/customplayermodels";
        public const string SeraphCode = "seraph";
        public const string SeraphModelConfigPath = "playermodellib:config/default-model-config.json";
        public const string SeraphModelConfigKey = "seraph";
        public const string PlayerEntityPath = "game:entities/humanoid/player.json";

        private static readonly string[] PlayerEntityPathCandidates =
        [
            PlayerEntityPath,
            "game:entities/player.json",
            "game:entities/playerentity.json",
            "game:entities/humanoid/playerentity.json",
            "entities/humanoid/player.json",
            "entities/humanoid/playerentity.json",
            "entity/humanoid/player.json",
            "entity/humanoid/playerentity.json",
            "game:entities/humanoid/playerentity-humanoid.json"
        ];

        /// <summary>
        /// Detect all races across all loaded mods, including the default seraph. Uses the
        /// asset manager, so it must run at or after AssetsLoaded.
        /// </summary>
        public static List<DetectedRace> DetectRaces(ICoreAPI api)
        {
            var results = new List<DetectedRace>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DetectedRace? seraph = DetectSeraph(api);
            if (seraph != null)
            {
                results.Add(seraph);
                seen.Add(seraph.FullCode);
            }

            List<IAsset> assets;
            try
            {
                assets = api.Assets.GetMany(CategoryPath, loadAsset: true);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Failed to enumerate customplayermodels assets: {0}", e);
                return results;
            }

            foreach (IAsset asset in assets)
            {
                if (asset?.Location == null) continue;
                if (!asset.Location.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                string domain = asset.Location.Domain;

                JObject? root;
                try
                {
                    root = JObject.Parse(asset.ToText());
                }
                catch (Exception e)
                {
                    api.Logger.Warning("[myracemyrules] Could not parse {0}: {1}", asset.Location, e.Message);
                    continue;
                }

                // Top-level keys are model codes; skip non-object values and known meta keys.
                foreach (var prop in root.Properties())
                {
                    if (prop.Value is not JObject modelObj) continue;
                    if (IsMetaKey(prop.Name)) continue;

                    var race = new DetectedRace
                    {
                        Domain = domain,
                        ModelCode = prop.Name,
                        AssetPath = asset.Location.ToString(),
                        SizeRange = ReadFloatArray(modelObj, "SizeRange"),
                        EyeHeight = ReadFloat(modelObj, "EyeHeight"),
                        CollisionBox = ReadFloatArray(modelObj, "CollisionBox"),
                        MinEyeHeight = ReadFloat(modelObj, "MinEyeHeight") ?? ReadFloat(modelObj, "EyeHeight"),
                        MaxEyeHeight = ReadFloat(modelObj, "MaxEyeHeight") ?? ReadFloat(modelObj, "EyeHeight"),
                        MinCollisionBox = ReadFloatArray(modelObj, "MinCollisionBox") ?? ReadFloatArray(modelObj, "CollisionBox"),
                        MaxCollisionBox = ReadFloatArray(modelObj, "MaxCollisionBox") ?? ReadFloatArray(modelObj, "CollisionBox"),
                        Enabled = ReadBool(modelObj, "Enabled"),
                        AvailableClasses = ReadStringList(modelObj, "AvailableClasses"),
                        ExtraTraits = ReadStringList(modelObj, "ExtraTraits"),
                        SkinParts = ReadSkinParts(GetPropCI(modelObj, "SkinnableParts") as JArray),
                    };

                    if (seen.Contains(race.FullCode)) continue;
                    seen.Add(race.FullCode);
                    results.Add(race);
                }
            }

            api.Logger.Notification("[myracemyrules] Detected {0} race(s).", results.Count);
            return results;
        }

        /// <summary>
        /// Detect the default seraph model: settings from PlayerModelLib's default-model-config,
        /// skin parts from the vanilla player entity JSON.
        /// </summary>
        public static DetectedRace? DetectSeraph(ICoreAPI api)
        {
            var race = new DetectedRace
            {
                Domain = "",
                ModelCode = SeraphCode,
                AssetPath = SeraphModelConfigPath,
                IsSeraph = true,
                SkinPartsAssetPath = PlayerEntityPath,
            };

            // Model settings (SizeRange, classes, ...) from default-model-config.json.
            try
            {
                IAsset? cfgAsset = api.Assets.TryGet(new AssetLocation(SeraphModelConfigPath));
                if (cfgAsset != null &&
                    JObject.Parse(cfgAsset.ToText())[SeraphModelConfigKey] is JObject cfg)
                {
                    race.SizeRange = ReadFloatArray(cfg, "SizeRange");
                    race.AvailableClasses = ReadStringList(cfg, "AvailableClasses");
                    race.ExtraTraits = ReadStringList(cfg, "ExtraTraits");
                    race.Enabled = ReadBool(cfg, "Enabled");
                }
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Could not parse seraph default-model-config: {0}", e.Message);
            }

            // Skin parts (hairstyles, facial hair, colors) + EyeHeight/CollisionBox from the
            // vanilla player entity JSON.
            try
            {
                JObject? entity = ResolvePlayerEntityJson(api);
                if (entity == null)
                {
                    api.Logger.Warning("[myracemyrules] Player entity asset not found; seraph detection incomplete.");
                    return race;
                }

                race.EyeHeight = ReadFloat(entity, "eyeHeight");

                if (GetPropCI(entity, "attributes") is JObject attributes)
                {
                    race.SkinParts = ReadSkinParts(GetPropCI(attributes, "skinnableParts") as JArray);
                }
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Could not parse player entity for seraph skin parts: {0}", e.Message);
            }

            return race;
        }

        public static IAsset? ResolvePlayerEntityAsset(ICoreAPI api)
        {
            foreach (string candidate in PlayerEntityPathCandidates)
            {
                if (api.Assets.TryGet(new AssetLocation(candidate)) is IAsset direct)
                {
                    api.Logger.Notification("[myracemyrules] Resolved seraph player entity asset to '{0}'.", direct.Location);
                    return direct;
                }
            }

            // The asset manager may not have registered the base player entity at this phase even when
            // the file exists on disk in the actual game install. Fall through to a direct filesystem
            // read using the game root the launcher is using.
            if (TryLoadPlayerEntityJsonFromDisk(out JObject? fsJson, out string? resolvedPath))
            {
                api.Logger.Notification("[myracemyrules] Resolved seraph player entity from the installed game files on disk: '{0}'.", resolvedPath);
                return null;
            }

            api.Logger.Warning("[myracemyrules] No player entity asset matched the known seraph paths under the asset manager.");
            return null;
        }

        public static JObject? ResolvePlayerEntityJson(ICoreAPI api)
        {
            foreach (string candidate in PlayerEntityPathCandidates)
            {
                if (api.Assets.TryGet(new AssetLocation(candidate)) is IAsset direct)
                return JObject.Parse(direct.ToText());
            }

            if (TryLoadPlayerEntityJsonFromDisk(out JObject? fsJson, out _))
                return fsJson;

            return null;
        }

        private static bool TryLoadPlayerEntityJsonFromDisk(out JObject? json, out string? resolvedPath)
        {
            json = null;
            resolvedPath = null;

            List<string> roots = [];
            string? vintageStory = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (!string.IsNullOrWhiteSpace(vintageStory)) roots.Add(vintageStory);

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            roots.Add(Path.Combine(appData, "Vintagestory"));
            roots.Add(Path.Combine(localAppData, "Vintagestory"));
            roots.Add(AppContext.BaseDirectory);

            AddLauncherRoots(roots, Path.Combine(appData, "VSLGameVersions"));
            AddLauncherRoots(roots, Path.Combine(appData, "VSLInstallations"));

            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                foreach (string rel in new[]
                {
                    Path.Combine("assets", "game", "entities", "humanoid", "player.json"),
                    Path.Combine("assets", "game", "entities", "humanoid", "playerentity.json"),
                    Path.Combine("assets", "game", "entities", "player.json"),
                    Path.Combine("assets", "entities", "humanoid", "player.json"),
                    Path.Combine("assets", "entities", "player.json")
                })
                {
                    string full = Path.Combine(root, rel);
                    if (File.Exists(full))
                {
                    try
                    {
                            json = JObject.Parse(File.ReadAllText(full));
                            resolvedPath = full;
                        return true;
                    }
                    catch { }
                    }
                }
            }

            return false;
        }

        private static void AddLauncherRoots(List<string> roots, string launcherRoot)
        {
            roots.Add(launcherRoot);
            if (!Directory.Exists(launcherRoot)) return;

            try
            {
                roots.AddRange(Directory.GetDirectories(launcherRoot));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Case-insensitive property lookup (asset JSON casing varies: vanilla lowercase, PML PascalCase).</summary>
        public static JToken? GetPropCI(JObject obj, string name)
        {
            foreach (var prop in obj.Properties())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            return null;
        }

        private static Dictionary<string, List<string>> ReadSkinParts(JArray? parts)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (parts == null) return result;

            foreach (var token in parts)
            {
                if (token is not JObject part) continue;
                string? code = (GetPropCI(part, "code") as JValue)?.Value?.ToString();
                if (string.IsNullOrEmpty(code)) continue;

                var variants = new List<string>();
                if (GetPropCI(part, "variants") is JArray variantArr)
                {
                    foreach (var v in variantArr)
                    {
                        if (v is not JObject variant) continue;
                        string? vcode = (GetPropCI(variant, "code") as JValue)?.Value?.ToString();
                        if (!string.IsNullOrEmpty(vcode)) variants.Add(vcode!);
                    }
                }
                result[code!] = variants;
            }
            return result;
        }

        private static bool IsMetaKey(string key)
        {
            return key switch
            {
                "DisabledElementsByShape" or "EnabledElementsByShape" or "AnimationsMetaData" => true,
                _ => false,
            };
        }

        private static float[]? ReadFloatArray(JObject o, string key)
        {
            if (GetPropCI(o, key) is not JArray arr) return null;
            var list = new List<float>();
            foreach (var t in arr)
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                    list.Add(t.Value<float>());
            return list.Count > 0 ? [.. list] : null;
        }

        private static float? ReadFloat(JObject o, string key)
        {
            var t = GetPropCI(o, key);
            if (t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer))
                return t.Value<float>();
            return null;
        }

        private static bool? ReadBool(JObject o, string key)
        {
            var t = GetPropCI(o, key);
            if (t != null && t.Type == JTokenType.Boolean) return t.Value<bool>();
            return null;
        }

        private static List<string>? ReadStringList(JObject o, string key)
        {
            if (GetPropCI(o, key) is not JArray arr) return null;
            var list = new List<string>();
            foreach (var t in arr)
                if (t.Type == JTokenType.String) list.Add(t.Value<string>()!);
            return list;
        }
    }
}
