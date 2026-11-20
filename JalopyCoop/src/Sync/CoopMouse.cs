using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// "A câmera/mouse teleporta": o jogo gira o corpo e a câmera no MouseLook.FixedUpdate lendo
    /// Input.GetAxis("Mouse X/Y"), que é o movimento do QUADRO inteiro. Num quadro um pouco mais lento a física roda
    /// 4-6 passos e cada passo aplica o mesmo movimento de novo (log do teste: passosDoMouse 8-12 = corpo+câmera)
    /// → a câmera dá um salto. Aqui, no co-op: o movimento de cada quadro vale UMA vez (no primeiro passo; se o quadro
    /// não teve passo, vai no próximo). Movimento que ninguém usou em 0,1 s (menu, pausa, olhar desligado) é jogado
    /// fora, para não virar um giro de uma vez depois.
    /// </summary>
    internal static class CoopMouse
    {
        private static float pendX, pendY;
        private static int addedFrame = -1;
        private static float usedX, usedY; // quando cada eixo foi usado pela última vez

        private static bool On
        {
            get { var net = CoopNet.Instance; return net != null && net.Connected; }
        }

        private static void AddFrame()
        {
            if (addedFrame == Time.frameCount) return;
            addedFrame = Time.frameCount;
            float now = Time.unscaledTime;
            if (now - usedX > 0.1f) pendX = 0f;
            if (now - usedY > 0.1f) pendY = 0f;
            pendX += Input.GetAxis("Mouse X");
            pendY += Input.GetAxis("Mouse Y");
        }

        /// <summary>No lugar de Input.GetAxis dentro do MouseLook.FixedUpdate.</summary>
        public static float Axis(string axis)
        {
            if (!On) return Input.GetAxis(axis);
            if (axis == "Mouse X") { AddFrame(); float v = pendX; pendX = 0f; usedX = Time.unscaledTime; return v; }
            if (axis == "Mouse Y") { AddFrame(); float v = pendY; pendY = 0f; usedY = Time.unscaledTime; return v; }
            return Input.GetAxis(axis);
        }

        /// <summary>Todo quadro (Update): guarda o movimento de quadros sem passo de física.</summary>
        internal static void Tick()
        {
            if (On) AddFrame();
            else { pendX = 0f; pendY = 0f; }
        }
    }

    /// <summary>Chama o CoopMouse.Tick todo quadro.</summary>
    public class CoopMouseTick : MonoBehaviour
    {
        private void Update() { CoopMouse.Tick(); }
    }

    [HarmonyPatch(typeof(MouseLook), "FixedUpdate")]
    internal static class MouseOncePerFramePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getAxis = AccessTools.Method(typeof(Input), "GetAxis", new[] { typeof(string) });
            var mine = AccessTools.Method(typeof(CoopMouse), "Axis");
            int n = 0;
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand as MethodInfo == getAxis)
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = mine;
                    n++;
                }
                yield return ins;
            }
            Plugin.Log.LogInfo("Mouse: " + n + " leituras do mouse no MouseLook passam a valer 1x por quadro (no co-op). Passo da física "
                + (Time.fixedDeltaTime * 1000f).ToString("0.0") + " ms (" + (1f / Time.fixedDeltaTime).ToString("0") + " por segundo).");
        }
    }
}
