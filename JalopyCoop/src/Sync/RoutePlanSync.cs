using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Plano das rotas igual ao do host.
    ///
    /// Cada opção do mapa (RouteGeneratorC.GetRoute1..3) guarda sorteios além dos trechos: clima, estado da estrada
    /// (boa/ruim/LAMA: o routeNRoadCode vira currentRoadCondition e o SpawnContinueC troca a estrada), distância,
    /// parada no meio e destino. O sorteio fixo deixa os trechos iguais, mas esses números podiam sair diferentes
    /// (contagens diferentes, a fronteira refaz as rotas só no host...) — no teste de 2026-11-16 a mesma rota saiu
    /// com lama só no tio. Aqui o host manda o plano inteiro quando ele muda e antes de criar a rota; o tio copia,
    /// troca os trechos se forem outros e atualiza o mapa.
    /// </summary>
    public class RoutePlanSync : MonoBehaviour
    {
        public static RoutePlanSync Instance { get; private set; }

        private static readonly string[] IntFields = BuildFields();
        private float timer;
        private uint lastSent;

        private static string[] BuildFields()
        {
            var l = new List<string>();
            for (int n = 1; n <= 4; n++)
            {
                l.Add("route" + n + "Distance"); l.Add("route" + n + "Weather"); l.Add("route" + n + "WeatherCode");
                l.Add("route" + n + "RoadCondition"); l.Add("route" + n + "RoadCode"); l.Add("route" + n + "StopOff");
                if (n <= 3) l.Add("route" + n + "StopOffPoint");
            }
            l.AddRange(new[] { "destinationNumber", "economyState", "sunChance", "fogChance", "lightRainChance", "heavyRainChance",
                "goodRoadConditionChance", "poorRoadConditionChance", "abandonCarSpawnChance", "currentRoadCondition", "currentWeatherCondition" });
            return l.ToArray();
        }

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => lastSent = 0;
        }

        private static int[] ReadInts(RouteGeneratorC rg)
        {
            var tr = Traverse.Create(rg);
            var a = new int[IntFields.Length];
            for (int i = 0; i < a.Length; i++)
            {
                var f = tr.Field(IntFields[i]);
                a[i] = f.FieldExists() ? f.GetValue<int>() : int.MinValue;
            }
            return a;
        }

        private static string[][] ReadSegments(RouteGeneratorC rg)
        {
            var tr = Traverse.Create(rg);
            var s = new string[4][];
            for (int n = 1; n <= 4; n++)
            {
                var arr = tr.Field("route" + n + "Segments").GetValue<GameObject[]>();
                s[n - 1] = arr == null ? new string[0] : Array.ConvertAll(arr, g => g != null ? g.name : "");
            }
            return s;
        }

        private static uint Hash(int level, int[] ints, string[][] segs)
        {
            var sb = new StringBuilder();
            sb.Append(level).Append('|');
            foreach (var v in ints) sb.Append(v).Append(',');
            foreach (var a in segs) { sb.Append('|'); foreach (var n in a) sb.Append(n).Append(','); }
            return SeededRandom.Hash(sb.ToString());
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || net.Mode != CoopMode.Host || !GameRefs.InGame) return;
            timer += Time.unscaledDeltaTime;
            if (timer < 1.5f) return;
            timer = 0f;
            SendIfChanged(false);
        }

        /// <summary>Host: manda o plano se mudou (force: manda mesmo assim, ex.: logo antes de criar a rota).</summary>
        internal void SendIfChanged(bool force)
        {
            try
            {
                var net = CoopNet.Instance;
                var rg = RouteGeneratorC.Global;
                if (net == null || !net.Connected || net.Mode != CoopMode.Host || rg == null) return;
                var ints = ReadInts(rg);
                var segs = ReadSegments(rg);
                int level = rg.routeLevel;
                bool home = rg.drivingTowardsHome;
                uint h = Hash(level, ints, segs);
                if (!force && h == lastSent) return;
                lastSent = h;
                net.SendReliable(Msg.RoutePlan, w =>
                {
                    w.Write(h); w.Write(level); w.Write(home);
                    w.Write(ints.Length); foreach (var v in ints) w.Write(v);
                    foreach (var a in segs) { w.Write(a.Length); foreach (var n in a) w.Write(n); }
                });
                Plugin.Log.LogInfo("Rota: plano mandado ao tio (#" + h.ToString("X8") + ", estrada " + Road(ints, 1) + "/" + Road(ints, 2) + "/" + Road(ints, 3) + ").");
            }
            catch (Exception e) { Plugin.Log.LogError("RoutePlanSync: " + e); }
        }

        private static string Road(int[] ints, int n)
        {
            int i = Array.IndexOf(IntFields, "route" + n + "RoadCode");
            if (i < 0) return "?";
            int c = ints[i];
            return c == 0 ? "boa" : c == 1 ? "ruim" : c == 2 ? "lama" : c.ToString();
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.RoutePlan) return;
            uint h = r.ReadUInt32(); int level = r.ReadInt32(); bool home = r.ReadBoolean();
            var ints = new int[r.ReadInt32()];
            for (int i = 0; i < ints.Length; i++) ints[i] = r.ReadInt32();
            var segs = new string[4][];
            for (int n = 0; n < 4; n++) { segs[n] = new string[r.ReadInt32()]; for (int i = 0; i < segs[n].Length; i++) segs[n][i] = r.ReadString(); }
            if (CoopNet.Instance.Mode != CoopMode.Client || !GameRefs.InGame) return;
            try { Apply(h, level, home, ints, segs); }
            catch (Exception e) { Plugin.Log.LogError("RoutePlanSync: " + e); }
        }

        private static void Apply(uint h, int level, bool home, int[] ints, string[][] segs)
        {
            var rg = RouteGeneratorC.Global;
            if (rg == null) return;
            if (ints.Length != IntFields.Length) { Plugin.Log.LogWarning("Rota: plano do host com outro formato."); return; }
            var mine = ReadInts(rg);
            var mySegs = ReadSegments(rg);
            if (Hash(rg.routeLevel, mine, mySegs) == h && rg.drivingTowardsHome == home) { Plugin.Log.LogInfo("Rota: plano igual ao do host (#" + h.ToString("X8") + ")."); return; }

            var diffs = new List<string>();
            if (rg.routeLevel != level) diffs.Add("país " + rg.routeLevel + "→" + level);
            if (rg.drivingTowardsHome != home) { diffs.Add("indo pra casa " + home); rg.drivingTowardsHome = home; }
            var tr = Traverse.Create(rg);
            for (int i = 0; i < ints.Length; i++)
            {
                if (ints[i] == int.MinValue || mine[i] == ints[i]) continue;
                diffs.Add(IntFields[i] + " " + mine[i] + "→" + ints[i]);
                tr.Field(IntFields[i]).SetValue(ints[i]);
            }
            for (int n = 1; n <= 4; n++)
            {
                if (SameNames(mySegs[n - 1], segs[n - 1])) continue;
                var arr = tr.Field("route" + n + "Segments").GetValue<GameObject[]>();
                var fresh = new GameObject[segs[n - 1].Length];
                int found = 0;
                for (int i = 0; i < fresh.Length; i++)
                {
                    fresh[i] = FindPrefab(segs[n - 1][i], arr);
                    if (fresh[i] != null || segs[n - 1][i] == "") found++;
                }
                if (found == fresh.Length) { tr.Field("route" + n + "Segments").SetValue(fresh); diffs.Add("trechos da rota " + n); }
                else Plugin.Log.LogWarning("Rota: trechos da rota " + n + " do host não achados aqui (" + found + "/" + fresh.Length + ").");
            }
            Plugin.Log.LogWarning("Rota: plano diferente do host, copiado (" + string.Join(", ", diffs.ToArray()) + ").");
            RefreshMap(rg);
        }

        private static bool SameNames(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static readonly Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>();

        /// <summary>Prefab do trecho pelo nome (só quando os trechos diferem: raro).</summary>
        private static GameObject FindPrefab(string name, GameObject[] current)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (current != null) foreach (var g in current) if (g != null && g.name == name) return g;
            GameObject p;
            if (prefabCache.TryGetValue(name, out p) && p != null) return p;
            foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (g == null || g.name != name || g.transform.parent != null) continue;
                if (g.scene.IsValid()) continue; // é cópia na cena, não o prefab
                if (g.GetComponent<SpawnContinueC>() == null) continue;
                prefabCache[name] = g;
                return g;
            }
            return null;
        }

        /// <summary>O mapa do tio mostra os mesmos números e ícones do host.</summary>
        private static void RefreshMap(RouteGeneratorC rg)
        {
            var tr = Traverse.Create(rg);
            try { rg.SetRoute1Text(); rg.SetRoute2Text(); rg.SetRoute3Text(); } catch (Exception e) { Plugin.Log.LogWarning("Rota: textos do mapa: " + e.Message); }
            var maps = new[] { rg.mapText1, rg.mapText2, rg.mapText3 };
            for (int n = 1; n <= 3; n++)
            {
                var relay = maps[n - 1] != null ? maps[n - 1].GetComponent<MapRelayC>() : null;
                if (relay == null) continue;
                int wc = tr.Field("route" + n + "WeatherCode").GetValue<int>();
                int rc = tr.Field("route" + n + "RoadCode").GetValue<int>();
                try
                {
                    if (relay.weatherIcon != null && wc >= 0 && wc < rg.weatherIcons.Length) relay.weatherIcon.GetComponent<SpriteRenderer>().sprite = rg.weatherIcons[wc];
                    if (relay.roadConditionIcon != null && rc >= 0 && rc < rg.roadConditionIcons.Length) relay.roadConditionIcon.GetComponent<SpriteRenderer>().sprite = rg.roadConditionIcons[rc];
                    relay.SetRouteToMap();
                }
                catch (Exception e) { Plugin.Log.LogWarning("Rota: mapa " + n + ": " + e.Message); }
            }
        }
    }
}
