using System;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Upgrades do carro iguais nos dois: bagageiro (0), para-choque (1), para-lamas (2), painel digital (3),
    /// suporte de faróis (4) e suporte de ferramentas nível 1-3 (5-7) — os ids do ExtraComponentC.
    ///  - Instalar: o jogo abre a caixa junto do carro e chama ExtraComponentC.ActionPart2 → ExtraUpgradesC.*Fitted.
    ///    Postfix no ActionPart2 (só na instalação de verdade, não no LoadExtras do save) manda id + peso; o outro lado
    ///    chama o mesmo *Fitted. A caixa some pelo ItemSync (ItemGone). Adesivo continua no CarLookSync.
    ///  - Entrar no jogo: o tio pede os upgrades do host (o save que o host manda pode ser de antes de instalar).
    /// Só por evento e por pedido: nada de varrer a cena.
    /// </summary>
    public class UpgradeSync : MonoBehaviour
    {
        public static UpgradeSync Instance { get; private set; }
        internal static bool Applying;

        private bool gotState;
        private float inGameSince = -1f;
        private float askTimer;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnReliable += m => { if (m == Msg.UpgradeAsk) SendState(); };
            net.OnConnectionChanged += c => { gotState = false; inGameSince = -1f; };
        }

        private static ExtraUpgradesC Upgrades()
        {
            var cl = GameRefs.CarLogic;
            return cl != null ? cl.GetComponent<ExtraUpgradesC>() : null;
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || net.Mode != CoopMode.Client || gotState) return;
            if (!GameRefs.InGame) { inGameSince = -1f; return; }
            if (inGameSince < 0f) { inGameSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - inGameSince < 5f) return; // deixa o LoadExtras do save rodar antes
            askTimer -= Time.unscaledDeltaTime;
            if (askTimer > 0f) return;
            askTimer = 3f;
            net.SendReliable(Msg.UpgradeAsk);
        }

        // ================= instalar =================

        internal static void LocalFitted(int id, int weight)
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) return;
            net.SendReliable(Msg.UpgradeFitted, w => { w.Write(id); w.Write(weight); });
            Plugin.Log.LogInfo("Upgrades: instalei " + Name(id) + " (peso " + weight + "); avisando o outro jogador.");
        }

        /// <summary>Instala como o jogo (mesmo *Fitted do ActionPart2), se ainda não tiver.</summary>
        private static bool Apply(ExtraUpgradesC up, int id, int weight)
        {
            Applying = true;
            try
            {
                switch (id)
                {
                    case 0: if (up.roofRackInstalled) return false; up.RoofRackFitted(weight); return true;
                    case 1: if (up.bullBarInstalled) return false; up.BullBarFitted(weight); return true;
                    case 2: if (up.mudGuardsInstalled) return false; up.MudGuardsFitted(weight); return true;
                    case 3: if (up.dashUIInstalled) return false; up.DigitalDashFitted(weight); return true;
                    case 4: if (up.lightRackInstalled) return false; up.LightRackFitted(weight); return true;
                    case 5: case 6: case 7:
                    {
                        int lvl = id - 4;
                        if (up.toolRackInstalled && up.toolRackLevel >= lvl) return false;
                        // sem suporte ainda: ToolRackLoad liga a peça (Action) e já sobe para o nível; com suporte: sobe de nível
                        if (!up.toolRackInstalled) up.ToolRackLoad(weight, lvl);
                        else up.ToolRackFitted(weight, lvl);
                        return true;
                    }
                }
                return false;
            }
            finally { Applying = false; }
        }

        internal static string Name(int id)
        {
            switch (id)
            {
                case 0: return "bagageiro";
                case 1: return "para-choque";
                case 2: return "para-lamas";
                case 3: return "painel digital";
                case 4: return "suporte de faróis";
                case 5: case 6: case 7: return "suporte de ferramentas nível " + (id - 4);
            }
            return "upgrade " + id;
        }

        // ================= estado (ao entrar) =================

        private static void SendState()
        {
            var net = CoopNet.Instance;
            var up = Upgrades();
            if (net == null || net.Mode != CoopMode.Host || up == null) return;
            net.SendReliable(Msg.UpgradeState, w =>
            {
                w.Write(up.roofRackInstalled); w.Write(up.roofRackWeight);
                w.Write(up.bullBarInstalled); w.Write(up.bullBarWeight);
                w.Write(up.mudGuardsInstalled); w.Write(up.mudGuardsWeight);
                w.Write(up.dashUIInstalled); w.Write(up.dashUIWeight);
                w.Write(up.lightRackInstalled); w.Write(up.lightRackWeight);
                w.Write(up.toolRackInstalled); w.Write(up.toolRackWeight); w.Write(up.toolRackLevel);
            });
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            try
            {
                if (type == Msg.UpgradeFitted)
                {
                    int id = r.ReadInt32(), weight = r.ReadInt32();
                    var up = Upgrades();
                    if (up == null) { Plugin.Log.LogWarning("Upgrades: o outro instalou " + Name(id) + ", mas não achei o carro aqui."); return; }
                    bool did = Apply(up, id, weight);
                    Plugin.Log.LogInfo("Upgrades: o outro jogador instalou " + Name(id) + (did ? "; instalado aqui também." : "; aqui já tinha."));
                }
                else if (type == Msg.UpgradeState)
                {
                    bool[] has = new bool[6]; int[] wt = new int[6]; int lvl;
                    for (int i = 0; i < 5; i++) { has[i] = r.ReadBoolean(); wt[i] = r.ReadInt32(); }
                    has[5] = r.ReadBoolean(); wt[5] = r.ReadInt32(); lvl = r.ReadInt32();
                    if (CoopNet.Instance.Mode != CoopMode.Client) return;
                    var up = Upgrades();
                    if (up == null) return;
                    gotState = true;
                    int added = 0;
                    for (int i = 0; i < 5; i++) if (has[i] && Apply(up, i, wt[i])) added++;
                    if (has[5] && lvl >= 1 && lvl <= 3 && Apply(up, 4 + lvl, wt[5])) added++;
                    Plugin.Log.LogInfo("Upgrades: estado do host recebido" + (added > 0 ? "; instalei " + added + " que faltavam aqui." : "; igual ao meu."));
                }
            }
            catch (Exception e) { Plugin.Log.LogError("UpgradeSync " + type + ": " + e); }
        }
    }

    /// <summary>A instalação de verdade (caixa aberta junto do carro). Adesivo fica com o CarLookSync.</summary>
    [HarmonyPatch(typeof(ExtraComponentC), "ActionPart2")]
    internal static class UpgradeFittedPatch
    {
        private static void Postfix(ExtraComponentC __instance)
        {
            try
            {
                if (UpgradeSync.Applying || __instance.isCustomDecal) return;
                var ec = __instance.GetComponent<EngineComponentC>();
                int weight = ec != null ? Mathf.RoundToInt(ec.weight) : 0;
                UpgradeSync.LocalFitted(__instance.componentID, weight);
            }
            catch (Exception e) { Plugin.Log.LogError("UpgradeFitted: " + e); }
        }
    }
}
