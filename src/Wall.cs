using System;
using System.Collections.Generic;
using UnityEngine;

namespace BiggerMap;

/// <summary>
/// A guaranteed edge for the bigger arena. Spreading the border ring and filling its gaps with copies still leaves
/// spots where the pieces don't overlap, and the rebuilt navigation would otherwise run out over the stretched
/// ground. So we put a ring of invisible, tall box colliders just inside the moved border (same physics layer as
/// the border's own colliders) and limit the navigation build to the area inside it.
/// </summary>
internal static class Wall
{
    private sealed class Candidate { public List<Transform> Leaves; public float Median; }

    private static readonly List<Candidate> candidates = new();
    internal static bool Built;
    internal static Vector3 Centre;     // world
    internal static float Radius;

    internal static void Reset() { candidates.Clear(); Built = false; Radius = 0f; }

    /// <summary>A ring-shaped group of pieces with colliders (the border), seen while spreading.</summary>
    internal static void Offer(Transform root, List<Transform> outerLeaves)
    {
        try
        {
            int withCollider = 0;
            foreach (var l in outerLeaves) if (l.GetComponentInChildren<Collider>(true) != null) withCollider++;
            if (withCollider < outerLeaves.Count / 2) return;
            var r = new List<float>();
            foreach (var l in outerLeaves) { var p = root.InverseTransformPoint(l.position); r.Add(new Vector2(p.x, p.z).magnitude); }
            r.Sort();
            candidates.Add(new Candidate { Leaves = new List<Transform>(outerLeaves), Median = r[r.Count / 2] });
        }
        catch { }
    }

    internal static void Build(Transform root)
    {
        Built = false;
        if (candidates.Count == 0) { Plugin.Log.LogWarning("No border ring found; no invisible wall added."); return; }
        candidates.Sort((a, b) => a.Median.CompareTo(b.Median));
        var ring = candidates[0].Leaves; // the innermost ring with colliders is the border

        // Centre of the ring and its inner radius (a low percentile of the pieces' distances, minus a margin).
        var c = Vector3.zero;
        foreach (var l in ring) c += l.position;
        c /= ring.Count;
        var d = new List<float>();
        foreach (var l in ring) d.Add(new Vector2(l.position.x - c.x, l.position.z - c.z).magnitude);
        d.Sort();
        float radius = d[Mathf.Clamp(d.Count / 7, 0, d.Count - 1)] - 2.5f;
        if (radius < 20f) { Plugin.Log.LogWarning($"Border ring looks too small ({radius:0} m); no invisible wall added."); return; }

        int layer = 0;
        foreach (var l in ring)
        {
            var col = l.GetComponentInChildren<Collider>(true);
            if (col != null) { layer = col.gameObject.layer; break; }
        }

        var holder = new GameObject("BiggerMap_Wall");
        holder.layer = layer;
        holder.transform.SetParent(root, false);
        holder.transform.position = new Vector3(c.x, root.position.y, c.z);

        const int pieces = 96;
        const float thickness = 3f, height = 40f;
        float chord = 2f * radius * Mathf.Sin(Mathf.PI / pieces) * 1.25f;
        for (int i = 0; i < pieces; i++)
        {
            float a = i * Mathf.PI * 2f / pieces;
            var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var go = new GameObject("Wall");
            go.layer = layer;
            go.transform.SetParent(holder.transform, false);
            go.transform.position = holder.transform.position + dir * (radius + thickness / 2f) + Vector3.up * (height / 2f - 5f);
            go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(chord, height, thickness);
        }

        Built = true;
        Centre = holder.transform.position;
        Radius = radius;
        Plugin.Log.LogInfo($"Invisible edge: radius {radius:0.#} m around ({c.x:0.#}, {c.z:0.#}), layer {LayerMask.LayerToName(layer)}.");
    }
}
