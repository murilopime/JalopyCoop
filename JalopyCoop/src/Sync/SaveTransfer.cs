using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Save no co-op: o mundo é o do host.
    ///  - O tio, ao conectar no menu, pede o save do host. O host manda o save que tem na memória (ES3.loadedSave).
    ///  - O tio troca o save só NA MEMÓRIA (o arquivo dele nunca é gravado no co-op) e guarda as configurações dele
    ///    (campo de visão, volumes, mouse, teclas). Assim, em outro PC, o tio começa no mesmo dia, com o mesmo
    ///    dinheiro, carro e mundo do host. No mesmo PC é o mesmo arquivo, nada muda.
    ///  - Se o tio clicar em Continuar antes de o save chegar, o Continuar espera e vai sozinho quando chegar.
    ///  - Dormir: o host salva no fim do dia (BedLogicC → SaveData); o tio não salva.
    /// </summary>
    public class SaveTransfer : MonoBehaviour
    {
        public static SaveTransfer Instance { get; private set; }

        public static bool Joining
        {
            get
            {
                foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-coop-join=")) return true;
                var n = CoopNet.Instance;
                return n != null && n.Mode == CoopMode.Client;
            }
        }

        /// <summary>Tio: já tem o save do host na memória.</summary>
        public static bool HaveHostSave { get; private set; }
        internal static MainMenuBookC PendingContinue;
        internal static bool ContinuingNow;
        /// <summary>Tio: o Continuar para o mundo do host já foi dado (bloqueia um 2º). Zera no menu principal e ao desconectar.</summary>
        internal static bool EnterStarted;

        private float askTimer;

        private void Start()
        {
            Instance = this;
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnReliable += m => { if (m == Msg.SaveRequest) SendSave(); };
            net.OnConnectionChanged += c =>
            {
                if (c) return;
                askTimer = 0f;
                if (!GameRefs.InGame && !EnterStarted) { HaveHostSave = false; PendingContinue = null; } // no menu: na próxima conexão pede o save de novo
            };
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) =>
            {
                if (s.buildIndex != 0) return; // 0 = menu principal (o jogo volta com LoadLevel(0))
                EnterStarted = false; HaveHostSave = false; PendingContinue = null;
            };
        }

        private float menuCheck;
        private float menuSince = -1f;

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Client || !net.Connected || GameRefs.InGame) { menuSince = -1f; return; }
            if (HaveHostSave) { EnterWhenMenuReady(); return; }
            askTimer -= Time.unscaledDeltaTime;
            if (askTimer > 0f) return;
            askTimer = 5f; // pede de novo se não chegar
            net.SendReliable(Msg.SaveRequest);
            Plugin.Log.LogInfo("Save: pedi o save do host.");
        }

        /// <summary>
        /// O save chegou antes do menu principal (tio conectou na tela inicial): quando o menu aparecer e não tiver mais
        /// a tela "aperte uma tecla", entra sozinho. Procura 1x/s, só fora do jogo.
        /// </summary>
        private void EnterWhenMenuReady()
        {
            if (EnterStarted) return;
            menuCheck -= Time.unscaledDeltaTime;
            if (menuCheck > 0f) return;
            menuCheck = 1f;
            var book = FindObjectOfType<MainMenuBookC>();
            if (book == null || FindObjectOfType<SplashScreen>() != null) { menuSince = -1f; return; }
            if (menuSince < 0f) { menuSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - menuSince < 2f) return; // deixa o menu terminar de abrir
            EnterStarted = true;
            ContinuingNow = true;
            try { book.CloseEyes(); } finally { ContinuingNow = false; }
            Plugin.Log.LogInfo("Save: entrando no mundo do host (o menu abriu depois do save chegar).");
        }

        private static float inGameHintUntil = -1f;

        /// <summary>Tio: o que está acontecendo, no meio da tela, até entrar no mundo do host.</summary>
        private void OnGUI()
        {
            var net = CoopNet.Instance;
            string msg = null;
            if (Time.realtimeSinceStartup < inGameHintUntil)
                msg = "Você já está dentro de um jogo.\nVolte ao menu principal e conecte de novo para entrar no mundo do host.";
            else if (net != null && net.Mode == CoopMode.Client && !GameRefs.InGame)
            {
                if (!net.Connected) msg = "Conectando no host...";
                else if (!HaveHostSave) msg = "Conectado. Esperando o host (ele precisa estar dentro do jogo)...";
                else if (EnterStarted) msg = "Entrando no mundo do host...";
            }
            if (msg == null) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(0, Screen.height * 0.5f - 40, Screen.width, 80), msg, style);
        }

        // ================= host =================

        private static bool loggedNotInGame;

        private static void SendSave()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host) return;
            // Só manda com o host dentro do mundo (o tio pede de novo a cada 5 s e espera; -coop-host abre a sala no menu)
            if (!GameRefs.InGame)
            {
                if (!loggedNotInGame) { loggedNotInGame = true; Plugin.Log.LogInfo("Save: o tio pediu o save, mas ainda estou no menu; mando quando eu entrar no jogo."); }
                return;
            }
            loggedNotInGame = false;
            try
            {
                byte[] data;
                if (ES3.loadedSave != null)
                {
                    var ms = new MemoryStream();
                    // -coop-traffic: o tio usa o aiCount do save do host; manda o valor que o host usa (só na cópia enviada)
                    int keepAi = ES3.loadedSave.aiCount;
                    if (Plugin.TrafficOverride >= 0) ES3.loadedSave.aiCount = Plugin.TrafficOverride;
                    try { new BinaryFormatter().Serialize(ms, ES3.loadedSave); }
                    finally { ES3.loadedSave.aiCount = keepAi; }
                    data = ms.ToArray();
                }
                else
                {
                    var path = Application.persistentDataPath + "/Saves/Save.sfminsk";
                    if (!File.Exists(path)) { Plugin.Log.LogWarning("Save: o host não tem save para mandar."); return; }
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        data = new byte[fs.Length];
                        fs.Read(data, 0, data.Length);
                    }
                }
                net.SendReliable(Msg.SaveData, w => { w.Write(data.Length); w.Write(data); });
                Plugin.Log.LogInfo("Save: mandei o save ao tio (" + data.Length + " bytes, #" + SeededRandom.Hash(Convert.ToBase64String(data)).ToString("X8") + ").");
            }
            catch (Exception e) { Plugin.Log.LogError("Save: falha ao mandar: " + e); }
        }

        // ================= tio =================

        private static readonly string[] KeepLocal = { "fov", "masterVolume", "effectsVolume", "musicVolume", "mouseSensitivity", "assignedInputStrings" };

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.SaveData) return;
            int len = r.ReadInt32();
            var data = r.ReadBytes(len);
            if (CoopNet.Instance.Mode != CoopMode.Client) return;
            if (GameRefs.InGame) { Plugin.Log.LogWarning("Save: chegou com o jogo já carregado; para entrar no mundo do host, volte ao menu."); inGameHintUntil = Time.realtimeSinceStartup + 10f; return; }
            try
            {
                SaveFile host;
                using (var ms = new MemoryStream(data)) host = (SaveFile)new BinaryFormatter().Deserialize(ms);
                var mine = ES3.loadedSave;
                if (mine != null)
                {
                    // as configurações são do jogador, não do mundo
                    foreach (var name in KeepLocal)
                    {
                        var f = typeof(SaveFile).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null) f.SetValue(host, f.GetValue(mine));
                    }
                }
                ES3.loadedSave = host; // só memória: o tio nunca grava o arquivo no co-op
                HaveHostSave = true;
                Plugin.Log.LogInfo("Save: usando o save do host (" + len + " bytes, #" + SeededRandom.Hash(Convert.ToBase64String(data)).ToString("X8") + "). O save deste PC não foi mexido.");
                // Entra sozinho no mundo do host: o tio só clicou Conectar no menu (ou já tinha clicado Continuar)
                var book = PendingContinue != null ? PendingContinue : FindObjectOfType<MainMenuBookC>();
                PendingContinue = null;
                if (book != null && !EnterStarted)
                {
                    EnterStarted = true;
                    ContinuingNow = true;
                    try { book.CloseEyes(); } finally { ContinuingNow = false; }
                    Plugin.Log.LogInfo("Save: entrando no mundo do host.");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Save: save do host inválido: " + e); }
        }
    }

    /// <summary>Tio: Continuar só depois de ter o save do host (senão o mundo não bate).</summary>
    [HarmonyPatch(typeof(MainMenuBookC), "CloseEyes")]
    internal static class ContinueWaitsForSavePatch
    {
        private static bool Prefix(MainMenuBookC __instance)
        {
            if (SaveTransfer.ContinuingNow) return true;
            // Já está entrando no mundo do host: um 2º Continuar (clique ou -coop-continue) carregaria a cena de novo
            if (SaveTransfer.Joining && SaveTransfer.EnterStarted) { Plugin.Log.LogInfo("Save: Continuar ignorado (já entrando no mundo do host)."); return false; }
            if (!SaveTransfer.Joining) return true;
            if (SaveTransfer.HaveHostSave) { SaveTransfer.EnterStarted = true; return true; }
            SaveTransfer.PendingContinue = __instance;
            Plugin.Log.LogInfo("Save: Continuar vai esperar o save do host.");
            return false;
        }
    }
}
