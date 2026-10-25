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
        try { if (Active(out _) && !Wall.Inside(position, 2f)) position = Wall.Clamp(position, 3f); } catch { }
    }

    // ---------- the line event (a row of enemies sweeping across the map) ----------

    // The line is sized for a 100 x 100 m map (constants in the game): make it as wide as the bigger one.
    [HarmonyPatch(typeof(LineEnemyEventStarter), nameof(LineEnemyEventStarter.CalculateIntersectionPoints))]
    [HarmonyPrefix]
    private static void LineSize_Prefix(ref float width, ref float height)
    {
        if (!Active(out float k)) return;
        width *= k;
        height *= k;
    }

    private static readonly Dictionary<IntPtr, float> lineDistance = new(), mobDistance = new();

    [HarmonyPatch(typeof(LineEnemyEventStarter), nameof(LineEnemyEventStarter.StartEvent))]
    [HarmonyPrefix]
    private static void LineStart_Prefix(LineEnemyEventStarter __instance)
    {
        try
        {
            var c = __instance.config;
            if (c == null) return;
            if (!lineDistance.TryGetValue(c.Pointer, out var orig)) lineDistance[c.Pointer] = orig = c.SpawnDistanceFromCenter;
            c.SpawnDistanceFromCenter = orig * (Active(out float k) ? k : 1f);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Line event size: {ex.Message}"); }
    }

    [HarmonyPatch(typeof(MobEnemyEventStarter), nameof(MobEnemyEventStarter.StartEvent))]
    [HarmonyPrefix]
    private static void MobStart_Prefix(MobEnemyEventStarter __instance)
    {
        try
        {
            var c = __instance.config;
            if (c == null) return;
            if (!mobDistance.TryGetValue(c.Pointer, out var orig)) mobDistance[c.Pointer] = orig = c.SpawnDistanceFromCenter;
            c.SpawnDistanceFromCenter = orig * (Active(out float k) ? k : 1f);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Mob event size: {ex.Message}"); }
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

    internal static void Tick() { }
}
