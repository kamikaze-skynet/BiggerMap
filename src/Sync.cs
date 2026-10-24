using System;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace BiggerMap;

/// <summary>
/// Everyone must use the same map size, and the map is laid out the moment the realm loads, before the run starts.
/// So the host repeats its settings every few seconds while players are connected (lobby included), and clients use
/// the latest values they received. Without a value from the host, a client uses its own config.
/// </summary>
internal static class Sync
{
    private const string MessageName = "girthy_mods.BiggerMap.Settings";
    private static CustomMessagingManager.HandleNamedMessageDelegate handler; // kept alive for IL2CPP
    private static IntPtr registeredOn;
    private static float nextSend;

    private static bool? hostEnabled;
    private static float? hostScale;
    internal static float AppliedScale;

    internal static bool Enabled => hostEnabled ?? Plugin.Enabled.Value;

    /// <summary>A client that hasn't heard from the host: if the host doesn't have the mod it never will, and then
    /// the map must stay normal.</summary>
    internal static bool WaitingForHost
    {
        get
        {
            try
            {
                var nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening && !nm.IsServer && hostScale == null;
            }
            catch { return false; }
        }
    }
    internal static float Scale => Mathf.Clamp(hostScale ?? Plugin.Scale.Value, 1f, Plugin.MaxScale);

    internal static void Tick()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null)
            {
                if (nm == null || !nm.IsListening) { hostEnabled = null; hostScale = null; registeredOn = IntPtr.Zero; }
                return;
            }
            var cmm = nm.CustomMessagingManager;
            if (!nm.IsServer)
            {
                if (registeredOn != cmm.Pointer)
                {
                    handler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(new Action<ulong, FastBufferReader>(OnReceived));
                    cmm.RegisterNamedMessageHandler(MessageName, handler);
                    registeredOn = cmm.Pointer;
                }
                return;
            }

            if (Time.unscaledTime < nextSend) return;
            nextSend = Time.unscaledTime + 1f;
            string payload = string.Join(";",
                "Enabled=" + Plugin.Enabled.Value,
                "Scale=" + Plugin.Scale.Value.ToString(CultureInfo.InvariantCulture));
            var writer = new FastBufferWriter(Encoding.UTF8.GetByteCount(payload) * 2 + 64, Allocator.Temp, -1);
            try
            {
                writer.WriteValueSafe(payload, false);
                cmm.SendNamedMessageToAll(MessageName, writer, NetworkDelivery.Reliable);
            }
            finally { writer.Dispose(); }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Settings sync: {ex.Message}");
            nextSend = Time.unscaledTime + 30f;
        }
    }

    private static void OnReceived(ulong sender, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out string payload, false);
            bool? en = null; float? sc = null;
            foreach (var part in payload.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq), val = part.Substring(eq + 1);
                if (key == "Enabled" && bool.TryParse(val, out var b)) en = b;
                if (key == "Scale" && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) sc = f;
            }
            bool changed = en != hostEnabled || sc != hostScale;
            hostEnabled = en; hostScale = sc;
            if (changed) Plugin.Log.LogInfo($"Using the host's map size: {(en == false ? "off" : $"x{sc:0.##}")}.");
            if (changed && AppliedScale > 0f && sc != AppliedScale)
                Plugin.Log.LogWarning("The host's map size changed after this realm was laid out; it applies from the next realm.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Settings sync (receive): {ex.Message}"); }
    }
}
