using System;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Depósito em casa (prancheta HomeStorageClipboardC): quem compra paga (o dinheiro já vai pelo ShopSync) e o jogo
    /// monta as prateleiras novas em PlaceOrder. Postfix no PlaceOrder manda o total de prateleiras; o outro lado monta
    /// as que faltam do mesmo jeito (mesmo prefab, mesmos lugares). Só por evento: nada de varrer a cena.
    /// Prefixo no PurchaseGo: o pedido não passa do espaço que sobrou (o outro pode ter comprado antes), senão o
    /// PlaceOrder do jogo cairia fora da lista de lugares.
    /// </summary>
    public class HomeStorageSync : MonoBehaviour
    {
        internal static bool Applying;

        private void Start()
        {
            CoopNet.Instance.OnReliableData += OnReliableData;
        }

        internal static int Room(HomeStorageClipboardC hs)
        {
            int max = hs.storageParents != null ? Mathf.Min(hs.storageMax, hs.storageParents.Length) : 0;
            return Mathf.Max(0, max - hs.storageInventory.Count);
        }

        internal static void LocalPlaced(HomeStorageClipboardC hs)
        {
            if (Applying) return;
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) return;
            int total = hs.storageInventory.Count;
            net.SendReliable(Msg.HomeStorage, w => w.Write(total));
            Plugin.Log.LogInfo("Depósito: comprei prateleira(s); agora são " + total + ". Avisando o outro jogador.");
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.HomeStorage) return;
            int total = r.ReadInt32();
            try
            {
                var hs = HomeStorageClipboardC.Global;
                if (hs == null) { Plugin.Log.LogWarning("Depósito: o outro jogador comprou prateleira, mas a casa não está carregada aqui."); return; }
                int added = 0;
                Applying = true;
                while (hs.storageInventory.Count < total && Room(hs) > 0)
                {
                    int i = hs.storageInventory.Count;
                    var parent = hs.storageParents[i];
                    var go = UnityEngine.Object.Instantiate(hs.storagePrefab, parent.position, parent.rotation);
                    go.transform.parent = parent;
                    hs.storageInventory.Add(go);
                    hs.currentStorage++;
                    added++;
                }
                // Pedido que eu tinha aberto não pode passar do espaço que sobrou.
                if (hs.storageOrder > Room(hs)) hs.storageOrder = Room(hs);
                Plugin.Log.LogInfo("Depósito: o outro jogador comprou prateleira(s); montei " + added + " (total " + hs.storageInventory.Count + ").");
            }
            catch (Exception e) { Plugin.Log.LogError("HomeStorageSync: " + e); }
            finally { Applying = false; }
        }
    }

    [HarmonyPatch(typeof(HomeStorageClipboardC), "PurchaseGo")]
    internal static class HomeStoragePurchasePatch
    {
        private static void Prefix(HomeStorageClipboardC __instance)
        {
            try
            {
                int room = HomeStorageSync.Room(__instance);
                if (__instance.storageOrder > room)
                {
                    Plugin.Log.LogInfo("Depósito: pedido de " + __instance.storageOrder + " passa do espaço (" + room + "); ajustei.");
                    __instance.storageOrder = room;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("HomeStorageSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(HomeStorageClipboardC), "PlaceOrder")]
    internal static class HomeStoragePlacePatch
    {
        private static void Prefix(HomeStorageClipboardC __instance, out int __state) { __state = __instance.storageInventory.Count; }

        private static void Postfix(HomeStorageClipboardC __instance, int __state)
        {
            try { if (__instance.storageInventory.Count > __state) HomeStorageSync.LocalPlaced(__instance); }
            catch (Exception e) { Plugin.Log.LogError("HomeStorageSync: " + e); }
        }
    }
}
