using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BiggerMap;

/// <summary>
/// Invisible walls left by spreading. A few kinds of solid pieces end up with nothing visible on them:
///  - rocks the game merged into one mesh, where no stand-in mesh was found (the merged mesh has to be hidden);
///  - collision-only pieces (a long blocker for a fence or cliff) whose visible parts spread apart without them;
///  - collision rings or blockers around the original, smaller arena.
/// After spreading, each solid obstacle is checked from above at a grid of points: where the collider is, is there
/// something visible? If most of it isn't covered by anything you can see, it is switched off. The ground, triggers
/// and gameplay objects are never touched.
/// </summary>
internal static class Blockers
{
    private const float Cell = 4f;

    internal static int Clean(Transform managerRoot, Transform variantRoot, HashSet<int> obstacleLayers)
    {
        Physics.SyncTransforms();

        // Visible things, bucketed by position.
        var boxes = new List<Bounds>();
        var buckets = new Dictionary<long, List<int>>();
        foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>()) // everything visible, wherever it lives
        {
            try
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                string tn = r.GetIl2CppType().Name;
                if (tn == "ParticleSystemRenderer" || tn == "LineRenderer" || tn == "TrailRenderer" || tn == "SpriteRenderer") continue;
                var b = r.bounds;
                if (b.size.y < 0.3f) continue; // decals, flat ground details
                int idx = boxes.Count;
                boxes.Add(b);
                int x0 = Key(b.min.x), x1 = Key(b.max.x), z0 = Key(b.min.z), z1 = Key(b.max.z);
                if ((x1 - x0 + 1) * (z1 - z0 + 1) > 400) { x0 = x1 = z0 = z1 = int.MinValue; } // huge (ground): skip
                for (int x = x0; x <= x1 && x != int.MinValue; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = ((long)x << 32) ^ (uint)z;
                        if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                        list.Add(idx);
                    }
            }
            catch { }
        }

        int checkedCount = 0;
        var off = new List<Collider>();
        var names = new List<string>();
        foreach (var c in variantRoot.GetComponentsInChildren<Collider>(false))
        {
            try
            {
                if (c == null || !c.enabled || c.isTrigger || c.GetIl2CppType().Name == "TerrainCollider") continue;
                var go = c.gameObject;
                if (!obstacleLayers.Contains(go.layer)) continue;
                if (go.name == "FloorCollider" || go.name == "Wall" && go.transform.parent != null && go.transform.parent.name == "BiggerMap_Wall") continue;
                if (IsGameplay(c.transform, variantRoot)) continue;

                var b = c.bounds;
                if (b.size.x < 0.2f && b.size.z < 0.2f) continue;
                int on = 0, covered = 0;
                const int n = 6;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        float x = Mathf.Lerp(b.min.x, b.max.x, (i + 0.5f) / n);
                        float z = Mathf.Lerp(b.min.z, b.max.z, (j + 0.5f) / n);
                        var ray = new Ray(new Vector3(x, b.max.y + 1f, z), Vector3.down);
                        if (!c.Raycast(ray, out var hit, b.size.y + 2f)) continue;
                        on++;
                        if (Visible(new Vector3(x, hit.point.y, z), b, boxes, buckets)) covered++;
                    }
                if (on < 3) continue;                       // too thin to judge from above
                checkedCount++;
                if (covered >= on * 0.4f) continue;          // mostly something you can see

                off.Add(c);
                if (names.Count < 8) names.Add($"{go.name} ({covered}/{on})");
            }
            catch { }
        }
        // Safety: if a big share looks invisible, the check itself is wrong for this realm (its visuals live somewhere
        // unexpected); switching them off would make real rocks walk-through, so leave everything as it is.
        if (off.Count > 0 && off.Count > Math.Max(10, checkedCount / 4))
        {
            Plugin.Log.LogWarning($"Invisible blockers: {off.Count} of {checkedCount} solid pieces looked invisible; that's too many to trust, so none were switched off. E.g. {string.Join(", ", names)}.");
            return 0;
        }
        foreach (var c in off) try { c.enabled = false; } catch { }
        if (off.Count > 0) Plugin.Log.LogInfo($"Switched off {off.Count} invisible blockers (solid with nothing visible there) out of {checkedCount}, e.g. {string.Join(", ", names)}.");
        else Plugin.Log.LogInfo($"No invisible blockers found among {checkedCount} solid pieces.");
        return off.Count;
    }

    private static int Key(float v) => Mathf.FloorToInt(v / Cell);

    private static bool Visible(Vector3 p, Bounds collider, List<Bounds> boxes, Dictionary<long, List<int>> buckets)
    {
        long key = ((long)Key(p.x) << 32) ^ (uint)Key(p.z);
        if (!buckets.TryGetValue(key, out var list)) return false;
        float minHeight = Mathf.Min(1f, collider.size.y * 0.5f);
        foreach (int i in list)
        {
            var r = boxes[i];
            if (r.size.y < minHeight) continue; // grass and flowers don't make a wall
            if (p.x < r.min.x - 0.4f || p.x > r.max.x + 0.4f || p.z < r.min.z - 0.4f || p.z > r.max.z + 0.4f) continue;
            if (r.max.y < collider.min.y || r.min.y > collider.max.y + 0.5f) continue;
            return true;
        }
        return false;
    }

    /// <summary>Objects with game scripts or networking (breakables, objectives, hazards) are left alone.</summary>
    private static bool IsGameplay(Transform t, Transform root)
    {
        for (var p = t; p != null && p != root; p = p.parent)
        {
            if (p.GetComponent<NetworkObject>() != null) return true;
            foreach (var mb in p.GetComponents<MonoBehaviour>())
                if (mb != null && !mb.GetIl2CppType().FullName.StartsWith("UnityEngine.", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
