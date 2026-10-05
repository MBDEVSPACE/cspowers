using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using src.utils;
using System.Collections.Concurrent;

namespace src.player.skills
{
    public class BunnyHop : ISkill
    {
        private const Skills skillName = Skills.BunnyHop;
        private static readonly ConcurrentDictionary<uint, int> playersLastJump = [];
        private static readonly List<CCSPlayerController> holderBuffer = [];

        public static void NewRound()
        {
            playersLastJump.Clear();
        }

        public static void PlayerDisconnect(uint playerIndex)
        {
            playersLastJump.TryRemove(playerIndex, out _);
        }

        public static void LoadSkill()
        {
            SkillUtils.RegisterSkill(skillName, SkillsInfo.GetValue<string>(skillName, "color"));
        }

        public static void OnTick()
        {
            PlayerManager.FillSkillHolders(skillName, holderBuffer);
            if (holderBuffer.Count == 0) return;

            foreach (var playerEvent in holderBuffer)
            {
                var player = PlayerManager.GetPlayerFromEvent(playerEvent) ?? playerEvent;
                if (player == null || !player.IsValid) continue;

                GiveBunnyHop(player);
            }
        }

        private static void GiveBunnyHop(CCSPlayerController player)
        {
            var eventPlayer = PlayerManager.GetPlayerEvent(player);
            var eventPlayerPawn = eventPlayer?.PlayerPawn?.Value;
            if (eventPlayerPawn == null || !eventPlayerPawn.IsValid) return;

            var playerPawn = player.PlayerPawn.Value;
            if (playerPawn == null || !playerPawn.IsValid) return;

            if (JumpBan.bannedPlayers.ContainsKey(player.Index)) return;

            var flags = (PlayerFlags)eventPlayerPawn.Flags;
            if (eventPlayerPawn.MoveType.HasFlag(MoveType_t.MOVETYPE_LADDER) || eventPlayerPawn.MoveType == MoveType_t.MOVETYPE_NONE) return;

            // No landing stamina penalty: the game would otherwise slow every hop and make the chain feel jerky.
            var movement = playerPawn.MovementServices;
            if (movement != null)
                Schema.SetSchemaValue(movement.Handle, "CCSPlayer_MovementServices", "m_flStamina", 0f);

            // Hold jump: as long as the key is down every landing turns straight into the next hop.
            bool jumpHeld = (player.Buttons & PlayerButtons.Jump) != 0
                || (movement?.Buttons?.ButtonStates[0] & (ulong)PlayerButtons.Jump) != 0;
            if (!jumpHeld || !flags.HasFlag(PlayerFlags.FL_ONGROUND)) return;

            // Only once per landing: a new hop needs a tick in the air in between.
            if (playersLastJump.TryGetValue(eventPlayer!.Index, out int lastTick) && lastTick + 2 >= Server.TickCount) return;
            playersLastJump[eventPlayer.Index] = Server.TickCount;

            float jumpVelocity = SkillsInfo.GetValue<float>(skillName, "jumpVelocity");
            float maxSpeed = SkillsInfo.GetValue<float>(skillName, "maxSpeed");
            float boost = Math.Clamp(SkillsInfo.GetValue<float>(skillName, "jumpBoost"), 1f, 1.5f);

            float vX = eventPlayerPawn.AbsVelocity.X;
            float vY = eventPlayerPawn.AbsVelocity.Y;
            float speed2D = MathF.Sqrt(vX * vX + vY * vY);

            // Gentle gain per hop, capped: no sudden speed jumps, no hard stop at the cap.
            float target = speed2D < 10f ? speed2D : Math.Min(speed2D * boost, Math.Max(maxSpeed, speed2D * 0.98f));
            float scale = speed2D > 0f ? target / speed2D : 1f;

            eventPlayerPawn.Teleport(null, null, new Vector(vX * scale, vY * scale, jumpVelocity));
        }

        public class SkillConfig(Skills skill = skillName, bool active = true, string color = "#d1430a", CsTeam onlyTeam = CsTeam.None, bool disableOnFreezeTime = false, bool needsTeammates = false, string requiredPermission = "", float? hudDuration = null, float? descriptionHudDuration = null, int maxPerServer = -1, Rarity rarity = Rarity.Common, float maxSpeed = 500f, float jumpVelocity = 300f, float jumpBoost = 1.08f) : SkillsInfo.DefaultSkillInfo(skill, active, color, onlyTeam, disableOnFreezeTime, needsTeammates, requiredPermission, hudDuration, descriptionHudDuration, maxPerServer, rarity)
        {
            public float MaxSpeed { get; set; } = maxSpeed;
            public float JumpVelocity { get; set; } = jumpVelocity;
            public float JumpBoost { get; set; } = jumpBoost;
        }
    }
}