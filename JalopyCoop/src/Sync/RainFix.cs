using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Chuva com "chiado cortado": o RainCheck do jogo chama o setter Outside em todo FixedUpdate, e com chuva o
    /// setter roda SetLightRainAudio/SetHeavyRainAudio, que desliga e liga as fontes de som (o som recomeça do zero).
    /// O recomeço é evitado no CarAmbienceFix (RainAudioRestartPatch); aqui só o log. Se ficar trocando
    /// dentro/fora várias vezes por segundo, escreve no log o que o raio para cima está acertando.
    /// </summary>
    [HarmonyPatch(typeof(RainCheck), "Outside", MethodType.Setter)]
    internal static class RainCheckOutsidePatch
    {
        private class State { public bool outside; public int rain; public int flips; public float since; }
        private static readonly Dictionary<RainCheck, State> last = new Dictionary<RainCheck, State>();

        /// <summary>
        /// DESLIGADO (2026-11-16): pular as chamadas repetidas fazia a chuva sumir e voltar (o jogo conta com o
        /// setter reaplicando a chuva todo FixedUpdate). O jogo volta a fazer como sempre; este patch só registra.
        /// </summary>
        internal const bool SkipRepeats = false;

        private static bool Prefix(RainCheck __instance, bool value)
        {
            try
            {
                int rain = __instance.route != null ? __instance.route.rainType : 0;
                State s;
                if (!last.TryGetValue(__instance, out s))
                {
                    last[__instance] = new State { outside = value, rain = rain, since = Time.realtimeSinceStartup };
                    if (rain > 0) Plugin.Log.LogInfo("Chuva: começou, " + (value ? "FORA" : "dentro") + "; " + RainCheckCount() + " RainCheck; raio de " + Where(__instance) + " acerta " + Hit(__instance) + ".");
                    return true;
                }
                if (s.outside == value && s.rain == rain) return !SkipRepeats; // nada mudou (com SkipRepeats o som não recomeçaria)
                if (s.outside != value) s.flips++;
                s.outside = value; s.rain = rain;
                float now = Time.realtimeSinceStartup;
                if (rain > 0 && now - lastLog > 2f)
                {
                    lastLog = now;
                    Plugin.Log.LogInfo("Chuva: agora " + (value ? "FORA (som de chuva aberto)" : "dentro") + "; raio de " + Where(__instance) + " acerta " + Hit(__instance) + "; " + CarAmbienceFix.Describe() + ".");
                }
                if (now - s.since >= 5f)
                {
                    if (s.flips >= 6) LogFlips(__instance, s.flips, now - s.since);
                    s.flips = 0; s.since = now;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("RainFix: " + e); }
            return true;
        }

        private static float lastLog = -100f;

        private static string Where(RainCheck rc)
        {
            var t = rc.rayTarget != null ? rc.rayTarget : rc.transform;
            string path = t.name;
            for (var p = t.parent; p != null && path.Length < 120; p = p.parent) path = p.name + "/" + path;
            var cam = Camera.main;
            string d = cam != null ? ", " + Vector3.Distance(cam.transform.position, t.position).ToString("0.0") + " m da câmera" : "";
            return path + d;
        }

        private static string Hit(RainCheck rc)
        {
            if (rc.rayTarget == null) return "(sem rayTarget)";
            RaycastHit h;
            if (Physics.Raycast(new Ray(rc.rayTarget.position, Vector3.up), out h, float.PositiveInfinity))
                return h.collider.name + " (" + h.transform.root.name + ", camada " + LayerMask.LayerToName(h.collider.gameObject.layer) + ", " + h.distance.ToString("0.0") + " m)";
            return "nada";
        }

        private static void LogFlips(RainCheck rc, int flips, float secs)
        {
            Plugin.Log.LogWarning("Chuva: dentro/fora trocou " + flips + "x em " + secs.ToString("0") + " s; " + RainCheckCount() + " RainCheck; o raio acerta " + Hit(rc) + "; " + CarAmbienceFix.Describe() + ".");
        }

        private static int RainCheckCount()
        {
            int n = 0;
            foreach (var kv in last) if (kv.Key != null) n++;
            return n;
        }
    }
}
