using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace BiggerMap;

/// <summary>
/// Keeps the game's own spawning and events working in the bigger arena:
/// - wave enemies (and anything else spawned through the enemy spawner) never appear outside the invisible edge;
/// - the line event (the row of dogs crossing the map) and the mob event are sized for the bigger map;
/// - timed objectives (herding the fungi, the statues, capture zones...) get more time, as everything in them is
///   farther apart.
/// </summary>
[HarmonyPatch]
internal static class Arena
{
    private static bool Active(out float k)
    {
        k = Sync.AppliedScale;
        return k > 1.01f && Wall.Built;
    }

    // ---------- enemies ----------

    [HarmonyPatch(typeof(EnemySpawner), nameof(EnemySpawner.SpawnEnemy))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void SpawnEnemy_Prefix(ref Vector3 position)
    {
        try { if (Active(out _)) position = Floor.Spot(position); } catch { }
    }

    // ---------- the line event (a row of enemies sweeping across the map) and the mob event ----------
    // Before 0.9.11 these hooked LineEnemyEventStarter/MobEnemyEventStarter.StartEvent(CancellationToken) and
    // CalculateIntersectionPoints. The modding layer can't pass a CancellationToken (a small struct the game passes
    // in a register) through a hook correctly, so the host crashed the moment either event started
    // (AccessViolationException in StartEvent); the other hook returns a 24-byte struct, which is just as fragile.
    // Now: the line is stretched where its spawn positions are worked out (only object and Vector3 arguments), and
    // the events' spawn distances are set on their config assets directly, with no hook at all.

    // The line is sized for a 100 x 100 m map (constants in the game): stretch it about its middle.
    [HarmonyPatch(typeof(LineEnemyEventStarter), nameof(LineEnemyEventStarter.GetEnemySpawnPositions))]
    [HarmonyPrefix]
    private static void LinePositions_Prefix(ref Vector3 lineStartPoint, ref Vector3 lineEndPoint)
    {
        try
        {
            if (!Active(out float k)) return;
            var mid = (lineStartPoint + lineEndPoint) * 0.5f;
            lineStartPoint = mid + (lineStartPoint - mid) * k;
            lineEndPoint = mid + (lineEndPoint - mid) * k;
        }
        catch { }
    }

    private static readonly Dictionary<IntPtr, float> lineDistance = new(), mobDistance = new();
    private static float configsAppliedFor = 1f, nextConfigLook, lookingFor = -1f;
    private static int looks;

    /// <summary>Every frame (cheap): keeps the events' spawn distances in step with the map size. The configs are
    /// found with a scene-wide search, so that only runs when the size changes (a few tries while the run loads).</summary>
    internal static void Tick()
    {
        float k = Active(out float kk) ? kk : 1f;
        if (Mathf.Abs(k - configsAppliedFor) < 0.001f) return;
        if (Mathf.Abs(k - lookingFor) > 0.001f) { lookingFor = k; looks = 0; nextConfigLook = 0f; }
        if (looks >= 5 || Time.unscaledTime < nextConfigLook) return;
        looks++;
        nextConfigLook = Time.unscaledTime + 4f;
        int found = 0;
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LineEnemyEventStarterConfiguration>()))
            {
                var c = o?.TryCast<LineEnemyEventStarterConfiguration>();
                if (c == null) continue;
                if (!lineDistance.TryGetValue(c.Pointer, out var orig)) lineDistance[c.Pointer] = orig = c.SpawnDistanceFromCenter;
                c.SpawnDistanceFromCenter = orig * k;
                found++;
            }
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MobEnemyEventStarterConfiguration>()))
            {
                var c = o?.TryCast<MobEnemyEventStarterConfiguration>();
                if (c == null) continue;
                if (!mobDistance.TryGetValue(c.Pointer, out var orig)) mobDistance[c.Pointer] = orig = c.SpawnDistanceFromCenter;
                c.SpawnDistanceFromCenter = orig * k;
                found++;
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Event sizes: {ex.Message}"); return; }
        if (found < 2 && looks < 5) return; // not loaded yet (they load with the run): look again in a few seconds
        configsAppliedFor = k;
        Plugin.Log.LogInfo($"Line and mob events sized for the map (x{k:0.##}).");
    }

    // ---------- objective time ----------
    // Hooked on the objective director's "objective started" (no scene searches: those cost a frame hitch).

    [HarmonyPatch(typeof(ObjectiveDirector), nameof(ObjectiveDirector.NotifyObjectiveStarted))]
    [HarmonyPostfix]
    private static void ObjectiveStarted(IAmObjective objective)
    {
        try
        {
            if (objective == null || !Active(out float k)) return;
            float factor = 1f + (k - 1f) * Mathf.Max(0f, Plugin.ObjectiveExtraTime.Value);
            if (factor <= 1.001f) return;
            var timed = objective.TryCast<IAmTimedObjective>();
            var t = timed?.GetTimeLimitTimer();
            if (t == null) return;
            float before = t.timeLimitInSeconds;
            if (before <= 0f) return;
            t.timeLimitInSeconds = before * factor;
            Plugin.Log.LogInfo($"Objective time limit: {before:0} s -> {before * factor:0} s for the bigger map.");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Objective time: {ex.Message}"); }
    }

}
