using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Drawing;
using static src.jRandomSkills;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace src.utils
{
    // A private on-screen banner that does not use the game's centre text slot: a point_worldtext entity kept a
    // few units in front of the player's eyes every tick, so it sits at a fixed spot on their screen (top by
    // default) in any size and colour. Only its owner receives the entity.
    public static class ScreenText
    {
        private sealed class Banner
        {
            public uint PlayerIndex;
            public uint EntityIndex;
            public DateTime Until;
            public float Up;
            public string Text = "";
        }

        private static readonly ConcurrentDictionary<uint, Banner> banners = [];

        // Distance in front of the eyes; the text is drawn at this depth so it never clips into walls.
        private const float Depth = 7f;

        public static bool Available => !EntitySafety.SpawningBlocked;

        // up: screen offset in world units at Depth (positive = higher on screen, ~2.5 is the top edge).
        public static void Show(CCSPlayerController? player, string text, Color color, float fontSize = 60f, float up = 2.2f, float seconds = 8f)
        {
            if (player == null || !player.IsValid || player.IsBot) return;
            if (!Available) return;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;

            Hide(player.Index);

            var ent = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (ent == null || !ent.IsValid) return;

            ent.MessageText = text;
            ent.Enabled = true;
            ent.FontSize = fontSize;
            ent.Color = color;
            ent.Fullbright = true;
            // World size of a pixel at Depth: scales the text with the font so 60 px reads as a headline.
            ent.WorldUnitsPerPx = 0.0075f;
            ent.DepthOffset = 0f;
            ent.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
            ent.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
            ent.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;

            var banner = new Banner { PlayerIndex = player.Index, EntityIndex = ent.Index, Until = DateTime.Now.AddSeconds(seconds), Up = up, Text = text };
            Place(pawn, ent, banner);
            ent.DispatchSpawn();
            Place(pawn, ent, banner);

            banners[player.Index] = banner;
            EntityManager.RegisterEntity(ent.Index, player.Index, "screen_text");
            src.player.Event.EnableTransmit();
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

        public static void OnTick()
        {
            if (banners.IsEmpty) return;

            foreach (var banner in banners.Values.ToArray())
            {
                var player = Utilities.GetPlayerFromIndex((int)banner.PlayerIndex);
                var pawn = player?.PlayerPawn.Value;
                var ent = Utilities.GetEntityFromIndex<CPointWorldText>((int)banner.EntityIndex);

                if (player == null || !player.IsValid || pawn == null || !pawn.IsValid || pawn.AbsOrigin == null
                    || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || ent == null || !ent.IsValid || DateTime.Now > banner.Until)
                {
                    Hide(banner.PlayerIndex);
                    continue;
                }

                Place(pawn, ent, banner);
            }
        }

        // Keeps the entity in front of the eyes, facing the player, at the chosen height on screen.
        private static void Place(CCSPlayerPawn pawn, CPointWorldText ent, Banner banner)
        {
            if (pawn.AbsOrigin == null) return;

            // One tick of prediction so the banner does not trail while moving.
            Vector eye = new(
                pawn.AbsOrigin.X + pawn.AbsVelocity.X / 64f,
                pawn.AbsOrigin.Y + pawn.AbsVelocity.Y / 64f,
                pawn.AbsOrigin.Z + pawn.AbsVelocity.Z / 64f + pawn.ViewOffset.Z);

            QAngle view = pawn.V_angle;
            Vector forward = SkillUtils.GetForwardVector(view);
            Vector up = SkillUtils.GetForwardVector(new QAngle(view.X - 90f, view.Y, 0));

            Vector pos = eye + forward * Depth + up * banner.Up;
            // Text plane faces the camera: yaw turned to face back, pitch follows the view.
            QAngle angles = new(0, view.Y + 270f, 90f - view.X);

            ent.Teleport(pos, angles);
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
