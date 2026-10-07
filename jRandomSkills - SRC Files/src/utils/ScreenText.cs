using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Drawing;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace src.utils
{
    // Text drawn at a fixed spot on one player's screen without using the centre message slot: a point_worldtext
    // entity kept in front of the eyes, the way CS2-GameHUD (used by InfoTop and similar plugins) does it. Only
    // the owner receives the entity.
    //
    // Lifecycle follows GameHUD: ONE text entity per player, created on first use and kept for the whole map.
    // Showing sets its message, hiding blanks it; the entity is only removed when the player leaves. Killing and
    // re-creating parented entities mid-round is exactly the kind of thing that can crash the engine.
    //
    // ScreenTextMethod:
    //   Pawn   - parented to the player pawn and re-aimed from the view angles every tick (GameHUD default).
    //   Orient - parented to a point_orient that follows the eyes on the client (GameHUD "method" cvar).
    public static class ScreenText
    {
        public enum Method { Pawn, Orient }

        public readonly record struct Style(
            float X, float Y, float Z, float FontSize, float UnitsPerPx, string? FontName,
            float BackgroundBorderHeight = 0f, float BackgroundBorderWidth = 0f);

        private sealed class Entry
        {
            public uint PlayerIndex;
            public uint TextIndex;
            public uint OrientIndex;
            public uint ParentHandle;
            public DateTime Until;
            public bool Visible;
            public Method Method;
            public Style Style;
        }

        private static readonly ConcurrentDictionary<uint, Entry> entries = [];
        private static bool viewModelNoteShown;

        public static bool Available => !EntitySafety.SpawningBlocked;

        public static Method ParseMethod(string? name)
        {
            switch (name?.Trim().ToLowerInvariant())
            {
                case "orient":
                    return Method.Orient;
                case "viewmodel":
                    if (!viewModelNoteShown)
                    {
                        viewModelNoteShown = true;
                        Server.PrintToConsole("[TiredPowers] ScreenBannerMethod \"ViewModel\" writes into engine memory and was removed; using \"Pawn\".");
                    }
                    return Method.Pawn;
                default:
                    return Method.Pawn;
            }
        }

        // X: right, Y: up, Z: distance in front of the eyes (world units at that distance, GameHUD convention).
        public static bool Show(CCSPlayerController? player, string text, Color color, Style style, float seconds, Method method = Method.Pawn)
        {
            if (player == null || !player.IsValid || player.IsBot) return false;
            if (!Available) return false;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return false;

            var entry = entries.GetOrAdd(player.Index, i => new Entry { PlayerIndex = i });

            // A different method or a dead text entity: start over for this player.
            var ent = Utilities.GetEntityFromIndex<CPointWorldText>((int)entry.TextIndex);
            if (ent == null || !ent.IsValid || entry.Method != method)
            {
                RemoveEntities(entry);
                entry.Method = method;

                ent = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
                if (ent == null || !ent.IsValid) return false;

                ent.MessageText = "";
                ent.Enabled = true;
                ent.Fullbright = true;
                ent.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
                ent.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_TOP;
                ent.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
                ApplyStyle(ent, style, color);
                ent.DispatchSpawn();

                entry.TextIndex = ent.Index;
                entry.ParentHandle = 0;
                src.player.Event.EnableTransmit();
            }
            else if (!entry.Style.Equals(style))
            {
                ApplyStyle(ent, style, color);
            }
            else
            {
                ent.Color = color;
                Utilities.SetStateChanged(ent, "CPointWorldText", "m_Color");
            }
            entry.Style = style;

            // Parent: the pawn itself, or a point_orient riding on the pawn. Re-parent when the pawn changed.
            CBaseEntity? parent = pawn;
            if (method == Method.Orient)
            {
                var orient = Utilities.GetEntityFromIndex<CPointOrient>((int)entry.OrientIndex);
                if (orient == null || !orient.IsValid)
                {
                    orient = CreateOrient(pawn);
                    if (orient == null) return false;
                    entry.OrientIndex = orient.Index;
                    entry.ParentHandle = 0;
                }
                parent = orient;
            }

            if (parent == null || !parent.IsValid) return false;
            if (entry.ParentHandle != parent.EntityHandle.Raw)
            {
                ent.AcceptInput("SetParent", parent, null, "!activator");
                entry.ParentHandle = parent.EntityHandle.Raw;
            }

            ent.AcceptInput("SetMessage", null, null, text);
            entry.Until = DateTime.Now.AddSeconds(seconds);
            entry.Visible = true;
            Place(pawn, ent, entry, parent);
            return true;
        }

        private static void ApplyStyle(CPointWorldText ent, Style style, Color color)
        {
            ent.FontSize = style.FontSize;
            if (!string.IsNullOrWhiteSpace(style.FontName)) ent.FontName = style.FontName;
            ent.Color = color;
            ent.WorldUnitsPerPx = style.UnitsPerPx;
            bool background = style.BackgroundBorderHeight != 0f || style.BackgroundBorderWidth != 0f;
            ent.DrawBackground = background;
            if (background)
            {
                ent.BackgroundBorderHeight = style.BackgroundBorderHeight;
                ent.BackgroundBorderWidth = style.BackgroundBorderWidth;
            }
        }

        private static void Place(CCSPlayerPawn pawn, CPointWorldText ent, Entry entry, CBaseEntity? parent)
        {
            QAngle angles;
            Vector origin;

            if (entry.Method == Method.Orient && parent != null && parent.IsValid && parent.AbsRotation != null && parent.AbsOrigin != null)
            {
                angles = parent.AbsRotation;
                origin = new Vector(parent.AbsOrigin.X, parent.AbsOrigin.Y, parent.AbsOrigin.Z);
            }
            else
            {
                if (pawn.AbsOrigin == null) return;
                angles = pawn.V_angle;
                origin = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + pawn.ViewOffset.Z);
            }

            AngleVectors(angles, out var forward, out var right, out var up);
            var s = entry.Style;
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
        private static CPointOrient? CreateOrient(CCSPlayerPawn pawn)
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

        // Blank the text; the entity stays for the next call.
        public static void Hide(uint playerIndex)
        {
            if (!entries.TryGetValue(playerIndex, out var entry) || !entry.Visible) return;

            entry.Visible = false;
            var ent = Utilities.GetEntityFromIndex<CPointWorldText>((int)entry.TextIndex);
            if (ent != null && ent.IsValid)
                ent.AcceptInput("SetMessage", null, null, "");
        }

        public static void HideAll()
        {
            foreach (var index in entries.Keys.ToArray())
                Hide(index);
        }

        // The player left: drop their entities.
        public static void Remove(uint playerIndex)
        {
            if (entries.TryRemove(playerIndex, out var entry))
                RemoveEntities(entry);
        }

        // Map change: the engine has already freed everything.
        public static void Clear() => entries.Clear();

        private static void RemoveEntities(Entry entry)
        {
            try
            {
                var ent = Utilities.GetEntityFromIndex<CPointWorldText>((int)entry.TextIndex);
                if (ent != null && ent.IsValid) ent.Remove();
            }
            catch { }
            try
            {
                var orient = Utilities.GetEntityFromIndex<CPointOrient>((int)entry.OrientIndex);
                if (orient != null && orient.IsValid) orient.Remove();
            }
            catch { }
            entry.TextIndex = 0;
            entry.OrientIndex = 0;
            entry.ParentHandle = 0;
            entry.Visible = false;
        }

        // Expiry and dead owners; the pawn method also re-aims the text from the current view angles.
        public static void OnTick()
        {
            if (entries.IsEmpty) return;

            foreach (var entry in entries.Values)
            {
                if (!entry.Visible) continue;

                var player = Utilities.GetPlayerFromIndex((int)entry.PlayerIndex);
                var pawn = player?.PlayerPawn.Value;
                var ent = Utilities.GetEntityFromIndex<CPointWorldText>((int)entry.TextIndex);

                if (player == null || !player.IsValid || pawn == null || !pawn.IsValid || ent == null || !ent.IsValid
                    || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || DateTime.Now > entry.Until)
                {
                    Hide(entry.PlayerIndex);
                    continue;
                }

                if (entry.Method == Method.Pawn)
                    Place(pawn, ent, entry, pawn);
            }
        }

        // Only the owner may receive their text.
        public static void FilterTransmit(CCheckTransmitInfoList infoList)
        {
            if (entries.IsEmpty) return;

            foreach (var (info, player) in infoList)
            {
                if (player == null || !player.IsValid) continue;
                foreach (var entry in entries.Values)
                    if (entry.TextIndex != 0 && entry.PlayerIndex != player.Index && info.TransmitEntities.Contains((int)entry.TextIndex))
                        info.TransmitEntities.Remove((int)entry.TextIndex);
            }
        }
    }
}
