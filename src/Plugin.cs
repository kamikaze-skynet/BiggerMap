using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BiggerMap;

/// <summary>
/// A bigger arena: the realm's layout is spread out from the centre (border ring, scenery, objectives, props),
/// the floor is widened and the enemy navigation is rebuilt. Everyone needs the mod; the host's size is used.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "girthy_mods.BiggerMap";
    public const string Name = "BiggerMap";
    public const string Version = "0.9.1";

    internal const float MaxScale = 1.8f; // past this the scenery leaves the realm's ground

    internal static new ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> Scale;
    internal static ConfigEntry<bool> FillGaps;
    internal static ConfigEntry<bool> FillGrass;
    internal static ConfigEntry<int> MaxGrassCopies;
    internal static ConfigEntry<bool> RebuildNavMesh;

    public override void Load()
    {
        Log = base.Log;

        Enabled = Config.Bind("Map", "Enabled", true, "Make the arena bigger.");
        Scale = Config.Bind("Map", "Scale", 1.5f,
            new ConfigDescription("How much wider the arena is (1.5 = 1.5x as wide, about 2.25x the area).", new AcceptableValueRange<float>(1f, MaxScale)));
        FillGaps = Config.Bind("Map", "FillBorderGaps", true, "Add copies of the border ring's pieces so the bigger ring has no gaps.");
        FillGrass = Config.Bind("Map", "FillGrass", true, "Add copies of grass and flowers so the bigger ground isn't bare.");
        MaxGrassCopies = Config.Bind("Map", "MaxGrassCopies", 2,
            new ConfigDescription("Most extra copies of each grass field (more copies = more objects to draw).", new AcceptableValueRange<int>(0, 3)));
        RebuildNavMesh = Config.Bind("Map", "RebuildNavigation", true, "Rebuild the enemies' navigation for the new layout (needed for enemies to reach the outer area).");

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(MapScaler));
        AddComponent<Runner>();
        Log.LogInfo($"{Name} {Version} ready.");
    }
}
