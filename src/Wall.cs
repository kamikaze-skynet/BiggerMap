using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace BiggerMap;

/// <summary>
/// A guaranteed edge for the bigger arena. Spreading the border ring and filling its gaps with copies still leaves
/// spots where pieces don't overlap. So we add invisible, tall box colliders that follow the border's own shape:
/// the arena is cut into narrow slices around the centre, and in each slice the wall stands right at the inner
/// face of the border's colliders (where the rocks already block you, so no playable ground is lost). Slices with no
/// border collider (the gaps) take the distance of their neighbours. Navigation is limited to the area inside.
///
/// (0.9.3 used one circle at the border's closest point, which cut off up to 20 m where the border bulges out.)
/// </summary>
internal static class Wall
{
    private sealed class Candidate { public string Name; public List<Transform> Leaves; public float Spread; }

    private static readonly List<Candidate> candidates = new();
    internal static bool Built;
    internal static Vector3 Centre;     // world
    internal static float Radius;       // the farthest wall slice (for the navigation volume)
    internal static float[] SliceRadius; // the wall's inner face, per slice (world metres from Centre)
    internal static Transform Holder;

    private const int Slices = 120;

    internal static void Reset() { candidates.Clear(); Built = false; Radius = 0f; SliceRadius = null; Holder = null; }

    /// <summary>A ring-shaped group of pieces with colliders, seen while spreading.</summary>
    internal static void Offer(Transform root, Transform container, List<Transform> outerLeaves)
    {
        try
        {
            int withCollider = 0;
            foreach (var l in outerLeaves) if (l.GetComponentInChildren<Collider>(true) != null) withCollider++;
            if (withCollider < outerLeaves.Count / 2) return;
            var r = new List<float>();
            foreach (var l in outerLeaves) { var p = root.InverseTransformPoint(l.position); r.Add(new Vector2(p.x, p.z).magnitude); }
            r.Sort();
            float med = r[r.Count / 2];
            // How ring-like it is: a border is a narrow band; rocks or trees scattered inside are not.
            float spread = med > 0f ? (r[r.Count - 1] - r[r.Count / 7]) / med : 9f;
            candidates.Add(new Candidate { Name = container.name, Leaves = new List<Transform>(outerLeaves), Spread = spread });
        }
        catch { }
    }

    private static Candidate PickBorder()
    {
        Candidate best = null;
        foreach (var c in candidates)
            if (c.Name.IndexOf("Border", StringComparison.OrdinalIgnoreCase) >= 0 && (best == null || c.Leaves.Count > best.Leaves.Count)) best = c;
        if (best != null) return best;
        foreach (var c in candidates) if (best == null || c.Spread < best.Spread) best = c;
        return best;
    }

    /// <summary>The layer the border's colliders are on (the realm's obstacle layer), -1 if unknown.</summary>
    internal static int BorderLayer()
    {
        var b = PickBorder();
        if (b == null) return -1;
        foreach (var l in b.Leaves)
        {
            var c = l.GetComponentInChildren<Collider>(true);
            if (c != null && !c.isTrigger) return c.gameObject.layer;
        }
        return -1;
    }

    internal static void Build(Transform root)
    {
        Built = false;
        var border = PickBorder();
        if (border == null) { Plugin.Log.LogWarning("No border ring found; no invisible wall added."); return; }

        // The border's colliders.
        var cols = new List<Collider>();
        foreach (var l in border.Leaves)
            foreach (var c in l.GetComponentsInChildren<Collider>(true))
                if (c != null && c.enabled && !c.isTrigger) cols.Add(c);
        if (cols.Count == 0) return;

        // The pieces were just moved; colliders only follow their transforms after a physics sync. (Before 0.9.6 the
        // wall was measured from the old, unmoved collider positions, so it stood at the original small border.)
        Physics.SyncTransforms();

        var centre = Vector3.zero;
        foreach (var l in border.Leaves) centre += l.position;
        centre /= border.Leaves.Count;
        centre.y = root.position.y;

        // Which colliders cover which slices (by their footprint), and a rough distance from their bounding boxes.
        var inner = new float[Slices];
        var perSlice = new List<Collider>[Slices];
        for (int i = 0; i < Slices; i++) { inner[i] = float.MaxValue; perSlice[i] = new List<Collider>(); }
        foreach (var c in cols)
        {
            Bounds b;
            try { b = c.bounds; } catch { continue; }
            float d = Mathf.Sqrt(b.SqrDistance(new Vector3(centre.x, b.center.y, centre.z)));
            if (d < 5f) continue; // something at the centre isn't border
            float a0 = float.MaxValue, a1 = float.MinValue;
            float ac = Angle(b.center - centre);
            foreach (var corner in new[] { new Vector3(b.min.x, 0, b.min.z), new Vector3(b.min.x, 0, b.max.z), new Vector3(b.max.x, 0, b.min.z), new Vector3(b.max.x, 0, b.max.z) })
            {
                float a = Angle(corner - new Vector3(centre.x, 0, centre.z));
                float rel = Mathf.DeltaAngle(ac, a);
                a0 = Mathf.Min(a0, rel); a1 = Mathf.Max(a1, rel);
            }
            int s0 = Mathf.FloorToInt((ac + a0) / 360f * Slices), s1 = Mathf.CeilToInt((ac + a1) / 360f * Slices);
            for (int sl = s0; sl <= s1; sl++)
            {
                int k = ((sl % Slices) + Slices) % Slices;
                inner[k] = Mathf.Min(inner[k], d);
                perSlice[k].Add(c);
            }
        }

        // The real rock face: rays from the centre outward at a few heights and across each slice, against that
        // slice's border colliders only. A bounding box reaches further in than the rock, so it's only the fallback.
        int rayHits = 0;
        float[] heights = { 1f, 2.5f, 4f };
        for (int i = 0; i < Slices; i++)
        {
            if (perSlice[i].Count == 0) continue;
            float best = float.MaxValue;
            for (int j = 0; j < 3; j++)
            {
                float a = (i + 0.2f + 0.3f * j) * Mathf.PI * 2f / Slices;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                foreach (float h in heights)
                {
                    var ray = new Ray(centre + Vector3.up * h, dir);
                    foreach (var c in perSlice[i])
                    {
                        try { if (c.Raycast(ray, out var hit, 400f) && hit.distance > 5f) best = Mathf.Min(best, hit.distance); } catch { }
                    }
                }
            }
            if (best < float.MaxValue) { inner[i] = best; rayHits++; }
        }

        // Gaps: take the nearer of the two neighbouring known distances.
        int known = 0;
        for (int i = 0; i < Slices; i++) if (inner[i] < float.MaxValue) known++;
        if (known < Slices / 4) { Plugin.Log.LogWarning($"Border colliders cover too little of the ring ({known}/{Slices}); no invisible wall added."); return; }
        var filled = (float[])inner.Clone();
        for (int i = 0; i < Slices; i++)
        {
            if (inner[i] < float.MaxValue) continue;
            float l = float.MaxValue, r = float.MaxValue;
            for (int k = 1; k < Slices && l == float.MaxValue; k++) l = inner[((i - k) % Slices + Slices) % Slices];
            for (int k = 1; k < Slices && r == float.MaxValue; k++) r = inner[(i + k) % Slices];
            filled[i] = Mathf.Min(l, r);
        }

        int layer = cols[0].gameObject.layer;
        var holder = new GameObject("BiggerMap_Wall");
        holder.layer = layer;
        holder.transform.SetParent(root, false);
        holder.transform.position = centre;

        const float thickness = 3f, height = 40f, into = 0.5f; // the wall's inner face sits 0.5 m inside the rock face
        float min = float.MaxValue, max = 0f;
        var radii = new float[Slices];
        for (int i = 0; i < Slices; i++)
        {
            float rad = filled[i] + into;
            radii[i] = rad;
            min = Mathf.Min(min, rad); max = Mathf.Max(max, rad);
            float a = (i + 0.5f) * Mathf.PI * 2f / Slices;
            var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            float chord = 2f * rad * Mathf.Sin(Mathf.PI / Slices) * 1.3f;
            var go = new GameObject("Wall");
            go.layer = layer;
            go.transform.SetParent(holder.transform, false);
            go.transform.position = centre + dir * (rad + thickness / 2f) + Vector3.up * (height / 2f - 5f);
            go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            go.AddComponent<BoxCollider>().size = new Vector3(chord, height, thickness);
            // Nothing may stand on top of the wall (it was walkable ground 35 m up for the navigation).
            try { var mod = go.AddComponent<NavMeshModifier>(); mod.overrideArea = true; mod.area = NotWalkable; } catch { }
        }

        Built = true;
        Centre = centre;
        Radius = max;
        SliceRadius = radii;
        Holder = holder.transform;
        BlockOutside(holder.transform, centre, radii);
        Plugin.Log.LogInfo($"Invisible edge along '{border.Name}': {min:0.#}-{max:0.#} m from ({centre.x:0.#}, {centre.z:0.#}), {known}/{Slices} slices on border rocks ({rayHits} measured at the rock face), layer {LayerMask.LayerToName(layer)}.");
    }

    private static float Angle(Vector3 v) => Mathf.Repeat(Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg, 360f);

    private const int NotWalkable = 1; // Unity's built-in navigation area

    /// <summary>The ground outside the wall is still floor, so the navigation would cover it: enemies, pickups and
    /// event spots snapped to the navigation could land outside the arena. Mark it all as not walkable.</summary>
    private static void BlockOutside(Transform holder, Vector3 centre, float[] radii)
    {
        int made = 0;
        try
        {
            const float depth = 12f;
            const int steps = 7; // out to ~84 m past the wall: past the corners of the navigation area
            for (int i = 0; i < Slices; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / Slices;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                for (int k = 0; k < steps; k++)
                {
                    float r0 = radii[i] + 2f + k * depth, r1 = r0 + depth;
                    float width = 2f * r1 * Mathf.Sin(Mathf.PI / Slices) * 1.15f;
                    var go = new GameObject("Outside");
                    go.transform.SetParent(holder, false);
                    go.transform.position = centre + dir * ((r0 + r1) / 2f) + Vector3.up * 10f;
                    go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                    var v = go.AddComponent<NavMeshModifierVolume>();
                    v.size = new Vector3(width, 120f, depth + 0.5f);
                    v.center = Vector3.zero;
                    v.area = NotWalkable;
                    made++;
                }
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Marking the outside as not walkable: {ex.Message} ({made} areas made)"); }
    }

    private static int SliceOf(Vector3 p)
    {
        int i = Mathf.FloorToInt(Angle(p - Centre) / 360f * Slices);
        return ((i % Slices) + Slices) % Slices;
    }

    /// <summary>Is this point inside the arena, at least margin metres from the edge?</summary>
    internal static bool Inside(Vector3 p, float margin)
    {
        if (!Built || SliceRadius == null) return true;
        var d = p - Centre; d.y = 0f;
        return d.magnitude <= SliceRadius[SliceOf(p)] - margin;
    }

    /// <summary>The point if it's inside the arena; else the nearest spot margin metres inside the edge, on walkable ground.</summary>
    internal static Vector3 Clamp(Vector3 p, float margin)
    {
        if (Inside(p, margin)) return p;
        var d = p - Centre; d.y = 0f;
        float r = Mathf.Max(5f, SliceRadius[SliceOf(p)] - margin);
        var q = Centre + (d.sqrMagnitude > 0.01f ? d.normalized : Vector3.forward) * r;
        q.y = p.y;
        try { if (NavMesh.SamplePosition(q, out var hit, 8f, -1) && Inside(hit.position, 1f)) q = hit.position; } catch { }
        return q;
    }
}

/// <summary>
/// The game picks spots for objectives, events and spawns on a fixed grid of free cells (MapPositionProvider), worked
/// out from the original, smaller layout. Every spot in the bigger map is a spot in the original one moved outward
/// by the scale, so questions are asked in original-map terms (position and distances divided by the scale) and the
/// answer is moved back out. Before 0.9.6 players standing past the original border got "No available candidates
/// found around position in the grid".
/// </summary>
[HarmonyPatch]
internal static class GridMap
{
    internal static Transform Root;
    private const float EdgeMargin = 4f; // spots (scrolls, objectives, events) stay this far inside the edge

    private static bool Active(out float k)
    {
        k = Sync.AppliedScale;
        if (k <= 1.01f) return false;
        try { return Root is not null && !Root.WasCollected && Root != null; } catch { return false; }
    }

    private static Vector3 ToGrid(Vector3 p, float k)
    {
        var l = Root.InverseTransformPoint(p);
        var g = Root.TransformPoint(new Vector3(l.x / k, l.y, l.z / k));
        return new Vector3(g.x, p.y, g.z);
    }

    private static Vector3 FromGrid(Vector3 p, float k)
    {
        var l = Root.InverseTransformPoint(p);
        var w = Root.TransformPoint(new Vector3(l.x * k, l.y, l.z * k));
        return new Vector3(w.x, p.y, w.z);
    }

    [HarmonyPatch(typeof(MapPositionProvider), nameof(MapPositionProvider.GetRandomPosition), new[] { typeof(Vector3), typeof(float), typeof(float) })]
    [HarmonyPrefix]
    private static void Around_Prefix(MapPositionProvider __instance, ref Vector3 aroundWorldPosition, ref float minDistance, ref float maxDistance)
    {
        if (!Active(out float k)) return;
        aroundWorldPosition = ToGrid(aroundWorldPosition, k);
        minDistance /= k;
        maxDistance /= k;
        // Near the edge or tucked against rocks a small search can still find no free cell; then the game logs an
        // error and picks a spot anywhere on the map. Widen the search and lean it towards the middle instead.
        try
        {
            if (Count(__instance, aroundWorldPosition, minDistance, maxDistance) > 0) return;
            var middle = Wall.Built ? ToGrid(Wall.Centre, k) : Root.position;
            for (int i = 1; i <= 4; i++)
            {
                var p = Vector3.Lerp(aroundWorldPosition, new Vector3(middle.x, aroundWorldPosition.y, middle.z), 0.15f * i);
                float max = maxDistance * (1f + 0.5f * i) + 2f * i;
                if (Count(__instance, p, minDistance, max) == 0) continue;
                aroundWorldPosition = p;
                maxDistance = max;
                return;
            }
        }
        catch { }
    }

    private static int Count(MapPositionProvider m, Vector3 around, float min, float max)
    {
        var list = m.FindAvailablePositions(around, min, max);
        return list == null ? 0 : list.Count;
    }

    [HarmonyPatch(typeof(MapPositionProvider), nameof(MapPositionProvider.GetRandomPosition), new[] { typeof(Vector3), typeof(float), typeof(float) })]
    [HarmonyPostfix]
    private static void Around_Postfix(ref Vector3 __result) { if (Active(out float k)) __result = Wall.Clamp(FromGrid(__result, k), EdgeMargin); }

    [HarmonyPatch(typeof(MapPositionProvider), nameof(MapPositionProvider.GetRandomPosition), new Type[0])]
    [HarmonyPostfix]
    private static void Anywhere_Postfix(ref Vector3 __result) { if (Active(out float k)) __result = Wall.Clamp(FromGrid(__result, k), EdgeMargin); }

    [HarmonyPatch(typeof(MapPositionProvider), nameof(MapPositionProvider.TryGetFurthestAvailablePosition))]
    [HarmonyPrefix]
    private static void Furthest_Prefix(ref Vector3 origin, ref float maxDistance)
    {
        if (!Active(out float k)) return;
        origin = ToGrid(origin, k);
        maxDistance /= k;
    }

    [HarmonyPatch(typeof(MapPositionProvider), nameof(MapPositionProvider.TryGetFurthestAvailablePosition))]
    [HarmonyPostfix]
    private static void Furthest_Postfix(bool __result, ref Vector3 position) { if (__result && Active(out float k)) position = Wall.Clamp(FromGrid(position, k), EdgeMargin); }

    [HarmonyPatch(typeof(MapPositionProvider), nameof(MapPositionProvider.IsPositionAvailable), new[] { typeof(Vector3) })]
    [HarmonyPrefix]
    private static void Available_Prefix(ref Vector3 position) { if (Active(out float k)) position = ToGrid(position, k); }
}
