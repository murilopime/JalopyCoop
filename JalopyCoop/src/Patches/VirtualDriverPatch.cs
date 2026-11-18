using HarmonyLib;
using JalopyCoop.Sync;

namespace JalopyCoop.Patches
{
    /// <summary>
    /// HOST, motorista virtual (tio no volante, host fora do carro): quando o carro está "dirigindo", o jogo
    /// prende o corpo do jogador no banco a cada FixedUpdate (RigidbodyControllerC.driving). Enquanto o host
    /// está fora, esse "driving" fica desligado só durante o FixedUpdate dele, e o host anda normalmente.
    /// (O consumo de combustível, que também olha esse "driving", continua funcionando: ele roda em outro script.)
    /// </summary>
    [HarmonyPatch(typeof(RigidbodyControllerC), "FixedUpdate")]
    public static class VirtualDriverPatch
    {
        private static void Prefix(RigidbodyControllerC __instance, out bool __state)
        {
            __state = false;
            if (DriveSync.VirtualDriver && __instance.driving && !__instance.isSat)
            {
                __instance.driving = false;
                __state = true;
            }
        }

        private static void Postfix(RigidbodyControllerC __instance, bool __state)
        {
            if (__state) __instance.driving = true;
        }
    }
}
