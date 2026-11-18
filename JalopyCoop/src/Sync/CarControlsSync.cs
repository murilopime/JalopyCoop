using System.Collections;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Chave (ignição) e freio de mão.
    ///
    /// HOST: manda o estado em todo HostState e executa os cliques que o tio manda quando está no volante.
    /// CLIENTE: só mostra o estado do host (chave virada, alavanca, som do motor). Os cliques do tio nesses
    /// controles não rodam no jogo local: viram mensagem para o host (patches abaixo).
    ///
    /// Partida pelo tio: no jogo é "segurar o clique"; aqui um clique faz o host girar a chave e dar a partida.
    /// </summary>
    public class CarControlsSync : MonoBehaviour
    {
        public static CarControlsSync Instance { get; private set; }

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            var previous = net.BuildHostStateExtra;
            net.BuildHostStateExtra = s =>
            {
                if (previous != null) s = previous(s);
                return FillState(s);
            };
            net.OnHostState += ApplyState;
            net.OnReliable += OnReliable;
        }

        // ---------- referências ----------

        private static HandbrakeLogicC Handbrake
        {
            get { var car = GameRefs.Car; return car != null ? car.root.GetComponentInChildren<HandbrakeLogicC>(true) : null; }
        }

        private static IgnitionLogicC Ignition
        {
            get { var car = GameRefs.Car; return car != null ? car.root.GetComponentInChildren<IgnitionLogicC>(true) : null; }
        }

        // ---------- HOST ----------

        private static HostStateData FillState(HostStateData s)
        {
            var ign = Ignition;
            var hb = Handbrake;
            var cl = GameRefs.CarLogic;
            if (ign != null) s.IgnitionPos = (byte)ign.currentPos;
            if (hb != null) s.HandbrakePos = (byte)hb.currentPos;
            if (cl != null) s.EngineOn = cl.engineOn;
            var ccs = CarControleScriptC.Global;
            s.Speed = ccs != null && ccs.enabled ? Mathf.Abs(ccs.currentSpeed) : 0f;
            return s;
        }

        private void OnReliable(Msg type)
        {
            var net = CoopNet.Instance;
            if (net.Mode != CoopMode.Host) return;
            if (type != Msg.UseHandbrake && type != Msg.UseIgnition) return;
            if (DriveSync.Instance == null || !DriveSync.Instance.RemoteDriving)
            {
                Plugin.Log.LogInfo("Clique do tio ignorado (" + type + "): ele não está no volante.");
                return;
            }
            if (type == Msg.UseHandbrake)
            {
                var hb = Handbrake;
                if (hb != null)
                {
                    Plugin.Log.LogInfo("Tio mexeu no freio de mão (pos " + hb.currentPos + ").");
                    HostPatches.AllowLocal = true;
                    try { hb.Trigger(); } finally { HostPatches.AllowLocal = false; }
                }
            }
            else if (type == Msg.UseIgnition)
            {
                var ign = Ignition;
                if (ign == null) return;
                if (ign.currentPos == 1)
                {
                    Plugin.Log.LogInfo("Tio desligou o carro.");
                    ign.StartCoroutine(ign.TurnBack());
                }
                else
                {
                    Plugin.Log.LogInfo("Tio deu a partida.");
                    StartCoroutine(RemoteStart(ign));
                }
            }
        }

        /// <summary>Gira a chave (Trigger do jogo) e, como o tio não está "segurando" no host, liga o motor.</summary>
        private IEnumerator RemoteStart(IgnitionLogicC ign)
        {
            // O Trigger do jogo não faz nada se o HOST estiver segurando um item (teste online: caixa na mão do
            // host = tio não ligava o carro). Solta a "mão" só durante o clique (a parte síncrona do Trigger).
            var cam = GameRefs.Cam;
            var drag = cam != null ? cam.GetComponent<DragRigidbodyC>() : null;
            var held = drag != null ? drag.isHolding1 : null;
            HostPatches.AllowLocal = true;
            try
            {
                if (drag != null) drag.isHolding1 = null;
                ign.StartCoroutine("Trigger");
            }
            finally
            {
                HostPatches.AllowLocal = false;
                if (drag != null) drag.isHolding1 = held;
            }
            yield return new WaitForSeconds(0.9f);
            if (ign == null || ign.currentPos != 1 || ign.ignitionIsGo) yield break;
            var cl = GameRefs.CarLogic;
            var perf = cl != null ? cl.GetComponent<CarPerformanceC>() : null;
            bool batteryOk = perf != null && perf.installedBattery != null &&
                             perf.installedBattery.GetComponent<EngineComponentC>().charge > 0f &&
                             perf.installedBattery.GetComponent<EngineComponentC>().Condition > 0f;
            if (batteryOk)
            {
                ign.StartCoroutine("IgnitionGo");
            }
            else
            {
                Plugin.Log.LogInfo("Partida pelo tio falhou: bateria.");
                ign.TurnBackKey();
            }
            Traverse.Create(ign).Field("isTriggered").SetValue(false);
        }

        // ---------- CLIENTE ----------

        private float remoteSpeed;

        /// <summary>Cliente: o velocímetro do tio segue a velocidade do host (o CarControleScriptC dele não roda).</summary>
        private void LateUpdate()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Client || !net.Connected) return;
            var ccs = CarControleScriptC.Global;
            if (ccs == null || ccs.speedOMeterPointer == null) return;
            var needle = ccs.speedOMeterPointer.GetComponent<SpeedometerLogicC>();
            if (needle != null) needle.SetNeedle(remoteSpeed * 3f);
        }

        private void ApplyState(HostStateData s)
        {
            var net = CoopNet.Instance;
            if (net.Mode != CoopMode.Client || !GameRefs.InGame) return;
            remoteSpeed = s.Speed;

            var hb = Handbrake;
            if (hb != null && hb.currentPos != s.HandbrakePos)
            {
                if (s.HandbrakePos == 1) hb.ReleaseHandBrake(); else hb.ApplyHandBrake();
            }

            var ign = Ignition;
            if (ign != null && ign.currentPos != s.IgnitionPos)
            {
                var lever = ign.transform.parent != null ? ign.transform.parent.gameObject : ign.gameObject;
                iTween.Stop(lever);
                if (s.IgnitionPos == 1)
                {
                    ign.currentPos = 1;
                    if (ign.keyObject != null) ign.keyObject.GetComponent<KeyJangleC>().IgnitionOn();
                    if (ign.positionRotation.Length > 1)
                        iTween.RotateTo(lever, iTween.Hash("rotation", ign.positionRotation[1], "islocal", true, "time", ign.timeToComplete, "easetype", ign.easeType));
                }
                else
                {
                    ign.TurnBackKey(); // pos 0, chave de volta, som da chave
                }
            }

            var cl = GameRefs.CarLogic;
            if (cl != null && cl.engineOn != s.EngineOn)
            {
                cl.engineOn = s.EngineOn;
                if (s.EngineOn) cl.EngineTurnedOn(); else cl.EngineTurnedOff(); // só som do motor e escapamento
            }
        }

        /// <summary>CLIENTE: o tio clicou na chave/freio. Retorna true se mandou para o host.</summary>
        public static bool ForwardFromClient(Msg type)
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Client || !net.Connected) return false;
            if (DriveSync.Instance != null && DriveSync.Instance.ClientDriving) net.SendReliable(type);
            return true; // no cliente esses controles nunca rodam localmente
        }
    }

    /// <summary>Intercepta os cliques no freio de mão e na chave do lado do cliente.</summary>
    [HarmonyPatch]
    public static class HostPatches
    {
        /// <summary>Quando o host executa um clique vindo do tio.</summary>
        public static bool AllowLocal;

        [HarmonyPatch(typeof(HandbrakeLogicC), "Trigger")]
        [HarmonyPrefix]
        private static bool HandbrakeTrigger()
        {
            if (AllowLocal) return true;
            return !CarControlsSync.ForwardFromClient(Msg.UseHandbrake);
        }

        // Clicar no volante faz o mesmo que o freio de mão. No tio o banco "do jogo" não está ocupado,
        // então o jogo entrava no modo EMPURRAR o carro (tirava o tio do banco e deixava dirigir a pé).
        // No cliente o clique vira o mesmo pedido do freio de mão para o host.
        [HarmonyPatch(typeof(WheelLogicC), "Trigger")]
        [HarmonyPrefix]
        private static bool WheelTrigger()
        {
            if (AllowLocal) return true;
            return !CarControlsSync.ForwardFromClient(Msg.UseHandbrake);
        }

        [HarmonyPatch(typeof(IgnitionLogicC), "Trigger")]
        [HarmonyPrefix]
        private static bool IgnitionTrigger(ref IEnumerator __result)
        {
            if (AllowLocal) return true;
            if (!CarControlsSync.ForwardFromClient(Msg.UseIgnition)) return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty() { yield break; }
    }
}
