using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace BiggerMap;

/// <summary>
/// Makes the realm's arena bigger by spreading it out from the centre.
///
/// Each realm scene has a WorldVariantManager with three layouts (variants); ActivateCorrectVariant turns one on as
/// the scene loads, on every player's game. Right after that we:
/// - widen the invisible floor (FloorCollider, 125 x 125) by the scale;
/// - move every placed object (border ring, rocks, trees, props, grass, objective and artifact spawn points, the
///   scenery outside the ring...) outward: its distance from the centre is multiplied by the scale, its size isn't;
///   big centred meshes (a realm's ground) are stretched instead;
/// - fill the gaps that opens in rings (the border and the scenery band behind it) with rotated copies, and thin
///   out less in grass fields with extra copies;
/// - rebuild the enemies' navigation mesh (NavMeshSurface.BuildNavMesh) so enemies can walk the new area.
///
/// Some layouts were "static batched" when the game was built: their meshes were merged into one big mesh that
/// ignores the objects' positions. Those objects get a copy of their mesh taken from the same object in another,
/// unbatched layout of the realm, and the merged original is hidden.
/// </summary>
[HarmonyPatch]
internal static class MapScaler
{
    private const float RingMinRadius = 30f;     // a container whose pieces all sit this far out is a ring
    private const int DenseCount = 100;           // grass-like containers
    private static readonly Dictionary<string, (Mesh mesh, Material[] mats)> donors = new();
    private static int standIns, missingDonors;

    [HarmonyPatch(typeof(WorldVariantManager), nameof(WorldVariantManager.ActivateCorrectVariant))]
    [HarmonyPostfix]
    private static void Activated(WorldVariantManager __instance)
    {
        if (Sync.WaitingForHost)
        {
            // A client without the host's size yet: lay out once it arrives (and not at all if the host hasn't got the mod).
            Runner.WaitForHost(__instance);
            Plugin.Log.LogInfo("Waiting for the host's map size before changing the layout.");
            return;
        }
        Apply(__instance);
    }

    internal static void Apply(WorldVariantManager __instance)
    {
        try
        {
            float k = Sync.Scale;
            var variant = __instance.GetActiveVariant();
            if (variant == null) { Plugin.Log.LogWarning("No active map layout found."); return; }
            if (!Sync.Enabled || k <= 1.01f) { TerrainStretch.Restore(variant.transform); return; }

            var sw = Stopwatch.StartNew();
            standIns = missingDonors = 0;
            CollectDonors(__instance.transform);
            Wall.Reset();
            int moved = Spread(variant.transform, k);
            GridMap.Root = variant.transform;
            if (Plugin.RemoveInvisibleBlockers.Value)
            {
                var layers = new HashSet<int>();
                for (int i = 0; i < 32; i++)
                    try { if (LayerMask.LayerToName(i) == "Obstacles") layers.Add(i); } catch { }
                int border = Wall.BorderLayer();
                if (border >= 0) layers.Add(border);
                Blockers.Clean(__instance.transform, variant.transform, layers);
            }
            if (Plugin.InvisibleEdge.Value) Wall.Build(variant.transform);
            TerrainStretch.Apply(variant.transform, k);
            ScaleConfiners(variant.transform, k);
            ScaleSpawnAreas(variant, k);
            Sync.AppliedScale = k;
            Plugin.Log.LogInfo($"{variant.name}: map scaled x{k:0.##} ({moved} objects moved, {standIns} batched meshes replaced" +
                               (missingDonors > 0 ? $", {missingDonors} hidden without a replacement" : "") + $") in {sw.ElapsedMilliseconds} ms.");

            if (Plugin.RebuildNavMesh.Value) Runner.RequestNavMesh(variant.transform, k);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Scaling the map: {ex}"); }
    }

    // ---------- spreading ----------

    private static int Spread(Transform root, float k)
    {
        int moved = 0;
        var groups = new List<Transform>();
        for (int i = 0; i < root.childCount; i++) groups.Add(root.GetChild(i));

        foreach (var g in groups)
        {
            if (g.GetComponent<Terrain>() != null) continue;
            if (g.name == "FloorCollider") { WidenFloor(g, k); continue; }
            if (IsContainer(g)) moved += Walk(root, g, k);
            else { Place(root, g, k); moved++; }
        }
        return moved;
    }

    private static void WidenFloor(Transform floor, float k)
    {
        if (floor.GetComponent<Renderer>() != null)
        {
            var s = floor.localScale;
            floor.localScale = new Vector3(s.x * k, s.y, s.z * k); // the visible floor and its box grow together
            return;
        }
        var box = floor.GetComponent<BoxCollider>();
        if (box != null) box.size = new Vector3(box.size.x * k, box.size.y, box.size.z * k);
    }

    private static void ScaleSpawnAreas(GameObject variant, float k)
    {
        try
        {
            var comp = variant.GetComponent<WorldVariantSpawnableArea>();
            var areas = comp?.spawnableAreas;
            if (areas == null) return;
            for (int i = 0; i < areas.Count; i++)
            {
                var a = areas[i];
                if (a == null) continue;
                a.Center = a.Center * k;
                a.Size = a.Size * k;
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Spawn areas: {ex.Message}"); }
    }

    /// <summary>A grouping object (no mesh or collider of its own) whose children are spread around the map.</summary>
    private static bool IsContainer(Transform t)
    {
        int n = t.childCount;
        if (n == 0) return false;
        if (t.GetComponent<Renderer>() != null || t.GetComponent<Collider>() != null || t.GetComponent<NetworkObject>() != null || t.GetComponent<LODGroup>() != null)
            return false;
        // Only when its children are spread around the map; a tight cluster (a rock made of parts, a wall) moves as one.
        float far = 0f;
        for (int i = 0; i < n; i++)
        {
            var p = t.GetChild(i).localPosition;
            far = Mathf.Max(far, new Vector2(p.x, p.z).magnitude);
        }
        return far > 8f;
    }

    private static int Walk(Transform root, Transform container, float k)
    {
        int moved = 0;
        var leaves = new List<Transform>();
        var children = new List<Transform>();
        for (int i = 0; i < container.childCount; i++) children.Add(container.GetChild(i));

        foreach (var c in children)
        {
            if (!c.gameObject.activeSelf) continue;
            if (IsContainer(c)) moved += Walk(root, c, k);
            else { Place(root, c, k); leaves.Add(c); moved++; }
        }

        if (leaves.Count == 0) return moved;
        // A ring: most of its pieces sit far out (the border, the scenery band behind it). Inner stragglers are
        // spread but not copied.
        var outer = leaves.FindAll(l => Radius(root, l) / k >= RingMinRadius);
        bool ring = outer.Count >= 8 && outer.Count >= leaves.Count * 0.7f;
        if (ring) Wall.Offer(root, container, outer);
        if (!IsScenery(container, leaves)) return moved; // spawn points, props and the like are never copied
        if (Plugin.FillGaps.Value && ring) moved += FillRing(root, outer, k);
        else if (Plugin.FillGrass.Value && leaves.Count >= DenseCount && !HasColliders(leaves)) moved += Thicken(root, leaves, k);
        return moved;
    }

    private static readonly string[] NotScenery = { "Spawn", "Point", "Portal", "Breakable", "Objective", "Artifact", "Zone", "Hole", "Geyser", "Tornado", "Shooter", "Dead" };

    /// <summary>Only plain scenery (meshes, colliders) may be copied: no gameplay objects or scripts.</summary>
    private static bool IsScenery(Transform container, List<Transform> leaves)
    {
        foreach (var w in NotScenery)
            if (container.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        for (int i = 0; i < leaves.Count && i < 30; i++)
        {
            var l = leaves[i];
            if (l.GetComponentInChildren<Renderer>(true) == null) return false;
            foreach (var mb in l.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && !mb.GetIl2CppType().FullName.StartsWith("UnityEngine.", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static float Radius(Transform root, Transform t)
    {
        var lp = root.InverseTransformPoint(t.position);
        return new Vector2(lp.x, lp.z).magnitude;
    }

    private static void Place(Transform root, Transform t, float k)
    {
        if (t.name == "SkyboxRotator") return;
        var lp = root.InverseTransformPoint(t.position);
        float r = new Vector2(lp.x, lp.z).magnitude;
        if (r < 3f && Extent(t) > 25f)
        {
            // A big mesh in the middle (a realm's ground or a ring in one piece): stretch it.
            var s = t.localScale;
            t.localScale = new Vector3(s.x * k, s.y, s.z * k);
        }
        else
        {
            t.position = root.TransformPoint(new Vector3(lp.x * k, lp.y, lp.z * k));
        }
        ReplaceBatched(t);
    }

    private static float Extent(Transform t)
    {
        float e = 0f;
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            var b = r.bounds.extents;
            e = Mathf.Max(e, Mathf.Max(b.x, b.z));
        }
        return e;
    }

    private static bool HasColliders(List<Transform> leaves)
    {
        for (int i = 0; i < leaves.Count && i < 30; i++)
            if (leaves[i].GetComponentInChildren<Collider>(true) != null) return true;
        return false;
    }

    // Rotated copies of a ring, each turned by a fraction of the spacing between its pieces.
    private static int FillRing(Transform root, List<Transform> leaves, float k)
    {
        int copies = Mathf.CeilToInt(k) - 1;
        if (copies <= 0) copies = 1;
        float spacing = 360f / leaves.Count;
        int made = 0;
        for (int j = 1; j <= copies; j++)
        {
            float angle = spacing * j / (copies + 1);
            foreach (var l in leaves) { Copy(root, l, angle); made++; }
        }
        return made;
    }

    // Extra copies of grass-like fields so they don't look bare after spreading.
    private static int Thicken(Transform root, List<Transform> leaves, float k)
    {
        int copies = Mathf.Clamp(Mathf.RoundToInt(k * k) - 1, 0, Plugin.MaxGrassCopies.Value);
        int made = 0;
        for (int j = 1; j <= copies; j++)
        {
            float angle = 137.5f * j; // golden angle: copies don't line up with the original
            foreach (var l in leaves) { Copy(root, l, angle); made++; }
        }
        return made;
    }

    private static void Copy(Transform root, Transform src, float angleDeg)
    {
        if (src.GetComponent<NetworkObject>() != null || src.GetComponentInChildren<NetworkObject>(true) != null) return;
        var go = UObject.Instantiate(src.gameObject, src.parent);
        go.name = src.name + " (BiggerMap)";
        var t = go.transform;
        var up = root.up;
        var centre = root.position;
        var rot = Quaternion.AngleAxis(angleDeg, up);
        t.position = centre + rot * (src.position - centre);
        t.rotation = rot * src.rotation;
    }

    // ---------- statically batched meshes ----------

    private static string Stem(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = name.Replace("(Clone)", "").Trim();
        int p = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (p > 0 && name.EndsWith(")")) name = name.Substring(0, p);
        return name.Trim();
    }

    private static string Key(Transform t) => Stem(t.name) + "|" + (t.parent != null ? Stem(t.parent.name) : "");

    private static bool IsBatched(MeshFilter mf)
    {
        try { var m = mf.sharedMesh; return m != null && m.name.Contains("Combined Mesh"); }
        catch { return false; }
    }

    // LODGroup.GetLODs isn't available here; LOD levels are named "..._LOD0", "..._LOD1"...
    private static bool IsLod0(Renderer r)
    {
        string n = r.name;
        int i = n.LastIndexOf("LOD", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 3 >= n.Length || !char.IsDigit(n[i + 3])) return true;
        return n[i + 3] == '0';
    }

    private static void CollectDonors(Transform managerRoot)
    {
        donors.Clear();
        foreach (var mf in managerRoot.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || IsBatched(mf) || mf.sharedMesh == null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            string key = Key(mf.transform);
            if (!donors.ContainsKey(key)) donors[key] = (mf.sharedMesh, mr.sharedMaterials);
        }
    }

    private static void ReplaceBatched(Transform t)
    {
        foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || !IsBatched(mf)) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled) continue;
            mr.enabled = false; // the merged mesh stays where the game put it; hide it
            if (!IsLod0(mr)) continue; // only one level of detail gets a replacement
            if (!donors.TryGetValue(Key(mf.transform), out var d)) { missingDonors++; continue; }

            var go = new GameObject(mf.name + " (BiggerMap mesh)");
            go.layer = mf.gameObject.layer;
            var gt = go.transform;
            gt.SetParent(mf.transform, false); // follows the object it replaces
            gt.localPosition = Vector3.zero;
            gt.localRotation = Quaternion.identity;
            gt.localScale = Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = d.mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = d.mats;
            r.shadowCastingMode = mr.shadowCastingMode;
            try { r.lightmapIndex = mr.lightmapIndex; r.lightmapScaleOffset = mr.lightmapScaleOffset; } catch { }
            r.receiveShadows = mr.receiveShadows;
            standIns++;
        }
    }

    // ---------- camera ----------

    private static readonly Dictionary<IntPtr, Vector3> confinerOriginal = new();

    private static void ScaleConfiners(Transform root, float k)
    {
        try
        {
            foreach (var c in UObject.FindObjectsOfType<Cinemachine.CinemachineConfiner>())
            {
                var vol = c.m_BoundingVolume;
                if (vol == null) continue;
                var box = vol.TryCast<BoxCollider>();
                // Remember the size the game gave it, so a confiner that outlives the realm isn't widened twice.
                Vector3 now = box != null ? box.size : vol.transform.localScale;
                if (!confinerOriginal.TryGetValue(vol.Pointer, out var orig)) { orig = now; confinerOriginal[vol.Pointer] = orig; }
                var wide = new Vector3(orig.x * k, orig.y, orig.z * k);
                if (box != null) box.size = wide; else vol.transform.localScale = wide;
                Plugin.Log.LogInfo($"Widened camera bounds {vol.name}.");
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Camera bounds: {ex.Message}"); }
    }

    // ---------- navigation ----------

    internal static void BuildNavMesh(Transform variant, float k)
    {
        try
        {
            Physics.SyncTransforms();
            var surfaces = new List<NavMeshSurface>();
            foreach (var s in UObject.FindObjectsOfType<NavMeshSurface>()) if (s != null) surfaces.Add(s);
            foreach (var s in variant.GetComponentsInChildren<NavMeshSurface>(true))
                if (s != null && !surfaces.Exists(x => x.Pointer == s.Pointer)) surfaces.Add(s);
            if (surfaces.Count == 0)
            {
                Plugin.Log.LogWarning("No navigation surfaces found: enemies may not reach the new outer area.");
                return;
            }
            foreach (var s in surfaces)
            {
                var sw = Stopwatch.StartNew();
                if (Wall.Built)
                {
                    // Only build inside the invisible edge (plus a little), not over the whole stretched ground.
                    var t = s.transform;
                    var ls = t.lossyScale;
                    float w = 2f * Wall.Radius + 12f;
                    var localCentre = t.InverseTransformPoint(Wall.Centre);
                    s.collectObjects = CollectObjects.Volume;
                    s.center = new Vector3(localCentre.x, 0f, localCentre.z);
                    s.size = new Vector3(w / Mathf.Max(0.01f, Mathf.Abs(ls.x)), 80f / Mathf.Max(0.01f, Mathf.Abs(ls.y)), w / Mathf.Max(0.01f, Mathf.Abs(ls.z)));
                }
                else if (s.collectObjects == CollectObjects.Volume)
                {
                    var z = s.size; var c = s.center;
                    s.size = new Vector3(z.x * k + 10f, z.y, z.z * k + 10f);
                    s.center = new Vector3(c.x * k, c.y, c.z * k);
                }
                // The floor is an invisible box collider and many meshes aren't readable at runtime: build from colliders.
                s.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
                s.BuildNavMesh();
                Plugin.Log.LogInfo($"Rebuilt navigation for {s.name} ({s.collectObjects}) in {sw.ElapsedMilliseconds} ms.");
            }
        }
        catch (Exception ex) { Plugin.Log.LogError($"Rebuilding navigation: {ex}"); }
    }
}

/// <summary>Runs the navigation rebuild one frame after the layout changed (once the moved colliders are in place),
/// and ticks the host-settings sync.</summary>
public class Runner : MonoBehaviour
{
    public Runner(IntPtr ptr) : base(ptr) { }

    private static WorldVariantManager waiting;

    internal static void WaitForHost(WorldVariantManager m) => waiting = m;

    private static Transform pendingVariant;
    private static float pendingScale;
    private static int frames;

    internal static void RequestNavMesh(Transform variant, float k)
    {
        pendingVariant = variant;
        pendingScale = k;
        frames = 1;
    }

    private void Update()
    {
        Sync.Tick();
        if (waiting is not null)
        {
            if (waiting.WasCollected || waiting == null) waiting = null;
            else if (!Sync.WaitingForHost) { var m = waiting; waiting = null; MapScaler.Apply(m); }
        }
        if (pendingVariant is null) return;
        if (frames-- > 0) return;
        var v = pendingVariant;
        pendingVariant = null;
        if (v.WasCollected || v == null) return;
        MapScaler.BuildNavMesh(v, pendingScale);
    }
}
