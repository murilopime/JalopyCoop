using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Conferência das cidades: a cidade (Hub_CitySpawnC) sorteia onde ficam loja, motel, posto, Laika, prédios e
    /// enfeites. Com o sorteio fixo (SeededRandom) tem que sair igual nos dois. Quando uma cidade aparece e fica
    /// parada uns segundos, cada lado faz a "impressão digital" (nome e posição de tudo dentro dela) e o host manda a
    /// dele; o tio compara e escreve no log IGUAL ou DIFERENTE (com o que mudou).
    /// </summary>
    public class CityCheck : MonoBehaviour
    {
        public static CityCheck Instance { get; private set; }

        /// <summary>Resultados no tio: nome da cidade -> true (igual) / false (diferente).</summary>
        public readonly Dictionary<string, bool> Results = new Dictionary<string, bool>();

        private float timer;
        private readonly Dictionary<Hub_CitySpawnC, float> firstSeen = new Dictionary<Hub_CitySpawnC, float>();
        private readonly HashSet<Hub_CitySpawnC> done = new HashSet<Hub_CitySpawnC>();
        private readonly Dictionary<Hub_CitySpawnC, Print> mine = new Dictionary<Hub_CitySpawnC, Print>();
        private readonly List<KeyValuePair<Vector3, Print>> pendingHost = new List<KeyValuePair<Vector3, Print>>();

        private class Print { public string Name; public uint Hash; public string Summary; public List<string> Parts; }

        private void Start()
        {
            Instance = this;
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { firstSeen.Clear(); done.Clear(); mine.Clear(); pendingHost.Clear(); };
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            timer += Time.unscaledDeltaTime;
            if (timer < 2f) return;
            timer = 0f;
            try
            {
                float now = Time.realtimeSinceStartup;
                foreach (var hub in FindObjectsOfType<Hub_CitySpawnC>())
                {
                    if (done.Contains(hub)) continue;
                    float t;
                    if (!firstSeen.TryGetValue(hub, out t)) { firstSeen[hub] = now; continue; }
                    if (now - t < 6f) continue; // deixa a cidade terminar de nascer
                    done.Add(hub);
                    var p = Make(hub);
                    mine[hub] = p;
                    Plugin.Log.LogInfo("Cidade: " + p.Name + " = #" + p.Hash.ToString("X8") + " (" + p.Parts.Count + " objetos) " + p.Summary);
                    if (net.Mode == CoopMode.Host)
                    {
                        var pos = hub.transform.position;
                        var parts = p.Parts;
                        net.SendReliable(Msg.CityPrint, w =>
                        {
                            NetIO.Put(w, pos); w.Write(p.Name); w.Write(p.Hash); w.Write(p.Summary);
                            w.Write(parts.Count); foreach (var s in parts) w.Write(s);
                        });
                    }
                }
                if (net.Mode == CoopMode.Client) ComparePending();
            }
            catch (Exception e) { Plugin.Log.LogError("CityCheck: " + e); }
        }

        private static string R(Vector3 v) { return Mathf.RoundToInt(v.x) + "," + Mathf.RoundToInt(v.y) + "," + Mathf.RoundToInt(v.z); }

        private static Print Make(Hub_CitySpawnC hub)
        {
            var parts = new List<string>();
            var root = hub.transform;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                if (t.GetComponentInParent<Rigidbody>() != null) continue; // coisas soltas (física) podem ter se mexido
                if (t.GetComponent<Renderer>() == null && t.GetComponent<Collider>() == null && t.childCount == 0) continue;
                parts.Add(t.name + "@" + R(root.InverseTransformPoint(t.position)) + (t.gameObject.activeInHierarchy ? "" : "(off)"));
            }
            parts.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (var s in parts) sb.Append(s).Append(';');
            var summary = new StringBuilder();
            Add(summary, "loja", hub.market, root);
            Add(summary, "motel", hub.spawnedMotel != null ? hub.spawnedMotel : hub.motel, root);
            Add(summary, "posto", hub.petrolStation, root);
            Add(summary, "laika", hub.laikaFactory, root);
            Add(summary, "música", hub.musicShop, root);
            return new Print { Name = hub.name + "@" + R(hub.transform.position), Hash = SeededRandom.Hash(sb.ToString()), Summary = summary.ToString(), Parts = parts };
        }

        private static void Add(StringBuilder sb, string what, GameObject go, Transform root)
        {
            sb.Append(what).Append('=');
            sb.Append(go != null ? R(root.InverseTransformPoint(go.transform.position)) + (go.activeInHierarchy ? "" : "(off)") : "-");
            sb.Append(' ');
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.CityPrint) return;
            var pos = NetIO.GetVector3(r);
            var p = new Print { Name = r.ReadString(), Hash = r.ReadUInt32(), Summary = r.ReadString(), Parts = new List<string>() };
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) p.Parts.Add(r.ReadString());
            if (CoopNet.Instance.Mode != CoopMode.Client) return;
            pendingHost.Add(new KeyValuePair<Vector3, Print>(pos, p));
            ComparePending();
        }

        private void ComparePending()
        {
            for (int i = pendingHost.Count - 1; i >= 0; i--)
            {
                var pos = pendingHost[i].Key; var host = pendingHost[i].Value;
                Print me = null; float bd = 25f;
                foreach (var kv in mine)
                {
                    if (kv.Key == null) continue;
                    float d = (kv.Key.transform.position - pos).sqrMagnitude;
                    if (d < bd) { bd = d; me = kv.Value; }
                }
                if (me == null)
                {
                    // ainda não tenho essa cidade (ou ela nem nasceu aqui): espera, e avisa se demorar
                    continue;
                }
                pendingHost.RemoveAt(i);
                bool same = me.Hash == host.Hash;
                Results[host.Name] = same;
                if (same) { Plugin.Log.LogInfo("Cidade: " + host.Name + " IGUAL ao host (#" + host.Hash.ToString("X8") + ")."); continue; }
                var hs = new HashSet<string>(host.Parts); var ms = new HashSet<string>(me.Parts);
                var onlyHost = new List<string>(); foreach (var s in host.Parts) if (!ms.Contains(s)) onlyHost.Add(s);
                var onlyMe = new List<string>(); foreach (var s in me.Parts) if (!hs.Contains(s)) onlyMe.Add(s);
                Plugin.Log.LogWarning("Cidade: " + host.Name + " DIFERENTE do host. Host: " + host.Summary + "| Tio: " + me.Summary
                    + "| só no host (" + onlyHost.Count + "): " + string.Join(" ", onlyHost.GetRange(0, Math.Min(12, onlyHost.Count)).ToArray())
                    + " | só no tio (" + onlyMe.Count + "): " + string.Join(" ", onlyMe.GetRange(0, Math.Min(12, onlyMe.Count)).ToArray()));
            }
        }
    }
}
