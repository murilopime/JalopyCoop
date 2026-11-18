using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Roteiro "chuva" (-coop-autotest=chuva): acha a causa do som estranho da chuva sem ninguém precisar ouvir.
    ///  1 s  tio senta no passageiro; host senta no volante
    ///  3 s  host força chuva forte na rota 1 e escolhe a rota (igual ao clique)
    /// 8-48 s os dois medem 1x/s: todas as fontes de som de chuva tocando (onde estão, volume, se recomeçaram),
    ///        o "abrigo" do carro (CarLogicC: raio para cima) e o RainCheck, e o que cada raio acerta.
    /// 50 s  fim
    /// </summary>
    public partial class AutoTest
    {
        private class SrcInfo { public float lastTime; public int restarts; }
        private readonly Dictionary<AudioSource, SrcInfo> rainSrc = new Dictionary<AudioSource, SrcInfo>();

        private static bool IsRainClip(AudioSource a)
        {
            if (a == null || a.clip == null) return false;
            string n = a.clip.name.ToLowerInvariant();
            return n.Contains("rain") || n.Contains("chuva") || n.Contains("storm");
        }

        private static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null && p.Length < 90; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        private static string RayUp(Vector3 origin, float max)
        {
            RaycastHit h;
            if (!Physics.Raycast(origin, Vector3.up, out h, max)) return "nada";
            return PathOf(h.transform) + " [" + LayerMask.LayerToName(h.collider.gameObject.layer) + ", " + h.distance.ToString("0.0") + " m]";
        }

        /// <summary>Amostra rápida (20x/s): conta quantas vezes cada som de chuva recomeçou.</summary>
        private IEnumerator RainRestartCounter(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end)
            {
                foreach (var a in FindObjectsOfType<AudioSource>())
                {
                    if (!IsRainClip(a)) continue;
                    SrcInfo s;
                    if (!rainSrc.TryGetValue(a, out s)) { rainSrc[a] = s = new SrcInfo { lastTime = a.time }; continue; }
                    if (a.isPlaying && a.time + 0.05f < s.lastTime) s.restarts++;
                    s.lastTime = a.time;
                }
                yield return new WaitForSeconds(0.05f);
            }
        }

        private void RainSample(string tag)
        {
            var sb = new StringBuilder(tag + ":");
            var cl = GameRefs.CarLogic;
            var rg = RouteGeneratorC.Global;
            if (rg != null) sb.Append(" chuva=" + rg.rainType + " cond=" + rg.currentWeatherCondition);
            if (cl != null)
            {
                sb.Append(" | carro: rainLvl=" + cl.rainLvl + " abrigo=" + Traverse.Create(cl).Field("isUnderShelter").GetValue<bool>());
                var seat = cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
                if (seat != null) sb.Append(" somDoBanco=" + seat.rainAudioPlay);
                sb.Append(" | " + CarAmbienceFix.Describe());
                var o = cl.transform.position + Vector3.up;
                sb.Append(" raioDoCarro→ " + RayUp(o, 100f));
            }
            foreach (var rc in FindObjectsOfType<RainCheck>())
                sb.Append(" | RainCheck " + PathOf(rc.transform) + " fora=" + rc.Outside + " raio→ " + (rc.rayTarget != null ? RayUp(rc.rayTarget.position, float.PositiveInfinity) : "-"));
            int playing = 0;
            foreach (var a in FindObjectsOfType<AudioSource>())
            {
                if (!IsRainClip(a) || !a.isPlaying) continue;
                playing++;
                SrcInfo s; rainSrc.TryGetValue(a, out s);
                sb.Append(" | SOM " + a.clip.name + " em " + PathOf(a.transform) + " vol=" + a.volume.ToString("0.00") + " t=" + a.time.ToString("0.0")
                    + " loop=" + a.loop + " recomeços=" + (s != null ? s.restarts : 0));
            }
            sb.Append(" | sons de chuva tocando: " + playing);
            Line(sb.ToString());
        }

        private IEnumerator RainMeasure(float t0)
        {
            StartCoroutine(RainRestartCounter(42f));
            for (int i = 0; i < 40; i++)
            {
                yield return Until(t0, 8f + i);
                RainSample("t" + (8 + i));
            }
        }

        private IEnumerator HostRainScript()
        {
            float t0 = Time.time;
            yield return Until(t0, 1f);
            var cl = GameRefs.CarLogic;
            var seat = cl != null && cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
            if (seat != null && !seat.isSat) { seat.Trigger(); Line("host sentou no volante"); }

            yield return Until(t0, 3f);
            var rg = RouteGeneratorC.Global;
            MapRelayC relay = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<MapRelayC>())
                if (m != null && m.gameObject.scene.IsValid() && m.routeNumber == 1) { relay = m; break; }
            if (rg != null && relay != null)
            {
                int heavy = Traverse.Create(rg).Field("heavyRainChance").GetValue<int>();
                rg.route1Weather = heavy; // dentro da faixa de chuva forte
                RouteSync.LockLikeClick(relay);
                relay.SpawnRoute();
                Line("host forçou chuva forte e escolheu a rota 1");
            }
            else Line("sem mapa/gerador para escolher a rota");
            yield return RainMeasure(t0);
            yield return Finish(t0, 50f);
        }

        private IEnumerator ClientRainScript()
        {
            float t0 = Time.time;
            yield return Until(t0, 1f);
            if (UncleSync.Instance != null) UncleSync.Instance.SetWantSeat(true);
            yield return RainMeasure(t0);
            yield return Finish(t0, 50f);
        }
    }
}
