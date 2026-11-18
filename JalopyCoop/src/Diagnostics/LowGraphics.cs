using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Gráficos no mínimo para rodar duas janelas no mesmo PC (testes).
    /// Ligado pelo argumento -coop-lowgfx (o testar-coop-local.bat passa).
    /// Depois de cada cena: qualidade 0, efeitos da câmera desligados e 30 FPS.
    /// Não mexe nas configurações salvas do jogo.
    /// </summary>
    public class LowGraphics : MonoBehaviour
    {
        private static readonly string[] Effects =
        {
            "SSAOPro", "BloomAndLensFlares", "SunShafts", "GlobalFog", "Vignetting", "AntialiasingAsPostEffect", "AmplifyColorEffect"
        };

        private void OnEnable() { SceneManager.sceneLoaded += OnSceneLoaded; Apply(); }
        private void OnDisable() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        private void OnSceneLoaded(Scene s, LoadSceneMode m) { StartCoroutine(ApplyLater()); }

        private IEnumerator ApplyLater()
        {
            // O jogo aplica as próprias opções ao carregar; aplica as nossas depois.
            yield return new WaitForSeconds(2f);
            Apply();
            yield return new WaitForSeconds(5f);
            Apply();
        }

        private void Apply()
        {
            QualitySettings.SetQualityLevel(0, true);
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.antiAliasing = 0;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
            int off = 0;
            var cam = Camera.main;
            if (cam != null)
            {
                foreach (var b in cam.GetComponents<Behaviour>())
                {
                    if (b == null || !b.enabled) continue;
                    var n = b.GetType().Name;
                    foreach (var e in Effects)
                    {
                        if (n == e) { b.enabled = false; off++; break; }
                    }
                }
            }
            Plugin.Log.LogInfo("Gráficos mínimos aplicados (" + off + " efeitos desligados).");
        }
    }
}
