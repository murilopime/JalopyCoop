using HarmonyLib;
using JalopyCoop.Net;

namespace JalopyCoop.Patches
{
    /// <summary>
    /// No cliente o carro é movido "teletransportado" para seguir o host, e a Unity chama OnCollisionEnter
    /// MESMO em scripts desligados (enabled=false não bloqueia mensagens de colisão). Por isso o tio via o
    /// carro quebrar (vidros, fumaça no motor). Aqui o dano por batida é bloqueado no cliente: dano é só do host.
    /// </summary>
    [HarmonyPatch]
    public static class ClientCollisionPatch
    {
        private static bool IsClient
        {
            get { var n = CoopNet.Instance; return n != null && n.Mode == CoopMode.Client && n.Connected; }
        }

        [HarmonyPatch(typeof(CarCollisionsC), "OnCollisionEnter")]
        [HarmonyPrefix]
        private static bool CarCollisions() { return !IsClient; }

        [HarmonyPatch(typeof(CarDamage2), "OnCollisionEnter")]
        [HarmonyPrefix]
        private static bool CarDamage() { return !IsClient; }

        // Também faz o tio "reclamar de batida" e mexe no Steam (conquistas): só no host.
        [HarmonyPatch(typeof(CarControleScriptC), "OnCollisionEnter")]
        [HarmonyPrefix]
        private static bool CarControle() { return !IsClient; }
    }
}
