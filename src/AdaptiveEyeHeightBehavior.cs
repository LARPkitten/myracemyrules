using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace MyRaceMyRules
{
    /// <summary>
    /// Per-tick player behavior that keeps the CAMERA out of the ceiling. Always on, for every
    /// player, regardless of race.
    ///
    /// This is purely a viewing aid. It never changes the collision box: whether a character
    /// fits through a space is still decided entirely by the race's configured CollisionBox
    /// (plus the engine's own sneak handling). What this fixes is the camera:
    ///   - a race with EyeHeight 2.2 and a 1.85 CollisionBox fits under a 2-block ceiling, but
    ///     the camera sits inside the ceiling block and the player sees the inside of it;
    ///   - a race with EyeHeight 1.1 and a 0.9 CollisionBox has the same problem in a 1-block gap.
    /// While the player is in (or walking into) a space lower than their eye height, the eye
    /// height is lowered to just under the ceiling; once the space opens up it returns to normal.
    ///
    /// How the engine works (verified against EntityPlayer.cs / EntityProperties.cs):
    ///   - Every tick (server + other players) and every frame (own player, OnSelfBeforeRender)
    ///     updateEyeHeight() takes Properties.EyeHeight as the target (x0.8 while sneaking /
    ///     unable to stand) and lerps LocalEyePos.Y toward it at 5*dt.
    ///   - Each entity carries its own CLONE of EntityProperties, so mutating them is per-player.
    ///
    /// Therefore this behavior does not write LocalEyePos itself (the engine would lerp it
    /// straight back every frame). It steers the engine's TARGET, Properties.EyeHeight, and lets
    /// the engine's own lerp do the smoothing. Because only the target moves, tick ordering
    /// relative to updateEyeHeight() does not matter.
    ///
    /// Baseline tracking: the race's normal eye height is whatever PlayerModelLib (or vanilla)
    /// put into Properties.EyeHeight. We remember the last value WE wrote; if the current value
    /// differs, something else (model/size change, respawn) set a new normal value and we adopt
    /// it. This keeps the behavior independent of PlayerModelLib's internals and correct for any
    /// race, tall or short.
    ///
    /// Runs on both sides: the client for the own camera, the server so server-side
    /// eye-position logic (block selection, reach) agrees. Both read the same world.
    /// </summary>
    public class AdaptiveEyeHeightBehavior : EntityBehavior
    {
        public const string Code = MyRaceMyRulesModSystem.Domain + ":adaptiveeyeheight";

        // ----- Tuning -----

        /// <summary>How far (blocks) below the ceiling the camera is kept.</summary>
        private const float HeadroomMargin = 0.15f;

        /// <summary>
        /// Lowest the camera may go, as a fraction of the race's normal eye height, so a very
        /// low ceiling cannot drag the camera to the floor.
        /// </summary>
        private const float MinEyeFraction = 0.3f;

        /// <summary>
        /// Distance (blocks) ahead of a moving player that is also checked for a ceiling, so
        /// the camera starts dropping before passing under a lintel instead of clipping into it.
        /// </summary>
        private const float LookAheadDistance = 0.6f;

        /// <summary>Half-width of the thin column probed above the player for a ceiling.</summary>
        private const float ProbeHalfWidth = 0.1f;

        /// <summary>Ignore target changes smaller than this to avoid flicker at ceiling edges.</summary>
        private const float TargetHysteresis = 0.01f;

        /// <summary>Tolerance for "did someone else change Properties.EyeHeight since we wrote it".</summary>
        private const double ExternalChangeEpsilon = 1e-4;

        // ----- State -----

        private readonly EntityPlayer _player;

        // Reused per tick — CollisionTester.IsColliding takes a Cuboidf relative to pos.
        private readonly Cuboidf _probeBox = new();
        private readonly Vec3d _probePos = new();

        private bool _hasBaseline;
        private double _baselineEye;
        private double _appliedEye;

        public AdaptiveEyeHeightBehavior(Entity entity) : base(entity)
        {
            _player = entity as EntityPlayer
                      ?? throw new ArgumentException($"{Code} can only be attached to a player entity", nameof(entity));
        }

        public override string PropertyName() => Code;

        public override void OnGameTick(float deltaTime)
        {
            base.OnGameTick(deltaTime);

            EntityProperties props = entity.Properties;
            if (props == null) return;

            SyncBaseline(props);
            if (!_hasBaseline || _baselineEye <= 0) return;

            if (!ShouldAdapt())
            {
                Restore(props);
                return;
            }

            double ceiling = MeasureCeiling();

            // Nothing above the eyes (or the obstruction is not a ceiling): normal eye height.
            if (ceiling >= _baselineEye + HeadroomMargin)
            {
                Restore(props);
                return;
            }

            double minEye = Math.Max(0.1, _baselineEye * MinEyeFraction);
            double targetEye = GameMath.Clamp(ceiling - HeadroomMargin, minEye, _baselineEye);

            if (Math.Abs(targetEye - _appliedEye) < TargetHysteresis) return;

            Apply(props, targetEye);
        }

        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            base.OnEntityDespawn(despawn);
            if (entity.Properties != null) Restore(entity.Properties);
        }

        /// <summary>
        /// Adopt Properties.EyeHeight as the baseline if we have none yet, or if it no longer
        /// matches what we last wrote (PlayerModelLib / the game changed the race's eye height).
        /// </summary>
        private void SyncBaseline(EntityProperties props)
        {
            double curEye = props.EyeHeight;

            bool changedExternally = !_hasBaseline || Math.Abs(curEye - _appliedEye) > ExternalChangeEpsilon;
            if (!changedExternally) return;

            _baselineEye = curEye;
            _appliedEye = curEye;
            _hasBaseline = true;
        }

        /// <summary>
        /// Situations where the engine already positions the camera its own way, or where fitting
        /// the camera to terrain makes no sense. Mirrors the exclusions in EntityPlayer.updateEyeHeight.
        /// </summary>
        private bool ShouldAdapt()
        {
            if (!entity.Alive) return false;
            if (_player.MountedOn != null) return false;
            if (entity.Swimming) return false;

            if (_player.Player?.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return false;

            // Same control set updateEyeHeight() reads (own controls locally, synced for others).
            EntityControls controls = _player.ServerControls;
            if (controls.IsFlying || controls.NoClip || controls.DetachedMode) return false;
            if (controls.IsClimbing || controls.FloorSitting) return false;

            return true;
        }

        /// <summary>
        /// Height of the first solid thing above the player's head column, at the current
        /// position and — when moving — a short distance ahead so the camera drops BEFORE it
        /// passes under a lintel rather than clipping into it for a moment. Returns a value at
        /// or above the baseline eye height when there is nothing to duck under.
        /// </summary>
        private double MeasureCeiling()
        {
            Vec3d pos = entity.Pos.XYZ;
            double ceiling = ProbeCeilingAt(pos);

            Vec3d motion = entity.Pos.Motion;
            double horizSq = motion.X * motion.X + motion.Z * motion.Z;
            if (horizSq > 1e-8)
            {
                double scale = LookAheadDistance / Math.Sqrt(horizSq);
                _probePos.Set(pos.X + motion.X * scale, pos.Y, pos.Z + motion.Z * scale);
                ceiling = Math.Min(ceiling, ProbeCeilingAt(_probePos));
            }

            return ceiling;
        }

        /// <summary>
        /// Probe a thin vertical column at <paramref name="at"/> and return the lowest height
        /// (from the feet) at which it hits terrain, searched between the column's base and the
        /// full eye height plus margin. The column starts part-way up the body so slabs or steps
        /// under the feet are never mistaken for a ceiling, and if the column collides even at
        /// its base the player is against a wall (or inside something) — that is not a ceiling
        /// problem, so "no ceiling" is returned and the camera is left alone.
        /// </summary>
        private double ProbeCeilingAt(Vec3d at)
        {
            var tester = entity.World.CollisionTester;
            var accessor = entity.World.BlockAccessor;

            float feetY = entity.OriginCollisionBox?.Y1 ?? 0f;
            float top = (float)(_baselineEye + HeadroomMargin);

            // Start half-way to the eyes, never below a quarter block above the feet.
            float columnBase = Math.Max(0.25f, (float)(_baselineEye * 0.5));
            if (columnBase >= top) return double.MaxValue;

            _probeBox.Set(-ProbeHalfWidth, feetY + columnBase, -ProbeHalfWidth,
                           ProbeHalfWidth, feetY + top, ProbeHalfWidth);

            // Column is clear all the way up: nothing to duck under.
            if (!tester.IsColliding(accessor, _probeBox, at, false)) return double.MaxValue;

            // Column collides even at its base: wall / embedded, not a ceiling.
            _probeBox.Y2 = feetY + columnBase + 0.05f;
            if (tester.IsColliding(accessor, _probeBox, at, false)) return double.MaxValue;

            float lo = columnBase + 0.05f;  // known free
            float hi = top;                 // known colliding
            for (int i = 0; i < 8 && hi - lo > 0.005f; i++)
            {
                float mid = (lo + hi) * 0.5f;
                _probeBox.Y2 = feetY + mid;
                if (tester.IsColliding(accessor, _probeBox, at, false)) hi = mid;
                else lo = mid;
            }

            return lo;
        }

        private void Apply(EntityProperties props, double eye)
        {
            props.EyeHeight = eye;
            _appliedEye = eye;
        }

        /// <summary>Put the race's normal eye height back (no-op if already there).</summary>
        private void Restore(EntityProperties props)
        {
            if (!_hasBaseline) return;
            if (Math.Abs(_appliedEye - _baselineEye) < ExternalChangeEpsilon) return;
            Apply(props, _baselineEye);
        }

        /// <summary>Attach to a player entity if not already present. Safe to call repeatedly.</summary>
        public static void EnsureAttached(ICoreAPI api, EntityPlayer? playerEntity)
        {
            if (playerEntity == null) return;
            if (playerEntity.HasBehavior(Code)) return;

            try
            {
                var behavior = new AdaptiveEyeHeightBehavior(playerEntity);
                behavior.Initialize(playerEntity.Properties, new JsonObject(new Newtonsoft.Json.Linq.JObject()));
                playerEntity.AddBehavior(behavior);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[myracemyrules] Could not attach adaptive eye height to player '{0}': {1}",
                    playerEntity.GetName(), e.Message);
            }
        }
    }
}
