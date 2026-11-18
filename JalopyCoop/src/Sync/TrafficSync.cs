using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Carros do trânsito (SmartAICar) iguais nos dois.
    ///
    /// Os trechos criam os carros nos mesmos lugares (sorteio fixo), mas depois cada jogo dirigia os seus sozinho e
    /// o modelo (SetChasis) era sorteado sem controle: um carro podia bater no Laika no host e estar em outro lugar no
    /// tio. Agora só o host dirige: quando um carro nasce no host (Start), ele manda id + lugar de nascimento + modelo;
    /// o tio casa com o carro dele que nasceu no mesmo lugar, desliga a direção/física dele e segue a posição que o
    /// host manda 10x/s. Carro que some no host some no tio; carro do tio sem par no host é escondido.
    /// Tudo por evento (Start/Destroy) e por uma lista própria — sem varrer a cena.
    /// </summary>
    public class TrafficSync : MonoBehaviour
    {
        public static TrafficSync Instance { get; private set; }

        private const float SendInterval = 0.1f;
        private const float MatchRadius = 8f;
        private const float OrphanAfter = 6f;

        // host
        private readonly Dictionary<int, SmartAICar> hostCars = new Dictionary<int, SmartAICar>();
        private int nextId = 1;
        private float sendTimer;

        // tio
        private class Remote { public SmartAICar car; public Vector3 pos; public Quaternion rot; public bool has; }
        private readonly Dictionary<int, Remote> remote = new Dictionary<int, Remote>();
        private class Pending { public int id; public Vector3 spawn; public Quaternion rot; public int mesh; public float since; }
        private readonly List<Pending> pending = new List<Pending>();
        private class Local { public SmartAICar car; public float born; public bool matched; }
        private readonly List<Local> localCars = new List<Local>();
        private float matchTimer;
        internal int expiredUnmatched;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { hostCars.Clear(); remote.Clear(); pending.Clear(); };
        }

        internal static bool IsClient { get { var n = CoopNet.Instance; return n != null && n.Connected && n.Mode == CoopMode.Client; } }
        internal static bool IsHost { get { var n = CoopNet.Instance; return n != null && n.Connected && n.Mode == CoopMode.Host; } }

        /// <summary>Teste: resumo do trânsito (host: carros vivos; tio: casados, esperando, escondidos, distância à posição do host).</summary>
        internal string DebugSummary(out int hostAlive, out int matched, out int waiting, out int orphans, out float maxGap)
        {
            hostAlive = 0; matched = 0; waiting = pending.Count; orphans = 0; maxGap = 0f;
            foreach (var kv in hostCars) if (kv.Value != null) hostAlive++;
            foreach (var r in remote.Values)
            {
                if (r.car == null) continue;
                matched++;
                if (r.has) maxGap = Mathf.Max(maxGap, Vector3.Distance(r.car.transform.position, r.pos));
            }
            foreach (var l in localCars) if (l.car != null && !l.matched && !l.car.gameObject.activeSelf) orphans++;
            return "host vivos " + hostAlive + " (ids até " + (nextId - 1) + ") | tio casados " + matched + ", esperando " + waiting
                + ", escondidos " + orphans + ", sem par (30 s) " + expiredUnmatched + ", maior distância " + maxGap.ToString("0.0") + " m";
        }

        private static int MeshIndex(SmartAICar c)
        {
            if (c.initialMesh == null || c.chasisMesh == null) return -1;
            var mf = c.initialMesh.GetComponent<MeshFilter>();
            return mf != null ? Array.IndexOf(c.chasisMesh, mf.sharedMesh) : -1;
        }

        // ================= nascimento =================

        internal void CarStarted(SmartAICar c)
        {
            if (IsHost)
            {
                int id = nextId++;
                hostCars[id] = c;
                var pos = c.transform.position; var rot = c.transform.rotation; int mesh = MeshIndex(c);
                CoopNet.Instance.SendReliable(Msg.TrafficSpawn, w => { w.Write(id); NetIO.Put(w, pos); NetIO.Put(w, rot); w.Write(mesh); });
                Plugin.Log.LogInfo("Trânsito: carro " + id + " nasceu (" + c.name + ", modelo " + mesh + ").");
            }
            else if (IsClient)
            {
                Freeze(c);
                localCars.Add(new Local { car = c, born = Time.realtimeSinceStartup });
                TryMatchAll();
            }
        }

        /// <summary>Tio: o carro não anda sozinho (a física fica parada; a posição vem do host).</summary>
        private static void Freeze(SmartAICar c)
        {
            var rb = c.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.useGravity = false; rb.interpolation = RigidbodyInterpolation.None; }
        }

        private void TryMatchAll()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                Local best = null; float bd = MatchRadius * MatchRadius;
                foreach (var l in localCars)
                {
                    if (l.matched || l.car == null) continue;
                    float d = (l.car.transform.position - p.spawn).sqrMagnitude;
                    if (d < bd) { bd = d; best = l; }
                }
                if (best == null) continue;
                best.matched = true;
                pending.RemoveAt(i);
                var c = best.car;
                if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                if (p.mesh >= 0 && c.initialMesh != null && c.chasisMesh != null && p.mesh < c.chasisMesh.Length)
                {
                    var mf = c.initialMesh.GetComponent<MeshFilter>();
                    if (mf != null) mf.mesh = c.chasisMesh[p.mesh];
                }
                Remote r;
                if (!remote.TryGetValue(p.id, out r)) remote[p.id] = r = new Remote();
                r.car = c;
                if (!r.has) { r.pos = c.transform.position; r.rot = c.transform.rotation; }
                Plugin.Log.LogInfo("Trânsito: carro " + p.id + " do host casado com " + c.name + " (" + Mathf.Sqrt(bd).ToString("0.0") + " m).");
            }
        }

        // ================= host: manda posições =================

        private void Update()
        {
            if (IsHost) HostUpdate();
            else if (IsClient) ClientUpdate();
        }

        private void HostUpdate()
        {
            sendTimer += Time.unscaledDeltaTime;
            if (sendTimer < SendInterval) return;
            sendTimer = 0f;
            if (hostCars.Count == 0) return;
            var gone = new List<int>();
            var ids = new List<int>(); var poss = new List<Vector3>(); var rots = new List<Quaternion>();
            foreach (var kv in hostCars)
            {
                if (kv.Value == null) { gone.Add(kv.Key); continue; }
                ids.Add(kv.Key); poss.Add(kv.Value.transform.position); rots.Add(kv.Value.transform.rotation);
            }
            var net = CoopNet.Instance;
            foreach (var id in gone)
            {
                hostCars.Remove(id);
                int gid = id;
                net.SendReliable(Msg.TrafficGone, w => w.Write(gid));
            }
            if (ids.Count == 0) return;
            net.SendReliable(Msg.TrafficMove, w =>
            {
                w.Write(ids.Count);
                for (int i = 0; i < ids.Count; i++) { w.Write(ids[i]); NetIO.Put(w, poss[i]); NetIO.Put(w, rots[i]); }
            });
        }

        // ================= tio: segue =================

        private void ClientUpdate()
        {
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            foreach (var r in remote.Values)
            {
                if (r.car == null || !r.has) continue;
                var t = r.car.transform;
                if ((t.position - r.pos).sqrMagnitude > 400f) { t.position = r.pos; t.rotation = r.rot; } // longe demais: pula
                else { t.position = Vector3.Lerp(t.position, r.pos, k); t.rotation = Quaternion.Slerp(t.rotation, r.rot, k); }
            }
            matchTimer += Time.unscaledDeltaTime;
            if (matchTimer < 0.5f) return;
            matchTimer = 0f;
            if (pending.Count > 0) TryMatchAll();
            float now = Time.realtimeSinceStartup;
            for (int i = localCars.Count - 1; i >= 0; i--)
            {
                var l = localCars[i];
                if (l.car == null) { localCars.RemoveAt(i); continue; }
                if (!l.matched && now - l.born > OrphanAfter && l.car.gameObject.activeSelf)
                {
                    l.car.gameObject.SetActive(false); // o host não tem esse carro
                    Plugin.Log.LogInfo("Trânsito: carro do tio sem par no host escondido (" + l.car.name + ").");
                }
            }
            for (int i = pending.Count - 1; i >= 0; i--)
                if (now - pending[i].since > 30f)
                {
                    expiredUnmatched++;
                    Plugin.Log.LogWarning("Trânsito: carro " + pending[i].id + " do host nunca achou par aqui em 30 s.");
                    pending.RemoveAt(i); // nunca nasceu aqui
                }
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            try
            {
                switch (type)
                {
                    case Msg.TrafficSpawn:
                    {
                        int id = r.ReadInt32(); var pos = NetIO.GetVector3(r); var rot = NetIO.GetQuaternion(r); int mesh = r.ReadInt32();
                        if (!IsClient) return;
                        pending.Add(new Pending { id = id, spawn = pos, rot = rot, mesh = mesh, since = Time.realtimeSinceStartup });
                        TryMatchAll();
                        break;
                    }
                    case Msg.TrafficMove:
                    {
                        int n = r.ReadInt32();
                        for (int i = 0; i < n; i++)
                        {
                            int id = r.ReadInt32(); var pos = NetIO.GetVector3(r); var rot = NetIO.GetQuaternion(r);
                            if (!IsClient) continue;
                            Remote rc;
                            if (!remote.TryGetValue(id, out rc)) remote[id] = rc = new Remote();
                            rc.pos = pos; rc.rot = rot; rc.has = true;
                        }
                        break;
                    }
                    case Msg.TrafficGone:
                    {
                        int id = r.ReadInt32();
                        if (!IsClient) return;
                        Remote rc;
                        if (remote.TryGetValue(id, out rc))
                        {
                            remote.Remove(id);
                            if (rc.car != null) Destroy(rc.car.gameObject);
                        }
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("TrafficSync " + type + ": " + e); }
        }
    }

    /// <summary>
    /// Log de cada trecho: quantos carros a config de trânsito do jogo (aiCount, 0..4) deixou nascer. No nível
    /// normal, aiCount 0 e 1 dão SEMPRE 0 carros (Random.Range(0, 1) = 0) e aiCount 2 dá 0 a 2.
    /// </summary>
    [HarmonyPatch(typeof(SpawnContinueC), "SpawnAICars")]
    internal static class TrafficSpawnCountPatch
    {
        private static bool warned;
        private static bool loggedOverride;
        private static int savedAi = int.MinValue; // valor do save trocado pelo -coop-traffic durante a chamada

        private static void Prefix(SpawnContinueC __instance, out int __state)
        {
            __state = __instance.trafficAINodes != null ? __instance.trafficAINodes.Count : 0;
            // -coop-traffic=N: o jogo lê o aiCount do save só aqui dentro; troca só durante esta chamada (o save não muda)
            if (Plugin.TrafficOverride >= 0 && ES3.loadedSave != null)
            {
                savedAi = ES3.loadedSave.aiCount;
                ES3.loadedSave.aiCount = Plugin.TrafficOverride;
            }
        }

        private static void Postfix(SpawnContinueC __instance, int __state)
        {
            try
            {
                int left = __instance.trafficAINodes != null ? __instance.trafficAINodes.Count : 0;
                int ai = ES3.loadedSave != null ? ES3.loadedSave.aiCount : -1;
                if (savedAi != int.MinValue && !loggedOverride) { loggedOverride = true; Plugin.Log.LogInfo("Trânsito: usando -coop-traffic=" + ai + " (o save tem " + savedAi + ", não muda)."); }
                var rg = RouteGeneratorC.Global;
                string lvl = rg != null ? "nível " + rg.routeLevel + (rg.drivingTowardsHome ? ", indo pra casa" : "") : "nível ?";
                Plugin.Log.LogInfo("Trânsito: trecho " + __instance.name + ": " + (__state - left) + " carros (" + __state + " lugares; config aiCount=" + ai + ", " + lvl + ").");
                if (!warned && ai >= 0 && ai <= 1 && __state > 0)
                {
                    warned = true;
                    Plugin.Log.LogWarning("Trânsito: a config de trânsito do jogo está em " + ai + " (0-4): com ela quase todas as rotas ficam sem carros.");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("TrafficSpawnCount: " + e); }
        }

        /// <summary>Volta o valor do save sempre, até se o jogo der erro no meio (senão ele poderia ser salvo).</summary>
        private static Exception Finalizer(Exception __exception)
        {
            if (savedAi != int.MinValue && ES3.loadedSave != null) ES3.loadedSave.aiCount = savedAi;
            savedAi = int.MinValue;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SmartAICar), "Start")]
    internal static class TrafficStartPatch
    {
        private static void Postfix(SmartAICar __instance)
        {
            try { if (TrafficSync.Instance != null) TrafficSync.Instance.CarStarted(__instance); }
            catch (Exception e) { Plugin.Log.LogError("TrafficSync: " + e); }
        }
    }

    /// <summary>Tio: o carro do trânsito não dirige nem usa física sozinho (o host manda a posição).</summary>
    [HarmonyPatch(typeof(SmartAICar), "Update")]
    internal static class TrafficUpdatePatch
    {
        private static bool Prefix() { return !TrafficSync.IsClient; }
    }

    [HarmonyPatch(typeof(SmartAICar), "FixedUpdate")]
    internal static class TrafficFixedUpdatePatch
    {
        private static bool Prefix() { return !TrafficSync.IsClient; }
    }
}
