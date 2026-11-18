using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Loja do Laika: compra pelo catálogo (EngineComponentsCataloguePageC). Ao pagar, o jogo cria as peças
    /// (SpawnGo) nos pontos de entrega, só no jogo de quem comprou. Aqui quem comprou manda a lista (nomes dos
    /// prefabs) e o outro lado roda o mesmo SpawnGo: as peças nascem no mesmo lugar nos dois e o ItemSync
    /// casa pelo nome e posição. O dinheiro vai pelo ShopSync (o outro lado não paga de novo).
    /// </summary>
    internal static class LaikaSync
    {
        private static bool applying;

        public static void Init()
        {
            CoopNet.Instance.OnReliableData += OnReliableData;
        }

        private static bool Online
        {
            get { var n = CoopNet.Instance; return n != null && n.Connected && ItemSync.Instance != null && ItemSync.Instance.Ready; }
        }

        internal static void LocalSpawn(EngineComponentsCataloguePageC page)
        {
            if (applying || !Online || page.shoppingList == null || page.shoppingList.Count == 0) return;
            // nome do prefab + ponto de entrega de cada peça (o outro lado cria direto, sem precisar do catálogo,
            // que só existe/acorda no jogo de quem abriu)
            var names = new List<string>();
            var spots = new List<Vector3>();
            for (int i = 0; i < page.shoppingList.Count && i < page.spawnLocations.Length; i++)
            {
                if (page.shoppingList[i] == null) continue;
                names.Add(page.shoppingList[i].name);
                spots.Add(page.spawnLocations[i].position);
            }
            CoopNet.Instance.SendReliable(Msg.LaikaOrder, w =>
            {
                w.Write(names.Count);
                for (int i = 0; i < names.Count; i++) { w.Write(names[i]); NetIO.Put(w, spots[i]); }
            });
            Plugin.Log.LogInfo("Laika: comprei " + string.Join(", ", names.ToArray()));
        }

        private static void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.LaikaOrder) return;
            int count = r.ReadInt32();
            var names = new List<string>();
            var spots = new List<Vector3>();
            for (int i = 0; i < count; i++) { names.Add(r.ReadString()); spots.Add(NetIO.GetVector3(r)); }
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var prefab = FindPrefab(names[i]);
                    if (prefab == null) { Plugin.Log.LogWarning("Laika: peça " + names[i] + " não achada aqui."); continue; }
                    // igual ao SpawnGo do jogo
                    var go = UnityEngine.Object.Instantiate(prefab, spots[i], Quaternion.identity);
                    var op = go.GetComponent<ObjectPickupC>();
                    if (op != null) op.forceCollision = true;
                    var col = go.GetComponent<Collider>();
                    if (col != null) col.isTrigger = false;
                    var rb = go.GetComponent<Rigidbody>();
                    if (rb != null) rb.isKinematic = false;
                    if (op != null) go.transform.eulerAngles = op.throwRotAdjustment;
                }
                Plugin.Log.LogInfo("Laika: o outro jogador comprou " + string.Join(", ", names.ToArray()));
            }
            catch (Exception e) { Plugin.Log.LogError("LaikaSync: " + e); }
        }

        /// <summary>Lugar fixo do catálogo: o 1º ponto de entrega (o catálogo em si vai até a câmera quando abre).</summary>
        private static Vector3 Key(EngineComponentsCataloguePageC page)
        {
            if (page.spawnLocations != null && page.spawnLocations.Length > 0 && page.spawnLocations[0] != null)
                return page.spawnLocations[0].transform.position;
            return page.transform.position;
        }

        /// <summary>Prefab (asset, fora da cena) com esse nome e ObjectPickupC.</summary>
        private static GameObject FindPrefab(string name)
        {
            foreach (var op in Resources.FindObjectsOfTypeAll<ObjectPickupC>())
            {
                var go = op.gameObject;
                if (go.name != name || go.transform.parent != null) continue;
                if (go.scene.IsValid()) continue; // objeto da cena, não prefab
                return go;
            }
            return null;
        }
    }

    /// <summary>Diagnóstico: o que o catálogo do Laika sorteou (extras e pinturas) — tem que ser igual nos dois.</summary>
    [HarmonyPatch(typeof(EngineComponentsCataloguePageC), "Start")]
    internal static class LaikaStockLogPatch
    {
        private static void Postfix(EngineComponentsCataloguePageC __instance)
        {
            try
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var g in __instance.prefabExtrasSelected) if (g != null) names.Add(g.name);
                foreach (var g in __instance.prefabPaintDecalsSelected) if (g != null) names.Add(g.name);
                Plugin.Log.LogInfo("Laika: produtos do dia = " + string.Join(", ", names.ToArray()));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Laika: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(EngineComponentsCataloguePageC), "SpawnGo")]
    internal static class LaikaSpawnPatch
    {
        private static void Prefix(EngineComponentsCataloguePageC __instance)
        {
            try { LaikaSync.LocalSpawn(__instance); }
            catch (Exception e) { Plugin.Log.LogError("LaikaSync: " + e); }
        }
    }
}

namespace JalopyCoop.Sync
{
    /// <summary>Diagnóstico: carteira saindo da tela ao abrir o catálogo. Registra onde ela está por 4 s.</summary>
    [HarmonyLib.HarmonyPatch(typeof(EngineComponentsCataloguePageC), "PageGo")]
    internal static class WalletWatchPatch
    {
        private static void Postfix(EngineComponentsCataloguePageC __instance)
        {
            try { __instance.StartCoroutine(Watch(__instance)); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Carteira: " + e.Message); }
        }

        private static System.Collections.IEnumerator Watch(EngineComponentsCataloguePageC page)
        {
            var d = UnityEngine.GameObject.FindWithTag("Director");
            var wallet = d != null ? d.GetComponent<DirectorC>().wallet : null;
            if (wallet == null) yield break;
            var comps = new System.Collections.Generic.List<string>();
            foreach (var c in wallet.GetComponents<UnityEngine.Component>()) comps.Add(c.GetType().Name);
            Plugin.Log.LogInfo("Carteira: componentes = " + string.Join(",", comps.ToArray()) + " | pai = " + Path(wallet.transform.parent)
                + " | holder = " + (page.walletHolder != null ? Path(page.walletHolder) + " " + page.walletHolder.position : "null"));
            for (int i = 0; i < 16; i++)
            {
                var cam = UnityEngine.Camera.main;
                var vp = cam != null ? cam.WorldToViewportPoint(wallet.transform.position) : UnityEngine.Vector3.zero;
                Plugin.Log.LogInfo("Carteira t=" + (i * 0.25f).ToString("F2") + " mundo=" + wallet.transform.position + " local=" + wallet.transform.localPosition
                    + " tela=" + vp.ToString("F2") + " pai=" + (wallet.transform.parent != null ? wallet.transform.parent.name : "-")
                    + " iTweens=" + wallet.GetComponents<iTween>().Length + " timeScale=" + UnityEngine.Time.timeScale);
                yield return new UnityEngine.WaitForSecondsRealtime(0.25f);
            }
        }

        private static string Path(UnityEngine.Transform t)
        {
            if (t == null) return "-";
            string s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Carteira no catálogo do Laika: o jogo só move a carteira (iTween) até o WalletHolder, mas ela continua filha
    /// do quadro do inventário (FrameHolder/.../WalletLoc), num lugar fixo do mundo. Com a câmera mexendo, ela vai
    /// saindo da tela. Aqui ela vira filha do WalletHolder (que anda com a câmera) enquanto o catálogo está aberto
    /// e volta para o pai de antes ao fechar.
    /// </summary>
    internal static class WalletHoldPatch
    {
        private static GameObject Wallet
        {
            get { var d = GameObject.FindWithTag("Director"); return d != null ? d.GetComponent<DirectorC>().wallet : null; }
        }

        [HarmonyPatch(typeof(EngineComponentsCataloguePageC), "PageGo")]
        internal static class Open
        {
            private static void Postfix(EngineComponentsCataloguePageC __instance)
            {
                try
                {
                    var w = Wallet;
                    if (w == null || __instance.walletHolder == null) return;
                    iTween.Stop(w);
                    w.transform.SetParent(__instance.walletHolder, false);
                    w.transform.localPosition = Vector3.zero;
                    w.transform.localRotation = Quaternion.identity;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Carteira: " + e.Message); }
            }
        }

        private static void Restore(EngineComponentsCataloguePageC page)
        {
            var w = Wallet;
            if (w == null || page.walletHolder == null || w.transform.parent != page.walletHolder) return;
            var prev = Traverse.Create(page).Field("walletPrevParent").GetValue<Transform>();
            if (prev != null) w.transform.SetParent(prev, false); // o jogo depois põe a posição/rotação locais de antes
        }

        [HarmonyPatch(typeof(EngineComponentsCataloguePageC), "PageClose")]
        internal static class CloseA
        {
            private static void Prefix(EngineComponentsCataloguePageC __instance)
            {
                try { Restore(__instance); } catch (Exception e) { Plugin.Log.LogWarning("Carteira: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(EngineComponentsCataloguePageC), "Close")]
        internal static class CloseB
        {
            private static void Prefix(EngineComponentsCataloguePageC __instance)
            {
                try { Restore(__instance); } catch (Exception e) { Plugin.Log.LogWarning("Carteira: " + e.Message); }
            }
        }
    }
}
