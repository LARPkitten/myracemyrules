using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using PlayerModelLib;

namespace MyRaceMyRules
{
    /// <summary>
    /// Live (in-session) application of overrides to PlayerModelLib's in-memory model data.
    ///
    /// Purpose: on a player's FIRST join to a server, their client applied nothing at load
    /// (empty cache), so the character-creation dialog would show the races' original
    /// SizeRange / classes / visibility. The server's sync packet arrives right after join —
    /// before the dialog opens — so we patch the relevant values into
    /// CustomModelsSystem.CustomModels here, making server settings apply to character
    /// creation on the very first session.
    ///
    /// Scope: ONLY plain data fields that drive the character-creation dialog —
    /// SizeRange, Enabled, AvailableClasses, ExtraTraits. Deliberately NOT shapes/textures
    /// (PlayerModelLib's crash-prone area) and NOT EyeHeight/CollisionBox (gameplay-behavior
    /// values that are safer applied at next load via the normal asset path; they don't
    /// affect the creation dialog).
    ///
    /// Why reflection: the CustomModels dictionary and CustomModelData fields (Enabled,
    /// AvailableClasses, ExtraTraits, EyeHeight, CollisionBox) are visible in PlayerModelLib's
    /// source, but exact member names/types for all of them (notably SizeRange) could not be
    /// verified offline. Reflection lets a mismatch degrade to "applies at next load" (2b
    /// steady-state still guarantees correctness) instead of a compile error or crash.
    /// Each failure is logged loudly so it is visible in testing. If you verify the members
    /// against playermodellib.dll, replacing this with typed access is a welcome improvement.
    /// </summary>
    public static class LiveModelUpdater
    {
        /// <summary>
        /// Try to apply the character-creation-relevant overrides to PlayerModelLib's live
        /// model data. Returns true only if EVERY targeted race + field applied cleanly
        /// (callers treat partial success as "reconnect still recommended").
        /// </summary>
        public static bool TryApply(ICoreAPI api, MyRaceMyRulesConfig config)
        {
            if (config.Overrides.Count == 0) return true;

            object? modelsSystem;
            IDictionary? customModels;
            try
            {
                modelsSystem = api.ModLoader.GetModSystem<PlayerModelLib.CustomModelsSystem>();
                customModels = GetMember(modelsSystem, "CustomModels") as IDictionary;
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Live apply unavailable (CustomModelsSystem not accessible): {0}", e.Message);
                return false;
            }

            if (modelsSystem == null || customModels == null)
            {
                api.Logger.Warning("[myracemyrules] Live apply unavailable (CustomModels dictionary not found).");
                return false;
            }

            bool allApplied = true;

            foreach ((string fullCode, RaceOverrideEntry ov) in config.Overrides)
            {
                bool anyDialogRelevant = ov.SizeRange != null || ov.Enabled.HasValue ||
                    ov.AvailableClasses != null || ov.ExtraTraits != null ||
                    ov.EnableAllSkinnableParts || ov.IncludeAllDefaultVariants || ov.SkinnableParts.Count > 0;
                if (!anyDialogRelevant) continue;

                if (!customModels.Contains(fullCode))
                {
                    api.Logger.Warning("[myracemyrules] Live apply: model '{0}' not in CustomModels; skipping.", fullCode);
                    allApplied = false;
                    continue;
                }

                object? modelData = customModels[fullCode];
                if (modelData == null) { allApplied = false; continue; }

                if (ov.Enabled.HasValue)
                    allApplied &= TrySet(api, modelData, "Enabled", ov.Enabled.Value, fullCode);

                if (ov.SizeRange is { Length: 2 })
                    allApplied &= TrySetSizeRange(api, modelData, ov.SizeRange[0], ov.SizeRange[1], fullCode);

                if (ov.AvailableClasses != null)
                    allApplied &= TrySetStringCollection(api, modelData, "AvailableClasses", ov.AvailableClasses, fullCode);

                if (ov.ExtraTraits != null)
                    allApplied &= TrySetStringCollection(api, modelData, "ExtraTraits", ov.ExtraTraits, fullCode);

                if (ov.EnableAllSkinnableParts || ov.IncludeAllDefaultVariants || ov.SkinnableParts.Count > 0)
                    allApplied &= TryApplySkinnableParts(api, customModels, modelData, ov, fullCode);
            }

            return allApplied;
        }

        /// <summary>
        /// Live-apply skinnable-part overrides to a CustomModelData: variant additions (from the
        /// default race), variant filtering, and part disabling.
        ///
        /// Adding variants works live because the default race (seraph) is ALWAYS loaded, so its
        /// SkinnablePart variants are already fully processed in memory (shapes resolved, swatch
        /// colors computed). We copy those runtime objects rather than building new ones from
        /// JSON, so no asset loading or texture-atlas work is needed.
        ///
        /// Remaining load-only cases (reported as "not fully applied" so they converge):
        ///   - Re-ENABLING a whole part: PlayerModelLib filters parts with enabled=false out at
        ///     load, so the object is not in memory to restore.
        ///   - Raw "Set" property merges: cannot be replayed onto runtime objects generically.
        ///
        /// Parts a race does not define are never added — a race that cannot wear hair removes
        /// the part, and that intent is respected.
        /// </summary>
        private static bool TryApplySkinnableParts(ICoreAPI api, IDictionary customModels,
            object modelData, RaceOverrideEntry ov, string codeForLog)
        {
            bool allApplied = true;

            IDictionary? skinParts = GetMember(modelData, "SkinParts") as IDictionary;
            if (skinParts == null)
            {
                api.Logger.Warning("[myracemyrules] Live apply: 'SkinParts' not found on model data ({0}).", codeForLog);
                return false;
            }

            // The default race's live parts are the canonical "all options" source.
            IDictionary? defaultParts = GetDefaultSkinParts(api, customModels);

            // Race-level: give every part this race defines the complete default variant list.
            if (ov.IncludeAllDefaultVariants)
            {
                if (defaultParts == null) allApplied = false;
                else
                {
                    foreach (object? key in skinParts.Keys.Cast<object>().ToList())
                    {
                        string partCode = key?.ToString() ?? "";
                        if (partCode.Length == 0) continue;
                        allApplied &= MergeDefaultVariantsLive(api, skinParts, defaultParts, partCode, codeForLog);
                    }
                }
            }

            // Race-level "enable all" cannot be applied live (disabled parts aren't in memory).
            if (ov.EnableAllSkinnableParts)
            {
                api.Logger.Notification("[myracemyrules] Live apply: ({0}) EnableAllSkinnableParts converges at next load.", codeForLog);
                allApplied = false;
            }

            foreach ((string partCode, SkinnablePartOverride pov) in ov.SkinnableParts)
            {
                // Raw merges can't be replayed onto runtime objects.
                if (pov.Set is { Count: > 0 }) allApplied = false;

                // A part the race does not define is not added (see summary).
                if (!skinParts.Contains(partCode))
                {
                    api.Logger.Notification("[myracemyrules] Live apply: ({0}) part '{1}' is not present on this race; " +
                        "leaving it alone.", codeForLog, partCode);
                    continue;
                }

                // Per-part: add the complete default variant list for this part.
                if (pov.IncludeDefaultVariants)
                {
                    if (defaultParts == null) allApplied = false;
                    else allApplied &= MergeDefaultVariantsLive(api, skinParts, defaultParts, partCode, codeForLog);
                }

                if (pov.EnableAll)
                {
                    // Variants are all present (nothing filtered live); the part is in memory so
                    // it was already enabled at load. Nothing further to do.
                    continue;
                }

                if (pov.Enabled == false)
                {
                    skinParts.Remove(partCode);
                    RemoveFromSkinPartsArray(api, modelData, partCode);
                    continue;
                }

                if (pov.Enabled == true)
                {
                    // Present in memory => already enabled. No-op.
                    continue;
                }

                if (pov.AllowedVariants == null && pov.RemoveVariants == null) continue;

                object? partObj = skinParts[partCode];
                if (partObj == null)
                {
                    api.Logger.Warning("[myracemyrules] Live apply: skinnable part '{0}' is null ({1}).", partCode, codeForLog);
                    allApplied = false;
                    continue;
                }

                try
                {
                    Array existing = GetMember(partObj, "Variants") as Array ?? Array.CreateInstance(typeof(object), 0);

                    Type elemType = existing.GetType().GetElementType() ?? typeof(object);
                    var filteredList = new List<object>();

                    foreach (object? v in existing)
                    {
                        if (v == null) continue;
                        string vcode = (GetMember(v, "Code") as string) ?? "";
                        bool allowed = pov.AllowedVariants == null || pov.AllowedVariants.Contains(vcode, StringComparer.OrdinalIgnoreCase);
                        bool removed = pov.RemoveVariants != null && pov.RemoveVariants.Contains(vcode, StringComparer.OrdinalIgnoreCase);
                        if (allowed && !removed) filteredList.Add(v);
                    }

                    if (filteredList.Count == 0)
                    {
                        api.Logger.Warning("[myracemyrules] Live apply: ({0}/{1}) filtering removed ALL variants; keeping originals.", codeForLog, partCode);
                        allApplied = false;
                        continue;
                    }

                    Array newArr = Array.CreateInstance(elemType, filteredList.Count);
                    for (int i = 0; i < filteredList.Count; i++) newArr.SetValue(filteredList[i], i);

                    SetMemberValue(partObj, "Variants", newArr);

                    var vb = GetMember(partObj, "VariantsByCode") as IDictionary;
                    if (vb != null)
                    {
                        var keepCodes = new HashSet<string>(filteredList.Select(v => (GetMember(v, "Code") as string) ?? ""), StringComparer.OrdinalIgnoreCase);
                        foreach (object key in vb.Keys.Cast<object>().ToList())
                        {
                            string ks = key?.ToString() ?? "";
                            if (!keepCodes.Contains(ks)) vb.Remove(key);
                        }
                    }
                }
                catch (Exception e)
                {
                    api.Logger.Warning("[myracemyrules] Live apply: variant filter failed for '{0}' ({1}): {2}", partCode, codeForLog, e.Message);
                    allApplied = false;
                }
            }

            return allApplied;
        }

        /// <summary>
        /// The default race's live SkinParts dictionary — the canonical "all options" source.
        /// Seraph is always loaded by PlayerModelLib, so its variants are fully processed.
        /// </summary>
        private static IDictionary? GetDefaultSkinParts(ICoreAPI api, IDictionary customModels)
        {
            const string defaultCode = "seraph";
            if (!customModels.Contains(defaultCode))
            {
                api.Logger.Warning("[myracemyrules] Live apply: default model '{0}' not loaded; cannot add default variants.", defaultCode);
                return null;
            }
            object? defaultModel = customModels[defaultCode];
            IDictionary? parts = defaultModel == null ? null : GetMember(defaultModel, "SkinParts") as IDictionary;
            if (parts == null)
                api.Logger.Warning("[myracemyrules] Live apply: could not read default model's SkinParts.");
            return parts;
        }

        /// <summary>
        /// Copy the default race's variants for one part into the target race's part, skipping
        /// variant codes it already has. Uses the already-loaded runtime SkinnablePartVariant
        /// objects, so shapes/colors are already resolved — no asset or atlas work.
        ///
        /// These are shared references, not clones: the same variant object ends up listed under
        /// both races. That is deliberate (it costs no extra memory), and safe because
        /// PlayerModelLib finishes processing variants during load, before this runs. If a future
        /// version starts mutating variants per-model after load, clone here instead.
        /// </summary>
        private static bool MergeDefaultVariantsLive(ICoreAPI api, IDictionary targetParts,
            IDictionary defaultParts, string partCode, string codeForLog)
        {
            if (!targetParts.Contains(partCode))
            {
                // Not defined on this race — respected, not added.
                return true;
            }
            if (!defaultParts.Contains(partCode))
            {
                api.Logger.Notification("[myracemyrules] Live apply: ({0}/{1}) default race has no such part; nothing to add.", codeForLog, partCode);
                return true;
            }

            object? targetObj = targetParts[partCode];
            object? sourceObj = defaultParts[partCode];
            if (targetObj == null || sourceObj == null)
            {
                api.Logger.Warning("[myracemyrules] Live apply: ({0}/{1}) part objects missing; cannot add variants.", codeForLog, partCode);
                return false;
            }

            try
            {
                Array existing = GetMember(targetObj, "Variants") as Array ?? Array.CreateInstance(typeof(object), 0);
                Array source = GetMember(sourceObj, "Variants") as Array ?? Array.CreateInstance(typeof(object), 0);

                Type elemType = existing.GetType().GetElementType() ?? source.GetType().GetElementType() ?? typeof(object);

                var have = new HashSet<string>(existing.Cast<object?>().Select(v => (GetMember(v, "Code") as string) ?? "").Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase);

                var toAdd = source.Cast<object?>()
                    .Where(v => (GetMember(v, "Code") as string) is string code && code.Length > 0 && !have.Contains(code))
                    .ToList();

                if (toAdd.Count == 0) return true;

                Array newArr = Array.CreateInstance(elemType, existing.Length + toAdd.Count);
                for (int i = 0; i < existing.Length; i++) newArr.SetValue(existing.GetValue(i), i);
                for (int i = 0; i < toAdd.Count; i++) newArr.SetValue(toAdd[i], existing.Length + i);

                SetMemberValue(targetObj, "Variants", newArr);

                var vb = GetMember(targetObj, "VariantsByCode") as IDictionary;
                if (vb != null)
                {
                    foreach (object? v in toAdd)
                    {
                        string code = (GetMember(v, "Code") as string) ?? "";
                        if (code.Length == 0) continue;
                        vb[code] = v!;
                    }
                }

                api.Logger.Notification("[myracemyrules] Live apply: ({0}/{1}) added {2} default variant(s); now {3}.", codeForLog, partCode, toAdd.Count, newArr.Length);
                return true;
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Live apply: ({0}/{1}) failed to add default variants: {2}", codeForLog, partCode, e.Message);
                return false;
            }
        }

        /// <summary>Remove a part from the SkinPartsArray member (kept in sync with the dictionary).</summary>
        private static void RemoveFromSkinPartsArray(ICoreAPI api, object modelData, string partCode)
        {
            try
            {
                if (GetMember(modelData, "SkinPartsArray") is not Array arr) return;

                var kept = new List<object>();
                foreach (object? item in arr)
                {
                    if (item != null)
                    {
                        string code = (GetMember(item, "Code") as string) ?? "";
                        if (string.Equals(code, partCode, StringComparison.OrdinalIgnoreCase)) continue;
                        kept.Add(item);
                    }
                }
                if (kept.Count == arr.Length) return;

                Type elemType = arr.GetType().GetElementType()!;
                Array newArr = Array.CreateInstance(elemType, kept.Count);
                for (int i = 0; i < kept.Count; i++) newArr.SetValue(kept[i], i);

                Type t = modelData.GetType();
                PropertyInfo? prop = t.GetProperty("SkinPartsArray", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) { prop.SetValue(modelData, newArr); return; }
                FieldInfo? field = t.GetField("SkinPartsArray", BindingFlags.Public | BindingFlags.Instance);
                field?.SetValue(modelData, newArr);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Live apply: could not update SkinPartsArray for '{0}': {1}", partCode, e.Message);
            }
        }

        // -------------------------------------------------------------------
        // reflection helpers — every failure logs and returns false, never throws
        // -------------------------------------------------------------------

        private static object? GetMember(object? target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            PropertyInfo? p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(target);
            FieldInfo? f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(target);
        }

        private static void SetMemberValue(object target, string name, object? value)
        {
            if (target == null) return;
            Type t = target.GetType();
            PropertyInfo? p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(target, value); return; }
            FieldInfo? f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(target, value);
        }

        private static bool TrySet(ICoreAPI api, object target, string name, object value, string codeForLog)
        {
            try
            {
                Type t = target.GetType();
                PropertyInfo? p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(target, Convert.ChangeType(value, p.PropertyType));
                    return true;
                }
                FieldInfo? f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null)
                {
                    f.SetValue(target, Convert.ChangeType(value, f.FieldType));
                    return true;
                }
                api.Logger.Warning("[myracemyrules] Live apply: member '{0}' not found on model data ({1}).", name, codeForLog);
                return false;
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Live apply: failed to set '{0}' on '{1}': {2}", name, codeForLog, e.Message);
                return false;
            }
        }

        /// <summary>
        /// SizeRange's exact runtime type is unverified (likely float[] or a vector type).
        /// Handle the plausible shapes; anything else logs and falls back to next-load apply.
        /// </summary>
        private static bool TrySetSizeRange(ICoreAPI api, object target, float min, float max, string codeForLog)
        {
            try
            {
                Type t = target.GetType();
                MemberInfo? member = (MemberInfo?)t.GetProperty("SizeRange", BindingFlags.Public | BindingFlags.Instance)
                                     ?? t.GetField("SizeRange", BindingFlags.Public | BindingFlags.Instance);
                if (member == null)
                {
                    api.Logger.Warning("[myracemyrules] Live apply: 'SizeRange' not found on model data ({0}).", codeForLog);
                    return false;
                }

                Type memberType = member is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)member).FieldType;

                object? newValue = null;
                if (memberType == typeof(float[]))
                {
                    newValue = new[] { min, max };
                }
                else
                {
                    // Vector-ish type with a (float, float) constructor (e.g. OpenTK Vector2).
                    ConstructorInfo? ctor = memberType.GetConstructor(new[] { typeof(float), typeof(float) });
                    if (ctor != null) newValue = ctor.Invoke(new object[] { min, max });
                }

                if (newValue == null)
                {
                    api.Logger.Warning("[myracemyrules] Live apply: unsupported SizeRange type '{0}' ({1}); will apply at next load instead.",
                        memberType.Name, codeForLog);
                    return false;
                }

                if (member is PropertyInfo prop && prop.CanWrite) prop.SetValue(target, newValue);
                else if (member is FieldInfo field) field.SetValue(target, newValue);
                else return false;

                return true;
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Live apply: failed to set SizeRange on '{0}': {1}", codeForLog, e.Message);
                return false;
            }
        }

        /// <summary>
        /// AvailableClasses / ExtraTraits are collections whose exact runtime type is
        /// unverified (List, HashSet, or array of string). Build the right one dynamically.
        /// </summary>
        private static bool TrySetStringCollection(ICoreAPI api, object target, string name, List<string> values, string codeForLog)
        {
            try
            {
                Type t = target.GetType();
                MemberInfo? member = (MemberInfo?)t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                                     ?? t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (member == null)
                {
                    api.Logger.Warning("[myracemyrules] Live apply: '{0}' not found on model data ({1}).", name, codeForLog);
                    return false;
                }

                Type memberType = member is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)member).FieldType;

                object? newValue = null;
                if (memberType == typeof(string[])) newValue = values.ToArray();
                else if (memberType.IsAssignableFrom(typeof(List<string>))) newValue = new List<string>(values);
                else if (memberType.IsAssignableFrom(typeof(HashSet<string>))) newValue = new HashSet<string>(values);

                if (newValue == null)
                {
                    api.Logger.Warning("[myracemyrules] Live apply: unsupported '{0}' type '{1}' ({2}); will apply at next load instead.",
                        name, memberType.Name, codeForLog);
                    return false;
                }

                if (member is PropertyInfo prop && prop.CanWrite) prop.SetValue(target, newValue);
                else if (member is FieldInfo field) field.SetValue(target, newValue);
                else return false;

                return true;
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Live apply: failed to set '{0}' on '{1}': {2}", name, codeForLog, e.Message);
                return false;
            }
        }
    }
}
