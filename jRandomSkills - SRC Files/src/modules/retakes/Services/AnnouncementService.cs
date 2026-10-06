using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

using RetakesPlugin.Utils;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Services;

public class AnnouncementService
{
    private readonly RetakesPlugin _plugin;
    private readonly Random _random;
    private readonly HashSet<CCSPlayerController> _hasMutedVoices;
    private readonly bool _voicesEnabled;
    private readonly bool _centerEnabled;
    private readonly bool _plantLocationEnabled;

    private static readonly string[] BombsiteAnnouncers =
    [
        "balkan_epic",
        "leet_epic",
        "professional_epic",
        "professional_fem",
        "seal_epic",
        "swat_epic",
        "swat_fem"
    ];

    public AnnouncementService(RetakesPlugin plugin, Random random, HashSet<CCSPlayerController> hasMutedVoices, bool voicesEnabled, bool centerEnabled, bool plantLocationEnabled)
    {
        _plugin = plugin;
        _random = random;
        _hasMutedVoices = hasMutedVoices;
        _voicesEnabled = voicesEnabled;
        _centerEnabled = centerEnabled;
        _plantLocationEnabled = plantLocationEnabled;
    }

    public void AnnouncePlantLocation(string? plantLocation, Bombsite bombsite)
    {
        if (!_plantLocationEnabled)
        {
            return;
        }

        // Fall back to the bombsite letter when the planter spawn has no callout name configured
        var locationName = string.IsNullOrWhiteSpace(plantLocation) ? bombsite.ToString() : plantLocation;

        var message = $"{_plugin.Localizer["retakes.prefix"]} {_plugin.Localizer["retakes.bombsite.plant_location", locationName]}";

        foreach (var player in Utilities.GetPlayers())
        {
            if (player.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist)
            {
                player.PrintToChat(message);
            }
        }

        Logger.LogInfo("Announcement", $"Announced plant location: {locationName}");
    }

    private static void ShowSiteBanner(CCSPlayerController player, string bannerText, System.Drawing.Color color, string fallbackHtml, string plainText)
    {
        if (src.utils.ScreenText.Available)
            src.utils.ScreenText.Show(player, bannerText, color, fontSize: 64f, up: 2.2f, seconds: 10f);
        else
            src.utils.SkillUtils.ShowCenterNotice(player, fallbackHtml, plainText, 10f);
    }

    // Chat colour codes are control characters; they show up as junk in a center alert.
    public static string StripColors(string text)
    {
        return new string(text.Where(c => c >= ' ').ToArray()).Trim();
    }

    public void AnnounceBombsite(Bombsite bombsite, bool onlyCenter = false)
    {
        var numTerrorist = PlayerHelper.GetPlayerCount(CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist);
        var numCounterTerrorist = PlayerHelper.GetPlayerCount(CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist);

        var announcementMessage = _plugin.Localizer["retakes.bombsite.announcement", bombsite.ToString(), numTerrorist, numCounterTerrorist];
        var centerAnnouncementMessage = _plugin.Localizer["retakes.center.bombsite.announcement", bombsite.ToString(), numTerrorist, numCounterTerrorist];

        // The site call is its own on-screen banner (ScreenText: a world-text entity pinned to the top of the
        // player's view), so it never shares the centre text slot with the skill HUD. If entity spawning is
        // blocked on this server it falls back to a line at the top of the HUD box.
        var centerText = StripColors(centerAnnouncementMessage);
        string siteColor = bombsite == Bombsite.A ? "#FF5050" : "#50A0FF";
        var siteRgb = bombsite == Bombsite.A ? System.Drawing.Color.FromArgb(255, 255, 80, 80) : System.Drawing.Color.FromArgb(255, 80, 160, 255);
        var centerHtml = src.utils.SkillUtils.NoticeHtml($"SITE {bombsite}", centerText, siteColor);
        var bannerText = $"SITE {bombsite}\n{centerText}";

        foreach (var player in Utilities.GetPlayers())
        {
            if (!onlyCenter)
            {
                player.PrintToChat($"{_plugin.Localizer["retakes.prefix"]} {announcementMessage}");

                if (_centerEnabled)
                {
                    ShowSiteBanner(player, bannerText, siteRgb, centerHtml, centerText);
                }

                if (_voicesEnabled && !_hasMutedVoices.Contains(player))
                {
                    var bombsiteAnnouncer = BombsiteAnnouncers[_random.Next(BombsiteAnnouncers.Length)];
                    player.ExecuteClientCommand($"play sounds/vo/agents/{bombsiteAnnouncer}/loc_{bombsite.ToString().ToLower()}_01");
                }

                continue;
            }

            if (!_centerEnabled)
            {
                continue;
            }

            ShowSiteBanner(player, bannerText, siteRgb, centerHtml, centerText);
        }

        Logger.LogInfo("Announcement", $"Announced bombsite {bombsite} ({numTerrorist}T vs {numCounterTerrorist}CT)");
    }
}