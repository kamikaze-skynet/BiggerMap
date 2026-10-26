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
        if (!haveMiddle)
        {
            if (!NavMesh.SamplePosition(Wall.Centre, out var mid, 25f, NavMesh.AllAreas)) return true; // can't tell: don't move things
            middle = mid.position;
            haveMiddle = true;
        }
        // Without a path check (see PathToMiddle), a rock top is told apart by height alone.
        if (!PathToMiddle(p, out ok)) ok = p.y <= middle.y + 1.5f;
        connected[key] = ok;
        return ok;
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
