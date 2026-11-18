using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4b: estado do carro e das peças.
    ///  - Números das peças (EngineComponentC): condição, combustível, carga da bateria, sujeira, água, mistura de óleo.
    ///  - Galão (PortableFuelC), garrafa d'água (WaterBottleLogicC) e óleo (OilBottleC): nível.
    ///  - Vidros quebrados: o jogo não amassa o carro; batida forte baixa CarPerformanceC.carCondition e
    ///    CarLogicC.CarDamageUp mostra as rachaduras (damageTarget). O host manda carCondition; o tio aplica igual.
    ///
    /// O host manda o que mudar (gasto ao dirigir, desgaste, batidas). O tio só manda o que um jogador faz
    /// (abastecer, pôr água/óleo, consertar): valores que SOBEM, ou níveis de galão/garrafa que mudam.
    /// Assim o gasto que roda no jogo do tio nunca briga com o do host.
    /// </summary>
    public class PartSync : MonoBehaviour
    {
        public static PartSync Instance { get; private set; }

        private const float Interval = 0.5f;

        private struct Stats
        {
            public float cond, fuel, charge, dirt;
            public int mix, water, level, levelMix;
        }

        private readonly Dictionary<int, Stats> known = new Dictionary<int, Stats>(); // último enviado/recebido por id
        private float timer;
        private float fullTimer;
        private float sentCarCondition = float.NaN;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { known.Clear(); sentCarCondition = float.NaN; };
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            var items = ItemSync.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame || items == null || !items.Ready) return;
            timer += Time.unscaledDeltaTime;
            if (timer < Interval) return;
            timer = 0f;
            try
            {
                SendChanges(net.Mode == CoopMode.Host);
                if (net.Mode == CoopMode.Host) SendCarCondition();
            }
            catch (Exception e) { Plugin.Log.LogError("PartSync: " + e); }
        }

        // ================= ler / escrever os números =================

        private static bool Read(ObjectPickupC op, out Stats s)
        {
            s = new Stats();
            var ec = op.GetComponent<EngineComponentC>();
            var can = op.GetComponent<PortableFuelC>();
            var bottle = op.GetComponent<WaterBottleLogicC>();
            var oil = op.GetComponent<OilBottleC>();
            if (ec == null && can == null && bottle == null && oil == null) return false;
            if (ec != null)
            {
                s.cond = ec.Condition; s.fuel = ec.currentFuelAmount; s.charge = ec.charge; s.dirt = ec.dirtAccumilation;
                s.mix = ec.fuelMix; s.water = ec.currentWaterCharges;
            }
            if (can != null) { s.level = can.fuelLevel; s.levelMix = can.fuelMix; }
            else if (bottle != null) s.level = bottle.waterLevel;
            else if (oil != null) s.level = oil.oilLevel;
            return true;
        }

        private static void Write(ObjectPickupC op, Stats s)
        {
            var ec = op.GetComponent<EngineComponentC>();
            if (ec != null)
            {
                bool waterChanged = ec.currentWaterCharges != s.water;
                ec.Condition = s.cond; ec.currentFuelAmount = s.fuel; ec.charge = s.charge; ec.dirtAccumilation = s.dirt;
                ec.fuelMix = s.mix; ec.currentWaterCharges = s.water;
                if (ec.isBattery) ec.batteryDepleted = s.charge <= 0f;
                if (waterChanged && ec.totalWaterCharges > 0)
                {
                    // nível da água no reservatório (o jogo redesenha pelo WaterSupplyRelayC)
                    var relay = op.GetComponentInChildren<WaterSupplyRelayC>(true);
                    if (relay != null) { try { relay.WaterUpdate(); } catch { } }
                }
            }
            // Galão / garrafa / óleo: além do número, redesenha o líquido (o jogo só faz isso quando o próprio jogador usa).
            var can = op.GetComponent<PortableFuelC>();
            if (can != null && (can.fuelLevel != s.level || can.fuelMix != s.levelMix))
            {
                can.fuelLevel = s.level; can.fuelMix = s.levelMix;
                try { can.FuelUpdate(); } catch { }
            }
            var bottle = op.GetComponent<WaterBottleLogicC>();
            if (bottle != null && bottle.waterLevel != s.level)
            {
                bottle.waterLevel = s.level;
                try { bottle.WaterUpdate(); } catch { }
            }
            var oil = op.GetComponent<OilBottleC>();
            if (oil != null && oil.oilLevel != s.level)
            {
                oil.oilLevel = s.level;
                try { oil.OilUpdate(); } catch { }
            }
        }

        private static bool HostChanged(Stats a, Stats b)
        {
            return Mathf.Abs(a.cond - b.cond) > 0.25f || Mathf.Abs(a.fuel - b.fuel) > 0.05f || Mathf.Abs(a.charge - b.charge) > 0.02f
                || Mathf.Abs(a.dirt - b.dirt) > 0.25f || a.mix != b.mix || a.water != b.water || a.level != b.level || a.levelMix != b.levelMix;
        }

        /// <summary>Tio: só ações do jogador (valores que sobem, sujeira que desce, níveis/mistura que mudam).</summary>
        private static bool ClientAction(Stats now, Stats last)
        {
            return now.cond > last.cond + 0.5f || now.fuel > last.fuel + 0.1f || now.charge > last.charge + 0.05f
                || now.dirt < last.dirt - 0.5f || now.water > last.water || now.mix != last.mix
                || now.level != last.level || now.levelMix != last.levelMix;
        }

        private void SendChanges(bool host)
        {
            var items = ItemSync.Instance;
            var batch = new List<KeyValuePair<int, Stats>>();
            bool fullSync = false;
            if (host)
            {
                fullTimer += Interval;
                if (fullTimer >= 5f) { fullTimer = 0f; fullSync = true; }
            }
            foreach (var kv in items.AllItems())
            {
                var op = kv.Value;
                if (op == null) continue;
                Stats now;
                if (!Read(op, out now)) continue;
                Stats last;
                if (!known.TryGetValue(kv.Key, out last))
                {
                    known[kv.Key] = now;           // primeira leitura: referência
                    if (host) batch.Add(new KeyValuePair<int, Stats>(kv.Key, now)); // host manda tudo uma vez
                    continue;
                }
                bool send = host ? (fullSync || HostChanged(now, last)) : ClientAction(now, last);
                if (!send) continue;
                known[kv.Key] = now;
                batch.Add(new KeyValuePair<int, Stats>(kv.Key, now));
            }
            if (batch.Count == 0) return;
            CoopNet.Instance.SendReliable(Msg.ItemStats, w =>
            {
                w.Write(batch.Count);
                foreach (var kv in batch)
                {
                    var s = kv.Value;
                    w.Write(kv.Key);
                    w.Write(s.cond); w.Write(s.fuel); w.Write(s.charge); w.Write(s.dirt);
                    w.Write(s.mix); w.Write(s.water); w.Write(s.level); w.Write(s.levelMix);
                }
            });
            if (!host) Plugin.Log.LogInfo("Peças: mandei " + batch.Count + " mudança(s) ao host (abastecer/consertar/água/óleo).");
        }

        // ================= vidros quebrados (condição da lataria) =================

        private void SendCarCondition()
        {
            var cl = GameRefs.CarLogic;
            var perf = cl != null ? cl.GetComponent<CarPerformanceC>() : null;
            if (perf == null) return;
            float c = perf.carCondition;
            if (!float.IsNaN(sentCarCondition) && Mathf.Abs(c - sentCarCondition) < 0.05f) return;
            sentCarCondition = c;
            CoopNet.Instance.SendReliable(Msg.CarCondition, w => w.Write(c));
        }

        private static void ApplyCarCondition(BinaryReader r)
        {
            float c = r.ReadSingle();
            var cl = GameRefs.CarLogic;
            var perf = cl != null ? cl.GetComponent<CarPerformanceC>() : null;
            if (perf == null) return;
            bool worse = c < perf.carCondition - 0.05f;
            perf.carCondition = c;
            cl.CarDamageUp(); // rachaduras dos vidros conforme a condição
            if (worse) Plugin.Log.LogInfo("Carro: batida no host, condição " + c.ToString("0.0") + "/" + perf.carDurability.ToString("0.0"));
        }

        // ================= rede =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.ItemStats && type != Msg.CarCondition) return;
            try
            {
                if (type == Msg.CarCondition) { if (CoopNet.Instance.Mode == CoopMode.Client) ApplyCarCondition(r); return; }

                var items = ItemSync.Instance;
                if (items == null || !items.Ready) return;
                int n = r.ReadInt32();
                bool partsChanged = false, tyresChanged = false;
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32();
                    var s = new Stats
                    {
                        cond = r.ReadSingle(), fuel = r.ReadSingle(), charge = r.ReadSingle(), dirt = r.ReadSingle(),
                        mix = r.ReadInt32(), water = r.ReadInt32(), level = r.ReadInt32(), levelMix = r.ReadInt32()
                    };
                    var op = items.Item(id);
                    if (op == null) continue;
                    Stats before;
                    Read(op, out before);
                    Write(op, s);
                    known[id] = s;
                    if (Mathf.Abs(before.cond - s.cond) > 0.25f)
                    {
                        var ec = op.GetComponent<EngineComponentC>();
                        if (ec != null && ec.bolt != null) tyresChanged = true; else partsChanged = true;
                    }
                    if (CoopNet.Instance.Mode == CoopMode.Host)
                        Plugin.Log.LogInfo("Peças: o tio mudou " + op.name + " (cond " + s.cond.ToString("0.0") + ", comb " + s.fuel.ToString("0.0") + ", nível " + s.level + ")");
                }
                RefreshCar(partsChanged, tyresChanged);
            }
            catch (Exception e) { Plugin.Log.LogError("PartSync " + type + ": " + e); }
        }

        /// <summary>Recalcula desempenho/fumaça e aderência depois de mudar condição de peças instaladas.</summary>
        private static void RefreshCar(bool parts, bool tyres)
        {
            var cl = GameRefs.CarLogic;
            var perf = cl != null ? cl.GetComponent<CarPerformanceC>() : null;
            if (perf == null) return;
            try
            {
                if (parts) perf.UpdatePerformanceTopSpeed(); // fumaça do motor conforme a condição
                if (tyres) perf.UpdateAllWheelGrip();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Peças: recalcular carro: " + e.Message); }
        }
    }
}
