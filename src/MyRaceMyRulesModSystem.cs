using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace MyRaceMyRules
{
    /// <summary>
    /// My Race My Rules — server-authoritative race-override system, universal.
    ///
    /// Steady state: the server's ModConfig/myracemyrules.json is authoritative. Each side
    /// applies its config by mutating race assets in AssetsLoaded (ExecuteOrder 0.05) —
    /// before the game parses entity types (~0.2, needed for seraph skin parts) and before
    /// PlayerModelLib reads model configs (0.21). On join the server pushes the config to the
    /// client, which caches it and applies it from the cache every load.
    ///
    /// The client cache is scoped PER SERVER (myracemyrules-servercache-&lt;hash&gt;.json, keyed
    /// off World.SavegameIdentifier) so one server's settings can never be applied to a
    /// different server or to a single-player world. Single player skips the cache entirely and
    /// reads the authoritative file on both sides, which is also what keeps those two sides in
    /// agreement. Tampering with a cache achieves nothing — the server overwrites it on join.
    ///
    /// First join: the cache is empty at load, but the sync packet arrives before the
    /// character-creation dialog opens, so the client LIVE-applies the dialog-relevant values
    /// (SizeRange, Enabled, AvailableClasses, ExtraTraits, skin part variants) to
    /// PlayerModelLib's in-memory CustomModels (see LiveModelUpdater). Server settings thus
    /// govern character creation on the very first session. EyeHeight/CollisionBox and raw
    /// skin-part property merges converge at the next load via the asset path.
    ///
    /// Seraph (default race) is special, per PlayerModelLib's LoadDefault():
    ///   - skin parts (hairstyles/facial hair/colors) + EyeHeight/CollisionBox come from the
    ///     vanilla player entity (game:entities/humanoid/player.json)
    ///   - model settings (SizeRange, classes, ...) come from
    ///     playermodellib:config/default-model-config.json under "playermodellib:seraph"
    /// Its config key here is plain "seraph".
    /// </summary>
    public class MyRaceMyRulesModSystem : ModSystem
    {
        public const string Domain = "myracemyrules";
        private const string ServerConfigFile = Domain + ".json";
        private const string ChannelName = "myracemyrules.sync";
        private const string AdminPrivilege = "controlserver";
        private const float MinSizeRangeMin = 0.2f;

        /// <summary>
        /// Upper bound on a synced config payload. Real configs are a few KB (they contain
        /// codes, not content), so this is generous while still refusing a server that tries
        /// to hand a client an unbounded string to parse.
        /// </summary>
        private const int MaxConfigJsonChars = 1_000_000;

        /// <summary>
        /// The config this side applies at load. Server: from ServerConfigFile. Client: from
        /// the server-cache file written by the last sync.
        /// </summary>
        public MyRaceMyRulesConfig Config = new();

        public List<DetectedRace> DetectedRaces = [];

        /// <summary>Hash of the config this side actually APPLIED at load (for change detection).</summary>
        private string _appliedHash = "";

        /// <summary>
        /// Client-side: true if the config this session applied had no overrides (brand-new
        /// client / empty cache). Used to keep the mod silent on first join.
        /// </summary>
        private bool _appliedWasEmpty = true;

        private ICoreClientAPI? _capi;
        private ICoreServerAPI? _sapi;
        private IServerNetworkChannel? _serverChannel;

        /// <summary>
        /// True only when the server config file did not exist and had to be created. In that
        /// startup case we seed every discovered race once so the operator can edit them in the
        /// file without having to manually create entries. Later loads never mutate the config.
        /// </summary>
        private bool _seededInitialRaces;

        /// <summary>
        /// Pristine copy of the default race's (seraph's) skinnableParts, captured BEFORE any
        /// overrides are applied. This is the canonical "all available options" list — every
        /// hairstyle, beard, color the game ships. Read live from the vanilla player entity so
        /// it always matches the current game version, and snapshotted first so that
        /// restricting seraph does not shrink what "all" means for other races.
        ///
        /// Only needed while overrides are being applied, so it is released immediately after
        /// (it is the largest single object this mod holds — the whole vanilla appearance set).
        /// </summary>
        private JArray? _defaultSkinnableParts;

        // Run before the game's registry loaders (~0.2, parse the player entity we mutate for
        // seraph) and before PlayerModelLib's CustomModelsSystem (0.21).
        public override double ExecuteOrder() => 0.05;

        // ---------------------------------------------------------------------
        // Shared
        // ---------------------------------------------------------------------

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            // Load the config THIS side will apply, before AssetsLoaded runs.
            if (api.Side == EnumAppSide.Server)
            {
                // Authoritative ModConfig/myracemyrules.json (created if missing).
                LoadServerConfig(api);
                return;
            }

            // Single player: client and server are the same machine, so read the authoritative
            // file directly instead of going through a sync cache. Reading the same file on both
            // sides is what keeps them from diverging.
            if (api is ICoreClientAPI { IsSinglePlayer: true })
            {
                LoadLocalConfigReadOnly(api);
                return;
            }

            // Multiplayer client: the cache written by the last visit to THIS server.
            LoadClientCache(api);
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);
            DetectedRaces = RaceDetector.DetectRaces(api);

            // Snapshot the canonical "all options" list (seraph's parts) BEFORE mutating
            // anything, so restricting seraph can't shrink what "all" means elsewhere.
            _defaultSkinnableParts = CaptureDefaultSkinnableParts(api);

            // Apply in AssetsLoaded (not AssetsFinalize): the seraph skin-part override
            // mutates the vanilla player entity JSON, which the game's registry loader parses
            // at ~ExecuteOrder 0.2 — during this same phase, after us (0.05). Custom-model
            // configs are read even later (PlayerModelLib AssetsFinalize, 0.21).
            if (_seededInitialRaces)
            {
                foreach (var race in DetectedRaces)
                {
                    if (!Config.Overrides.ContainsKey(race.FullCode))
                        Config.Overrides[race.FullCode] = new RaceOverrideEntry();
                }

                try
                {
                    api.StoreModConfig(Config, ServerConfigFile);
                    api.Logger.Notification("[myracemyrules] Seeded initial config with {0} detected race(s).", Config.Overrides.Count);
                }
                catch (Exception e)
                {
                    api.Logger.Warning("[myracemyrules] Failed to save seeded initial race list: {0}", e.Message);
                }

                _seededInitialRaces = false;
            }

            _appliedHash = HashConfig(Config);
            _appliedWasEmpty = Config.Overrides.Count == 0;
            ApplyOverrides(api);

            // The snapshot exists only to serve ApplyOverrides. Drop it now rather than holding
            // the entire vanilla appearance set for the lifetime of the world.
            _defaultSkinnableParts = null;
        }

        /// <summary>
        /// A ModSystem instance is created per world load, so anything this one hooked into the
        /// game has to be unhooked here. The PlayerJoin subscription is the one that matters:
        /// left attached, it keeps this instance (and its config and race list) alive after the
        /// world unloads, and a second copy accumulates on the next load.
        /// </summary>
        public override void Dispose()
        {
            _sapi?.Event.PlayerJoin -= OnPlayerJoin;
            _sapi = null;

            _capi = null;
            _serverChannel = null;
            _defaultSkinnableParts = null;
            DetectedRaces = [];

            base.Dispose();
        }

        /// <summary>Deep-clone the vanilla player entity's skinnableParts array.</summary>
        private static JArray? CaptureDefaultSkinnableParts(ICoreAPI api)
        {
            JObject? entity = LoadAssetJson(api, RaceDetector.PlayerEntityPath);
            if (entity == null)
            {
                api.Logger.Warning("[myracemyrules] Could not resolve the default player entity asset for seraph defaults.");
                return null;
            }

            if (RaceDetector.GetPropCI(entity, "attributes") is not JObject attributes) return null;
            if (RaceDetector.GetPropCI(attributes, "skinnableParts") is not JArray parts) return null;
            return (JArray)parts.DeepClone();
        }

        // ---------------------------------------------------------------------
        // Server side
        // ---------------------------------------------------------------------

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            _sapi = sapi;

            _serverChannel = sapi.Network
                .RegisterChannel(ChannelName)
                .RegisterMessageType<ConfigSyncPacket>();

            sapi.Event.PlayerJoin += OnPlayerJoin;

            sapi.ChatCommands
                .Create("myracemyrules")
                .WithDescription("List detected races and overrides; give a race code to list its skin parts and variants")
                .RequiresPrivilege(AdminPrivilege)
                .WithArgs(sapi.ChatCommands.Parsers.OptionalWord("racecode"),
                    sapi.ChatCommands.Parsers.OptionalWord("command"),
                    sapi.ChatCommands.Parsers.OptionalWord("value1"),
                    sapi.ChatCommands.Parsers.OptionalWord("value2"))
                .HandleWith(args => HandleRaceCommand(sapi, args));

            sapi.ChatCommands
                .Create("mrmr")
                .WithDescription("Alias for /myracemyrules")
                .RequiresPrivilege(AdminPrivilege)
                .WithArgs(sapi.ChatCommands.Parsers.OptionalWord("racecode"),
                    sapi.ChatCommands.Parsers.OptionalWord("command"),
                    sapi.ChatCommands.Parsers.OptionalWord("value1"),
                    sapi.ChatCommands.Parsers.OptionalWord("value2"))
                .HandleWith(args => HandleRaceCommand(sapi, args));
        }

        private TextCommandResult HandleRaceCommand(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            if (args == null || args.ArgCount == 0 || args[0] == null)
                return TextCommandResult.Success(DescribeRaces());

            string? first = Convert.ToString(args[0]);
            if (string.IsNullOrWhiteSpace(first))
                return TextCommandResult.Success(DescribeRaces());

            if (string.Equals(first, "help", StringComparison.OrdinalIgnoreCase))
                return TextCommandResult.Success(DescribeHelp());

            if (string.Equals(first, "race", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "races", StringComparison.OrdinalIgnoreCase))
                return TextCommandResult.Success(DescribeRaces());

            if (args.ArgCount == 1)
                return TextCommandResult.Success(DescribeSkinParts(first));

            string? action = args.ArgCount > 1 ? Convert.ToString(args[1]) : null;
            if (string.IsNullOrWhiteSpace(action))
                return TextCommandResult.Success(DescribeSkinParts(first));

            if (string.Equals(action, "help", StringComparison.OrdinalIgnoreCase))
                return TextCommandResult.Success(DescribeHelp());

            if (string.Equals(action, "enableall", StringComparison.OrdinalIgnoreCase))
            {
                if (args.ArgCount < 3) return TextCommandResult.Error("Usage: /myracemyrules mod:race or all, then enableall part");
                string partCode = Convert.ToString(args[2]) ?? "";

                if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase))
                {
                    var allTargets = GetTargetRaceCodes(first);
                    foreach (var race in allTargets)
                        SetEnableAllForPart(api, race, partCode);

                    return TextCommandResult.Success($"Applied 'enableall {partCode}' to {allTargets.Count} race(s): {string.Join(", ", allTargets)}.");
                }

                return TextCommandResult.Success(SetEnableAllForPart(api, first, partCode));
            }

            if (string.Equals(action, "sizerange", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(first, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Error("SizeRange cannot be edited for seraph.");

                if (!GetTargetRaceCodes(first).Any())
                    return TextCommandResult.Error($"Race '{first}' is not detected on this server. Use /myracemyrules to list detected races.");

                if (args.ArgCount >= 3 && string.Equals(Convert.ToString(args[2]), "default", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        var allTargets = GetTargetRaceCodes(first)
                            .Where(race => !string.Equals(race, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var race in allTargets)
                            SetDefaultSizeRange(api, race);
                        return TextCommandResult.Success($"Reset size range to default for {allTargets.Count} race(s): {string.Join(", ", allTargets)}.");
                    }

                    return TextCommandResult.Success(SetDefaultSizeRange(api, first));
                }

                if (args.ArgCount < 4) return TextCommandResult.Error("Usage: /myracemyrules mod:race or all, then sizerange min max");
                if (!float.TryParse(Convert.ToString(args[2]), out float min) ||
                    !float.TryParse(Convert.ToString(args[3]), out float max))
                    return TextCommandResult.Error("Size range requires two numeric values.");

                if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase))
                {
                    var allTargets = GetTargetRaceCodes(first)
                        .Where(race => !string.Equals(race, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var race in allTargets)
                    {
                        float targetMin = min;
                        if (targetMin < MinSizeRangeMin)
                        {
                            api.Logger.Error("[myracemyrules] Size range minimum for '{0}' was {1}; changing it to {2} to enforce the minimum allowed value.",
                                race, targetMin, MinSizeRangeMin);
                            targetMin = MinSizeRangeMin;
                        }

                        SetSizeRange(api, race, targetMin, max);
                    }

                    return TextCommandResult.Success($"Applied size range [{(min < MinSizeRangeMin ? MinSizeRangeMin : min)}, {max}] to {allTargets.Count} race(s): {string.Join(", ", allTargets)}.");
                }

                if (min < MinSizeRangeMin)
                {
                    api.Logger.Error("[myracemyrules] Size range minimum for '{0}' was {1}; changing it to {2} to enforce the minimum allowed value.",
                        first, min, MinSizeRangeMin);
                    min = MinSizeRangeMin;
                }
                return TextCommandResult.Success(SetSizeRange(api, first, min, max));
            }

            if (string.Equals(action, "eyeheight", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(first, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Error("EyeHeight cannot be edited for seraph.");

                if (args.ArgCount < 3) return TextCommandResult.Error("Usage: /myracemyrules mod:race eyeheight baseValue");
                if (string.Equals(Convert.ToString(args[2]), "default", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        var allTargets = GetTargetRaceCodes(first)
                            .Where(race => !string.Equals(race, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var race in allTargets)
                            SetDefaultEyeHeight(api, race);
                        return TextCommandResult.Success($"Reset eye height to default for {allTargets.Count} race(s): {string.Join(", ", allTargets)}.");
                    }

                    return TextCommandResult.Success(SetDefaultEyeHeight(api, first));
                }
                if (!float.TryParse(Convert.ToString(args[2]), out float baseEyeHeight))
                    return TextCommandResult.Error("Eye height must be numeric.");

                if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase))
                {
                    var allTargets = GetTargetRaceCodes(first)
                        .Where(race => !string.Equals(race, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var raceCode in allTargets)
                    {
                        DetectedRace? race = DetectedRaces.FirstOrDefault(r =>
                            string.Equals(r.FullCode, raceCode, StringComparison.OrdinalIgnoreCase));
                        if (race?.SizeRange is not { Length: 2 }) continue;
                        float[] values = ScaleBySizeRange(race, baseEyeHeight);
                        SetEyeHeight(api, raceCode, baseEyeHeight, values[0], values[1]);
                    }
                    return TextCommandResult.Success($"Applied eye height base {baseEyeHeight} to {allTargets.Count} race(s): {string.Join(", ", allTargets)}.");
                }

                DetectedRace? eyeRace = DetectedRaces.FirstOrDefault(r =>
                    string.Equals(r.FullCode, first, StringComparison.OrdinalIgnoreCase));
                if (eyeRace?.SizeRange is not { Length: 2 })
                    return TextCommandResult.Error($"Race '{first}' has no valid SizeRange.");
                float[] eyeHeightRange = ScaleBySizeRange(eyeRace, baseEyeHeight);
                return TextCommandResult.Success(SetEyeHeight(api, first, baseEyeHeight, eyeHeightRange[0], eyeHeightRange[1]));
            }

            if (string.Equals(action, "collision", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(first, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Error("CollisionBox cannot be edited for seraph.");

                if (args.ArgCount < 3) return TextCommandResult.Error("Usage: /myracemyrules mod:race collision width height");
                if (string.Equals(Convert.ToString(args[2]), "default", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        var allTargets = GetTargetRaceCodes(first)
                            .Where(race => !string.Equals(race, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var race in allTargets)
                            SetDefaultCollisionBox(api, race);
                        return TextCommandResult.Success($"Reset collision box to default for {allTargets.Count} race(s): {string.Join(", ", allTargets)}.");
                    }

                    return TextCommandResult.Success(SetDefaultCollisionBox(api, first));
                }
                if (args.ArgCount < 4 ||
                    !float.TryParse(Convert.ToString(args[2]), out float baseWidth) ||
                    !float.TryParse(Convert.ToString(args[3]), out float baseHeight))
                    return TextCommandResult.Error("Collision box requires two numeric base values: width height.");

                if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase))
                {
                    var allTargets = GetTargetRaceCodes(first)
                        .Where(race => !string.Equals(race, RaceDetector.SeraphCode, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var raceCode in allTargets)
                    {
                        DetectedRace? race = DetectedRaces.FirstOrDefault(r =>
                            string.Equals(r.FullCode, raceCode, StringComparison.OrdinalIgnoreCase));
                        if (race?.SizeRange is not { Length: 2 }) continue;
                        float[][] values = ScaleCollisionBySizeRange(race, baseWidth, baseHeight);
                        SetCollisionBox(api, raceCode, [baseWidth, baseHeight], values[0], values[1]);
                    }
                    return TextCommandResult.Success($"Applied collision box base=[{baseWidth}, {baseHeight}] to {allTargets.Count} race(s): {string.Join(", ", allTargets)}.");
                }

                DetectedRace? collisionRace = DetectedRaces.FirstOrDefault(r =>
                    string.Equals(r.FullCode, first, StringComparison.OrdinalIgnoreCase));
                if (collisionRace?.SizeRange is not { Length: 2 })
                    return TextCommandResult.Error($"Race '{first}' has no valid SizeRange.");
                float[][] collisionRange = ScaleCollisionBySizeRange(collisionRace, baseWidth, baseHeight);
                return TextCommandResult.Success(SetCollisionBox(api, first, [baseWidth, baseHeight], collisionRange[0], collisionRange[1]));
            }

            if (string.Equals(action, "disable", StringComparison.OrdinalIgnoreCase))
                return TextCommandResult.Success(SetEnabled(api, first, false));

            if (string.Equals(action, "enable", StringComparison.OrdinalIgnoreCase))
                return TextCommandResult.Success(SetEnabled(api, first, true));

            return TextCommandResult.Success(DescribeSkinParts(first));
        }

        private string SetEnabled(ICoreServerAPI api, string raceCode, bool value)
        {
            var ov = GetOrCreateOverride(raceCode);
            ov.Enabled = value;
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' is now {(value ? "enabled" : "disabled")}.";
        }

        private string SetSizeRange(ICoreServerAPI api, string raceCode, float min, float max)
        {
            if (min < MinSizeRangeMin)
            {
                api.Logger.Error("[myracemyrules] Size range minimum for '{0}' was {1}; changing it to {2} to enforce the minimum allowed value.",
                    raceCode, min, MinSizeRangeMin);
                min = MinSizeRangeMin;
            }

            var ov = GetOrCreateOverride(raceCode);
            ov.SizeRange = [min, max];
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' size range set to [{min}, {max}].";
        }

        private string SetDefaultSizeRange(ICoreServerAPI api, string raceCode)
        {
            var ov = GetOrCreateOverride(raceCode);
            ov.SizeRange = null;
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' size range reset to default.";
        }

        private static float RoundToHundredth(float value) =>
            (float)Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private static float[] ScaleBySizeRange(DetectedRace race, float baseValue)
        {
            return
            [
                RoundToHundredth(baseValue * race.SizeRange![0]),
                RoundToHundredth(baseValue * race.SizeRange[1])
            ];
        }

        private static float[][] ScaleCollisionBySizeRange(DetectedRace race, float baseWidth, float baseHeight)
        {
            float[] width = ScaleBySizeRange(race, baseWidth);
            float[] height = ScaleBySizeRange(race, baseHeight);
            return
            [
                [width[0], height[0]],
                [width[1], height[1]]
            ];
        }

        private string SetEyeHeight(ICoreServerAPI api, string raceCode, float baseValue, float min, float max)
        {
            var ov = GetOrCreateOverride(raceCode);
            ov.EyeHeight = baseValue;
            ov.MinEyeHeight = min;
            ov.MaxEyeHeight = max;
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' eye height set to {baseValue} (Range: [{min}, {max}]).";
        }

        private string SetDefaultEyeHeight(ICoreServerAPI api, string raceCode)
        {
            var ov = GetOrCreateOverride(raceCode);
            ov.EyeHeight = null;
            ov.MinEyeHeight = null;
            ov.MaxEyeHeight = null;
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' eye height range reset to default.";
        }

        private string SetCollisionBox(ICoreServerAPI api, string raceCode, float[] baseValue, float[] min, float[] max)
        {
            var ov = GetOrCreateOverride(raceCode);
            ov.CollisionBox = baseValue;
            ov.MinCollisionBox = min;
            ov.MaxCollisionBox = max;
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' collision box set to {string.Join(", ", baseValue)} (Range: [{string.Join(", ", min)}, {string.Join(", ", max)}]).";
        }

        private string SetDefaultCollisionBox(ICoreServerAPI api, string raceCode)
        {
            var ov = GetOrCreateOverride(raceCode);
            ov.CollisionBox = null;
            ov.MinCollisionBox = null;
            ov.MaxCollisionBox = null;
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' collision box range reset to default.";
        }

        private string SetEnableAllForPart(ICoreServerAPI api, string raceCode, string partCode)
        {
            var ov = GetOrCreateOverride(raceCode);
            ov.SkinnableParts.TryGetValue(partCode, out var part);
            if (part == null)
            {
                part = new SkinnablePartOverride();
                ov.SkinnableParts[partCode] = part;
            }
            part.IncludeDefaultVariants = true;
            part.AllowedVariants = null;
            part.RemoveVariants = null;
            SaveConfig(api);
            ReapplyOverrides(api);
            return $"Race '{raceCode}' part '{partCode}' set to include all default variants.";
        }

        private RaceOverrideEntry GetOrCreateOverride(string raceCode)
        {
            if (!Config.Overrides.TryGetValue(raceCode, out var ov))
            {
                ov = new RaceOverrideEntry();
                Config.Overrides[raceCode] = ov;
            }
            return ov;
        }

        private void SaveConfig(ICoreServerAPI api)
        {
            try { api.StoreModConfig(Config, ServerConfigFile); }
            catch (Exception e) { api.Logger.Warning("[myracemyrules] Failed to save config from chat command: {0}", e.Message); }
        }

        private void ReapplyOverrides(ICoreServerAPI api)
        {
            try
            {
                _defaultSkinnableParts = CaptureDefaultSkinnableParts(api);
                ApplyOverrides(api);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Failed to apply updated overrides from chat command: {0}", e.Message);
            }
            finally
            {
                _defaultSkinnableParts = null;
            }
        }

        private List<string> GetTargetRaceCodes(string raceCode)
        {
            if (string.Equals(raceCode, "all", StringComparison.OrdinalIgnoreCase))
            {
                return [.. DetectedRaces
                    .Select(r => r.FullCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)];
            }

            return [raceCode];
        }

        private static int CountRaceOverrides(RaceOverrideEntry? overrideEntry)
        {
            if (overrideEntry == null) return 0;

            int count = 0;
            if (overrideEntry.SizeRange != null) count++;
            if (overrideEntry.EyeHeight.HasValue) count++;
            if (overrideEntry.MinEyeHeight.HasValue || overrideEntry.MaxEyeHeight.HasValue) count++;
            if (overrideEntry.CollisionBox != null) count++;
            if (overrideEntry.MinCollisionBox != null || overrideEntry.MaxCollisionBox != null) count++;
            if (overrideEntry.Enabled.HasValue) count++;
            if (overrideEntry.AvailableClasses != null) count++;
            if (overrideEntry.ExtraTraits != null) count++;
            if (overrideEntry.IncludeAllDefaultVariants) count++;

            foreach (var partOverride in overrideEntry.SkinnableParts.Values)
            {
                if (partOverride == null) continue;
                if (partOverride.IncludeDefaultVariants) count++;
                if (partOverride.Enabled.HasValue) count++;
                if (partOverride.AllowedVariants != null) count++;
                if (partOverride.RemoveVariants != null) count++;
            }

            return count;
        }

        private static string DescribeHelp()
        {
            var sb = new StringBuilder();
            sb.AppendLine("MyRaceMyRules commands:");
            sb.AppendLine("  /myracemyrules help");
            sb.AppendLine("  /myracemyrules mod:race");
            sb.AppendLine("  /myracemyrules mod:race enable");
            sb.AppendLine("  /myracemyrules mod:race disable");
            sb.AppendLine("  /myracemyrules mod:race eyeheight baseValue");
            sb.AppendLine("  /myracemyrules mod:race collision width height");
            sb.AppendLine("  /myracemyrules mod:race sizerange min max");
            sb.AppendLine("  /myracemyrules mod:race enableall part");
            sb.AppendLine("  /myracemyrules all sizerange min max");
            sb.AppendLine("  /myracemyrules all enableall part");
            sb.AppendLine();
            sb.AppendLine("Aliases: /mrmr");
            sb.AppendLine("Notes:");
            sb.AppendLine("  - 'sizerange' enforces a minimum of 0.2");
            sb.AppendLine("  - Use 'default' for eyeheight, collision, or sizerange to reset to the default value");
            sb.AppendLine("  - 'eyeheight' and 'collision' also scale by the race's SizeRange to produce a min/max range");
            return sb.ToString();
        }  

        private string DescribeRaces()
        {
            var sb = new StringBuilder();
            sb.AppendLine("MyRaceMyRules: use '/myracemyrules help' for all commands.");
            sb.AppendLine($"Detected races: {DetectedRaces.Count}");
            foreach (var r in DetectedRaces)
            {
                int raceOverrideCount = Config.Overrides.TryGetValue(r.FullCode, out var entry)
                    ? CountRaceOverrides(entry)
                    : 0;
                sb.AppendLine($"  {r.FullCode} - Active overrides: {raceOverrideCount}");
            }
            sb.AppendLine("Use '/myracemyrules racecode' to list a race's skin parts and variant codes.");
            sb.AppendLine("Use '/myracemyrules help' for a complete command list.");
            sb.AppendLine("Edit ModConfig/" + ServerConfigFile + " on the server and reload the world to change overrides.");
            return sb.ToString();
        }

        private string DescribeSkinParts(string raceCode)
        {
            DetectedRace? race = DetectedRaces.FirstOrDefault(
                r => string.Equals(r.FullCode, raceCode, StringComparison.OrdinalIgnoreCase));
            if (race == null)
                return $"Race '{raceCode}' not found. Run /myracemyrules to list race codes.";

            var sb = new StringBuilder();
            string availableClassesText = race.AvailableClasses is null || race.AvailableClasses.Count == 0
                ? "all classes (unrestricted)"
                : string.Join(", ", race.AvailableClasses);
            string extraTraitsText = race.ExtraTraits is null || race.ExtraTraits.Count == 0
                ? "(none)"
                : string.Join(", ", race.ExtraTraits);

            sb.AppendLine($"Race: {race.FullCode}");
            sb.AppendLine($"=================================");
            sb.AppendLine($"AvailableClasses: {availableClassesText}");
            sb.AppendLine($"ExtraTraits: {extraTraitsText}");
            sb.AppendLine($"SizeRange: {(race.SizeRange is null ? "(not specified)" : $"[{string.Join(", ", race.SizeRange)}]")}");
            string eyeHeightText = race.MinEyeHeight.HasValue || race.MaxEyeHeight.HasValue
                ? $"[{race.MinEyeHeight?.ToString() ?? "-"}, {race.MaxEyeHeight?.ToString() ?? "-"}]"
                : "(not specified)";
            string collisionBoxText = race.MinCollisionBox != null || race.MaxCollisionBox != null
                ? $"min=[{(race.MinCollisionBox == null ? "-" : string.Join(", ", race.MinCollisionBox))}], max=[{(race.MaxCollisionBox == null ? "-" : string.Join(", ", race.MaxCollisionBox))}]"
                : "(not specified)";
            sb.AppendLine($"EyeHeight: {(race.EyeHeight.HasValue ? race.EyeHeight.Value.ToString() : "(not specified)")}");
            sb.AppendLine($"EyeHeight Range: {eyeHeightText}");
            sb.AppendLine($"CollisionBox: {(race.CollisionBox is null ? "(not specified)" : $"[{string.Join(", ", race.CollisionBox)}]")}");
            sb.AppendLine($"CollisionBox Range: {collisionBoxText}");
            sb.AppendLine($"Skin parts ({race.SkinParts.Count}):");
            sb.AppendLine($"=================================");
            foreach ((string code, List<string> variants) in race.SkinParts)
            {
                sb.AppendLine($"{code} ({variants.Count} variant(s)):");
                if (variants.Count > 0)
                    sb.AppendLine($"    {string.Join(", ", variants)}");
                    sb.AppendLine($"---------------------------------");
            }
            return sb.ToString();
        }

        private void OnPlayerJoin(IServerPlayer player)
        {
            if (_serverChannel == null) return;
            string json = JsonConvert.SerializeObject(Config);
            _serverChannel.SendPacket(new ConfigSyncPacket
            {
                ConfigJson = json,
                ConfigHash = HashConfig(Config)
            }, player);
        }

        private void LoadServerConfig(ICoreAPI api)
        {
            bool configMissing = IsServerConfigMissing(api);
            _seededInitialRaces = configMissing;

            try
            {
                MyRaceMyRulesConfig? loadedConfig = api.LoadModConfig<MyRaceMyRulesConfig>(ServerConfigFile);
                if (loadedConfig == null)
                {
                    if (!configMissing)
                    {
                        api.Logger.Error("[myracemyrules] Server config could not be read and was left unchanged. Repair '{0}' and restart the world.",
                            ServerConfigFile);
                        Config = new MyRaceMyRulesConfig();
                        _seededInitialRaces = false;
                        return;
                    }

                    loadedConfig = new MyRaceMyRulesConfig();
                }

                Config = loadedConfig;
            }
            catch (Exception e)
            {
                api.Logger.Error("[myracemyrules] Failed to load server config; the existing file was left unchanged. Repair '{0}' and restart the world: {1}",
                    ServerConfigFile, e);
                Config = new MyRaceMyRulesConfig();
                _seededInitialRaces = false;
                return;
            }

            SanitizeConfig(api, Config);

            // Write back so first run leaves a well-formed file for the operator to edit.
            try { api.StoreModConfig(Config, ServerConfigFile); }
            catch (Exception e) { api.Logger.Error("[myracemyrules] Failed to write server config: {0}", e); }
        }

        private static void SanitizeConfig(ICoreAPI api, MyRaceMyRulesConfig config)
        {
            foreach (var pair in config.Overrides)
            {
                var ov = pair.Value;
                if (ov == null) continue;
                if (ov.SizeRange == null || ov.SizeRange.Length != 2) continue;
                if (ov.SizeRange[0] < MinSizeRangeMin)
                {
                    api.Logger.Error("[myracemyrules] Override for '{0}' had a sizeRange minimum of {1}; changing to {2} to enforce the minimum allowed value.",
                        pair.Key, ov.SizeRange[0], MinSizeRangeMin);
                    ov.SizeRange[0] = MinSizeRangeMin;
                }
            }
        }

        private static bool IsServerConfigMissing(ICoreAPI api)
        {
            try
            {
                string path = Path.Combine(api.GetOrCreateDataPath("ModConfig"), ServerConfigFile);
                return !File.Exists(path);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Could not determine whether the server config file existed yet: {0}", e.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // Client side
        // ---------------------------------------------------------------------

        public override void StartClientSide(ICoreClientAPI capi)
        {
            _capi = capi;

            // Note: the client cache was already loaded in Start() and applied in
            // AssetsLoaded() this session. Here we only register the channel to receive the
            // server's authoritative config and detect whether it changed since we applied.
            capi.Network
                .RegisterChannel(ChannelName)
                .RegisterMessageType<ConfigSyncPacket>()
                .SetMessageHandler<ConfigSyncPacket>(OnServerConfigSync);
        }

        private void OnServerConfigSync(ConfigSyncPacket packet)
        {
            if (_capi == null) return;

            // A server can send whatever it likes here, so bound the payload before parsing it.
            // Legitimate configs are a few KB — they hold codes, not assets.
            if (packet.ConfigJson.Length > MaxConfigJsonChars)
            {
                _capi.Logger.Warning("[myracemyrules] Ignoring server config: {0} characters exceeds the {1} limit.",
                    packet.ConfigJson.Length, MaxConfigJsonChars);
                return;
            }
            // Did the server's config change relative to what we APPLIED at load this session?
            if (packet.ConfigHash == _appliedHash)
            {
                // Already running the server's values. Refresh the cache silently in case the
                // JSON representation changed without changing effective values.
                WriteClientCache(_capi, packet.ConfigJson);
                return;
            }

            // Values differ. Persist the new authoritative config to the cache so the NEXT
            // load applies everything (EyeHeight/CollisionBox, raw skin-part merges) via the
            // asset path.
            WriteClientCache(_capi, packet.ConfigJson);

            // FIRST-JOIN FIX: apply the character-creation-relevant values (SizeRange,
            // Enabled, AvailableClasses, ExtraTraits, skin part variants) to PlayerModelLib's
            // live model data NOW, before the player opens character creation.
            MyRaceMyRulesConfig? serverConfig = null;
            try { serverConfig = JsonConvert.DeserializeObject<MyRaceMyRulesConfig>(packet.ConfigJson); }
            catch (Exception e) { _capi.Logger.Warning("[myracemyrules] Could not parse synced config: {0}", e.Message); }

            bool liveApplied = serverConfig != null && LiveModelUpdater.TryApply(_capi, serverConfig);

            if (liveApplied)
            {
                _appliedHash = packet.ConfigHash;
                _appliedWasEmpty = serverConfig!.Overrides.Count == 0;
                _capi.Logger.Notification("[myracemyrules] Live-applied server overrides for character creation (hash={0}).",
                    packet.ConfigHash);
                return;
            }

            if (_appliedWasEmpty)
            {
                // First join and the live apply could not fully cover it: cache is written, so
                // the next load converges. Stay silent per design (mod invisible to players).
                _capi.Logger.Notification("[myracemyrules] Received server config on empty cache; live apply incomplete, " +
                    "cached for next load (server={0}).", packet.ConfigHash);
                return;
            }

            // The client applied non-empty overrides this session, they are stale, and the
            // live apply could not fully correct them — tell the player to reconnect.
            _capi.ShowChatMessage(
                "[My Race My Rules] Race overrides on this server differ from your current session. " +
                "Reconnect (or reload the world) to apply them before creating/resetting your character.");
            _capi.Logger.Notification("[myracemyrules] Server config differs from applied; cached for next load. " +
                "applied={0} server={1}", _appliedHash, packet.ConfigHash);
        }

        /// <summary>
        /// Single-player client: read the authoritative local config without writing it back
        /// (the server side of the same session owns that file).
        /// </summary>
        private void LoadLocalConfigReadOnly(ICoreAPI api)
        {
            try
            {
                Config = api.LoadModConfig<MyRaceMyRulesConfig>(ServerConfigFile) ?? new MyRaceMyRulesConfig();
                api.Logger.Notification("[myracemyrules] Single player: using the local config directly.");
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Failed to read local config on client, using empty: {0}", e);
                Config = new MyRaceMyRulesConfig();
            }
        }

        /// <summary>
        /// Multiplayer client: load the cache belonging to the server being joined. The cache is
        /// keyed per server so one server's settings can never be applied to another server or
        /// to a single-player world. If the server can't be identified yet, nothing is applied —
        /// the sync that arrives on join still covers everything character creation needs.
        /// </summary>
        private void LoadClientCache(ICoreAPI api)
        {
            Config = new MyRaceMyRulesConfig();

            string? cacheFile = ClientCacheFileFor(api);
            if (cacheFile == null)
            {
                api.Logger.Notification("[myracemyrules] No server identity available at load; " +
                    "skipping cache (the join sync will still apply character-creation settings).");
                return;
            }

            try
            {
                Config = api.LoadModConfig<MyRaceMyRulesConfig>(cacheFile) ?? new MyRaceMyRulesConfig();
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Failed to load client cache, using empty: {0}", e);
                Config = new MyRaceMyRulesConfig();
            }
            // Do NOT write it back here; the client cache is written only when the server syncs.
        }

        private static void WriteClientCache(ICoreAPI api, string configJson)
        {
            string? cacheFile = ClientCacheFileFor(api);
            if (cacheFile == null)
            {
                api.Logger.Warning("[myracemyrules] Cannot cache server config: server identity unavailable.");
                return;
            }

            try
            {
                var cfg = JsonConvert.DeserializeObject<MyRaceMyRulesConfig>(configJson) ?? new MyRaceMyRulesConfig();
                api.StoreModConfig(cfg, cacheFile);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Failed to write client cache: {0}", e);
            }
        }

        /// <summary>
        /// Cache filename for the server this client is talking to, or null if that server can't
        /// be identified yet.
        ///
        /// The identifier comes from the server, so it is hashed rather than used directly: an
        /// untrusted string must never reach a file path. The hash also gives a fixed, safe
        /// charset and a predictable length.
        /// </summary>
        private static string? ClientCacheFileFor(ICoreAPI api)
        {
            string? savegameId = null;
            try { savegameId = api.World?.SavegameIdentifier; }
            catch (Exception) { /* not available this early — treated as unknown */ }

            if (string.IsNullOrEmpty(savegameId)) return null;
            return $"{Domain}-servercache-{ShortHash(savegameId!)}.json";
        }

        /// <summary>Short, filename-safe digest of an untrusted string.</summary>
        private static string ShortHash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash, 0, 8); // 16 hex chars, plenty to separate servers
        }

        // ---------------------------------------------------------------------
        // Override application (both sides, at AssetsLoaded)
        // ---------------------------------------------------------------------

        private void ApplyOverrides(ICoreAPI api)
        {
            if (Config.Overrides.Count == 0)
            {
                api.Logger.Notification("[myracemyrules] No overrides configured.");
                return;
            }

            var byCode = new Dictionary<string, DetectedRace>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in DetectedRaces) byCode[r.FullCode] = r;

            int applied = 0;
            foreach ((string fullCode, RaceOverrideEntry ov) in Config.Overrides)
            {
                if (!byCode.TryGetValue(fullCode, out var race))
                {
                    api.Logger.Warning("[myracemyrules] Override targets '{0}' but no such race was detected; skipping.", fullCode);
                    continue;
                }

                bool ok = race.IsSeraph
                    ? ApplySeraphOverride(api, race, ov)
                    : ApplyCustomModelOverride(api, race, ov);

                if (ok)
                {
                    applied++;
                    api.Logger.Notification("[myracemyrules] Applied override to '{0}'.", fullCode);
                }
            }

            api.Logger.Notification("[myracemyrules] Applied {0}/{1} override(s) (side={2}).",
                applied, Config.Overrides.Count, api.Side);
        }

        /// <summary>Custom model: everything lives in one customplayermodels asset.</summary>
        private bool ApplyCustomModelOverride(ICoreAPI api, DetectedRace race, RaceOverrideEntry ov)
        {
            JObject? root = LoadAssetJson(api, race.AssetPath);
            if (root == null) return false;

            if (root[race.ModelCode] is not JObject model)
            {
                api.Logger.Warning("[myracemyrules] Model key '{0}' vanished from '{1}'; skipping.", race.ModelCode, race.AssetPath);
                return false;
            }

            var sizeRange = ClampMinSizeRange(api, ov.SizeRange, race.FullCode, "SizeRange");
            if (IsPair(api, sizeRange, "SizeRange", race.FullCode))
                model["SizeRange"] = new JArray(sizeRange![0], sizeRange[1]);
            if (ov.EyeHeight.HasValue) model["EyeHeight"] = ov.EyeHeight.Value;
            if (ov.MinEyeHeight.HasValue || ov.MaxEyeHeight.HasValue)
            {
                if (ov.MinEyeHeight.HasValue) model["MinEyeHeight"] = ov.MinEyeHeight.Value;
                if (ov.MaxEyeHeight.HasValue) model["MaxEyeHeight"] = ov.MaxEyeHeight.Value;
            }

            if (IsPair(api, ov.CollisionBox, "CollisionBox", race.FullCode))
                model["CollisionBox"] = new JArray(ov.CollisionBox![0], ov.CollisionBox[1]);
            if (ov.MinCollisionBox != null || ov.MaxCollisionBox != null)
            {
                if (IsPair(api, ov.MinCollisionBox, "MinCollisionBox", race.FullCode))
                    model["MinCollisionBox"] = new JArray(ov.MinCollisionBox![0], ov.MinCollisionBox[1]);
                if (IsPair(api, ov.MaxCollisionBox, "MaxCollisionBox", race.FullCode))
                    model["MaxCollisionBox"] = new JArray(ov.MaxCollisionBox![0], ov.MaxCollisionBox[1]);
            }
            if (ov.Enabled.HasValue) model["Enabled"] = ov.Enabled.Value;
            if (ov.AvailableClasses != null) model["AvailableClasses"] = new JArray(ov.AvailableClasses);
            if (ov.ExtraTraits != null) model["ExtraTraits"] = new JArray(ov.ExtraTraits);

            if ((ov.IncludeAllDefaultVariants || ov.SkinnableParts.Count > 0) &&
                RaceDetector.GetPropCI(model, "SkinnableParts") is JArray parts)
                ApplySkinnablePartOverrides(api, parts, ov, race.FullCode);

            return StoreAssetJson(api, race.AssetPath, root);
        }

        /// <summary>
        /// Seraph: model settings go to PlayerModelLib's default-model-config asset; skin
        /// parts + EyeHeight/CollisionBox go to the vanilla player entity asset.
        /// </summary>
        private bool ApplySeraphOverride(ICoreAPI api, DetectedRace race, RaceOverrideEntry ov)
        {
            bool ok = true;

            // 1) Model settings (SizeRange, classes, traits, Enabled).
            if (ov.SizeRange != null || ov.MinEyeHeight.HasValue || ov.MaxEyeHeight.HasValue ||
                ov.MinCollisionBox != null || ov.MaxCollisionBox != null || ov.Enabled.HasValue ||
                ov.AvailableClasses != null || ov.ExtraTraits != null)
            {
                JObject? cfgRoot = LoadAssetJson(api, RaceDetector.SeraphModelConfigPath);
                if (cfgRoot?[RaceDetector.SeraphModelConfigKey] is JObject cfg)
                {
                    var sizeRange = ClampMinSizeRange(api, ov.SizeRange, race.FullCode, "SizeRange");
                    if (IsPair(api, sizeRange, "SizeRange", race.FullCode))
                        cfg["SizeRange"] = new JArray(sizeRange![0], sizeRange[1]);
                    if (ov.EyeHeight.HasValue) cfg["EyeHeight"] = ov.EyeHeight.Value;
                    if (ov.MinEyeHeight.HasValue) cfg["MinEyeHeight"] = ov.MinEyeHeight.Value;
                    if (ov.MaxEyeHeight.HasValue) cfg["MaxEyeHeight"] = ov.MaxEyeHeight.Value;
                    if (IsPair(api, ov.CollisionBox, "CollisionBox", race.FullCode))
                        cfg["CollisionBox"] = new JArray(ov.CollisionBox![0], ov.CollisionBox[1]);
                    if (IsPair(api, ov.MinCollisionBox, "MinCollisionBox", race.FullCode))
                        cfg["MinCollisionBox"] = new JArray(ov.MinCollisionBox![0], ov.MinCollisionBox[1]);
                    if (IsPair(api, ov.MaxCollisionBox, "MaxCollisionBox", race.FullCode))
                        cfg["MaxCollisionBox"] = new JArray(ov.MaxCollisionBox![0], ov.MaxCollisionBox[1]);
                    if (ov.Enabled.HasValue) cfg["Enabled"] = ov.Enabled.Value;
                    if (ov.AvailableClasses != null) cfg["AvailableClasses"] = new JArray(ov.AvailableClasses);
                    if (ov.ExtraTraits != null) cfg["ExtraTraits"] = new JArray(ov.ExtraTraits);
                    ok &= StoreAssetJson(api, RaceDetector.SeraphModelConfigPath, cfgRoot);
                }
                else
                {
                    api.Logger.Warning("[myracemyrules] Seraph default-model-config not found or malformed; model settings skipped.");
                    ok = false;
                }
            }

            // 2) Entity-level values + skin parts (hairstyles, facial hair, colors).
            bool wantsSkinParts = ov.IncludeAllDefaultVariants || ov.SkinnableParts.Count > 0;
            if (wantsSkinParts)
            {
                JObject? entity = LoadAssetJson(api, RaceDetector.PlayerEntityPath);
                if (entity == null) return false;

                if (wantsSkinParts)
                {
                    if (RaceDetector.GetPropCI(entity, "attributes") is JObject attributes &&
                        RaceDetector.GetPropCI(attributes, "skinnableParts") is JArray parts)
                    {
                        ApplySkinnablePartOverrides(api, parts, ov, race.FullCode);
                    }
                    else
                    {
                        api.Logger.Warning("[myracemyrules] Player entity skinnableParts not found; seraph skin parts skipped.");
                        ok = false;
                    }
                }

                ok &= StoreAssetJson(api, RaceDetector.PlayerEntityPath, entity);
            }

            return ok;
        }

        /// <summary>
        /// Apply skinnable-part overrides to a skinnableParts JSON array (shared by custom
        /// models and the seraph entity).
        ///
        /// Order: the race-level IncludeAllDefaultVariants flag runs first, then per-part
        /// entries refine it — so "restore everything, then restrict a few" works.
        /// </summary>
        private void ApplySkinnablePartOverrides(ICoreAPI api, JArray parts,
            RaceOverrideEntry ov, string raceForLog)
        {
            // Race-level: give every part this race defines the complete default variant list.
            if (ov.IncludeAllDefaultVariants)
            {
                if (_defaultSkinnableParts == null)
                {
                    api.Logger.Warning("[myracemyrules] ({0}) IncludeAllDefaultVariants requested but the default " +
                        "option list could not be read; skipping.", raceForLog);
                }
                else
                {
                    foreach (JObject defaultPart in _defaultSkinnableParts.OfType<JObject>())
                    {
                        string? defaultCode = (RaceDetector.GetPropCI(defaultPart, "code") as JValue)?.Value?.ToString();
                        if (string.IsNullOrEmpty(defaultCode)) continue;
                        MergeDefaultVariants(api, parts, defaultCode!, raceForLog);
                    }
                }
            }

            foreach ((string partCode, SkinnablePartOverride pov) in ov.SkinnableParts)
            {
                // Per-part: pull in the complete default variant list for this part first, so
                // any filtering below applies to the merged set.
                if (pov.IncludeDefaultVariants)
                {
                    if (_defaultSkinnableParts == null)
                        api.Logger.Warning("[myracemyrules] ({0}/{1}) IncludeDefaultVariants requested but the " +
                            "default option list could not be read; skipping.", raceForLog, partCode);
                    else
                        MergeDefaultVariants(api, parts, partCode, raceForLog);
                }

                JObject? part = FindPart(parts, partCode);
                if (part == null)
                {
                    api.Logger.Warning("[myracemyrules] ({0}) Skinnable part '{1}' not found; skipping.", raceForLog, partCode);
                    continue;
                }

                if (pov.Enabled.HasValue)
                    part["enabled"] = pov.Enabled.Value;

                if ((pov.AllowedVariants != null || pov.RemoveVariants != null) &&
                    RaceDetector.GetPropCI(part, "variants") is JArray variants)
                {
                    FilterVariants(api, variants, pov, raceForLog, partCode);
                }
            }
        }

        private static JObject? FindPart(JArray parts, string partCode) =>
            parts.OfType<JObject>().FirstOrDefault(p =>
                string.Equals((RaceDetector.GetPropCI(p, "code") as JValue)?.Value?.ToString(),
                    partCode, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Merge the default race's (seraph's) variants for one part into this race's part, so
        /// "all hairstyles"/"all colors" needs no hand-copied list. Variants the race already
        /// has are left alone (its own extras are preserved).
        ///
        /// A part the race does not define is never added: races that cannot wear hair remove
        /// the part entirely, and that intent is respected.
        /// </summary>
        private void MergeDefaultVariants(ICoreAPI api, JArray parts, string partCode, string raceForLog)
        {
            JObject? targetPart = FindPart(parts, partCode);
            if (targetPart == null)
            {
                api.Logger.Notification("[myracemyrules] ({0}/{1}) Part is not present on this race; leaving it alone.",
                    raceForLog, partCode);
                return;
            }

            JObject? defaultPart = FindPart(_defaultSkinnableParts!, partCode);
            if (defaultPart == null)
            {
                api.Logger.Notification("[myracemyrules] ({0}/{1}) Default race has no such part; nothing to add.",
                    raceForLog, partCode);
                return;
            }

            targetPart["enabled"] = true;

            if (RaceDetector.GetPropCI(defaultPart, "variants") is not JArray defaultVariants)
            {
                api.Logger.Notification("[myracemyrules] ({0}/{1}) Default part has no variants to merge.", raceForLog, partCode);
                return;
            }

            // Ensure the target has a variants array to merge into.
            if (RaceDetector.GetPropCI(targetPart, "variants") is not JArray targetVariants)
            {
                targetVariants = [];
                targetPart["variants"] = targetVariants;
            }

            var existing = new HashSet<string>(
                targetVariants.OfType<JObject>()
                    .Select(v => (RaceDetector.GetPropCI(v, "code") as JValue)?.Value?.ToString() ?? "")
                    .Where(c => c.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (JObject dv in defaultVariants.OfType<JObject>())
            {
                string? vcode = (RaceDetector.GetPropCI(dv, "code") as JValue)?.Value?.ToString();
                if (string.IsNullOrEmpty(vcode) || existing.Contains(vcode!)) continue;
                targetVariants.Add(dv.DeepClone());
                added++;
            }

            api.Logger.Notification("[myracemyrules] ({0}/{1}) Merged {2} default variant(s); part now has {3}.",
                raceForLog, partCode, added, targetVariants.Count);
        }

        /// <summary>
        /// Keep only variants passing the whitelist/blacklist. If filtering would remove every
        /// variant (likely an admin typo), the original list is kept and a warning logged
        /// rather than breaking the part.
        /// </summary>
        private static void FilterVariants(ICoreAPI api, JArray variants, SkinnablePartOverride pov,
            string raceForLog, string partCode)
        {
            var keep = new List<JToken>();
            foreach (var v in variants)
            {
                string? vcode = (v is JObject vo ? RaceDetector.GetPropCI(vo, "code") as JValue : null)?.Value?.ToString();
                if (vcode == null) { keep.Add(v); continue; }

                bool allowed = pov.AllowedVariants == null ||
                               pov.AllowedVariants.Contains(vcode, StringComparer.OrdinalIgnoreCase);
                bool removed = pov.RemoveVariants != null &&
                               pov.RemoveVariants.Contains(vcode, StringComparer.OrdinalIgnoreCase);
                if (allowed && !removed) keep.Add(v);
            }

            if (keep.Count == 0)
            {
                api.Logger.Warning("[myracemyrules] ({0}/{1}) Variant filtering removed ALL variants; " +
                    "keeping original list to avoid breaking the part.", raceForLog, partCode);
                return;
            }

            variants.Clear();
            foreach (var v in keep) variants.Add(v);
        }

        // ---------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// True if a [a, b] config field is present and well-formed. A field with the wrong
        /// number of entries is a config mistake, so say so rather than half-applying it.
        /// </summary>
        private static float[]? ClampMinSizeRange(ICoreAPI api, float[]? value, string raceForLog, string fieldName)
        {
            if (value == null || value.Length != 2) return value;
            if (value[0] < MinSizeRangeMin)
            {
                api.Logger.Error("[myracemyrules] ({0}) '{1}' minimum was {2}; clamping to {3}.",
                    raceForLog, fieldName, value[0], MinSizeRangeMin);
                return [MinSizeRangeMin, value[1]];
            }
            return value;
        }

        private static bool IsPair(ICoreAPI api, float[]? value, string fieldName, string raceForLog)
        {
            if (value == null) return false;
            if (value.Length == 2) return true;
            api.Logger.Warning("[myracemyrules] ({0}) '{1}' needs exactly 2 numbers, got {2}; ignoring it.",
                raceForLog, fieldName, value.Length);
            return false;
        }

        private static JObject? LoadAssetJson(ICoreAPI api, string path)
        {
            IAsset? asset = api.Assets.TryGet(new AssetLocation(path));
            if (asset == null && path == RaceDetector.PlayerEntityPath)
            {
                JObject? fileJson = RaceDetector.ResolvePlayerEntityJson(api);
                if (fileJson != null) return fileJson;
            }

            if (asset == null)
            {
                api.Logger.Warning("[myracemyrules] Asset '{0}' not found.", path);
                return null;
            }

            try { return JObject.Parse(asset.ToText()); }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Could not parse '{0}': {1}", path, e.Message);
                return null;
            }
        }

        private static bool StoreAssetJson(ICoreAPI api, string path, JObject root)
        {
            IAsset? asset = api.Assets.TryGet(new AssetLocation(path));
            if (asset == null) return false;
            asset.Data = Encoding.UTF8.GetBytes(root.ToString());
            return true;
        }

        /// <summary>
        /// Content hash of a config, used to detect "server values changed since I applied
        /// them". Server and client hash the same config object the same way, so identical
        /// configs produce identical hashes; that is all the change-detection needs.
        /// </summary>
        private static string HashConfig(MyRaceMyRulesConfig cfg)
        {
            string json = JsonConvert.SerializeObject(cfg, Formatting.None);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash);
        }

        private static string FormatArr(float[]? a) => a == null ? "-" : "[" + string.Join(",", a) + "]";
    }
}
