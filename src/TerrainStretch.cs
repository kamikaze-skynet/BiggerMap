using System;
using System.Collections.Generic;
using UnityEngine;

namespace BiggerMap;

/// <summary>
/// The realms' ground is a Terrain: flat in the arena, rising into hills under the border and the scenery behind it.
/// Players walk on a flat invisible floor, so after the border moves out the old hills would stick up through the
/// bigger arena (you'd walk "under the ground"). We stretch the whole terrain outward by the same scale as
/// everything else: its width and length are multiplied (TerrainData.size, which stretches the hills and the ground
/// textures with it) and it's moved so the arena's centre stays put.
///
/// (0.9.1 rewrote the height map through a raw engine call and crashed the game; this uses the engine's own size
/// setter, which handles the native object itself.)
///
/// TerrainData is a shared asset that stays in memory between loads, so its original size is remembered and every
/// load starts from it (also when the mod is off or the scale changes).
/// </summary>
internal static class TerrainStretch
{
    private static readonly Dictionary<string, Vector3> originalSize = new();

    private static string Key(TerrainData td) => td.name + "#" + td.heightmapResolution;

    internal static void Apply(Transform variantRoot, float k)
    {
        foreach (var t in variantRoot.GetComponentsInChildren<Terrain>(true))
        {
            if (t == null) continue;
            try { Stretch(t, variantRoot.position, k); }
            catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't stretch the ground ({t.name}): {ex.Message}"); }
        }
    }

    private static void Stretch(Terrain terrain, Vector3 centre, float k)
    {
        var td = terrain.terrainData;
        if (td == null) return;
        string key = Key(td);
        var size = td.size;
        if (!originalSize.TryGetValue(key, out var orig)) { orig = size; originalSize[key] = orig; }

        var wanted = new Vector3(orig.x * k, orig.y, orig.z * k);
        if ((size - wanted).sqrMagnitude > 0.0001f) td.size = wanted;

        // The terrain grows from its corner; move the corner so the arena centre stays where it was.
        var p = terrain.transform.position;
        terrain.transform.position = new Vector3(centre.x + (p.x - centre.x) * k, p.y, centre.z + (p.z - centre.z) * k);
        Plugin.Log.LogInfo($"Stretched the ground {terrain.name} x{k:0.##} ({orig.x:0}x{orig.z:0} -> {wanted.x:0}x{wanted.z:0}).");
    }

    /// <summary>A realm loaded with the mod off (or not scaled): put back any size we changed earlier.</summary>
    internal static void Restore(Transform variantRoot)
    {
        foreach (var t in variantRoot.GetComponentsInChildren<Terrain>(true))
        {
            try
            {
                var td = t?.terrainData;
                if (td == null) continue;
                if (originalSize.TryGetValue(Key(td), out var orig) && (td.size - orig).sqrMagnitude > 0.0001f) td.size = orig;
            }
            catch { }
        }
    }
}
