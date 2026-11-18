using HarmonyLib;
using JalopyCoop.Net;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Patches
{
    /// <summary>
    /// HOST: com o jogador-tio conectado, nenhuma fala do tio-NPC aparece (nem a caixa, nem o ícone do mouse).
    /// Algumas falas não respeitam restrictTalk, então o bloqueio é na própria caixa de diálogo.
    /// A fala é dada como "terminada" na hora, para as rotinas do jogo seguirem.
    /// </summary>
    [HarmonyPatch]
    public static class UncleDialoguePatches
    {
        private static bool lastSpeakerWasUncle;

        private static bool Silencing
        {
            // Host: o tio-NPC é só o corpo do jogador-tio. Cliente: o jogador É o tio (o NPC local fica escondido).
            get { var n = CoopNet.Instance; return n != null && n.Mode != CoopMode.Off && n.Connected; }
        }

        private static bool IsUncle(Transform t)
        {
            var uncle = GameRefs.Uncle;
            return uncle != null && t != null && (t == uncle.transform || t.IsChildOf(uncle.transform));
        }

        [HarmonyPatch(typeof(DialogueStuffsC), "StartTypeText")]
        [HarmonyPrefix]
        private static bool StartTypeText(GameObject target1)
        {
            lastSpeakerWasUncle = target1 != null && IsUncle(target1.transform);
            if (!Silencing || !lastSpeakerWasUncle) return true;
            target1.SendMessage("TextFinished", SendMessageOptions.DontRequireReceiver);
            return false;
        }

        [HarmonyPatch(typeof(DirectorC), "BubbleOn")]
        [HarmonyPrefix]
        private static bool BubbleOn(Transform target)
        {
            return !(Silencing && IsUncle(target));
        }

        [HarmonyPatch(typeof(DialogueStuffsC), "LeftClickAnim")]
        [HarmonyPrefix]
        private static bool LeftClickAnim()
        {
            return !(Silencing && lastSpeakerWasUncle);
        }
    }

    /// <summary>
    /// Entrar e sair do carro como no jogo (MVP 3): portas abrem/fecham normalmente; o banco e a saída
    /// do MOTORISTA são os do jogo; os do PASSAGEIRO são cópias do mod (PassengerSeat).
    ///  - CLIENTE: clicar no banco do motorista pede o volante; a saída do motorista (com o tio no volante)
    ///    devolve o volante e sai por esse lado. O "sentar" do jogo nunca roda no cliente.
    ///  - HOST: com o tio no volante, o banco do motorista é dele (o host usa o passageiro).
    /// </summary>
    [HarmonyPatch]
    public static class ClientSeatPatches
    {
        private static bool IsClient { get { var n = CoopNet.Instance; return n != null && n.Mode == CoopMode.Client && n.Connected; } }

        [HarmonyPatch(typeof(DoorExitLogicC), "Trigger")]
        [HarmonyPrefix]
        private static bool DoorExit()
        {
            var ds = DriveSync.Instance;
            if (IsClient)
            {
                if (UncleSync.ClientSeatedByCoop && ds != null && ds.ClientDriving) ds.ClientLeaveDriver();
                return false;
            }
            if (ds != null && ds.HostPassenger) ds.OnHostExitByGame();
            return true;
        }

        [HarmonyPatch(typeof(SeatLogicC), "Trigger")]
        [HarmonyPrefix]
        private static bool Seat()
        {
            var ds = DriveSync.Instance;
            if (IsClient)
            {
                if (!UncleSync.ClientSeatedByCoop && ds != null) ds.ClientRequestDriver();
                return false;
            }
            if (ds != null && !DriveSync.AllowHostSeat)
            {
                if (ds.RemoteDriving)
                {
                    ds.SayHint("O tio está no volante: use o banco do passageiro", 5f);
                    return false;
                }
                ds.OnHostSatDriverByGame(); // entrou pelo motorista: é o motorista de novo
            }
            return true;
        }
    }
}