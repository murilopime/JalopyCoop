using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Coisas pequenas dos prédios iguais nos dois (motel, casa):
    ///  - Registros/torneiras (OvenDialC: currentPos 0 fechado, 1 aberto; a torneira é o FaucetLogicC alvo dele).
    ///  - Tampas/portinholas (ToiletSeatLogicC: tampa da privada, geladeira...: currentPos 0/1).
    ///  - Descarga (ToiletFlushC: um puxão; vai como evento).
    ///  - Abrir/fechar: cortinas (CurtainsLogicC.open), porta do forno (OvenDoorC), gavetas (PushDrawC), portinhas/
    ///    caixa de pão/armários (BreadBinDoorC) e quebra-sol do carro (MirrorLogicC). Os do carro vão no espaço do carro
    ///    (o carro do tio chega um pouco atrasado). TriggerClose (fechar porque bateu/abriu o vizinho) vai como fechar.
    /// Quem mexe pelo jogo manda (tipo, posição, estado); o outro chama o mesmo Trigger do jogo.
    /// Vai pelo clique (patch no Trigger), sem varrer a cena toda hora (isso dava lag).
    /// </summary>
    public class InteractSync : MonoBehaviour
    {
        private const byte Dial = 1, Lid = 2, Flush = 3, Briefcase = 4, Letter = 5,
            Curtain = 6, OvenDoor = 7, Drawer = 8, BinDoor = 9, Visor = 10;
        private const byte CarSpace = 0x80; // bit no tipo: posição no espaço do carro

        internal static bool Applying;

        private void Start()
        {
            Instance = this;
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
        }

        /// <summary>Registro/torneira ou tampa clicado aqui: manda o estado que vai ficar (o Trigger alterna).</summary>
        internal static void LocalToggle(Component c, byte kind, int currentPos)
        {
            if (Applying) return;
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            int state = currentPos == 0 ? 1 : 0;
            SendState(c, kind, state);
            Plugin.Log.LogInfo("Objeto: " + c.name + (kind == Dial ? (state == 1 ? " aberto (torneira/registro)" : " fechado (torneira/registro)") : (state == 1 ? " aberto" : " fechado")));
        }

        internal const byte KindDial = Dial, KindLid = Lid, KindCurtain = Curtain, KindOvenDoor = OvenDoor,
            KindDrawer = Drawer, KindBinDoor = BinDoor, KindVisor = Visor;

        /// <summary>Abrir/fechar clicado aqui (ou fechado pelo jogo): manda o estado que vai ficar.</summary>
        internal static void LocalState(Component c, byte kind, int state)
        {
            if (Applying) return;
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            SendState(c, kind, state);
            Plugin.Log.LogInfo("Objeto: " + c.name + (state == 1 ? " aberto" : " fechado"));
        }

        private static void SendState(Component c, byte kind, int state)
        {
            var car = GameRefs.Car;
            byte k = kind;
            var pos = c.transform.position;
            if (car != null && c.transform.IsChildOf(car)) { k |= CarSpace; pos = car.InverseTransformPoint(pos); }
            CoopNet.Instance.SendReliable(Msg.Interact, w => { w.Write(k); NetIO.Put(w, pos); w.Write(state); });
        }

        /// <summary>Carta da maleta lida (carimbo do diário): vale para os dois (o host salva).</summary>
        internal static void LocalLetter(int id)
        {
            if (Applying) return;
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) return;
            net.SendReliable(Msg.Interact, w => { w.Write(Letter); NetIO.Put(w, Vector3.zero); w.Write(id); });
            Plugin.Log.LogInfo("Carta: li a carta " + id + " (carimbo no diário).");
        }

        /// <summary>
        /// Manhã depois de dormir: a maleta do tio abre no carro com a carta do país (no co-op o tio do jogo não deita,
        /// então a maleta não abria de noite e as cartas se perdiam). É o que o jogo faz quando o tio não está
        /// (UncleLogicC.UncleGoneForeverLogic → OpenBriefcaseLogicC.CarTrigger). Roda igual nos dois.
        /// </summary>
        internal static void OpenUncleBriefcaseForMorning()
        {
            var uncle = GameRefs.Uncle;
            var logic = uncle != null ? uncle.GetComponent<UncleLogicC>() : null;
            var relay = logic != null && logic.suitcase != null ? logic.suitcase.GetComponent<SuitcaseRelayC>() : null;
            var lk = relay != null && relay._lock != null ? relay._lock.GetComponent<OpenBriefcaseLogicC>() : null;
            if (lk == null) { Plugin.Log.LogWarning("Carta: maleta do tio não achada."); return; }
            if (!logic.suitcase.activeInHierarchy) { Plugin.Log.LogInfo("Carta: a maleta do tio está escondida no co-op; não abri."); return; }
            if (lk.currentPos != 0) return;
            Applying = true;
            try { lk.CarTrigger(); }
            finally { Applying = false; }
            Plugin.Log.LogInfo("Carta: a maleta do tio abriu com a carta do país (estado " + lk.currentPos + ").");
        }

        internal static InteractSync Instance;

        private static void Said(int state, Component c)
        {
            Plugin.Log.LogInfo("Objeto: o outro jogador " + (state == 1 ? "abriu " : "fechou ") + c.name);
        }

        internal static void LocalFlush(ToiletFlushC f)
        {
            if (Applying || f.currentPos != 0) return;
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) return;
            var pos = f.transform.position;
            net.SendReliable(Msg.Interact, w => { w.Write(Flush); NetIO.Put(w, pos); w.Write(1); });
            Plugin.Log.LogInfo("Objeto: dei descarga (" + f.name + ").");
        }

        private static T Nearest<T>(Vector3 pos) where T : Component
        {
            T best = null; float bd = 0.36f; // 60 cm
            foreach (var c in FindObjectsOfType<T>())
            {
                float d = (c.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.Interact) return;
            byte kind = r.ReadByte();
            var pos = NetIO.GetVector3(r);
            int state = r.ReadInt32();
            if ((kind & CarSpace) != 0)
            {
                kind = (byte)(kind & ~CarSpace);
                var car = GameRefs.Car;
                if (car == null) return;
                pos = car.TransformPoint(pos);
            }
            try
            {
                Applying = true;
                switch (kind)
                {
                    case Dial:
                    {
                        var d = Nearest<OvenDialC>(pos);
                        if (d == null) { Plugin.Log.LogWarning("Objeto: registro sem par aqui."); return; }
                        if (d.currentPos != state) d.Trigger();
                        Plugin.Log.LogInfo("Objeto: o outro jogador " + (state == 1 ? "abriu " : "fechou ") + d.name);
                        break;
                    }
                    case Lid:
                    {
                        var l = Nearest<ToiletSeatLogicC>(pos);
                        if (l == null) { Plugin.Log.LogWarning("Objeto: tampa sem par aqui."); return; }
                        if (l.currentPos != state) l.StartCoroutine(l.Trigger());
                        Plugin.Log.LogInfo("Objeto: o outro jogador " + (state == 1 ? "abriu " : "fechou ") + l.name);
                        break;
                    }
                    case Briefcase:
                    {
                        var b = Nearest<OpenBriefcaseLogicC>(pos);
                        if (b == null) { Plugin.Log.LogWarning("Objeto: maleta sem par aqui."); return; }
                        if (b.currentPos == state) return;
                        if (state == 0 || b.currentPos != 0) b.Trigger();     // fecha
                        else if (state == 2) b.CarTrigger();                   // abre no carro
                        else b.Trigger();                                      // abre fora do carro
                        Plugin.Log.LogInfo("Objeto: o outro jogador " + (state == 0 ? "fechou" : "abriu") + " a maleta do tio.");
                        break;
                    }
                    case Letter:
                    {
                        var menu = MainMenuC.Global;
                        if (menu == null) return;
                        menu.UnlockLetter(state);
                        Plugin.Log.LogInfo("Carta: o outro jogador leu a carta " + state + " (carimbo no diário aqui também).");
                        break;
                    }
                    case Curtain:
                    {
                        var c = Nearest<CurtainsLogicC>(pos);
                        if (c == null) { Plugin.Log.LogWarning("Objeto: cortina sem par aqui."); return; }
                        if (c.open != (state == 1)) c.Trigger();
                        Said(state, c);
                        break;
                    }
                    case OvenDoor:
                    {
                        var d = Nearest<OvenDoorC>(pos);
                        if (d == null) { Plugin.Log.LogWarning("Objeto: porta do forno sem par aqui."); return; }
                        if (d.currentPos != state) d.Trigger();
                        Said(state, d);
                        break;
                    }
                    case Drawer:
                    {
                        var d = Nearest<PushDrawC>(pos);
                        if (d == null) { Plugin.Log.LogWarning("Objeto: gaveta sem par aqui."); return; }
                        if (d.currentPos != state) { if (state == 0) d.TriggerClose(); else d.Trigger(); }
                        Said(state, d);
                        break;
                    }
                    case BinDoor:
                    {
                        var d = Nearest<BreadBinDoorC>(pos);
                        if (d == null) { Plugin.Log.LogWarning("Objeto: portinha sem par aqui."); return; }
                        if (d.currentPos == state) return;
                        if (state == 0) d.TriggerClose();
                        else { d.isLocked = false; d.Trigger(); } // o outro abriu: lá ela já estava destrancada
                        Said(state, d);
                        break;
                    }
                    case Visor:
                    {
                        var m = Nearest<MirrorLogicC>(pos);
                        if (m == null) { Plugin.Log.LogWarning("Objeto: quebra-sol sem par aqui."); return; }
                        if (m.currentPos == state) return;
                        // A chave escondida atrás é do ItemSync (quem clicou é quem pega); aqui só mexe o quebra-sol.
                        var kh = m.keyHolder;
                        m.keyHolder = null;
                        // O quebra-sol L_Door não tem AudioSource: o Trigger do jogo gira e depois dá erro no som (só o som falta).
                        try { m.Trigger(); } catch (NullReferenceException) { } finally { m.keyHolder = kh; }
                        Said(state, m);
                        break;
                    }
                    case Flush:
                    {
                        var f = Nearest<ToiletFlushC>(pos);
                        if (f == null) { Plugin.Log.LogWarning("Objeto: descarga sem par aqui."); return; }
                        if (f.currentPos == 0) f.StartCoroutine(f.Trigger());
                        Plugin.Log.LogInfo("Objeto: o outro jogador deu descarga.");
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
            finally { Applying = false; }
        }
    }

    [HarmonyPatch(typeof(MainMenuC), "UnlockLetter")]
    internal static class LetterReadPatch
    {
        private static void Postfix(int id)
        {
            try { InteractSync.LocalLetter(id); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(OvenDialC), "Trigger")]
    internal static class OvenDialClickPatch
    {
        private static void Prefix(OvenDialC __instance)
        {
            try { InteractSync.LocalToggle(__instance, InteractSync.KindDial, __instance.currentPos); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    /// <summary>Tampa: o Trigger é corrotina; o prefixo roda no clique, antes de trocar o currentPos.</summary>
    [HarmonyPatch(typeof(ToiletSeatLogicC), "Trigger")]
    internal static class ToiletSeatClickPatch
    {
        private static void Prefix(ToiletSeatLogicC __instance)
        {
            try { InteractSync.LocalToggle(__instance, InteractSync.KindLid, __instance.currentPos); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(CurtainsLogicC), "Trigger")]
    internal static class CurtainClickPatch
    {
        private static void Prefix(CurtainsLogicC __instance)
        {
            try { InteractSync.LocalState(__instance, InteractSync.KindCurtain, __instance.open ? 0 : 1); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    /// <summary>Porta do forno: o currentPos só troca no fim da animação (o colisor fica desligado até lá).</summary>
    [HarmonyPatch(typeof(OvenDoorC), "Trigger")]
    internal static class OvenDoorClickPatch
    {
        private static void Prefix(OvenDoorC __instance)
        {
            try { InteractSync.LocalToggle(__instance, InteractSync.KindOvenDoor, __instance.currentPos); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(PushDrawC), "Trigger")]
    internal static class DrawerClickPatch
    {
        private static void Prefix(PushDrawC __instance)
        {
            try { InteractSync.LocalToggle(__instance, InteractSync.KindDrawer, __instance.currentPos); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(PushDrawC), "TriggerClose")]
    internal static class DrawerClosePatch
    {
        private static void Prefix(PushDrawC __instance)
        {
            try { if (__instance.currentPos != 0) InteractSync.LocalState(__instance, InteractSync.KindDrawer, 0); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    /// <summary>Portinha/caixa de pão/armário. Trancada só toca o som: não manda nada.</summary>
    [HarmonyPatch(typeof(BreadBinDoorC), "Trigger")]
    internal static class BinDoorClickPatch
    {
        private static void Prefix(BreadBinDoorC __instance)
        {
            try { if (!__instance.isLocked) InteractSync.LocalToggle(__instance, InteractSync.KindBinDoor, __instance.currentPos); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(BreadBinDoorC), "TriggerClose")]
    internal static class BinDoorClosePatch
    {
        private static void Prefix(BreadBinDoorC __instance)
        {
            try { if (__instance.currentPos != 0) InteractSync.LocalState(__instance, InteractSync.KindBinDoor, 0); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(MirrorLogicC), "Trigger")]
    internal static class VisorClickPatch
    {
        private static void Prefix(MirrorLogicC __instance)
        {
            try { InteractSync.LocalToggle(__instance, InteractSync.KindVisor, __instance.currentPos); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }

    /// <summary>Descarga: o Trigger é uma corrotina; o prefixo roda no clique (antes do puxão).</summary>
    [HarmonyPatch(typeof(ToiletFlushC), "Trigger")]
    internal static class ToiletFlushPatch
    {
        private static void Prefix(ToiletFlushC __instance)
        {
            try { InteractSync.LocalFlush(__instance); }
            catch (Exception e) { Plugin.Log.LogError("InteractSync: " + e); }
        }
    }
}
