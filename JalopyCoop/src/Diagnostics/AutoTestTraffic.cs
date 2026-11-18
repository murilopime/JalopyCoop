using System.Collections;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Roteiro "transito" (-coop-autotest=transito): carros do trânsito iguais nos dois.
    ///  1 s  os dois anotam a config de trânsito do jogo (aiCount) e, só durante o teste, põem no máximo (4);
    ///       tio senta no passageiro; host senta no volante
    ///  3 s  host escolhe a rota 1 (igual ao clique)
    /// 8-58 s os dois medem 1x/s (host: carros vivos; tio: casados, esperando, escondidos, distância à posição do host)
    /// 60 s  confere e fecha. A config volta ao valor de antes no fim (e o save é restaurado depois do teste).
    /// </summary>
    public partial class AutoTest
    {
        private int trafficOriginalAi = -1;
        private int trafficMaxHost, trafficMaxMatched, trafficMaxWaitingLate;
        private float trafficMaxGap;

        private void TrafficForceConfig()
        {
            if (ES3.loadedSave == null) { Line("sem save carregado (ES3.loadedSave)"); return; }
            trafficOriginalAi = ES3.loadedSave.aiCount;
            Line("config de trânsito do jogo: aiCount=" + trafficOriginalAi + (trafficOriginalAi <= 1 ? " (com ela as rotas normais ficam SEM carros)" : ""));
            ES3.loadedSave.aiCount = 4;
            Line("config de trânsito posta em 4 só durante o teste");
        }

        private void TrafficRestoreConfig()
        {
            if (ES3.loadedSave != null && trafficOriginalAi >= 0)
            {
                ES3.loadedSave.aiCount = trafficOriginalAi;
                Line("config de trânsito voltou para " + trafficOriginalAi);
            }
        }

        /// <summary>
        /// Lugares para carros só nos trechos que a rota USA (os primeiros "distância" da lista: o resto da lista é
        /// sobra de sorteios antigos — no 1º teste a rota 1 contou 24, mas os 3 trechos criados tinham 0).
        /// </summary>
        private static int TrafficNodes(GameObject[] segments, int distance, out string names)
        {
            int n = 0;
            names = "";
            if (segments == null) return 0;
            int count = Mathf.Min(distance, segments.Length);
            for (int i = 0; i < count; i++)
            {
                var s = segments[i];
                var sc = s != null ? s.GetComponent<SpawnContinueC>() : null;
                int k = sc != null && sc.trafficAINodes != null ? sc.trafficAINodes.Count : 0;
                n += k;
                names += (names.Length > 0 ? ", " : "") + (s != null ? s.name : "-") + "=" + k;
            }
            return n;
        }

        private IEnumerator TrafficMeasure(float t0)
        {
            for (int i = 0; i <= 50; i++)
            {
                yield return Until(t0, 8f + i);
                var ts = TrafficSync.Instance;
                if (ts == null) { Line("t" + (8 + i) + ": sem TrafficSync"); continue; }
                int host, matched, waiting, orphans; float gap;
                string s = ts.DebugSummary(out host, out matched, out waiting, out orphans, out gap);
                trafficMaxHost = Mathf.Max(trafficMaxHost, host);
                trafficMaxMatched = Mathf.Max(trafficMaxMatched, matched);
                if (i >= 20) { trafficMaxWaitingLate = Mathf.Max(trafficMaxWaitingLate, waiting); trafficMaxGap = Mathf.Max(trafficMaxGap, gap); }
                if (i % 5 == 0) Line("t" + (8 + i) + ": " + s);
            }
        }

        private IEnumerator HostTrafficScript()
        {
            float t0 = Time.time;
            yield return Until(t0, 1f);
            TrafficForceConfig();
            var cl = GameRefs.CarLogic;
            var seat = cl != null && cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
            if (seat != null && !seat.isSat) { seat.Trigger(); Line("host sentou no volante"); }

            yield return Until(t0, 3f);
            // Só alguns trechos têm lugar para carros (trafficAINodes no prefab). Olha os trechos já sorteados das
            // 3 rotas (sem criar nada) e escolhe a que tem mais lugares.
            int route = 1, best = -1;
            var rg = RouteGeneratorC.Global;
            if (rg != null)
            {
                var all = new[] { rg.route1Segments, rg.route2Segments, rg.route3Segments };
                var dist = new[] { rg.route1Distance, rg.route2Distance, rg.route3Distance };
                for (int r = 0; r < 3; r++)
                {
                    string names;
                    int nodes = TrafficNodes(all[r], dist[r], out names);
                    Line("rota " + (r + 1) + ": " + nodes + " lugares para carros em " + dist[r] + " trechos (" + names + ")");
                    if (nodes > best) { best = nodes; route = r + 1; }
                }
            }
            if (best == 0) Line("nenhuma rota tem lugar para carros nos trechos: o teste não vai ter trânsito");
            MapRelayC relay = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<MapRelayC>())
                if (m != null && m.gameObject.scene.IsValid() && m.routeNumber == route) { relay = m; break; }
            if (relay != null)
            {
                RouteSync.LockLikeClick(relay);
                relay.SpawnRoute();
                Line("host escolheu a rota " + route);
            }
            else Line("sem mapa para escolher a rota " + route);

            yield return TrafficMeasure(t0);
            Check("host: nasceram carros do trânsito", trafficMaxHost > 0, "máximo vivos ao mesmo tempo " + trafficMaxHost);
            TrafficRestoreConfig();
            yield return Finish(t0, 62f);
        }

        private IEnumerator ClientTrafficScript()
        {
            float t0 = Time.time;
            yield return Until(t0, 1f);
            TrafficForceConfig();
            if (UncleSync.Instance != null) UncleSync.Instance.SetWantSeat(true);

            yield return TrafficMeasure(t0);
            Check("tio: carros do host casados", trafficMaxMatched > 0, "máximo casados " + trafficMaxMatched);
            Check("tio: nenhum carro do host esperando par (depois de 28 s)", trafficMaxWaitingLate == 0, "máximo esperando " + trafficMaxWaitingLate);
            int expired = TrafficSync.Instance != null ? TrafficSync.Instance.expiredUnmatched : -1;
            Check("tio: nenhum carro do host ficou sem par", expired == 0, "sem par em 30 s: " + expired);
            Check("tio: carros seguem a posição do host", trafficMaxGap < 3f, "maior distância " + trafficMaxGap.ToString("0.0") + " m");
            TrafficRestoreConfig();
            yield return Finish(t0, 62f);
        }
    }
}
