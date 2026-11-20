using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// "Voltar para casa" (menu de pausa): o jogo fecha os olhos, salva com a rota 0 (MainMenuC.SaveData(3)) e
    /// recarrega a cena. No tio o save é bloqueado, então antes só o host voltava.
    ///  - Host: depois do SaveData(3) manda o save novo; o tio troca o save da memória e recarrega a cena também.
    ///  - Tio escolheu voltar: pede ao host, que volta pelo próprio jogo (RestartDemo) e leva os dois.
    ///  - As sincronias reiniciam como numa reconexão (SessionReset), sem cair a conexão: itens, portas,
    ///    controles e upgrades casam de novo com a cena nova.
    /// Só por evento (patch no SaveData e mensagem): nada de varrer a cena.
    /// </summary>
    public class ReturnHomeSync : MonoBehaviour
    {
        private static ReturnHomeSync instance;
        private static bool reloading;   // esperando a cena nova para avisar "conectado" às sincronias
        private static bool hostGoing;   // host: já está voltando (não repete pelo pedido do tio)

        private void Start()
        {
            instance = this;
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnReliable += m => { if (m == Msg.ReturnHomeAsk) HostAsked(); };
            net.OnConnectionChanged += c => { if (!c && !CoopNet.Instance.Connected) { reloading = false; hostGoing = false; } };
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            hostGoing = false;
            if (!reloading) return;
            reloading = false;
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) return;
            // Reinicia só com a cena nova já carregada: antes, o modelo do host era recriado na cena velha (entre o
            // aviso e a recarga), a recarga o destruía e ele voltava sem pose ("sentado no ar").
            net.SessionReset(false);
            net.SessionReset(true);
            Plugin.Log.LogInfo("Voltar para casa: cena nova carregada; sincronias recomeçando.");
        }

        // ================= host =================

        /// <summary>Host: o jogo acabou de salvar para voltar para casa (a cena recarrega no fim do quadro).</summary>
        internal static void HostSaved()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host || !net.Connected || ES3.loadedSave == null) return;
            hostGoing = true;
            byte[] data = SaveTransfer.MemorySaveBytes();
            net.SendReliable(Msg.ReturnHome, w => { w.Write(data.Length); w.Write(data); });
            Plugin.Log.LogInfo("Voltar para casa: mandei o save novo ao tio (" + data.Length + " bytes).");
            reloading = true;
        }

        private static void HostAsked()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host) return;
            var menu = MainMenuC.Global;
            if (hostGoing || menu == null || !GameRefs.InGame) { Plugin.Log.LogInfo("Voltar para casa: pedido do tio ignorado (já voltando ou fora do jogo)."); return; }
            hostGoing = true;
            Plugin.Log.LogInfo("Voltar para casa: o tio pediu; voltando os dois.");
            menu.RestartDemo();
        }

        // ================= tio =================

        internal static void ClientAsked()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Client || !net.Connected) return;
            net.SendReliable(Msg.ReturnHomeAsk);
            Plugin.Log.LogInfo("Voltar para casa: pedi ao host (ele leva os dois).");
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.ReturnHome) return;
            int len = r.ReadInt32();
            var data = r.ReadBytes(len);
            if (CoopNet.Instance.Mode != CoopMode.Client) return;
            try
            {
                SaveTransfer.UseHostSave(data);
                Plugin.Log.LogInfo("Voltar para casa: o host voltou; usando o save novo dele (" + len + " bytes) e recarregando.");
                reloading = true;
                StartCoroutine(ClientReload());
            }
            catch (Exception e) { Plugin.Log.LogError("Voltar para casa: save do host inválido: " + e); }
        }

        private IEnumerator ClientReload()
        {
            Time.timeScale = 1f; // o menu de pausa pode estar aberto
            yield return new WaitForSecondsRealtime(0.3f);
            Application.LoadLevel(Application.loadedLevel);
        }
    }

    [HarmonyPatch(typeof(MainMenuC), "SaveData")]
    internal static class ReturnHomeSavePatch
    {
        private static void Postfix(int state)
        {
            if (state != 3) return;
            try { ReturnHomeSync.HostSaved(); }
            catch (Exception e) { Plugin.Log.LogError("ReturnHomeSync: " + e); }
        }
    }
}
