using System;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Hora do dia igual nos dois (parte do 4f). O relógio do jogo é o DNC_DayNight (DirectorC.dayNightCycle).
    /// O host manda a hora 1x/s; o tio corrige se passar de ~1 minuto do jogo e copia pausado/manhã.
    /// O desmaio das 23h (LateNight) só roda no host (no 4g isso vira a regra do motel).
    /// </summary>
    public class TimeSync : MonoBehaviour
    {
        private float timer;
        /// <summary>Último estado do host recebido (para o teste automático).</summary>
        internal static float HostTime; internal static int HostRoute; internal static int[] HostWeather; internal static bool HasHost;

        private void Start()
        {
            CoopNet.Instance.OnReliableData += OnReliableData;
        }

        internal static DNC_DayNight Clock
        {
            get
            {
                var d = GameObject.FindWithTag("Director");
                var dc = d != null ? d.GetComponent<DirectorC>() : null;
                return dc != null && dc.dayNightCycle != null ? dc.dayNightCycle.GetComponent<DNC_DayNight>() : null;
            }
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host || !net.Connected || !GameRefs.InGame) return;
            timer += Time.unscaledDeltaTime;
            if (timer < 1f) return;
            timer = 0f;
            var c = Clock;
            if (c == null) return;
            float t = c._timeInSeconds;
            bool paused = c.isPaused, morning = c.isMorning;
            var rg = RouteGeneratorC.Global;
            int chosen = rg != null ? rg.routeChosen : -1;
            int w1 = rg != null ? rg.route1Weather : 0, w2 = rg != null ? rg.route2Weather : 0, w3 = rg != null ? rg.route3Weather : 0;
            int rain = rg != null ? rg.rainType : 0;
            var used = UsedChances[0] < 0 && rg != null ? ReadChances(rg) : UsedChances;
            net.SendReliable(Msg.TimeOfDay, w =>
            {
                w.Write(t); w.Write(paused); w.Write(morning);
                w.Write(chosen); w.Write(w1); w.Write(w2); w.Write(w3); w.Write(rain); // clima (4f)
                for (int i = 0; i < 4; i++) w.Write(used[i]);
            });
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.TimeOfDay) return;
            float t = r.ReadSingle();
            bool paused = r.ReadBoolean(), morning = r.ReadBoolean();
            int chosen = r.ReadInt32(), w1 = r.ReadInt32(), w2 = r.ReadInt32(), w3 = r.ReadInt32(), rain = r.ReadInt32();
            var chances = new int[4];
            for (int i = 0; i < 4; i++) chances[i] = r.ReadInt32();
            HostTime = t; HostRoute = chosen; HostWeather = new[] { w1, w2, w3, rain }; HasHost = true;
            if (CoopNet.Instance.Mode != CoopMode.Client || !GameRefs.InGame) return;
            try
            {
                var c = Clock;
                if (c == null) return;
                float diff = Mathf.Abs(c._timeInSeconds - t);
                if (diff > 43200f) diff = 86400f - diff; // virada da meia-noite
                c.isPaused = paused;
                c.isMorning = morning;
                if (diff > 60f)
                {
                    c._timeInSeconds = t;
                    c.timeInHours = t / 3600f;
                    if (diff > 600f)
                    {
                        Traverse.Create(c).Field("_currentTransition").SetValue(null); // pulo grande: recalcula céu/luz
                        Plugin.Log.LogInfo("Hora: ajustada para a do host (" + (t / 3600f).ToString("0.00") + " h).");
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("TimeSync: " + e); }
            try { FixWeather(chosen, w1, w2, w3, rain, chances); }
            catch (Exception e) { Plugin.Log.LogError("TimeSync clima: " + e); }
        }

        private float lastWeatherFix = -100f;
        private string lastWeatherTry;

        /// <summary>
        /// Clima: cada rota já nasce com o mesmo sorteio nos dois (SeededRandom). Se mesmo assim o tio estiver diferente
        /// do host na rota escolhida, copia os valores de clima do host e roda o SetWeather do jogo (chuva, neblina, céu).
        /// </summary>
        private static readonly string[] ChanceFields = { "sunChance", "fogChance", "lightRainChance", "heavyRainChance" };

        /// <summary>
        /// Host: as chances (sol/neblina/chuva fraca/forte) que o último SetWeather usou. O resultado do clima depende
        /// delas e não só do sorteio da rota: no teste o tio tinha o mesmo sorteio mas ficava sem chuva.
        /// </summary>
        internal static int[] UsedChances = { -1, -1, -1, -1 };

        internal static int[] ReadChances(RouteGeneratorC rg)
        {
            var tr = Traverse.Create(rg);
            var a = new int[4];
            for (int i = 0; i < 4; i++) a[i] = tr.Field(ChanceFields[i]).GetValue<int>();
            return a;
        }

        private static void WriteChances(RouteGeneratorC rg, int[] a)
        {
            var tr = Traverse.Create(rg);
            for (int i = 0; i < 4; i++) tr.Field(ChanceFields[i]).SetValue(a[i]);
        }

        private void FixWeather(int chosen, int w1, int w2, int w3, int rain, int[] chances)
        {
            var rg = RouteGeneratorC.Global;
            if (rg == null || chosen < 1 || rg.routeChosen != chosen) return; // rota ainda não igual: o RouteSync cuida
            // Só a chuva de AGORA (rainType) conta. Os números route1..3Weather mudam na fronteira (opções do mapa
            // seguinte) e o SetWeather tiraria deles o clima da rota nova antes da hora (teste: chuva fraca virou forte
            // só no tio, e a chuva foi refeita duas vezes).
            if (rg.rainType == rain) { lastWeatherTry = null; return; }
            if (Time.realtimeSinceStartup - lastWeatherFix < 10f) return; // não ficar refazendo a chuva
            string attempt = chosen + "/" + w1 + "/" + w2 + "/" + w3 + "/" + rain;
            if (attempt == lastWeatherTry) return; // já tentei com esses números e não deu: não refazer a chuva toda hora
            lastWeatherTry = attempt;
            lastWeatherFix = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo("Clima: diferente do host (meu " + rg.route1Weather + "/" + rg.route2Weather + "/" + rg.route3Weather + " chuva " + rg.rainType
                + ", host " + w1 + "/" + w2 + "/" + w3 + " chuva " + rain + "): copiando.");
            rg.route1Weather = w1; rg.route2Weather = w2; rg.route3Weather = w3;
            // o SetWeather cria a chuva nova sem apagar a antiga quando já chovia: apaga antes (senão ficam duas)
            if (rg.rainTarget != null) { UnityEngine.Object.Destroy(rg.rainTarget); rg.rainTarget = null; }
            if (chances[0] < 0) { rg.SetWeather(); if (rg.rainType != rain) Plugin.Log.LogWarning("Clima: refeito, mas a chuva continua diferente do host (meu " + rg.rainType + ", host " + rain + ")."); return; }
            // roda o SetWeather com as mesmas chances do host e depois volta as do tio (o resto do jogo usa as atuais)
            var mine = ReadChances(rg);
            Plugin.Log.LogInfo("Clima: chances do host " + string.Join("/", Array.ConvertAll(chances, x => x.ToString()))
                + ", minhas " + string.Join("/", Array.ConvertAll(mine, x => x.ToString())) + ".");
            WriteChances(rg, chances);
            try { rg.SetWeather(); }
            finally { WriteChances(rg, mine); }
            if (rg.rainType != rain) Plugin.Log.LogWarning("Clima: refeito, mas a chuva continua diferente do host (meu " + rg.rainType + ", host " + rain + "); não tento de novo com os mesmos números.");
        }
    }

    /// <summary>Host: guarda as chances que o SetWeather usou (vão junto com o clima para o tio).</summary>
    [HarmonyPatch(typeof(RouteGeneratorC), "SetWeather")]
    internal static class SetWeatherChancesPatch
    {
        private static void Prefix(RouteGeneratorC __instance)
        {
            try
            {
                var net = CoopNet.Instance;
                if (net != null && net.Mode == CoopMode.Host) TimeSync.UsedChances = TimeSync.ReadChances(__instance);
                Plugin.Log.LogInfo("Clima: SetWeather (rota " + __instance.routeChosen + ", já tinha chuva criada: " + (__instance.rainTarget != null) + ").");
            }
            catch (Exception e) { Plugin.Log.LogError("TimeSync: " + e); }
        }
    }

    /// <summary>Tio: o desmaio das 23h é decidido no host.</summary>
    [HarmonyPatch(typeof(DNC_DayNight), "LateNight")]
    internal static class ClientLateNightPatch
    {
        private static bool Prefix()
        {
            var net = CoopNet.Instance;
            return net == null || !net.Connected || net.Mode != CoopMode.Client;
        }
    }
}
