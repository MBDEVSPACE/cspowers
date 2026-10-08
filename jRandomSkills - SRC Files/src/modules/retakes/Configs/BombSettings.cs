using System.Text.Json.Serialization;

namespace RetakesPlugin.Configs;

public class BombSettings
{
    // true: the bomb is planted for the T side. Auto-plant spawns the planted bomb at freeze end; while
    // jRandomSkills blocks entity spawning (EntitySpawnSafety in config.json, CounterStrikeSharp behind the
    // CS2 build) the planter spawns with the bomb instead and their plant finishes on the first click.
    // false: the planter gets the bomb and plants it by hand like in a normal round.
    [JsonPropertyName("IsAutoPlantEnabled")]
    public bool IsAutoPlantEnabled { get; set; } = true;

    // "Weapon" (default): the planter spawns holding the C4 and the plugin arms it at freeze end, so the
    // game's own plant code plants it (no entity is created by the plugin; safe on any CS2 build).
    // "Entity": the old way, a planted_c4 entity is created directly (needs CounterStrikeSharp to match the
    // CS2 build; with EntitySpawnSafety blocking it the Weapon way is used anyway).
    [JsonPropertyName("AutoPlantMethod")]
    public string AutoPlantMethod { get; set; } = "Weapon";
}