using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Testes: com -coop-continue, o jogo aperta "Continuar" sozinho no menu principal
    /// (mesma rotina do botão: MainMenuBookC.CloseEyes). Só uma vez por execução.
    /// </summary>
    public class AutoContinue : MonoBehaviour
    {
        private float foundAt = -1f;
        private bool done;

        private float splashSeen = -1f;
        private bool splashDone;

        private void Update()
        {
            // Tela "Press any key to start" + idioma: escolhe a que já está marcada (inglês), como o 2º clique.
            if (!splashDone)
            {
                var splash = FindObjectOfType<SplashScreen>();
                if (splash != null)
                {
                    if (splashSeen < 0f) splashSeen = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - splashSeen > 6f)
                    {
                        splashDone = true;
                        var tr = HarmonyLib.Traverse.Create(splash);
                        tr.Field("updateDone").SetValue(true);
                        var lang = tr.Field("selectedLanguage").GetValue();
                        HarmonyLib.Traverse.Create(splash.fadeCode).Method("SelectLanguage", lang).GetValue();
                        Plugin.Log.LogInfo("Teste: tela inicial pulada (idioma " + lang + ").");
                    }
                }
            }
            if (done) return;
            var book = FindObjectOfType<MainMenuBookC>();
            if (book == null) { foundAt = -1f; return; }
            if (foundAt < 0f) { foundAt = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - foundAt < 2f) return; // deixa o menu terminar de carregar
            done = true;
            Plugin.Log.LogInfo("Teste: Continuar automático.");
            book.CloseEyes();
        }
    }
}
