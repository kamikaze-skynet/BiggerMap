using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Bindings;

namespace BiggerMap;

/// <summary>
/// The realms' ground is a Terrain: flat in the arena, rising into hills under the border and the scenery behind it.
/// Players walk on a flat invisible floor, so after the border moves out the old hills would stick up through the
/// bigger arena (you'd walk "under the ground"). We stretch the terrain's height map outward by the same scale as
/// everything else: the height at a point becomes the original height at (point / scale) from the centre.
///
/// TerrainData's public SetHeights is stripped from this build, so the heights are read from the IL2CPP float[,]
/// that GetHeights returns and written back through the engine call behind SetHeights. The original heights are
/// kept per terrain, so loading a realm again (or with another size, or with the mod off) starts from them.
/// </summary>
internal static class TerrainStretch
{
    private static readonly Dictionary<string, float[]> originals = new();

    internal static void Apply(Transform variantRoot, float k)
    {
        foreach (var t in variantRoot.GetComponentsInChildren<Terrain>(true))
        {
            if (t == null) continue;
            try { Stretch(t, variantRoot.position, k); }
            catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't reshape the ground ({t.name}): {ex.Message}"); }
        }
    }

    private static unsafe void Stretch(Terrain terrain, Vector3 centre, float k)
    {
        var td = terrain.terrainData;
        if (td == null) return;
        int res = td.heightmapResolution;
        if (res < 2 || res > 8193) return;
        int n = res * res;
        string key = td.name + "#" + res;

        if (!originals.TryGetValue(key, out var orig))
        {
            var arr = td.GetHeights(0, 0, res, res);
            if (arr == null || arr.Pointer == IntPtr.Zero) return;
            // IL2CPP array: 16-byte object header, bounds pointer, length, then the floats (row = z, column = x).
            long length = *(long*)((byte*)arr.Pointer + 0x18);
            if (length != n) { Plugin.Log.LogWarning($"Unexpected height map size {length} (expected {n})."); return; }
            orig = new float[n];
            Marshal.Copy(arr.Pointer + 0x20, orig, 0, n);
            originals[key] = orig;
        }

        var size = td.size;
        var pos = terrain.transform.position;
        var dst = new float[n];
        float step = 1f / (res - 1);
        for (int z = 0; z < res; z++)
        {
            float wz = pos.z + z * step * size.z;
            float fz = Mathf.Clamp((centre.z + (wz - centre.z) / k - pos.z) / size.z * (res - 1), 0f, res - 1);
            int z0 = Mathf.Min((int)fz, res - 2); float tz = fz - z0;
            for (int x = 0; x < res; x++)
            {
                float wx = pos.x + x * step * size.x;
                float fx = Mathf.Clamp((centre.x + (wx - centre.x) / k - pos.x) / size.x * (res - 1), 0f, res - 1);
                int x0 = Mathf.Min((int)fx, res - 2); float tx = fx - x0;
                int i = z0 * res + x0;
                float a = Mathf.Lerp(orig[i], orig[i + 1], tx);
                float b = Mathf.Lerp(orig[i + res], orig[i + res + 1], tx);
                dst[z * res + x] = Mathf.Lerp(a, b, tz);
            }
        }
        Write(td, res, k <= 1.001f ? orig : dst);
        Plugin.Log.LogInfo($"Reshaped the ground {terrain.name} ({res}x{res}) x{k:0.##}.");
    }

    private static unsafe void Write(TerrainData td, int res, float[] heights)
    {
        fixed (float* p = heights)
        {
            ManagedSpanWrapper span = default;
            *(IntPtr*)&span = (IntPtr)p;
            *(int*)((byte*)&span + IntPtr.Size) = heights.Length;
            TerrainData.Internal_SetHeights_Injected(td.Pointer, 0, 0, res, res, ref span);
        }
    }

    /// <summary>A realm loaded with the mod off (or not scaled): put back any heights we changed earlier.</summary>
    internal static void Restore(Transform variantRoot)
    {
        foreach (var t in variantRoot.GetComponentsInChildren<Terrain>(true))
        {
            try
            {
                var td = t?.terrainData;
                if (td == null) continue;
                int res = td.heightmapResolution;
                if (originals.TryGetValue(td.name + "#" + res, out var orig)) Write(td, res, orig);
            }
            catch { }
        }
    }
}
