using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace src.utils
{
    // Text drawn at a fixed spot on one player's screen without using the centre message slot: a point_worldtext
    // entity kept in front of the eyes, the way CS2-GameHUD (used by InfoTop and similar plugins) does it. Only
    // the owner receives the entity. Three ways to hold it in place (ScreenTextMethod):
    //   Pawn      - parented to the player pawn and re-aimed from the view angles every tick (GameHUD default).
    //   Orient    - parented to a point_orient that follows the eyes on the client (GameHUD "method" cvar).
    //   ViewModel - parented to a spare predicted view-model slot (screen-menu plugins).
    public static class ScreenText
    {
        public enum Method { Pawn, Orient, ViewModel }

        public readonly record struct Style(
            float X, float Y, float Z, float FontSize, float UnitsPerPx, string? FontName,
            float BackgroundBorderHeight = 0f, float BackgroundBorderWidth = 0f);

        private sealed class Banner
        {
            public uint PlayerIndex;
            public uint EntityIndex;
            public uint OrientIndex;
            public DateTime Until;
            public Method Method;
            public Style Style;
        }

        private static readonly ConcurrentDictionary<uint, Banner> banners = [];
        private static bool viewModelBroken;

        public static bool Available => !EntitySafety.SpawningBlocked;

        public static Method ParseMethod(string? name) => name?.Trim().ToLowerInvariant() switch
        {
            "orient" => Method.Orient,
            "viewmodel" => Method.ViewModel,
            _ => Method.Pawn,
        };

        // X: right, Y: up, Z: distance in front of the eyes (world units at that distance, GameHUD convention).
        public static bool Show(CCSPlayerController? player, string text, Color color, Style style, float seconds, Method method = Method.Pawn)
        {
            if (player == null || !player.IsValid || player.IsBot) return false;
            if (!Available) return false;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return false;

            Hide(player.Index);

            if (method == Method.ViewModel && viewModelBroken) method = Method.Pawn;

            CBaseEntity? parent = null;
            uint orientIndex = 0;
            try
            {
                switch (method)
                {
                    case Method.ViewModel:
                        parent = EnsureViewModel(pawn);
                        break;
                    case Method.Orient:
                        parent = CreateOrient(pawn);
                        orientIndex = parent?.Index ?? 0;
                        break;
                    default:
                        parent = pawn;
                        break;
                }
            }
            catch (Exception ex)
            {
                if (method == Method.ViewModel)
                {
                    viewModelBroken = true;
                    Server.PrintToConsole($"[TiredPowers] Screen text: view-model method unavailable on this build ({ex.Message}); using the pawn method.");
                    parent = pawn;
                    method = Method.Pawn;
                }
                else
                {
                    Server.PrintToConsole($"[TiredPowers] Screen text failed: {ex.Message}");
                    return false;
                }
            }
            if (parent == null) return false;

            var ent = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (ent == null || !ent.IsValid) return false;

            ent.MessageText = text;
            ent.Enabled = true;
            ent.FontSize = style.FontSize;
            if (!string.IsNullOrWhiteSpace(style.FontName)) ent.FontName = style.FontName;
            ent.Color = color;
            ent.Fullbright = true;
            ent.WorldUnitsPerPx = style.UnitsPerPx;
            ent.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
            ent.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_TOP;
            ent.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            if (style.BackgroundBorderHeight != 0f || style.BackgroundBorderWidth != 0f)
            {
                ent.DrawBackground = true;
                ent.BackgroundBorderHeight = style.BackgroundBorderHeight;
                ent.BackgroundBorderWidth = style.BackgroundBorderWidth;
            }

            ent.DispatchSpawn();
            ent.AcceptInput("SetParent", parent, null, "!activator");

            var banner = new Banner { PlayerIndex = player.Index, EntityIndex = ent.Index, OrientIndex = orientIndex, Until = DateTime.Now.AddSeconds(seconds), Method = method, Style = style };
            Place(pawn, ent, banner, parent);

            banners[player.Index] = banner;
            EntityManager.RegisterEntity(ent.Index, player.Index, "screen_text");
            if (orientIndex != 0) EntityManager.RegisterEntity(orientIndex, player.Index, "screen_text_orient");
            src.player.Event.EnableTransmit();
            return true;
        }

        private static void Place(CCSPlayerPawn pawn, CPointWorldText ent, Banner banner, CBaseEntity? parent)
        {
            QAngle angles;
            Vector origin;

            if (banner.Method == Method.Orient && parent != null && parent.IsValid && parent.AbsRotation != null && parent.AbsOrigin != null)
            {
                angles = parent.AbsRotation;
                origin = new Vector(parent.AbsOrigin.X, parent.AbsOrigin.Y, parent.AbsOrigin.Z);
            }
            else
            {
                if (pawn.AbsOrigin == null) return;
                angles = banner.Method == Method.ViewModel ? pawn.EyeAngles : pawn.V_angle;
                origin = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + pawn.ViewOffset.Z);
            }

            AngleVectors(angles, out var forward, out var right, out var up);
            var s = banner.Style;
            Vector pos = origin + forward * s.Z + right * s.X + up * s.Y;
            QAngle textAngles = new(0, angles.Y + 270f, 90f - angles.X);
            ent.Teleport(pos, textAngles);
        }

        private static void AngleVectors(QAngle angles, out Vector forward, out Vector right, out Vector up)
        {
            (float sy, float cy) = MathF.SinCos(angles.Y * MathF.PI / 180f);
            (float sp, float cp) = MathF.SinCos(angles.X * MathF.PI / 180f);
            forward = new Vector(cp * cy, cp * sy, -sp);
            right = new Vector(sy, -cy, 0);
            up = new Vector(sp * cy, sp * sy, cp);
        }

        // A point_orient that turns with the player's eyes on the client; the text rides on it.
        private static CBaseEntity? CreateOrient(CCSPlayerPawn pawn)
        {
            if (pawn.AbsOrigin == null) return null;

            var orient = Utilities.CreateEntityByName<CPointOrient>("point_orient");
            if (orient == null || !orient.IsValid) return null;

            orient.Active = true;
            orient.GoalDirection = PointOrientGoalDirectionType_t.eEyesForward;
            orient.DispatchSpawn();
            orient.Teleport(new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + pawn.ViewOffset.Z));
            orient.AcceptInput("SetParent", pawn, null, "!activator");
            orient.AcceptInput("SetTarget", pawn, null, "!activator");
            return orient;
        }

        // The third view-model slot is unused by the game; a predicted_viewmodel entity there survives weapon
        // switches and is predicted on the client, so anything parented to it sits still on screen.
        private static CBaseEntity? EnsureViewModel(CCSPlayerPawn pawn)
        {
            int servicesOffset = Schema.GetSchemaOffset("CCSPlayerPawnBase", "m_pViewModelServices");
            int handleOffset = Schema.GetSchemaOffset("CPlayer_ViewModelServices", "m_hViewModel");
            if (servicesOffset <= 0 || handleOffset <= 0) throw new InvalidOperationException("view-model schema offsets not found");

            nint services = Marshal.ReadIntPtr(pawn.Handle + servicesOffset);
            if (services == nint.Zero) return null;

            nint slot = services + handleOffset + 2 * sizeof(uint);
            uint raw = (uint)Marshal.ReadInt32(slot);
            if (raw != uint.MaxValue)
            {
                var existing = Utilities.GetEntityFromIndex<CBaseEntity>((int)(raw & 0x7FFF));
                if (existing != null && existing.IsValid && existing.EntityHandle.Raw == raw)
                    return existing;
            }

            var viewModel = Utilities.CreateEntityByName<CBaseEntity>("predicted_viewmodel");
            if (viewModel == null || !viewModel.IsValid) return null;

            viewModel.DispatchSpawn();
            Marshal.WriteInt32(slot, (int)viewModel.EntityHandle.Raw);
            Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_pViewModelServices");
            return viewModel;
        }

        public static void Hide(uint playerIndex)
        {
            if (banners.TryRemove(playerIndex, out var banner))
            {
                EntityManager.DestroyEntity(banner.EntityIndex, 0f);
                if (banner.OrientIndex != 0) EntityManager.DestroyEntity(banner.OrientIndex, 0f, hideFromTransmit: false);
            }
        }

        public static void HideAll()
        {
            foreach (var index in banners.Keys.ToArray())
                Hide(index);
        }

        // Expiry and dead owners; the pawn method also re-aims the text from the current view angles.
        public static void OnTick()
        {
            if (banners.IsEmpty) return;

            foreach (var banner in banners.Values.ToArray())
            {
                var player = Utilities.GetPlayerFromIndex((int)banner.PlayerIndex);
                var pawn = player?.PlayerPawn.Value;
                var ent = Utilities.GetEntityFromIndex<CPointWorldText>((int)banner.EntityIndex);

                if (player == null || !player.IsValid || pawn == null || !pawn.IsValid || ent == null || !ent.IsValid
                    || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || DateTime.Now > banner.Until)
                {
                    Hide(banner.PlayerIndex);
                    continue;
                }

                if (banner.Method == Method.Pawn)
                    Place(pawn, ent, banner, pawn);
            }
        }

        // Only the owner may receive their banner.
        public static void FilterTransmit(CCheckTransmitInfoList infoList)
        {
            if (banners.IsEmpty) return;

            foreach (var (info, player) in infoList)
            {
                if (player == null || !player.IsValid) continue;
                foreach (var banner in banners.Values)
                    if (banner.PlayerIndex != player.Index && info.TransmitEntities.Contains((int)banner.EntityIndex))
                        info.TransmitEntities.Remove((int)banner.EntityIndex);
            }
        }
    }
}
