using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Substitui as leituras de teclado/controle dos scripts do carro.
    /// No HOST, enquanto o jogador-tio está no volante, as teclas de direção
    /// (assignedInputStrings[8..15] e os eixos do controle) vêm da rede; o teclado local do host é ignorado
    /// para essas teclas. Fora disso, devolve o Input normal da Unity.
    /// </summary>
    public static class CoopInput
    {
        public static bool RemoteDriving;     // ligado pelo DriveSync no host
        private static ushort bits;           // estado atual vindo do tio
        private static ushort prevBits;       // estado do quadro anterior (para GetKeyDown/Up)
        private static float joyX, joyAcc, joyRev;

        public static void SetRemote(ClientStateData s)
        {
            bits = s.DriveBits;
            joyX = s.JoyX;
            joyAcc = s.JoyAccelerate;
            joyRev = s.JoyReverse;
        }

        public static void ClearRemote()
        {
            bits = prevBits = 0;
            joyX = joyAcc = joyRev = 0f;
        }

        /// <summary>Chamado uma vez por quadro (DriveSync.LateUpdate) para as bordas de tecla.</summary>
        public static void EndFrame()
        {
            prevBits = bits;
        }

        private static int DriveIndex(string key)
        {
            var menu = MainMenuC.Global;
            if (menu == null || menu.assignedInputStrings == null || key == null) return -1;
            for (int i = DriveKeys.First; i <= DriveKeys.Last && i < menu.assignedInputStrings.Length; i++)
            {
                if (menu.assignedInputStrings[i] == key) return i - DriveKeys.First;
            }
            return -1;
        }

        public static bool GetKey(string key)
        {
            if (RemoteDriving)
            {
                int i = DriveIndex(key);
                if (i >= 0) return (bits & (1 << i)) != 0;
            }
            return Input.GetKey(key);
        }

        public static bool GetKeyDown(string key)
        {
            if (RemoteDriving)
            {
                int i = DriveIndex(key);
                if (i >= 0) return (bits & (1 << i)) != 0 && (prevBits & (1 << i)) == 0;
            }
            return Input.GetKeyDown(key);
        }

        public static bool GetKeyUp(string key)
        {
            if (RemoteDriving)
            {
                int i = DriveIndex(key);
                if (i >= 0) return (bits & (1 << i)) == 0 && (prevBits & (1 << i)) != 0;
            }
            return Input.GetKeyUp(key);
        }

        public static float GetAxis(string axis)
        {
            if (RemoteDriving)
            {
                if (axis == "JoypadX") return joyX;
                if (axis == "JoypadAccelerate") return joyAcc;
                if (axis == "JoypadReverse") return joyRev;
            }
            return Input.GetAxis(axis);
        }

        /// <summary>No CLIENTE: lê as teclas de direção locais para mandar ao host.</summary>
        public static ushort ReadLocalDriveBits()
        {
            var menu = MainMenuC.Global;
            if (menu == null || menu.assignedInputStrings == null) return 0;
            ushort b = 0;
            for (int i = DriveKeys.First; i <= DriveKeys.Last && i < menu.assignedInputStrings.Length; i++)
            {
                try { if (Input.GetKey(menu.assignedInputStrings[i])) b |= (ushort)(1 << (i - DriveKeys.First)); }
                catch { /* tecla inválida na config do jogo */ }
            }
            return b;
        }

        public static float SafeAxis(string axis)
        {
            try { return Input.GetAxis(axis); } catch { return 0f; }
        }
    }

    /// <summary>
    /// Troca Input.GetKey/GetKeyDown/GetKeyUp(string) e Input.GetAxis(string) por CoopInput
    /// em todos os métodos dos scripts que dirigem o carro.
    /// </summary>
    [HarmonyPatch]
    public static class CarInputTranspiler
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var types = new[] { typeof(WheelScriptPCC), typeof(CarControleScriptC) };
            foreach (var t in types)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                    yield return m;
                }
            }
        }

        private static readonly MethodInfo InGetKey = AccessTools.Method(typeof(Input), "GetKey", new[] { typeof(string) });
        private static readonly MethodInfo InGetKeyDown = AccessTools.Method(typeof(Input), "GetKeyDown", new[] { typeof(string) });
        private static readonly MethodInfo InGetKeyUp = AccessTools.Method(typeof(Input), "GetKeyUp", new[] { typeof(string) });
        private static readonly MethodInfo InGetAxis = AccessTools.Method(typeof(Input), "GetAxis", new[] { typeof(string) });

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (var ins in code)
            {
                if (ins.opcode == OpCodes.Call && ins.operand is MethodInfo mi)
                {
                    if (mi == InGetKey) ins.operand = AccessTools.Method(typeof(CoopInput), "GetKey");
                    else if (mi == InGetKeyDown) ins.operand = AccessTools.Method(typeof(CoopInput), "GetKeyDown");
                    else if (mi == InGetKeyUp) ins.operand = AccessTools.Method(typeof(CoopInput), "GetKeyUp");
                    else if (mi == InGetAxis) ins.operand = AccessTools.Method(typeof(CoopInput), "GetAxis");
                }
                yield return ins;
            }
        }
    }
}
