using UnityEngine;

namespace JalopyCoop
{
    /// <summary>
    /// Teste local (-coop-mute): este jogo fica sem som. Com dois jogos no mesmo PC os sons iguais (chuva, motor)
    /// tocam duas vezes um pouco defasados e soam estranhos; assim só se ouve o outro jogo.
    /// </summary>
    public class MuteAudio : MonoBehaviour
    {
        private void Start() { Plugin.Log.LogInfo("Teste: este jogo está sem som (-coop-mute)."); }
        private void Update() { if (AudioListener.volume != 0f) AudioListener.volume = 0f; }
    }
}
