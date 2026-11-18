using System;
using System.IO;
using System.Text;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4d: rota escolhida no mapa igual nos dois.
    ///
    /// Os dois podem escolher no mapa (como no jogo). Quem cria é sempre o host: se o tio escolhe, vai um
    /// pedido (RouteRequest) e o host cria a rota como se tivesse escolhido. Quando o jogo do host cria a rota
    /// (MapRelayC.SpawnRoute), o mod manda a escolha (RouteChosen) e o tio cria a mesma. Vale a 1ª que chegar.
    /// O sorteio fixo (SeededRandom) faz os trechos, cidades e lojas saírem iguais.
    ///
    /// Para conferir: os dois escrevem no log a "impressão digital" das opções do mapa e do mundo criado.
    /// </summary>
    public class RouteSync : MonoBehaviour
    {
        public static RouteSync Instance { get; private set; }
        internal static bool ApplyingRemote;

        private float fingerprintAt = -1f;
        private bool optionsLogged;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { optionsLogged = false; };
            InvokeRepeating("ResetRouteFlag", 5f, 5f);
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            if (net.Mode == CoopMode.Host) ResetRouteFlag();
            if (!optionsLogged && RouteGeneratorC.Global != null)
            {
                optionsLogged = true;
                Plugin.Log.LogInfo("Rota: opções do mapa = " + OptionsFingerprint(RouteGeneratorC.Global));
            }
            if (fingerprintAt > 0f && Time.realtimeSinceStartup >= fingerprintAt)
            {
                fingerprintAt = -1f;
                Plugin.Log.LogInfo("Rota: mundo criado = " + WorldFingerprint(RouteGeneratorC.Global));
            }
        }

        /// <summary>Nova viagem (chegou na cidade, rota apagada): libera escolher de novo.</summary>
        private void ResetRouteFlag()
        {
            var rg = RouteGeneratorC.Global;
            // rota apagada ao chegar/dormir (RouteGeneratorC.DeleteRoute zera routeGenerated e a lista de trechos)
            // (o laço do DeleteRoute não esvazia a lista inteira, então só o routeGenerated vale)
            if (routeStarted && rg != null && !rg.routeGenerated) { routeStarted = false; Plugin.Log.LogInfo("Rota: viagem nova, dá para escolher a rota de novo."); }
        }

        // ================= outras opções de rota (teste) =================

        /// <summary>HOST (F4): nova semente do mundo; os dois refazem as opções do mapa iguais.</summary>
        internal string HostReroll()
        {
            var rg = RouteGeneratorC.Global;
            if (rg == null) return "Sem gerador de rota.";
            if (routeStarted || rg.spawnedRouteSegments.Count > 1) return "Já tem rota criada nesta viagem; sorteie antes de escolher.";
            uint salt = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            ApplyReroll(salt);
            var net = CoopNet.Instance;
            if (net != null && net.Connected) net.SendReliable(Msg.WorldSeed, w => w.Write(salt));
            return "Outras rotas sorteadas (semente " + salt.ToString("X8") + "). Olhe o mapa.";
        }

        /// <summary>Como o jogo faz depois de dormir (BedLogicC): sorteia as rotas de novo e atualiza o mapa.</summary>
        private static void ApplyReroll(uint salt)
        {
            SeededRandom.SetSalt(salt);
            var rg = RouteGeneratorC.Global;
            if (rg == null) return;
            rg.RouteDistances();
            foreach (var m in Resources.FindObjectsOfTypeAll<MapRelayC>())
            {
                if (m == null || !m.gameObject.scene.IsValid()) continue;
                if (m.routeNumber >= 1 && m.routeNumber <= 4) { try { m.SetRouteToMap(); } catch { } }
            }
            if (rg.mapObject != null) { var ml = rg.mapObject.GetComponent<MapLogicC>(); if (ml != null) { try { ml.NewLevelUpdate(); } catch { } } }
            Plugin.Log.LogInfo("Rota: novas opções (semente " + salt.ToString("X8") + ") = " + OptionsFingerprint(rg));
        }

        /// <summary>
        /// O que o clique no mapa faz antes de criar a rota (MapRelayC.LockMap): trava o mapa e marca
        /// routeGenerated (sem isso o guarda da fronteira pede para escolher a rota de novo). loadRouteGo = true para o
        /// Update do mapa não criar a rota uma segunda vez.
        /// </summary>
        internal static void LockLikeClick(MapRelayC relay)
        {
            try { relay.LockMap(); }
            catch (Exception e) { Plugin.Log.LogWarning("Rota: LockMap: " + e.Message); }
            relay.loadRouteGo = true;
            var rg = RouteGeneratorC.Global;
            if (rg != null) rg.routeGenerated = true;
        }

        // ================= host escolhe =================

        internal void OnHostSpawnRoute(MapRelayC relay)
        {
            var net = CoopNet.Instance;
            var rg = RouteGeneratorC.Global;
            if (net == null || !net.Connected || net.Mode != CoopMode.Host || rg == null) return;
            routeStarted = true;
            // o plano (estrada boa/ruim/lama, clima...) chega antes da escolha: o tio cria com os números do host
            if (RoutePlanSync.Instance != null) RoutePlanSync.Instance.SendIfChanged(true);
            int number = relay.routeNumber, distance = relay.distance;
            bool home = rg.drivingTowardsHome;
            int level = rg.routeLevel;
            net.SendReliable(Msg.RouteChosen, w => { w.Write(number); w.Write(distance); w.Write(home); w.Write(level); });
            Plugin.Log.LogInfo("Rota: host escolheu a rota " + number + " (" + distance + " trechos, nível " + level + ", indo pra casa " + home + ")");
            fingerprintAt = Time.realtimeSinceStartup + 12f;
        }

        // ================= tio repete =================

        private bool routeStarted; // host: já criou rota nesta viagem (ignora pedido atrasado do tio)
        internal bool RouteStarted { get { return routeStarted; } }

        /// <summary>Tio escolheu no mapa dele: manda para o host em vez de criar sozinho.</summary>
        internal void ClientRequest(MapRelayC relay)
        {
            var rg = RouteGeneratorC.Global;
            int number = relay.routeNumber, distance = relay.distance;
            bool home = rg != null && rg.drivingTowardsHome;
            CoopNet.Instance.SendReliable(Msg.RouteRequest, w => { w.Write(number); w.Write(distance); w.Write(home); });
            Plugin.Log.LogInfo("Rota: o tio escolheu a rota " + number + "; pedido mandado ao host.");
        }

        private void HostApplyRequest(BinaryReader r)
        {
            int number = r.ReadInt32(), distance = r.ReadInt32();
            bool home = r.ReadBoolean();
            var rg = RouteGeneratorC.Global;
            if (rg == null) return;
            if (routeStarted) { Plugin.Log.LogInfo("Rota: pedido do tio ignorado (rota já criada)."); return; }
            rg.drivingTowardsHome = home;
            MapRelayC relay = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<MapRelayC>())
                if (m != null && m.gameObject.scene.IsValid() && m.routeNumber == number) { relay = m; break; }
            if (relay == null) { Plugin.Log.LogWarning("Rota: rota " + number + " do tio não achada no mapa do host."); return; }
            relay.distance = distance;
            LockLikeClick(relay);
            Plugin.Log.LogInfo("Rota: o tio escolheu a rota " + number + "; criando no host.");
            relay.SpawnRoute(); // o postfix manda RouteChosen ao tio
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type == Msg.WorldSeed)
            {
                uint salt = r.ReadUInt32();
                if (CoopNet.Instance.Mode == CoopMode.Client)
                {
                    try { ApplyReroll(salt); } catch (Exception e) { Plugin.Log.LogError("RouteSync semente: " + e); }
                }
                return;
            }
            if (type == Msg.RouteRequest)
            {
                if (CoopNet.Instance.Mode == CoopMode.Host)
                {
                    try { HostApplyRequest(r); } catch (Exception e) { Plugin.Log.LogError("RouteSync pedido: " + e); }
                }
                return;
            }
            if (type != Msg.RouteChosen) return;
            int number = r.ReadInt32(), distance = r.ReadInt32();
            bool home = r.ReadBoolean();
            int level = r.ReadInt32();
            if (CoopNet.Instance.Mode != CoopMode.Client) return;
            try
            {
                var rg = RouteGeneratorC.Global;
                if (rg == null) { Plugin.Log.LogWarning("Rota: sem RouteGeneratorC no tio."); return; }
                if (rg.routeLevel != level) Plugin.Log.LogWarning("Rota: nível diferente (host " + level + ", tio " + rg.routeLevel + ").");
                rg.drivingTowardsHome = home;

                // o mesmo que MapRelayC.SpawnRoute faz no jogo, com os números do host
                MapRelayC relay = null;
                foreach (var m in Resources.FindObjectsOfTypeAll<MapRelayC>())
                    if (m != null && m.gameObject.scene.IsValid() && m.routeNumber == number) { relay = m; break; }
                ApplyingRemote = true;
                try
                {
                    if (relay != null)
                    {
                        relay.distance = distance;
                        LockLikeClick(relay);
                        relay.SpawnRoute();
                    }
                    else
                    {
                        rg.routeChosenLength = distance;
                        rg.routeGenerated = true;
                        AccessTools.Method(typeof(RouteGeneratorC), "SpawnRoute" + number).Invoke(rg, null);
                    }
                }
                finally { ApplyingRemote = false; }
                Plugin.Log.LogInfo("Rota: criei a rota " + number + " escolhida pelo host (" + distance + " trechos).");
                fingerprintAt = Time.realtimeSinceStartup + 12f;
            }
            catch (Exception e) { Plugin.Log.LogError("RouteSync: " + e); }
        }

        // ================= impressões digitais (conferir nos dois logs) =================

        internal static string OptionsFingerprint(RouteGeneratorC rg)
        {
            var sb = new StringBuilder();
            sb.Append("destino ").Append(rg.destinationNumber).Append(" |");
            AppendNames(sb, rg.route1Segments);
            AppendNames(sb, rg.route2Segments);
            AppendNames(sb, rg.route3Segments);
            return "#" + SeededRandom.Hash(sb.ToString()).ToString("X8") + " (" + sb + ")";
        }

        private static void AppendNames(StringBuilder sb, GameObject[] list)
        {
            if (list == null) return;
            foreach (var g in list) sb.Append(g != null ? g.name : "-").Append(',');
            sb.Append('|');
        }

        private static string WorldFingerprint(RouteGeneratorC rg)
        {
            if (rg == null) return "?";
            var sb = new StringBuilder();
            int n = 0;
            foreach (var g in rg.spawnedRouteSegments)
            {
                if (g == null) continue;
                var p = g.transform.position;
                sb.Append(g.name).Append('@').Append(Mathf.RoundToInt(p.x)).Append(',').Append(Mathf.RoundToInt(p.z)).Append(';');
                n++;
            }
            if (rg.spawnedHub != null) sb.Append("hub=").Append(rg.spawnedHub.name);
            int items = UnityEngine.Object.FindObjectsOfType<ObjectPickupC>().Length;
            return "#" + SeededRandom.Hash(sb.ToString()).ToString("X8") + " (" + n + " trechos, " + items + " itens, " + (rg.spawnedHub != null ? rg.spawnedHub.name : "sem cidade") + ")";
        }
    }

    [HarmonyPatch(typeof(MapRelayC), "SpawnRoute")]
    internal static class MapSpawnRoutePatch
    {
        /// <summary>Tio conectado: a rota vem do host (não cria sozinho pelo mapa dele).</summary>
        private static bool ran;

        private static bool Prefix()
        {
            ran = Decide();
            return ran;
        }

        private static bool Decide()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) return true;
            if (net.Mode == CoopMode.Host)
            {
                // a rota desta viagem já foi criada (ex.: o tio escolheu antes): não cria outra
                if (RouteSync.Instance != null && RouteSync.Instance.RouteStarted)
                {
                    Plugin.Log.LogInfo("Rota: já existe rota nesta viagem (escolhida pelo tio).");
                    return false;
                }
                return true;
            }
            if (RouteSync.ApplyingRemote) return true;
            return false; // tio: vira pedido ao host (Postfix), a rota vem do host
        }

        private static void Postfix(MapRelayC __instance)
        {
            if (RouteSync.Instance == null) return;
            var net = CoopNet.Instance;
            bool hostSkipped = !ran && net != null && net.Connected && net.Mode == CoopMode.Host;
            if (hostSkipped) return; // a rota não foi criada aqui: não manda escolha ao tio
            if (net != null && net.Connected && net.Mode == CoopMode.Client)
            {
                if (!RouteSync.ApplyingRemote) RouteSync.Instance.ClientRequest(__instance);
                return;
            }
            RouteSync.Instance.OnHostSpawnRoute(__instance);
        }
    }
}
