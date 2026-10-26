using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace BiggerMap;

/// <summary>
/// Everything the game spawns on the ground (wave and event enemies, objective items such as the fungi and the
/// statues, pickups) lands on the arena floor: inside the invisible edge, and not on top of rocks.
///
/// All of these go through NetworkSpawnManager.InstantiateAndSpawn on the host, so that's the one place to check.
/// Several of the game's events place their enemies by distance alone (the mob, the converging circle, the line),
/// and in the bigger arena some of those spots are outside the edge or on a rock. Rock tops are walkable patches of
/// their own in the navigation, cut off from the floor, so a spot counts as floor only if a path leads from it to
/// the middle of the arena. A spot that isn't is moved to the nearest one that is.
/// </summary>
[HarmonyPatch]
internal static class Floor
{
    private enum Kind { Unchecked, Ground, Other }

    private static readonly Dictionary<IntPtr, Kind> kinds = new();
    private static readonly Dictionary<long, bool> connected = new();
    private static Vector3 middle;
    private static bool haveMiddle;
    private static IntPtr stamp;
    private static int moved;
    private static float nextReport;

    private static bool Active()
    {
        if (Sync.AppliedScale <= 1.01f || !Wall.Built) return false;
        // A new arena (a new run, a rebuilt navigation): forget what we knew about the old one.
        var h = Wall.Holder;
        var p = h is null ? IntPtr.Zero : h.Pointer;
        if (p != stamp) { stamp = p; connected.Clear(); haveMiddle = false; }
        return true;
    }

    [HarmonyPatch(typeof(NetworkSpawnManager), nameof(NetworkSpawnManager.InstantiateAndSpawn))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Spawn_Prefix(NetworkObject networkPrefab, bool isPlayerObject, ref Vector3 position)
    {
        try
        {
            if (isPlayerObject || networkPrefab == null || !Active()) return;
            if (KindOf(networkPrefab) != Kind.Ground) return;
            var before = position;
            position = Spot(position);
            if ((position - before).sqrMagnitude > 1f)
            {
                moved++;
                if (Time.unscaledTime >= nextReport)
                {
                    nextReport = Time.unscaledTime + 30f;
                    Plugin.Log.LogInfo($"Moved {moved} spawn(s) off rocks or back inside the edge so far (latest: {networkPrefab.name}).");
                }
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Spawn spot: {ex.Message}"); }
    }

    /// <summary>Things that live on the ground. Projectiles, attack effects, pings and the like are left alone.</summary>
    private static Kind KindOf(NetworkObject prefab)
    {
        var key = prefab.Pointer;
        if (kinds.TryGetValue(key, out var k)) return k;
        k = Kind.Other;
        try
        {
            var go = prefab.gameObject;
            if (go.GetComponent<Enemy>() != null
                || go.GetComponent<HerdingTarget>() != null
                || go.GetComponent<PetrifiedEnemy>() != null
                || go.GetComponent<Collectable>() != null
                || go.GetComponent<PushObjectivePushable>() != null
                || go.GetComponent<CapturePoint>() != null
                || go.GetComponent<HealthPickup>() != null
                || go.GetComponent<MagnetPickup>() != null
                || go.GetComponent<ArtifactChestWithPickup>() != null
                || go.GetComponent(Il2CppType.Of<IPickup>()) != null)
                k = Kind.Ground;
        }
        catch { }
        kinds[key] = k;
        return k;
    }

    /// <summary>The spot itself if it's on the arena floor, else the nearest floor spot: around it first, then
    /// towards the middle.</summary>
    internal static Vector3 Spot(Vector3 p)
    {
        var q = Wall.Inside(p, 1.5f) ? p : Wall.Clamp(p, 3f);
        if (OnFloor(q, out var f)) return f;
        // Search at floor height: a spot on a tall rock is further above the floor than the navigation sample reaches.
        if (EnsureMiddle()) q.y = middle.y;
        if (OnFloor(q, out f)) return f;
        for (int ring = 1; ring <= 3; ring++)
            for (int a = 0; a < 8; a++)
            {
                float ang = (a + 0.5f * (ring & 1)) * Mathf.PI / 4f;
                var c = q + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (ring * 2.5f);
                if (Wall.Inside(c, 1.5f) && OnFloor(c, out f)) return f;
            }
        var toMiddle = Wall.Centre - q; toMiddle.y = 0f;
        float dist = toMiddle.magnitude;
        if (dist > 0.1f)
        {
            var dir = toMiddle / dist;
            for (float s = 2f; s < dist; s += 2f)
                if (OnFloor(q + dir * s, out f)) return f;
        }
        return q;
    }

    private static bool OnFloor(Vector3 p, out Vector3 floor)
    {
        floor = p;
        if (!NavMesh.SamplePosition(p, out var hit, 3f, NavMesh.AllAreas)) return false;
        if (!Wall.Inside(hit.position, 1f)) return false;
        floor = hit.position;
        return Connected(hit.position);
    }

    /// <summary>Is there a path from here to the middle of the arena? (Remembered per 2 m cell.)</summary>
    private static bool Connected(Vector3 p)
    {
        long key = ((long)Mathf.RoundToInt(p.x / 2f) & 0xFFFFF) | (((long)Mathf.RoundToInt(p.z / 2f) & 0xFFFFF) << 20) | (((long)Mathf.RoundToInt(p.y / 1.5f) & 0xFFFFF) << 40);
        if (connected.TryGetValue(key, out var ok)) return ok;
        if (!EnsureMiddle()) return true; // can't tell: don't move things
        // Without a path check (see PathToMiddle), a rock top is told apart by height alone.
        if (!PathToMiddle(p, out ok)) ok = p.y <= middle.y + 1.5f;
        connected[key] = ok;
        return ok;
    }

    private static bool EnsureMiddle()
    {
        if (haveMiddle) return true;
        if (!NavMesh.SamplePosition(Wall.Centre, out var mid, 25f, NavMesh.AllAreas)) return false;
        middle = mid.position;
        haveMiddle = true;
        return true;
    }

    // ---------- spell cores pushed up onto rocks ----------
    // The cores of the push objective roll on physics: a hard shove into a rock can leave one on top, where nobody
    // can reach it to push it on. Whoever's game moves the core (its owner) brings it back down to the nearest floor
    // spot once it has sat up there for a few seconds.

    private static readonly List<PushObjectivePushable> cores = new();
    private static readonly Dictionary<IntPtr, float> strandedSince = new();
    private static float nextRescue;

    [HarmonyPatch(typeof(PushObjectivePushable), nameof(PushObjectivePushable.OnNetworkSpawn))]
    [HarmonyPostfix]
    private static void Core_Spawn(PushObjectivePushable __instance)
    {
        try { if (__instance != null && !cores.Exists(c => c.Pointer == __instance.Pointer)) cores.Add(__instance); } catch { }
    }

    /// <summary>Every frame (cheap: looks once a second while there are cores).</summary>
    internal static void Tick()
    {
        if (cores.Count == 0 || Time.unscaledTime < nextRescue) return;
        nextRescue = Time.unscaledTime + 1f;
        bool active = Active();
        for (int i = cores.Count - 1; i >= 0; i--)
        {
            var c = cores[i];
            try
            {
                if (c is null || c.WasCollected || c == null || !c.IsSpawned) { cores.RemoveAt(i); continue; }
                if (!active || !c.IsOwner) { strandedSince.Remove(c.Pointer); continue; }
                var pos = c.transform.position;
                if (!Stranded(pos)) { strandedSince.Remove(c.Pointer); continue; }
                if (!strandedSince.TryGetValue(c.Pointer, out var since)) { strandedSince[c.Pointer] = Time.unscaledTime; continue; }
                if (Time.unscaledTime - since < 3f) continue;
                strandedSince.Remove(c.Pointer);
                var spot = Spot(pos) + Vector3.up * 0.8f;
                var rb = c.rigidBody;
                if (rb != null)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.position = spot;
                }
                c.transform.position = spot;
                Plugin.Log.LogInfo($"Brought a spell core down from the rocks ({pos.x:0}, {pos.y:0.#}, {pos.z:0}) -> ({spot.x:0}, {spot.y:0.#}, {spot.z:0}).");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Spell core check: {ex.Message}"); cores.RemoveAt(i); }
        }
    }

    // Up on a rock or outside the edge: no floor close below it, or the floor under it is cut off from the arena.
    private static bool Stranded(Vector3 pos)
    {
        if (!Wall.Inside(pos, 0.5f)) return true;
        if (!NavMesh.SamplePosition(pos, out var hit, 3f, NavMesh.AllAreas)) return true;
        if (pos.y - hit.position.y > 2.5f) return true;
        return !Connected(hit.position);
    }

    // The game build has no NavMeshPath constructor (stripped), so the path object is made and queried through the
    // engine's own calls.
    private static IntPtr nativePath;
    private static bool pathBroken;

    private static bool PathToMiddle(Vector3 from, out bool complete)
    {
        complete = false;
        if (pathBroken) return false;
        try
        {
            if (nativePath == IntPtr.Zero) nativePath = NavMeshPath.InitializeNavMeshPath();
            if (nativePath == IntPtr.Zero) { pathBroken = true; return false; }
            var a = from;
            var b = middle;
            bool found = NavMesh.CalculatePathInternal_Injected(ref a, ref b, NavMesh.AllAreas, nativePath);
            complete = found && NavMeshPath.get_status_Injected(nativePath) == NavMeshPathStatus.PathComplete;
            return true;
        }
        catch (Exception ex)
        {
            pathBroken = true;
            Plugin.Log.LogWarning($"Path check unavailable ({ex.Message}); telling rock tops apart by height instead.");
            return false;
        }
    }
}
