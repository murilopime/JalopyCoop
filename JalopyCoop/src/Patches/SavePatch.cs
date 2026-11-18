using HarmonyLib;
using JalopyCoop.Net;

namespace JalopyCoop.Patches
{
    /// <summary>
    /// Só o host salva. No cliente o save do jogo é bloqueado para não sobrescrever
    /// o progresso do jogador-tio com um mundo que é cópia do host.
    /// </summary>
    [HarmonyPatch(typeof(MainMenuC), "SaveData")]
    public static class SavePatch
    {
        private static bool Prefix(int state)
        {
            var net = CoopNet.Instance;
            if (net != null && net.Mode == CoopMode.Client)
            {
                Plugin.Log.LogInfo("Save bloqueado no cliente (estado " + state + "). Só o host salva.");
                return false;
            }
            return true;
        }
    }
}

namespace JalopyCoop.Patches
{
    /// <summary>
    /// O tio nunca grava o arquivo de save (o MainMenuC.OnDestroy chama ES3.SaveFiles direto, sem SaveData).
    /// Os dois jogos usam a mesma pasta de saves: gravar dos dois lados ao mesmo tempo zerou o save (17 bytes).
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(ES3), "SaveFiles")]
    public static class SaveFilesPatch
    {
        private static bool Prefix()
        {
            var net = JalopyCoop.Net.CoopNet.Instance;
            bool client = net != null && net.Mode == JalopyCoop.Net.CoopMode.Client;
            foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith("-coop-join=")) client = true;
            if (!client) return true;
            Plugin.Log.LogInfo("Gravar arquivo de save bloqueado no tio.");
            return false;
        }
    }
}

namespace JalopyCoop.Patches
{
    /// <summary>
    /// O jogo abre o save com File.Open(..., FileMode.Open): leitura+escrita e SEM compartilhar. Com dois jogos
    /// abrindo juntos (mesma pasta), um deles dá "Sharing violation", fica com loadedSave = null e depois grava
    /// um save vazio (17 bytes). Aqui: só leitura, compartilhando, com algumas tentativas.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(ES3), "LoadSaveFile")]
    public static class SaveLoadPatch
    {
        private static bool Prefix()
        {
            string dir = UnityEngine.Application.persistentDataPath + "/Saves";
            string path = dir + "/Save.sfminsk";
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            if (!System.IO.File.Exists(path)) { ES3.loadedSave = new SaveFile(); return false; }
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                    {
                        ES3.loadedSave = (SaveFile)new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(fs);
                    }
                    Plugin.Log.LogInfo("Save lido" + (i > 0 ? " (tentativa " + (i + 1) + ")" : "") + ".");
                    return false;
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Save: leitura falhou (" + e.GetType().Name + "), tentando de novo.");
                    System.Threading.Thread.Sleep(250);
                }
            }
            Plugin.Log.LogError("Save: não deu para ler. O jogo vai ficar sem save nesta sessão (e não grava por cima).");
            ES3.loadedSave = null;
            return false;
        }
    }

    /// <summary>Nunca gravar um save vazio (loadedSave null = leitura falhou): é isso que virava o arquivo de 17 bytes.</summary>
    [HarmonyLib.HarmonyPatch(typeof(ES3), "SaveFiles")]
    public static class SaveEmptyGuardPatch
    {
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
        private static bool Prefix()
        {
            if (ES3.loadedSave != null) return true;
            Plugin.Log.LogWarning("Save: memória vazia, NÃO gravei (protege o save).");
            return false;
        }
    }
}
