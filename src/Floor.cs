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

    [HarmonyPatch(typeof(NetworkSpawnManager), nameof(NetworkSpawnManager.InstantiateAndSpawn))]
    [HarmonyPostfix]
    private static void Spawn_Postfix(NetworkObject networkPrefab, bool isPlayerObject, NetworkObject __result)
    {
        try
        {
            if (isPlayerObject || networkPrefab == null || __result == null || !Active()) return;
            if (KindOf(networkPrefab) == Kind.Ground) Keep(__result);
        }
        catch { }
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
            if (go.GetComponentInChildren<Enemy>(true) != null
                || go.GetComponentInChildren<EnemyNavigationController>(true) != null
                || go.GetComponentInChildren<HerdingTarget>(true) != null
                || go.GetComponentInChildren<PetrifiedEnemy>(true) != null
                || go.GetComponentInChildren<Collectable>(true) != null
                || go.GetComponentInChildren<PushObjectivePushable>(true) != null
                || go.GetComponentInChildren<CapturePoint>(true) != null
                || go.GetComponentInChildren<HealthPickup>(true) != null
                || go.GetComponentInChildren<MagnetPickup>(true) != null
                || go.GetComponentInChildren<ArtifactChestWithPickup>(true) != null
                || go.GetComponentInChildren(Il2CppType.Of<IPickup>(), true) != null)
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

    // ---------- keeping things on the floor after they've spawned ----------
    // A spot can still go wrong later: physics can throw a spell core onto a rock, a knock-back can push an enemy
    // where it can't walk back from, a pickup can drop where an enemy died. Everything spawned on the ground is
    // watched by whichever game moves it (the host, or a player who's pushing a core), and anything that has been up
    // on a rock or past the edge for a few seconds is put back on the nearest floor spot.

    private sealed class Kept
    {
        public NetworkObject Obj;
        public EnemyNavigationController Nav;
        public int Strikes;
    }

    private static readonly List<Kept> kept = new();
    private static readonly HashSet<IntPtr> keptPtrs = new();
    private static int cursor;
    private static float nextKeep;

    private static void Keep(NetworkObject o)
    {
        if (o == null || !keptPtrs.Add(o.Pointer)) return;
        EnemyNavigationController nav = null;
        try { nav = o.GetComponentInChildren<EnemyNavigationController>(true); } catch { }
        kept.Add(new Kept { Obj = o, Nav = nav });
    }

    // Players' games don't see the host's spawns, but a player can be the one moving a spell core.
    [HarmonyPatch(typeof(PushObjectivePushable), nameof(PushObjectivePushable.OnNetworkSpawn))]
    [HarmonyPostfix]
    private static void Core_Spawn(PushObjectivePushable __instance)
    {
        try { if (__instance != null) Keep(__instance.NetworkObject); } catch { }
    }

    /// <summary>Every frame (cheap): a few tracked things are looked at every quarter of a second.</summary>
    internal static void Tick()
    {
        if (kept.Count == 0 || Time.unscaledTime < nextKeep) return;
        nextKeep = Time.unscaledTime + 0.25f;
        bool active = Active();
        int budget = Math.Min(kept.Count, 40);
        for (int n = 0; n < budget && kept.Count > 0; n++)
        {
            if (cursor >= kept.Count) cursor = 0;
            var k = kept[cursor];
            try
            {
                var o = k.Obj;
                if (o is null || o.WasCollected || o == null || !o.IsSpawned) { keptPtrs.Remove(k.Obj?.Pointer ?? IntPtr.Zero); kept.RemoveAt(cursor); continue; }
                cursor++;
                if (!active || !o.IsOwner) { k.Strikes = 0; continue; }
                Vector3 pos = o.transform.position;
                if (k.Nav != null && !k.Nav.WasCollected) try { pos = k.Nav.GetNavigatorPosition(); } catch { }
                if (!Stranded(pos)) { k.Strikes = 0; continue; }
                // Seen up there three times in a row (a second or more) before anything is moved: things fly through
                // the air for a moment when knocked about.
                if (++k.Strikes < 3) continue;
                k.Strikes = 0;
                Bring(k, pos);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Keeping things on the floor: {ex.Message}"); keptPtrs.Remove(k.Obj?.Pointer ?? IntPtr.Zero); kept.Remove(k); }
        }
    }

    private static int brought;
    private static float nextBroughtLog;

    private static void Bring(Kept k, Vector3 from)
    {
        var o = k.Obj;
        var spot = Spot(from);
        if ((spot - from).sqrMagnitude < 0.25f) return; // nowhere better found
        bool moved = false;
        // Enemies walk with a separate navigation agent; their body follows it.
        if (k.Nav != null && !k.Nav.WasCollected)
        {
            try
            {
                var t = k.Nav.GetNavigator()?.GetTransform();
                var agent = t != null ? t.GetComponent<NavMeshAgent>() : null;
                if (agent != null) { agent.Warp(spot); moved = true; }
            }
            catch { }
        }
        else
        {
            try { var agent = o.GetComponentInChildren<NavMeshAgent>(); if (agent != null && agent.enabled) { agent.Warp(spot); moved = true; } } catch { }
        }
        var lift = o.GetComponentInChildren<PushObjectivePushable>() != null ? 0.8f : 0f;
        try
        {
            var rb = o.GetComponentInChildren<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = spot + Vector3.up * lift;
            }
        }
        catch { }
        o.transform.position = spot + Vector3.up * lift;
        brought++;
        if (brought <= 10 || Time.unscaledTime >= nextBroughtLog)
        {
            nextBroughtLog = Time.unscaledTime + 30f;
            Plugin.Log.LogInfo($"Brought {o.name} back onto the floor ({from.x:0}, {from.y:0.#}, {from.z:0}) -> ({spot.x:0}, {spot.y:0.#}, {spot.z:0}){(moved ? "" : " (no agent)")}. {brought} so far.");
        }
    }

    // Up on a rock or outside the edge: no floor close below it, or the floor under it is cut off from the arena.
    private static bool Stranded(Vector3 pos)
    {
        if (!Wall.Inside(pos, -1f)) return true; // pressed against the edge is fine; past it isn't
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
