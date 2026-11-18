using HarmonyLib;
using JalopyCoop.Sync;

namespace JalopyCoop.Patches
{
    /// <summary>
    /// O CursorHit (na câmera do jogador) faz o tio olhar para a câmera do jogador a cada quadro.
    /// No host, enquanto o jogador-tio controla a cabeça do tio, esse comando é ignorado.
    /// </summary>
    [HarmonyPatch(typeof(CursorHit), "LateUpdate")]
    public static class CursorHitPatch
    {
        private static bool Prefix()
        {
            return !UncleSync.OverridingUncleLook;
        }
    }
}
