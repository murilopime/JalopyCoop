using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4e: dinheiro e lojas.
    ///  - Carteira única: cada lado manda o quanto o dinheiro mudou por ação dele (compra, venda); o outro soma.
    ///    O host manda o valor certo a cada 5 s (corrige qualquer diferença).
    ///  - Itens pagos: ObjectPickupC.isPurchased (quem pagou manda; o outro marca como pago e tira da lista de
    ///    "não pagos" da loja, senão o portão da loja do outro ficaria fechado).
    ///  - Portão da loja (StoreC.gateClosed): abre/fecha nos dois.
    ///  - Item que some (vendido, gasto): o ItemSync avisa e o outro lado apaga o mesmo item.
    /// </summary>
    public class ShopSync : MonoBehaviour
    {
        private const float Interval = 0.25f;

        private float timer, fullTimer;
        private float knownMoney = float.NaN;
        private readonly Dictionary<int, bool> knownPurchased = new Dictionary<int, bool>();
        private readonly Dictionary<StoreC, bool> knownGate = new Dictionary<StoreC, bool>();
        private readonly Dictionary<StoreC, float> gateIgnoreUntil = new Dictionary<StoreC, float>();

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            ShopCounterSync.Init(); // balcão: sacola, visor, pagar, cancelar
            LaikaSync.Init();       // catálogo do Laika: peças compradas nascem nos dois
            net.OnConnectionChanged += c => { knownMoney = float.NaN; knownPurchased.Clear(); knownGate.Clear(); };
        }

        private static WalletC Wallet
        {
            get
            {
                var d = GameObject.FindWithTag("Director");
                var dc = d != null ? d.GetComponent<DirectorC>() : null;
                return dc != null && dc.wallet != null ? dc.wallet.GetComponent<WalletC>() : null;
            }
        }

        private static float Money(WalletC w) { return w.TotalWealth + w.incomingFunds; }

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
                PollMoney(net);
                PollPurchased(items);
                PollGates();
            }
            catch (Exception e) { Plugin.Log.LogError("ShopSync: " + e); }
        }

        // ================= carteira =================

        private void PollMoney(CoopNet net)
        {
            var w = Wallet;
            if (w == null) return;
            float now = Money(w);
            if (float.IsNaN(knownMoney)) knownMoney = now;
            if (Mathf.Abs(now - knownMoney) >= 0.5f)
            {
                float delta = now - knownMoney;
                knownMoney = now;
                net.SendReliable(Msg.MoneyDelta, wr => wr.Write(delta));
                Plugin.Log.LogInfo("Dinheiro: " + (delta > 0 ? "+" : "") + delta + " (meu), total " + now);
            }
            if (net.Mode == CoopMode.Host)
            {
                fullTimer += Interval;
                if (fullTimer >= 5f)
                {
                    fullTimer = 0f;
                    float total = now;
                    net.SendReliable(Msg.MoneyTotal, wr => wr.Write(total));
                }
            }
        }

        private void ApplyMoney(float target, string why)
        {
            var w = Wallet;
            if (w == null) return;
            if (Mathf.Abs(Money(w) - target) < 0.5f) { knownMoney = Money(w); return; }
            w.incomingFunds = 0f;
            w.TotalWealth = target;
            var d = GameObject.FindWithTag("Director");
            var dc = d != null ? d.GetComponent<DirectorC>() : null;
            if (dc != null) dc.totalWealth = target;
            w.ChangeMoneyPart2(); // redesenha as notas sem a animação (a animação tirava a carteira da tela)
            knownMoney = Money(w);
            Plugin.Log.LogInfo("Dinheiro: " + why + ", total " + target);
        }

        // ================= itens pagos =================

        private void PollPurchased(ItemSync items)
        {
            foreach (var kv in items.AllItems())
            {
                var op = kv.Value;
                if (op == null) continue;
                bool now = op.isPurchased;
                bool last;
                if (!knownPurchased.TryGetValue(kv.Key, out last)) { knownPurchased[kv.Key] = now; continue; }
                if (now == last) continue;
                knownPurchased[kv.Key] = now;
                int id = kv.Key;
                CoopNet.Instance.SendReliable(Msg.ItemPurchased, w => { w.Write(id); w.Write(now); });
                Plugin.Log.LogInfo("Loja: " + op.name + (now ? " pago." : " voltou a não pago."));
            }
        }

        private void ApplyPurchased(ObjectPickupC op, int id, bool purchased)
        {
            knownPurchased[id] = purchased;
            op.isPurchased = purchased;
            op.isInShoppingKart = false;
            if (purchased)
            {
                foreach (var store in UnityEngine.Object.FindObjectsOfType<StoreC>())
                {
                    if (store.unPaidCatalogue.Remove(op.gameObject)) { try { store.GateCheck(); } catch { } }
                }
            }
        }

        // ================= portões =================

        private static List<StoreC> Stores()
        {
            var list = new List<StoreC>(); foreach (var st in SceneCache.Get<StoreC>()) if (st != null) list.Add(st);
            // ordem igual nos dois (o mundo é igual): pela posição
            list.Sort((a, b) =>
            {
                var pa = a.transform.position; var pb = b.transform.position;
                int c = pa.x.CompareTo(pb.x);
                return c != 0 ? c : pa.z.CompareTo(pb.z);
            });
            return list;
        }

        private void PollGates()
        {
            float now = Time.realtimeSinceStartup;
            var stores = Stores();
            for (int i = 0; i < stores.Count; i++)
            {
                var s = stores[i];
                bool closed = s.gateClosed;
                bool last;
                if (!knownGate.TryGetValue(s, out last)) { knownGate[s] = closed; continue; }
                float until;
                if (gateIgnoreUntil.TryGetValue(s, out until) && now < until) continue;
                if (closed == last) continue;
                knownGate[s] = closed;
                var pos = s.transform.position;
                CoopNet.Instance.SendReliable(Msg.ShopGate, w => { NetIO.Put(w, pos); w.Write(closed); });
                Plugin.Log.LogInfo("Loja: portão " + (closed ? "fechou" : "abriu") + " (meu).");
            }
        }

        private void ApplyGate(Vector3 pos, bool closed)
        {
            StoreC best = null; float bd = 4f;
            foreach (var s in Stores())
            {
                float d = (s.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = s; }
            }
            if (best == null) { Plugin.Log.LogWarning("Loja: portão sem par aqui."); return; }
            knownGate[best] = closed;
            gateIgnoreUntil[best] = Time.realtimeSinceStartup + 1f;
            if (closed) best.GateClose(); else best.GateOpen();
            Plugin.Log.LogInfo("Loja: o outro jogador " + (closed ? "fechou" : "abriu") + " o portão.");
        }

        // ================= rede =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            try
            {
                switch (type)
                {
                    case Msg.MoneyDelta:
                    {
                        float delta = r.ReadSingle();
                        var w = Wallet;
                        if (w == null) return;
                        ApplyMoney(Money(w) + delta, "o outro jogador " + (delta > 0 ? "ganhou " : "gastou ") + Mathf.Abs(delta));
                        break;
                    }
                    case Msg.MoneyTotal:
                    {
                        float total = r.ReadSingle();
                        if (CoopNet.Instance.Mode == CoopMode.Client) ApplyMoney(total, "corrigido pelo host");
                        break;
                    }
                    case Msg.ItemPurchased:
                    {
                        int id = r.ReadInt32();
                        bool purchased = r.ReadBoolean();
                        var items = ItemSync.Instance;
                        var op = items != null && items.Ready ? items.Item(id) : null;
                        if (op != null) ApplyPurchased(op, id, purchased);
                        break;
                    }
                    case Msg.ShopGate:
                    {
                        var pos = NetIO.GetVector3(r);
                        bool closed = r.ReadBoolean();
                        ApplyGate(pos, closed);
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("ShopSync " + type + ": " + e); }
        }
    }
}
