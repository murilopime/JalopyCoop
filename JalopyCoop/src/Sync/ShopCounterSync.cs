using System;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Balcão da loja igual nos dois: itens na sacola, preço no visor, pagar e cancelar (alavanca).
    /// Quem está no balcão faz pelo jogo; o mod avisa cada passo e o outro lado repete o efeito
    /// (sem cobrar de novo: o dinheiro vai pelo ShopSync).
    /// </summary>
    internal static class ShopCounterSync
    {
        internal static bool Applying;

        private static bool Online
        {
            get { var n = CoopNet.Instance; return n != null && n.Connected && ItemSync.Instance != null && ItemSync.Instance.Ready; }
        }

        public static void Init()
        {
            CoopNet.Instance.OnReliableData += OnReliableData;
        }

        private static ShopC ShopAt(Vector3 pos)
        {
            ShopC best = null; float bd = 4f;
            foreach (var s in UnityEngine.Object.FindObjectsOfType<ShopC>())
            {
                float d = (s.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }

        // ================= local -> rede (patches abaixo) =================

        internal static void LocalAdded(ShopC shop, bool sell)
        {
            if (Applying || !Online || shop.shoppingKart.Count == 0) return;
            var op = shop.shoppingKart[shop.shoppingKart.Count - 1].GetComponent<ObjectPickupC>();
            int id;
            if (op == null || !ItemSync.Instance.TryGetId(op, out id)) return;
            var pos = shop.transform.position;
            CoopNet.Instance.SendReliable(Msg.ShopCounter, w => { w.Write((byte)(sell ? 2 : 1)); NetIO.Put(w, pos); w.Write(id); });
        }

        internal static void LocalPaid(ShopC shop)
        {
            if (Applying || !Online) return;
            var pos = shop.transform.position;
            CoopNet.Instance.SendReliable(Msg.ShopCounter, w => { w.Write((byte)3); NetIO.Put(w, pos); w.Write(-1); });
            Plugin.Log.LogInfo("Loja: paguei no balcão.");
        }

        internal static void LocalCancel(ShopC shop)
        {
            if (Applying || !Online) return;
            var pos = shop.transform.position;
            CoopNet.Instance.SendReliable(Msg.ShopCounter, w => { w.Write((byte)4); NetIO.Put(w, pos); w.Write(-1); });
        }

        // ================= bomba do posto =================
        // Quem abastece: cada litro soma 1 no ShopC.totalPrice e no fuelUsed SÓ no jogo dele (RefuelLogicC). Se o
        // outro fosse pagar, o balcão dele mostrava menos (até 0, e o portão abria pagando nada). Quando a mangueira
        // volta para a bomba (PlaceAtPump), manda quantos litros foram desde o último aviso; o outro soma igual.

        private static readonly System.Collections.Generic.Dictionary<RefuelLogicC, int> fuelReported = new System.Collections.Generic.Dictionary<RefuelLogicC, int>();

        private static int Units(RefuelLogicC pump) { return Mathf.Max(0, Mathf.RoundToInt(pump.fuelWatcher) - 1); }

        private static readonly System.Collections.Generic.Dictionary<RefuelLogicC, int> canReported = new System.Collections.Generic.Dictionary<RefuelLogicC, int>();

        /// <summary>Litros que foram para galões (o jogo conta à parte: no cancelar tira do galão, não do carro).</summary>
        private static int CanUnits(ShopC shop)
        {
            int n = 0;
            if (shop != null && shop.portableFuelRefilledNumbers != null) foreach (var k in shop.portableFuelRefilledNumbers) n += k;
            return n;
        }

        internal static void LocalFuel(RefuelLogicC pump)
        {
            if (Applying || !Online || pump == null || pump.shop == null) return;
            var shop = pump.shop.GetComponent<ShopC>();
            int units = Units(pump), cans = CanUnits(shop), last, lastCan;
            fuelReported.TryGetValue(pump, out last);
            canReported.TryGetValue(pump, out lastCan);
            if (units < last) { last = 0; lastCan = 0; } // pagou/cancelou (o jogo zera o contador)
            if (cans < lastCan) lastCan = 0;
            int delta = units - last, canDelta = Mathf.Clamp(cans - lastCan, 0, Mathf.Max(0, delta));
            fuelReported[pump] = units; canReported[pump] = cans;
            if (delta <= 0) return;
            int carDelta = delta - canDelta;
            var pos = pump.shop.transform.position;
            int packed = (carDelta & 0xFFFF) | (canDelta << 16);
            CoopNet.Instance.SendReliable(Msg.ShopCounter, w => { w.Write((byte)5); NetIO.Put(w, pos); w.Write(packed); });
            Plugin.Log.LogInfo("Posto: abasteci " + delta + " litro(s) (" + canDelta + " em galão); avisei o outro balcão.");
        }

        private static void AddFuel(ShopC shop, int packed)
        {
            int carDelta = packed & 0xFFFF, canDelta = (packed >> 16) & 0xFFFF, delta = carDelta + canDelta;
            if (delta <= 0) return;
            shop.totalPrice += delta;   // o preço conta tudo
            shop.fuelUsed += carDelta;  // o cancelar tira isto do CARRO: só os litros do carro
            var pump = shop.fuelHandle != null ? shop.fuelHandle.GetComponent<RefuelLogicC>() : null;
            if (pump != null)
            {
                pump.fuelUsed += delta;
                pump.fuelWatcher += delta;
                fuelReported[pump] = Units(pump); // não mandar de volta
                canReported[pump] = CanUnits(shop);
            }
            if (shop.visualPrice != null) shop.visualPrice.GetComponent<TextMesh>().text = shop.totalPrice + ".00";
            Plugin.Log.LogInfo("Posto: o outro jogador abasteceu " + delta + " litro(s) (" + canDelta + " em galão); total no balcão " + shop.totalPrice);
        }

        /// <summary>Paguei aqui: o jogo já zerou a bomba; esquece o que foi avisado.</summary>
        internal static void AfterLocalPay(ShopC shop)
        {
            var pump = shop != null && shop.fuelHandle != null ? shop.fuelHandle.GetComponent<RefuelLogicC>() : null;
            if (pump != null) { fuelReported[pump] = 0; canReported[pump] = 0; }
        }

        /// <summary>Pagou (aqui ou lá): zera os contadores da bomba como o ShopC.Payed.</summary>
        private static void ResetFuel(ShopC shop)
        {
            shop.fuelUsed = 0f;
            var pump = shop.fuelHandle != null ? shop.fuelHandle.GetComponent<RefuelLogicC>() : null;
            if (pump != null) { pump.fuelUsed = 0f; pump.fuelWatcher = 1f; fuelReported[pump] = 0; canReported[pump] = 0; }
            foreach (var c in shop.petrolCounters) if (c != null) { var e = c.transform.localEulerAngles; e.z = 0f; c.transform.localEulerAngles = e; }
            foreach (var c in shop.petrolPriceCounters) if (c != null) { var e = c.transform.localEulerAngles; e.z = 0f; c.transform.localEulerAngles = e; }
        }

        // ================= rede -> local =================

        private static void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.ShopCounter) return;
            int kind = r.ReadByte();
            var pos = NetIO.GetVector3(r);
            int id = r.ReadInt32();
            try
            {
                var shop = ShopAt(pos);
                if (shop == null) { Plugin.Log.LogWarning("Loja: balcão sem par aqui."); return; }
                Applying = true;
                try
                {
                    switch (kind)
                    {
                        case 1:
                        case 2: AddToKart(shop, id, kind == 2); break;
                        case 3: Paid(shop); break;
                        case 4: shop.CancelTransaction(); Plugin.Log.LogInfo("Loja: o outro jogador cancelou a compra."); break;
                        case 5: AddFuel(shop, id); break;
                    }
                }
                finally { Applying = false; }
            }
            catch (Exception e) { Plugin.Log.LogError("ShopCounterSync: " + e); }
        }

        /// <summary>Como ShopC.Trigger + PlaceObjectIntoShoppingBag(ConvertToMoney), sem a mão do jogador local.</summary>
        private static void AddToKart(ShopC shop, int id, bool sell)
        {
            var op = ItemSync.Instance.Item(id);
            if (op == null || shop.shoppingKart.Contains(op.gameObject)) return;
            ItemSync.Instance.ForgetRemote(op, 0f);
            var store = shop.transform.parent != null ? shop.transform.parent.GetComponent<StoreC>() : null;
            if (!sell && store != null && store.unPaidCatalogue.Count > 0) store.unPaidCatalogue.Remove(op.gameObject);
            if (shop.shutterOpen) shop.CloseShutter();
            var old = op.GetComponent<Rigidbody>();
            if (old != null) UnityEngine.Object.DestroyImmediate(old); // o jogo põe um Rigidbody novo
            op.transform.parent = null;
            Traverse.Create(shop).Field("currentlyProcessedObject").SetValue(op.gameObject);
            if (sell) shop.PlaceObjectIntoShoppingBagConvertToMoney();
            else shop.PlaceObjectIntoShoppingBag();
            Plugin.Log.LogInfo("Loja: o outro jogador pôs " + op.name + " no balcão (" + (sell ? "venda" : "compra") + "), total " + shop.totalPrice);
        }

        /// <summary>Como ShopC.Payed, sem mexer na carteira (o ShopSync já manda o dinheiro).</summary>
        private static void Paid(ShopC shop)
        {
            foreach (var g in shop.shoppingKart)
            {
                if (g == null) continue;
                var op = g.GetComponent<ObjectPickupC>();
                if (op != null) { op.isPurchased = true; op.isInShoppingKart = false; }
            }
            shop.shoppingKart.Clear();
            shop.totalPrice = 0f;
            ResetFuel(shop);
            if (shop.visualPrice != null) shop.visualPrice.GetComponent<TextMesh>().text = "00.00";
            var audio = shop.GetComponent<AudioSource>();
            if (audio != null && shop.payedAudio != null) audio.PlayOneShot(shop.payedAudio, 1f);
            shop.OpenShutter();
            var store = shop.transform.parent != null ? shop.transform.parent.GetComponent<StoreC>() : null;
            if (store != null) store.GateCheck();
            Plugin.Log.LogInfo("Loja: o outro jogador pagou.");
        }
    }

    [HarmonyPatch(typeof(ShopC), "PlaceObjectIntoShoppingBag")]
    internal static class ShopBagPatch
    {
        private static void Postfix(ShopC __instance) { ShopCounterSync.LocalAdded(__instance, false); }
    }

    [HarmonyPatch(typeof(ShopC), "PlaceObjectIntoShoppingBagConvertToMoney")]
    internal static class ShopSellPatch
    {
        private static void Postfix(ShopC __instance) { ShopCounterSync.LocalAdded(__instance, true); }
    }

    [HarmonyPatch(typeof(ShopC), "Payed")]
    internal static class ShopPaidPatch
    {
        private static void Postfix(ShopC __instance) { ShopCounterSync.LocalPaid(__instance); ShopCounterSync.AfterLocalPay(__instance); }
    }

    [HarmonyPatch(typeof(ShopC), "CancelTransaction")]
    internal static class ShopCancelPatch
    {
        private static void Postfix(ShopC __instance) { ShopCounterSync.LocalCancel(__instance); }
    }

    /// <summary>Mangueira de volta na bomba: avisa os litros abastecidos ao outro balcão.</summary>
    [HarmonyPatch(typeof(RefuelLogicC), "PlaceAtPump")]
    internal static class PumpPlacedPatch
    {
        private static void Postfix(RefuelLogicC __instance)
        {
            try { ShopCounterSync.LocalFuel(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Posto: " + e); }
        }
    }
}
