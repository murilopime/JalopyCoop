using System.Collections;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Roteiro "clima" (-coop-autotest=clima): hora e clima iguais.
    ///  2 s  host escolhe a rota 1 no mapa (MapRelayC.SpawnRoute, igual ao clique) — o tio recebe pelo RouteSync
    /// 14 s  tio confere: mesma rota, mesmo clima (route1..3Weather, rainType) e mesma hora do host
    /// 15 s  host põe 20:00                         -> 19 s tio confere a hora
    /// 20 s  tio estraga o próprio clima (de propósito) -> 34 s tio confere que voltou ao do host
    /// 37 s  fim
    /// </summary>
    public partial class AutoTest
    {
        private string WeatherTxt()
        {
            var rg = RouteGeneratorC.Global;
            var c = TimeSync.Clock;
            if (rg == null) return "sem RouteGeneratorC";
            return "rota " + rg.routeChosen + " clima " + rg.route1Weather + "/" + rg.route2Weather + "/" + rg.route3Weather
                + " chuva " + rg.rainType + " condição " + rg.currentWeatherCondition
                + " hora " + (c != null ? (c._timeInSeconds / 3600f).ToString("0.00") : "-");
        }

        private IEnumerator HostWeatherScript()
        {
            float t0 = Time.time;
            Line("começo: " + WeatherTxt());
            yield return Until(t0, 2f);
            MapRelayC relay = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<MapRelayC>())
                if (m != null && m.gameObject.scene.IsValid() && m.routeNumber == 1) { relay = m; break; }
            if (relay != null) { relay.SpawnRoute(); Line("host escolheu a rota 1"); }
            else Line("sem mapa da rota 1");

            yield return Until(t0, 12f);
            Line("depois da rota: " + WeatherTxt());
            yield return ObserverShot(GameRefs.Car, "01_host_clima", 7f);

            yield return Until(t0, 15f);
            var c = TimeSync.Clock;
            if (c != null) { c._timeInSeconds = 20f * 3600f; c.timeInHours = 20f; Line("host pôs 20:00"); }
            yield return Until(t0, 19f);
            Line("20h: " + WeatherTxt());
            yield return ObserverShot(GameRefs.Car, "02_host_20h", 7f);
            yield return Until(t0, 34f);
            Line("fim: " + WeatherTxt());
            yield return Finish(t0, 37f);
        }

        private IEnumerator ClientWeatherScript()
        {
            float t0 = Time.time;
            Line("começo: " + WeatherTxt());
            yield return Until(t0, 14f);
            Line("depois da rota: " + WeatherTxt());
            CheckWeather("tio: mesma rota e clima do host");
            yield return ObserverShot(GameRefs.Car, "01_tio_clima", 7f);

            yield return Until(t0, 19f);
            var c = TimeSync.Clock;
            float h = c != null ? c._timeInSeconds / 3600f : -1f;
            float hh = TimeSync.HostTime / 3600f;
            Check("tio: hora igual à do host depois do 20:00", c != null && Mathf.Abs(h - hh) < 0.05f && h > 19.9f && h < 20.2f,
                "tio " + h.ToString("0.00") + " h, host " + hh.ToString("0.00") + " h");
            yield return ObserverShot(GameRefs.Car, "02_tio_20h", 7f);

            yield return Until(t0, 20f);
            var rg = RouteGeneratorC.Global;
            if (rg != null)
            {
                rg.route1Weather = (rg.route1Weather + 37) % 100;
                rg.route2Weather = (rg.route2Weather + 37) % 100;
                rg.route3Weather = (rg.route3Weather + 37) % 100;
                rg.rainType = (rg.rainType + 1) % 3;
                Line("estraguei meu clima: " + WeatherTxt());
            }
            yield return Until(t0, 34f);
            Line("fim: " + WeatherTxt());
            CheckWeather("tio: clima voltou ao do host");
            yield return Finish(t0, 37f);
        }

        private void CheckWeather(string name)
        {
            var rg = RouteGeneratorC.Global;
            var hw = TimeSync.HostWeather;
            if (rg == null || !TimeSync.HasHost || hw == null) { Check(name, false, "sem dados"); return; }
            bool same = rg.routeChosen == TimeSync.HostRoute && rg.route1Weather == hw[0] && rg.route2Weather == hw[1]
                && rg.route3Weather == hw[2] && rg.rainType == hw[3];
            Check(name, same, "tio: rota " + rg.routeChosen + " " + rg.route1Weather + "/" + rg.route2Weather + "/" + rg.route3Weather + " chuva " + rg.rainType
                + " | host: rota " + TimeSync.HostRoute + " " + hw[0] + "/" + hw[1] + "/" + hw[2] + " chuva " + hw[3]);
        }
    }
}
