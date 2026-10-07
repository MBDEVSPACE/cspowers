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
    // entity parented to a spare, predicted view-model slot of the player, so the client moves it with the camera
    // itself (no per-tick teleport, no lag, no net traffic after the spawn). The same trick the CS2 screen-menu
    // plugins use. Only the owner receives the entity.
    public static class ScreenText
    {
        private sealed class Banner
        {
            public uint PlayerIndex;
            public uint EntityIndex;
            public DateTime Until;
        }

        private static readonly ConcurrentDictionary<uint, Banner> banners = [];
        private static bool offsetsBroken;

        // Distance in front of the eyes; the text is drawn at this depth so it never clips into walls.
        private const float Depth = 7f;

        public static bool Available => !EntitySafety.SpawningBlocked && !offsetsBroken;

        // up: height on screen in world units at Depth (0 = centre, about 2.5 = top edge); right: sideways offset.
        public static bool Show(CCSPlayerController? player, string text, Color color, float fontSize = 80f, float up = 2.0f, float right = 0f, float seconds = 4f, string? fontName = null)
        {
            if (player == null || !player.IsValid || player.IsBot) return false;
            if (!Available) return false;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return false;

            Hide(player.Index);

            CBaseEntity? viewModel;
            try
            {
                viewModel = EnsureViewModel(pawn);
            }
            catch (Exception ex)
            {
                offsetsBroken = true;
                Server.PrintToConsole($"[TiredPowers] Screen text is unavailable on this build ({ex.Message}); site calls use the centre HUD banner.");
                return false;
            }
            if (viewModel == null) return false;

            var ent = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (ent == null || !ent.IsValid) return false;

            ent.MessageText = text;
            ent.Enabled = true;
            ent.FontSize = fontSize;
            if (!string.IsNullOrWhiteSpace(fontName)) ent.FontName = fontName;
            ent.Color = color;
            ent.Fullbright = true;
            ent.WorldUnitsPerPx = 0.0075f;
            ent.DepthOffset = 0f;
            ent.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
            ent.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
            ent.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;

            // Placed relative to the eyes once; the parent (view model) carries it with the camera from then on.
            QAngle eyeAngles = pawn.EyeAngles;
            Vector eye = new(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + pawn.ViewOffset.Z);
            Vector forward = SkillUtils.GetForwardVector(eyeAngles);
            Vector upVec = SkillUtils.GetForwardVector(new QAngle(eyeAngles.X - 90f, eyeAngles.Y, 0));
            Vector rightVec = SkillUtils.GetForwardVector(new QAngle(0, eyeAngles.Y - 90f, 0));

            Vector pos = eye + forward * Depth + upVec * up + rightVec * right;
            QAngle angles = new(0, eyeAngles.Y + 270f, 90f - eyeAngles.X);

            ent.Teleport(pos, angles);
            ent.DispatchSpawn();
            ent.AcceptInput("SetParent", viewModel, null, "!activator");

            banners[player.Index] = new Banner { PlayerIndex = player.Index, EntityIndex = ent.Index, Until = DateTime.Now.AddSeconds(seconds) };
            EntityManager.RegisterEntity(ent.Index, player.Index, "screen_text");
            src.player.Event.EnableTransmit();
            return true;
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
                EntityManager.DestroyEntity(banner.EntityIndex, 0f);
        }

        public static void HideAll()
        {
            foreach (var index in banners.Keys.ToArray())
                Hide(index);
        }

        // Expiry and dead owners only; the client does the positioning.
        public static void OnTick()
        {
            if (banners.IsEmpty) return;

            foreach (var banner in banners.Values.ToArray())
            {
                var player = Utilities.GetPlayerFromIndex((int)banner.PlayerIndex);
                var pawn = player?.PlayerPawn.Value;

                if (player == null || !player.IsValid || pawn == null || !pawn.IsValid
                    || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || DateTime.Now > banner.Until)
                    Hide(banner.PlayerIndex);
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
