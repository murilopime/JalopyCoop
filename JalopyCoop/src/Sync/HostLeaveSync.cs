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
    /// Host saiu do mundo: "Salvar e sair para o menu" (MainMenuC.SaveData(2)) ou fechou o jogo.
    ///  - Host: manda "saí" (HostLeft); fechando o jogo, a conexão cai de qualquer jeito.
    ///  - Tio: com "saí" ou com a conexão caindo dentro do mundo do host, desconecta e volta ao menu principal.
    ///  - Tio "Salvar e sair" dele: o save fica bloqueado (só o host salva), então desconecta e vai ao menu também.
    ///  - Save do tio protegido: enquanto ele está no mundo do host (InHostWorld) nada grava o arquivo, mesmo depois
    ///    de desconectar; no menu o save do PC dele volta para a memória (o do host era só cópia).
    /// Só por evento: nada de varrer a cena.
    /// </summary>
    public class HostLeaveSync : MonoBehaviour
    {
        /// <summary>Tio: está (ou estava, até chegar no menu) dentro do mundo do host.</summary>
        internal static bool InHostWorld;

        private static HostLeaveSync instance;
        private static bool leaving;
        private static string notice;
        private static float noticeUntil = -1f;

        private void Start()
        {
            instance = this;
            var net = CoopNet.Instance;
            net.OnReliable += m => { if (m == Msg.HostLeft) OnHostLeft(); };
            net.OnConnectionChanged += OnConnectionChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            var net = CoopNet.Instance;
            if (s.buildIndex != 0)
            {
                if (net != null && net.Mode == CoopMode.Client) InHostWorld = true;
                return;
            }
            leaving = false;
            if (!InHostWorld) return;
            InHostWorld = false;
            RestoreOwnSave();
        }

        /// <summary>Tio no menu: tira da memória a cópia do save do host e lê de novo o save deste PC.</summary>
        private static void RestoreOwnSave()
        {
            try
            {
                var es = ES3.Global;
                var load = AccessTools.Method(typeof(ES3), "LoadSaveFile");
                if (es != null && load != null) load.Invoke(es, null); // passa pelo SaveLoadPatch (só leitura)
                Plugin.Log.LogInfo("Sair: de volta ao menu; o save deste PC voltou para a memória (o do host era só cópia).");
            }
            catch (Exception e) { Plugin.Log.LogError("Sair: não deu para reler o save deste PC: " + e); }
        }

        private void OnConnectionChanged(bool connected)
        {
            var net = CoopNet.Instance;
            if (connected || net.Connected) return; // SessionReset (voltar para casa) não é saída
            if (!InHostWorld || leaving) return;
            if (SceneManager.GetActiveScene().buildIndex == 0) return;
            GoToMenu("A conexão com o host caiu. Você voltou ao menu.");
        }

        private static void OnHostLeft()
        {
            var net = CoopNet.Instance;
            if (net.Mode != CoopMode.Client) return;
            Plugin.Log.LogInfo("Sair: o host saiu do mundo.");
            if (!InHostWorld || leaving) { net.Stop(); return; }
            GoToMenu("O host saiu do jogo. Você voltou ao menu.");
            net.Stop();
        }

        /// <summary>Tio escolheu "Salvar e sair": no co-op só o host salva; sai da sala e vai ao menu.</summary>
        internal static void ClientQuit()
        {
            var net = CoopNet.Instance;
            if (leaving) return;
            GoToMenu("Você saiu do mundo do host (o progresso fica no save do host).");
            if (net != null) net.Stop();
        }

        private static void GoToMenu(string why)
        {
            if (leaving || instance == null) return;
            leaving = true;
            notice = why;
            noticeUntil = Time.realtimeSinceStartup + 8f;
            Plugin.Log.LogInfo("Sair: " + why);
            instance.StartCoroutine(instance.LoadMenu());
        }

        private IEnumerator LoadMenu()
        {
            Time.timeScale = 1f; // o menu de pausa pode estar aberto
            yield return new WaitForSecondsRealtime(0.5f);
            Application.LoadLevel(0);
        }

        // ================= host =================

        internal static void HostQuitToMenu()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host || !net.Connected) return;
            net.SendReliable(Msg.HostLeft);
            Plugin.Log.LogInfo("Sair: fui para o menu; avisei o tio.");
        }

        private void OnGUI()
        {
            if (notice == null || Time.realtimeSinceStartup > noticeUntil) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(0, Screen.height * 0.5f - 40, Screen.width, 80), notice, style);
        }
    }

    [HarmonyPatch(typeof(MainMenuC), "SaveData")]
    internal static class HostQuitSavePatch
    {
        private static void Postfix(int state)
        {
            if (state != 2) return;
            try { HostLeaveSync.HostQuitToMenu(); }
            catch (Exception e) { Plugin.Log.LogError("HostLeaveSync: " + e); }
        }
    }
}
