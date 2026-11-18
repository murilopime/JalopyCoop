using System.Collections;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Roteiro "cidade" (-coop-autotest=cidade): o host escolhe no mapa a rota que leva à cidade de destino (a cidade
    /// nasce no fim dela); o CityCheck compara todas as cidades (a atual e a nova) entre host e tio.
    /// </summary>
    public partial class AutoTest
    {
        private IEnumerator HostCityScript()
        {
            float t0 = Time.time;
            yield return Until(t0, 2f);
            var rg = RouteGeneratorC.Global;
            int dest = rg != null ? rg.destinationNumber : 1;
            if (dest < 1 || dest > 3) dest = 1;
            MapRelayC relay = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<MapRelayC>())
                if (m != null && m.gameObject.scene.IsValid() && m.routeNumber == dest) { relay = m; break; }
            if (relay != null) { relay.SpawnRoute(); Line("host escolheu a rota " + dest + " (a da cidade)"); }
            else Line("sem mapa da rota " + dest);
            yield return Until(t0, 30f);
            foreach (var hub in Object.FindObjectsOfType<Hub_CitySpawnC>()) Line("cidade aqui: " + hub.name + " em " + hub.transform.position);
            yield return Finish(t0, 33f);
        }

        private IEnumerator ClientCityScript()
        {
            float t0 = Time.time;
            yield return Until(t0, 30f);
            var cc = CityCheck.Instance;
            int n = 0;
            if (cc != null)
                foreach (var kv in cc.Results) { n++; Check("cidade " + kv.Key + " igual ao host", kv.Value, kv.Value ? "mesma impressão digital" : "ver o log (DIFERENTE)"); }
            Check("pelo menos 1 cidade conferida", n >= 1, n + " conferidas");
            foreach (var hub in Object.FindObjectsOfType<Hub_CitySpawnC>()) Line("cidade aqui: " + hub.name + " em " + hub.transform.position);
            yield return Finish(t0, 33f);
        }
    }
}
