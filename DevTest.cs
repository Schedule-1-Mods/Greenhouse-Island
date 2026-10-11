#if DEVTEST
using System.Collections.Generic;
using System.IO;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using MelonLoader.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PelicanKey
{
    // TEST BUILDS ONLY (the DEVTEST symbol; never in a release): lets a test script move the
    // player around the island and measure frame times without keyboard input. Commands are
    // written, one per file, to UserData/PelicanKey_cmd.txt:
    //   tp <x> <y> <z> <yaw>   put the player at that island-local spot, facing that yaw (deg)
    //   fps <seconds>          log the frame-time statistics over that many seconds
    //   say <text>             write a marker line in the log
    internal static class DevTest
    {
        private static float _next;
        private static float _fpsUntil;
        private static string _fpsLabel;
        private static readonly List<float> _frames = new List<float>();

        private static Dictionary<string, List<Renderer>> _groups;
        private static readonly Dictionary<string, List<Light>> _lightGroups = new Dictionary<string, List<Light>>();

        // vis <glass|plant|trees|merged|all|lights|shadows|rest> <0|1>
        private static void SetVisible(string what, bool on)
        {
            var root = IslandBuilder.Root;
            if (_groups == null)
            {
                _groups = new Dictionary<string, List<Renderer>> { ["glass"] = new List<Renderer>(), ["plant"] = new List<Renderer>(), ["trees"] = new List<Renderer>(), ["merged"] = new List<Renderer>(), ["all"] = new List<Renderer>(), ["rest"] = new List<Renderer>() };
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    _groups["all"].Add(r);
                    bool tree = false, plant = false, merged = false;
                    for (var t = r.transform; t != null && t != root; t = t.parent) { if (t.name.StartsWith("GameTree")) tree = true; if (t.name == "WeedPlant") plant = true; }
                    if (r.gameObject.name.StartsWith("Merged")) merged = true;
                    var m = r.sharedMaterial;
                    bool glass = m != null && m.renderQueue >= 3000;
                    if (glass) _groups["glass"].Add(r); else if (tree) _groups["trees"].Add(r); else if (plant) _groups["plant"].Add(r); else if (merged) _groups["merged"].Add(r); else _groups["rest"].Add(r);
                }
                Core.Log.Msg($"[DevTest] groups: glass {_groups["glass"].Count}, trees {_groups["trees"].Count}, plant {_groups["plant"].Count}, merged {_groups["merged"].Count}, rest {_groups["rest"].Count}.");
            }
            if (what == "lights") { foreach (var l in root.GetComponentsInChildren<Light>(true)) if (l != null) l.enabled = on; }
            else if (what == "shadows") { QualitySettings.shadows = on ? ShadowQuality.All : ShadowQuality.Disable; }
            else if (_groups.TryGetValue(what, out var list)) { foreach (var r in list) if (r != null) r.enabled = on; }
            Core.Log.Msg($"[DevTest] {what} {(on ? "on" : "off")}.");
        }

        public static void Tick()
        {
            if (_fpsUntil > 0f)
            {
                _frames.Add(Time.unscaledDeltaTime * 1000f);
                if (Time.unscaledTime >= _fpsUntil)
                {
                    _fpsUntil = 0f;
                    _frames.Sort();
                    float sum = 0f; foreach (var f in _frames) sum += f;
                    int n = _frames.Count;
                    Core.Log.Msg($"[FPS] {_fpsLabel}: {n} frames, avg {sum / n:0.0} ms ({1000f / (sum / n):0} fps), median {_frames[n / 2]:0.0}, p95 {_frames[(int)(n * 0.95f)]:0.0}, p99 {_frames[(int)(n * 0.99f)]:0.0}, max {_frames[n - 1]:0.0} ms.");
                }
            }

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            string path = Path.Combine(MelonEnvironment.UserDataDirectory, "PelicanKey_cmd.txt");
            if (!File.Exists(path)) return;
            string line;
            try { line = File.ReadAllText(path).Trim(); File.Delete(path); } catch { return; }
            var p = line.Split(' ');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                switch (p[0])
                {
                    case "tp":
                    {
                        var island = IslandBuilder.Island;
                        var local = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv));
                        float yaw = float.Parse(p[4], inv);
                        var world = island.TransformPoint(local);
                        var fwd = island.TransformDirection(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward);
                        var rot = Quaternion.LookRotation(fwd, Vector3.up);
                        PlayerSingleton<PlayerMovement>.Instance.Teleport(world, rot);
                        Core.Log.Msg($"[DevTest] teleported to {world} (island {local}, yaw {yaw}).");
                        break;
                    }
                    case "fps":
                        _fpsLabel = p.Length > 2 ? string.Join(" ", p, 2, p.Length - 2) : "";
                        _frames.Clear();
                        _fpsUntil = Time.unscaledTime + float.Parse(p[1], inv);
                        break;
                    case "vis":
                        SetVisible(p[1], p[2] == "1");
                        break;
                    case "emps":
                        foreach (var emp in JungleProperty.Property.Employees)
                        {
                            if (emp == null) continue;
                            string doing = "?";
                            try { var b = emp.Behaviour.activeBehaviour; doing = b != null ? ((Object)b).name : "nothing"; } catch { }
                            var ag = Compat.AgentOf(emp.Movement);
                            Core.Log.Msg($"[Emps] {((Object)emp).name}: {doing}; at {emp.transform.position}; agent {(ag == null ? "none" : (ag.enabled ? "on" : "OFF") + ", navmesh " + ag.isOnNavMesh + ", moving " + (ag.hasPath ? "path" : "-"))}; umbrella {emp.HasUmbrella}.");
                        }
                        break;
                    case "look":
                    {
                        var island = IslandBuilder.Island;
                        var w = island.TransformPoint(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
                        PlayerSingleton<PlayerCamera>.Instance.LookAt(w, 0.05f);
                        break;
                    }
                    case "ray":
                    {
                        var island = IslandBuilder.Island;
                        var from = island.TransformPoint(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
                        int mask = -1;
                        foreach (var bu in Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.Building.BuildUpdate_Grid>()) { mask = bu.detectionMask.value; break; }
                        var names = new List<string>(); for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) names.Add(LayerMask.LayerToName(i));
                        Core.Log.Msg($"[Ray] game placement mask 0x{mask:X}: {string.Join(", ", names)}");
                        foreach (var m in new[] { mask, -1 })
                        {
                            var hits = Physics.RaycastAll(from, Vector3.down, 6f, m, QueryTriggerInteraction.Ignore);
                            var sorted = new List<RaycastHit>(); foreach (var h0 in hits) sorted.Add(h0);
                            sorted.Sort((a, b) => a.distance.CompareTo(b.distance));
                            var parts = new List<string>();
                            foreach (var h in sorted) parts.Add($"{h.collider.name} [{LayerMask.LayerToName(h.collider.gameObject.layer)}] y={h.point.y:0.00} size {h.collider.bounds.size.x:0.0}x{h.collider.bounds.size.y:0.0}x{h.collider.bounds.size.z:0.0}{(h.collider.isTrigger ? " trig" : "")}");
                            Core.Log.Msg($"[Ray] from {from}, mask {(m == -1 ? "ALL" : "game")}: {string.Join(" > ", parts)}");
                        }
                        break;
                    }
                    case "say":
                        Core.Log.Msg("[DevTest] " + string.Join(" ", p, 1, p.Length - 1));
                        break;
                }
            }
            catch (System.Exception e) { Core.Log.Warning("[DevTest] " + line + ": " + e.Message); }
        }
    }
}
#endif
