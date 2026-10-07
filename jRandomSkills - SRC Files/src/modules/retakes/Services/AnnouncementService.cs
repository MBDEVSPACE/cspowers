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

    private static void ShowSiteBanner(CCSPlayerController player, string bannerHtml, string plainText)
    {
        // Round-start alert: the centre box shows only this banner, lifted towards the top of the screen, for a
        // few seconds; the skill HUD takes the box back afterwards.
        var retakes = src.utils.Config.LoadedConfig.Modules.Retakes;
        float seconds = Math.Max(1f, retakes.SiteBannerSeconds);
        src.utils.SkillUtils.ShowCenterNotice(player, bannerHtml, plainText, seconds, exclusive: retakes.SiteBannerHidesSkill);
    }

    // The banner is a picture when the config has an image URL for the site (same trick as CS2-CenterAdvert:
    // an <img> inside the centre HTML message), otherwise the big coloured "SITE A" text.
    private static string BannerHtml(Bombsite bombsite)
    {
        var retakes = src.utils.Config.LoadedConfig.Modules.Retakes;
        string image = (bombsite == Bombsite.A ? retakes.SiteImageA : retakes.SiteImageB)?.Trim() ?? "";

        if (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"<img src='{image.Replace("'", "%27")}'/>";

        string siteColor = bombsite == Bombsite.A ? "#FF5050" : "#50A0FF";
        return $"<font class='fontWeight-Bold fontSize-l' color='{siteColor}'>▶  SITE {bombsite}  ◀</font>";
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

        // The site call is a round-start alert in the centre HTML box: the box shows only this banner, lifted
        // towards the top of the screen, and the skill HUD takes the box back when it expires.
        var centerText = StripColors(centerAnnouncementMessage);
        var centerHtml = BannerHtml(bombsite);

        foreach (var player in Utilities.GetPlayers())
        {
            if (!onlyCenter)
            {
                player.PrintToChat($"{_plugin.Localizer["retakes.prefix"]} {announcementMessage}");

                if (_centerEnabled)
                {
                    ShowSiteBanner(player, centerHtml, centerText);
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

            ShowSiteBanner(player, centerHtml, centerText);
        }

        Logger.LogInfo("Announcement", $"Announced bombsite {bombsite} ({numTerrorist}T vs {numCounterTerrorist}CT)");
    }
}