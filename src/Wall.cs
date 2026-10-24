using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

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

    private const int Slices = 120;

    internal static void Reset() { candidates.Clear(); Built = false; Radius = 0f; }

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

    internal static void Build(Transform root)
    {
        Built = false;
        var border = PickBorder();
        if (border == null) { Plugin.Log.LogWarning("No border ring found; no invisible wall added."); return; }

        // The border's colliders.
        var cols = new List<Collider>();
        foreach (var l in border.Leaves)
            foreach (var c in l.GetComponentsInChildren<Collider>(true))
                if (c != null && !c.isTrigger) cols.Add(c);
        if (cols.Count == 0) return;

        var centre = Vector3.zero;
        foreach (var l in border.Leaves) centre += l.position;
        centre /= border.Leaves.Count;
        centre.y = root.position.y;

        // Inner face of the border per slice: for each collider, its nearest distance to the centre, spread over the
        // slices its footprint covers.
        var inner = new float[Slices];
        for (int i = 0; i < Slices; i++) inner[i] = float.MaxValue;
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
            for (int s = s0; s <= s1; s++)
            {
                int k = ((s % Slices) + Slices) % Slices;
                inner[k] = Mathf.Min(inner[k], d);
            }
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
        for (int i = 0; i < Slices; i++)
        {
            float rad = filled[i] + into;
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
        }

        Built = true;
        Centre = centre;
        Radius = max;
        Plugin.Log.LogInfo($"Invisible edge along '{border.Name}': {min:0.#}-{max:0.#} m from ({centre.x:0.#}, {centre.z:0.#}), {known}/{Slices} slices on border rocks, layer {LayerMask.LayerToName(layer)}.");
    }

    private static float Angle(Vector3 v) => Mathf.Repeat(Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg, 360f);
}

/// <summary>
/// The game places objectives and similar things on a fixed 150 x 150 m grid around the centre
/// (MapPositionProvider / WorldGrid). On the bigger map players can stand outside it, and asking for a position
/// "around" them then fails ("No available candidates found around position in the grid"). Keep the request
/// inside the grid.
/// </summary>
[HarmonyPatch]
internal static class GridClamp
{
    private const float Half = 75f; // WorldGrid.GridSize / 2

    [HarmonyPatch(typeof(MapPositionProvider), nameof(MapPositionProvider.GetRandomPosition), new[] { typeof(Vector3), typeof(float), typeof(float) })]
    [HarmonyPrefix]
    private static void GetRandomPosition_Prefix(ref Vector3 aroundWorldPosition, float maxDistance)
    {
        if (Sync.AppliedScale <= 1.01f) return;
        float limit = Mathf.Max(10f, Half - Mathf.Min(maxDistance, Half) * 0.5f - 2f);
        float m = Mathf.Max(Mathf.Abs(aroundWorldPosition.x), Mathf.Abs(aroundWorldPosition.z));
        if (m <= limit) return;
        float f = limit / m;
        aroundWorldPosition = new Vector3(aroundWorldPosition.x * f, aroundWorldPosition.y, aroundWorldPosition.z * f);
    }
}
