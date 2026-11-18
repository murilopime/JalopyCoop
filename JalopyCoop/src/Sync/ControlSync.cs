using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Controles do painel/carro iguais nas duas telas: faróis, pisca-alerta, limpador, esguicho de água,
    /// vidros (manivela), luz interna e buzina. (Rádio fica no 4c.)
    ///
    /// Mesmo esquema das portas: cada lado olha o estado local; mudou por ação do jogador local -> manda.
    /// O outro lado aciona o Trigger do próprio jogo até chegar no mesmo estado (animação e som iguais).
    /// </summary>
    public class ControlSync : MonoBehaviour
    {
        public static ControlSync Instance { get; private set; }

        private const float ApplyWindow = 1.5f;

        private class Control
        {
            public string name;
            public MonoBehaviour comp;
            public Func<int> get;               // estado atual
            public Action<int> apply;           // leva ao estado (pode levar alguns quadros)
            public bool oneShot;                // esguicho: 0 -> 1 -> volta sozinho; só a ida importa
            public int known;
            public float ignoreUntil;
        }

        private readonly List<Control> controls = new List<Control>();
        private Transform builtFor;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { builtFor = null; };
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            var car = GameRefs.Car;
            if (car == null) return;
            if (builtFor != car || PassengerSeat.PassengerDoor() != checkedDoor) Build(car);
            try { Poll(); }
            catch (Exception e) { Plugin.Log.LogError("ControlSync: " + e); }
        }

        // ================= lista de controles (mesma ordem nos dois jogos) =================

        private void Build(Transform car)
        {
            builtFor = car;
            controls.Clear();
            var root = car.root;

            foreach (var h in root.GetComponentsInChildren<HeadlightLogicC>(true))
            {
                var c = h;
                Add("faróis", c, () => c.currentPos, target => Cycle(c, () => c.currentPos, target));
            }
            foreach (var h in root.GetComponentsInChildren<HazardsLogicC>(true))
            {
                var c = h;
                Add("pisca-alerta", c, () => c.currentPos, target => Cycle(c, () => c.currentPos, target));
            }
            foreach (var w in root.GetComponentsInChildren<WindowWipersLogicC>(true))
            {
                var c = w;
                Add("limpador", c, () => c.currentPos, target => Cycle(c, () => c.currentPos, target));
            }
            foreach (var s in root.GetComponentsInChildren<WindowWiperSprayLogicC>(true))
            {
                var c = s;
                var ctl = Add("esguicho", c, () => c.currentPos, target => { if (target == 1 && c.currentPos == 0) c.StartCoroutine("Trigger"); });
                ctl.oneShot = true;
            }
            var windows = new List<WindowLogicC>(root.GetComponentsInChildren<WindowLogicC>(true));
            // Vidro do passageiro sempre no fim (ver abaixo): com o tio-NPC sentado ele aparece no meio da busca
            // e a lista ficava em ordem diferente nos dois jogos (teste online: rádio e vidros trocados).
            var pdoor = PassengerSeat.PassengerDoor();
            var pwin = pdoor != null && pdoor.windowScriptTrgt != null ? pdoor.windowScriptTrgt.GetComponent<WindowLogicC>() : null;
            if (pwin != null) windows.Remove(pwin);
            foreach (var w in windows) AddWindow(w);
            foreach (var l in root.GetComponentsInChildren<LightSwitchC>(true))
            {
                if (!l.isCarComponent) continue;
                var c = l;
                Add("luz interna", c, () => c.isOn ? 1 : 0, target => Cycle(c, () => c.isOn ? 1 : 0, target));
            }
            // Vidro do passageiro: fica na porta do tio, fora da hierarquia do carro. No fim da lista (índice igual nos 2).
            var pd = PassengerSeat.PassengerDoor();
            checkedDoor = pd; // reconstrói só se a porta mudar (evita refazer todo quadro)
            hasPassengerWindow = pd != null && pd.windowScriptTrgt != null;
            if (hasPassengerWindow)
            {
                var pw = pd.windowScriptTrgt.GetComponent<WindowLogicC>();
                if (pw != null) { lateWindow = pw; }
            }
            // Rádio (4c): liga/desliga (CarLogicC.radioOn) e volume alto/baixo. A faixa vai pelo RadioSync.
            foreach (var rf in root.GetComponentsInChildren<RadioFreqLogicC>(true))
            {
                var c = rf;
                Func<int> on = () => { var cl = GameRefs.CarLogic; return cl != null && cl.radioOn ? 1 : 0; };
                Add("rádio", c, on, target => Cycle(c, on, target));
            }
            foreach (var rv in root.GetComponentsInChildren<RadioVolLogicC>(true))
            {
                var c = rv;
                Add("volume do rádio", c, () => c.volumeSetting, target => Cycle(c, () => c.volumeSetting, target));
            }
            foreach (var h in root.GetComponentsInChildren<CarHornC>(true))
            {
                var c = h;
                var isOn = Traverse.Create(c).Field("isOn");
                Add("buzina", c, () => isOn.GetValue<bool>() ? 1 : 0, target =>
                {
                    bool on = isOn.GetValue<bool>();
                    if (target == 1 && !on) c.Trigger();
                    else if (target == 0 && on) c.StopAction();
                });
            }
            if (lateWindow != null) { AddWindow(lateWindow); lateWindow = null; }
            Plugin.Log.LogInfo("Controles do carro: " + controls.Count + " (faróis, pisca, limpador, esguicho, vidros, luz, buzina).");
        }

        private WindowLogicC lateWindow;
        private bool hasPassengerWindow;
        private DoorLogicC checkedDoor;

        private void AddWindow(WindowLogicC w)
        {
            var c = w;
            Add("vidro", c, () => c.isOpen ? 1 : 0, target =>
            {
                if ((c.isOpen ? 1 : 0) == target || c.isAnimating) return;
                int chance = c.openChance;
                c.openChance = 100; // sem a chance de travar: o outro lado já abriu
                try { c.StartCoroutine("Trigger"); } finally { c.openChance = chance; }
            });
        }

        private Control Add(string name, MonoBehaviour comp, Func<int> get, Action<int> apply)
        {
            var c = new Control { name = name, comp = comp, get = get, apply = apply };
            c.known = SafeGet(c);
            controls.Add(c);
            return c;
        }

        private static int SafeGet(Control c)
        {
            try { return c.comp != null ? c.get() : 0; } catch { return 0; }
        }

        /// <summary>Clica (Trigger do jogo) até o estado bater, no máximo 3 vezes.</summary>
        private void Cycle(MonoBehaviour comp, Func<int> get, int target)
        {
            StartCoroutine(CycleRoutine(comp, get, target));
        }

        private IEnumerator CycleRoutine(MonoBehaviour comp, Func<int> get, int target)
        {
            for (int i = 0; i < 3 && comp != null && get() != target; i++)
            {
                comp.StartCoroutine("Trigger");
                yield return new WaitForSeconds(0.15f);
            }
        }

        // ================= local -> rede =================

        private void Poll()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < controls.Count; i++)
            {
                var c = controls[i];
                if (c.comp == null || now < c.ignoreUntil) continue;
                int s = SafeGet(c);
                if (s == c.known) continue;
                c.known = s;
                if (c.oneShot && s == 0) continue; // esguicho voltando sozinho
                int idx = i, state = s;
                CoopNet.Instance.SendReliable(Msg.ControlState, w => { w.Write((short)idx); w.Write((byte)state); });
            }
        }

        // ================= rede -> local =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.ControlState) return;
            try
            {
                int idx = r.ReadInt16();
                int state = r.ReadByte();
                var car = GameRefs.Car;
                if (car == null) return;
                if (builtFor != car) Build(car);
                if (idx < 0 || idx >= controls.Count) return;
                var c = controls[idx];
                if (c.comp == null) return;
                c.known = state;
                c.ignoreUntil = Time.realtimeSinceStartup + ApplyWindow;
                if (c.oneShot) c.known = 0;
                c.apply(state);
                if (c.name != "buzina") Plugin.Log.LogInfo("Controles: o outro jogador mexeu no " + c.name + " (" + state + ")");
            }
            catch (Exception e) { Plugin.Log.LogError("ControlSync: " + e); }
        }
    }

    /// <summary>
    /// Tio: o velocímetro do jogo é calculado pelas rodas, que no tio não giram (o carro segue o host) e zeravam
    /// o ponteiro todo quadro. No tio conectado esse cálculo não roda; o CarControlsSync põe a velocidade do host.
    /// </summary>
    [HarmonyPatch(typeof(CarControleScriptC), "CalculateSpeed")]
    internal static class ClientSpeedometerPatch
    {
        private static bool Prefix()
        {
            var net = CoopNet.Instance;
            return net == null || net.Mode != CoopMode.Client || !net.Connected;
        }
    }
}
