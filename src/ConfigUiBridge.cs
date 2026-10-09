using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace MyRaceMyRules
{
    /// <summary>
    /// Reflection bridge to an in-game settings UI, rendered by whichever of ConfigKit or
    /// configlib is installed — with NO hard dependency and NO compile-time reference to either.
    ///
    /// Both libraries expose, on their ModSystem, the identical method
    ///   void RegisterCustomManagedConfig(string domain, object config, string path,
    ///       Action onSyncedFromServer, Action&lt;string&gt; onSettingChanged, Action onConfigSaved)
    /// (configlib natively; ConfigKit keeps it as an exact-signature alias of RegisterManagedConfig
    /// specifically for reflection users). We invoke it by name via reflection, so the mod loads
    /// normally when neither library is present and simply has no settings screen.
    ///
    /// Design 1 (view-only): the library renders <see cref="ConfigUiModel"/> and reports edits;
    /// this bridge maps those edits onto the authoritative <see cref="MyRaceMyRulesConfig"/> using
    /// the mod's existing server-side setters, and refreshes the model from the real config on
    /// load/sync. The real config file + the mod's own server-authoritative sync + migration
    /// remain the single source of truth. Server-side privilege enforcement is handled by the
    /// library (it checks controlserver before applying a client edit), the same gate the mod's
    /// chat commands use.
    /// </summary>
    public sealed class ConfigUiBridge
    {
        private const string Domain = "myracemyrules";
        private const string ConfigKitType = "ConfigKit.ConfigKitModSystem";
        private const string ConfigLibType = "ConfigLib.ConfigLibModSystem";
        private const string RegisterMethod = "RegisterCustomManagedConfig";

        private readonly ICoreAPI _api;
        private readonly MyRaceMyRulesModSystem _mod;
        private readonly ConfigUiModel _model = new();
        private bool _applyingFromReal; // guard against feedback loops while we repopulate the model

        public ConfigUiBridge(ICoreAPI api, MyRaceMyRulesModSystem mod)
        {
            _api = api;
            _mod = mod;
        }

        /// <summary>
        /// Register with whichever library is enabled. Call from StartPre on both sides. Kept in
        /// its own [NoInlining] method so the library type is only resolved when a library is
        /// actually present — a client/server without either loads the mod normally.
        /// </summary>
        public void TryRegister()
        {
            try
            {
                if (_api.ModLoader.IsModEnabled("configkit"))
                {
                    if (RegisterWith(ConfigKitType))
                        _api.Logger.Notification("[myracemyrules] Settings screen available via ConfigKit.");
                }
                else if (_api.ModLoader.IsModEnabled("configlib"))
                {
                    if (RegisterWith(ConfigLibType))
                        _api.Logger.Notification("[myracemyrules] Settings screen available via Config lib.");
                }
                // Neither installed: no settings screen; the JSON file + /mrmr commands still work.
            }
            catch (Exception e)
            {
                string msg = (e as TargetInvocationException)?.InnerException?.Message ?? e.Message;
                _api.Logger.Warning("[myracemyrules] Could not register the settings screen ({0}); the config file and /mrmr commands still work.", msg);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool RegisterWith(string modSystemFullName)
        {
            ModSystem? system = _api.ModLoader.GetModSystem(modSystemFullName);
            if (system == null) return false;

            MethodInfo? register = system.GetType().GetMethod(RegisterMethod, new[]
            {
                typeof(string), typeof(object), typeof(string),
                typeof(Action), typeof(Action<string>), typeof(Action)
            });
            if (register == null)
            {
                _api.Logger.Warning("[myracemyrules] '{0}' found but '{1}' is unavailable; no settings screen.", modSystemFullName, RegisterMethod);
                return false;
            }

            // Populate the model from the authoritative config before the library reads it.
            // TODO(on-device): confirm the library reads _model lazily (when the GUI opens) vs.
            // snapshotting it at registration. We register in StartPre (model empty) and refill
            // the SAME dict instance in AssetsLoaded; if a library snapshots at register time it
            // would miss races added later. Untestable without configkit/configlib + a build.
            SyncModelFromReal();

            Action onSynced = SyncModelFromReal;                 // library pulled server values
            Action<string> onChanged = OnSettingChanged;         // a setting was edited
            Action onSaved = () => { };

            register.Invoke(system, new object?[] { Domain, _model, Domain + "-ui.json", onSynced, onChanged, onSaved });
            return true;
        }

        // ------------------------------------------------------------------
        // real config -> model (refresh the screen from the source of truth)
        // ------------------------------------------------------------------

        /// <summary>Rebuild the UI model from the authoritative config + detected races.</summary>
        public void SyncModelFromReal()
        {
            _applyingFromReal = true;
            try
            {
                _model.Races.Clear();
                foreach (DetectedRace race in _mod.DetectedRaces)
                {
                    if (race?.FullCode == null) continue;
                    try
                    {
                        _mod.Config.Overrides.TryGetValue(race.FullCode, out var ov);
                        _model.Races[race.FullCode] = BuildRaceEntry(race, ov);
                    }
                    catch (Exception e)
                    {
                        _api.Logger.Warning("[myracemyrules] Could not build settings row for '{0}': {1}", race.FullCode, e.Message);
                    }
                }
            }
            finally { _applyingFromReal = false; }
        }

        private static RaceUiEntry BuildRaceEntry(DetectedRace race, RaceOverrideEntry? ov)
        {
            var e = new RaceUiEntry
            {
                Enabled = ov?.Enabled,
                Name = ov?.Name ?? "",
                Description = ov?.Description ?? "",
                SizeRange = Pair(ov?.SizeRange),
                EyeHeight = ov?.EyeHeight?.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
                Collision = Pair(ov?.CollisionBox),
                AvailableClasses = new List<string>(ov?.AvailableClasses ?? race.AvailableClasses ?? new List<string>()),
                ExtraTraits = new List<string>(ov?.ExtraTraits ?? race.ExtraTraits ?? new List<string>()),
                IncludeAllDefaultVariants = ov?.IncludeAllDefaultVariants ?? false,
            };
            if (ov?.SkinnableParts != null)
            {
                foreach ((string part, SkinnablePartOverride pov) in ov.SkinnableParts)
                {
                    if (part == null || pov == null) continue; // tolerate "part": null in the JSON
                    e.Sections[part] = new PartUiEntry
                    {
                        Enabled = pov.Enabled,
                        AllowedVariants = new List<string>(pov.AllowedVariants ?? new List<string>()),
                        RemoveVariants = new List<string>(pov.RemoveVariants ?? new List<string>()),
                    };
                }
            }
            return e;
        }

        private static string Pair(float[]? v) =>
            v is { Length: 2 }
                ? $"{v[0].ToString("0.###", CultureInfo.InvariantCulture)} {v[1].ToString("0.###", CultureInfo.InvariantCulture)}"
                : "";

        // ------------------------------------------------------------------
        // model -> real config (route an edit through the existing setters)
        // ------------------------------------------------------------------

        /// <summary>
        /// A setting changed in the model. The code is the setting's path (format differs
        /// between libraries and is treated as a hint, not a contract). To avoid stale blanks in
        /// the model clobbering values set elsewhere, we do NOT blindly push the whole race:
        /// we diff the model race against the authoritative config and apply ONLY the fields that
        /// actually differ. That is correct whatever the path string looks like. Runs server-side
        /// (the only place authoritative values land); then re-syncs the model so clamps and
        /// canonicalizations flow back into the screen.
        /// </summary>
        private void OnSettingChanged(string code)
        {
            if (_applyingFromReal) return;               // our own repopulation, not a user edit
            // TODO(on-device): verify which side the library fires onSettingChanged on for a
            // server-owned setting. We apply only server-side (authority). If a given library
            // version fires client-side only, edits won't reach the server and this needs a
            // client->server hop. Untestable without configkit/configlib installed + a build.
            if (_api is not ICoreServerAPI sapi) return; // authority is server-side only

            string? raceCode = ExtractRaceCode(code) ?? SingleRaceCodeOrNull();
            try
            {
                if (raceCode != null && _model.Races.TryGetValue(raceCode, out var entry))
                {
                    _mod.ApplyUiRaceEntryDiff(sapi, raceCode, entry);
                }
                else
                {
                    // Path didn't resolve to a known race (unexpected library path format): fall
                    // back to diffing every race so the edit still lands, and log for diagnosis.
                    _api.Logger.Notification("[myracemyrules] UI edit path '{0}' did not map to a single race; applying by full diff.", code);
                    foreach (var kv in _model.Races) _mod.ApplyUiRaceEntryDiff(sapi, kv.Key, kv.Value);
                }
            }
            catch (Exception e)
            {
                _api.Logger.Warning("[myracemyrules] Failed to apply UI edit ('{0}'): {1}", code, e.Message);
            }

            // Reflect any server-side clamp/canonicalization/validation back into the screen.
            SyncModelFromReal();
        }

        /// <summary>Pull the race code out of a "Races/&lt;code&gt;/..." style path. Codes contain ':', never '.'.</summary>
        private string? ExtractRaceCode(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split(new[] { '/', '.' }, StringSplitOptions.RemoveEmptyEntries);
            int i = Array.FindIndex(parts, p => string.Equals(p, "Races", StringComparison.OrdinalIgnoreCase));
            string? candidate = (i >= 0 && i + 1 < parts.Length) ? parts[i + 1]
                              : (parts.Length >= 2 ? parts[^2] : null); // last-resort: segment before the field
            if (candidate == null) return null;
            // Match case-insensitively against known races, return the canonical key.
            return _model.Races.Keys.FirstOrDefault(k => string.Equals(k, candidate, StringComparison.OrdinalIgnoreCase));
        }

        private string? SingleRaceCodeOrNull() => _model.Races.Count == 1 ? _model.Races.Keys.First() : null;
    }
}
