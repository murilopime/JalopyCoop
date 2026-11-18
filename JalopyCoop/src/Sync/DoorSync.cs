using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Portas do carro (motorista, passageiro), porta-malas e capô abertos/fechados iguais nas duas telas.
    ///
    /// Cada lado olha o estado do jogo local (DoorLogicC.open, CarLogicC.bonnetPopped/bonnetOpen). Mudou por
    /// ação do jogador local: manda. Do outro lado, o mod aciona a mesma animação do jogo (Trigger / PopBonnet),
    /// sem a falha aleatória de porta quebrada. O host manda o estado inteiro quando o tio entra no jogo.
    /// </summary>
    public class DoorSync : MonoBehaviour
    {
        public static DoorSync Instance { get; private set; }

        private const float ApplyWindow = 1.2f; // tempo da animação: não reenvia o que veio do outro lado

        private Transform builtFor;
        private bool hasPassengerDoor;
        private DoorLogicC[] doors = new DoorLogicC[0];
        private bool[] known = new bool[0];
        private float[] ignoreUntil = new float[0];

        private BonnetLogicC bonnet;
        private BonnetHandleLogicC bonnetHandle;
        private byte bonnetKnown;
        private float bonnetIgnoreUntil;
        private bool sentSnapshot;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += connected => { builtFor = null; sentSnapshot = false; };
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            var car = GameRefs.Car;
            if (car == null) return;
            if (builtFor != car || (!hasPassengerDoor && PassengerSeat.PassengerDoor() != null)) Build(car);

            try { Poll(); }
            catch (Exception e) { Plugin.Log.LogError("DoorSync: " + e); }
        }

        private void Build(Transform car)
        {
            builtFor = car;
            var list = new List<DoorLogicC>();
            foreach (var d in car.root.GetComponentsInChildren<DoorLogicC>(true))
            {
                if (d != null && !d.isEnvironmentalDoor) list.Add(d);
            }
            // A porta do passageiro (do tio) fica fora da hierarquia do carro: vem do UncleLogicC.uncleDoorTrigger.
            // Sempre no fim da lista, para o índice ser igual nos dois jogos. Quando o tio-NPC está sentado ele vira
            // filho do carro e a porta aparece no meio da busca (teste online: porta-malas trocado com o passageiro).
            var pd = PassengerSeat.PassengerDoor();
            hasPassengerDoor = pd != null;
            if (pd != null) { list.Remove(pd); list.Add(pd); }
            doors = list.ToArray();
            known = new bool[doors.Length];
            ignoreUntil = new float[doors.Length];
            for (int i = 0; i < doors.Length; i++) known[i] = doors[i].open;

            bonnet = car.root.GetComponentInChildren<BonnetLogicC>(true);
            bonnetHandle = car.root.GetComponentInChildren<BonnetHandleLogicC>(true);
            bonnetKnown = BonnetState();

            var names = new List<string>();
            foreach (var d in doors) names.Add(d.name + (d.isBoot ? "(porta-malas)" : d.driversDoor ? "(motorista)" : d.isUncleDoor ? "(passageiro)" : ""));
            Plugin.Log.LogInfo("Portas: " + doors.Length + " [" + string.Join(", ", names.ToArray()) + "], capô " + (bonnet != null ? "ok" : "não achado"));
        }

        private static byte BonnetState()
        {
            var cl = GameRefs.CarLogic;
            if (cl == null) return 0;
            return (byte)((cl.bonnetPopped ? 1 : 0) | (cl.bonnetOpen ? 2 : 0));
        }

        // ================= local -> rede =================

        private void Poll()
        {
            var net = CoopNet.Instance;
            float now = Time.realtimeSinceStartup;

            // Host: estado inteiro quando o tio está pronto (ele pede a lista de itens ao entrar no jogo).
            if (net.Mode == CoopMode.Host && !sentSnapshot && ItemSync.Instance != null && ItemSync.Instance.Ready)
            {
                sentSnapshot = true;
                for (int i = 0; i < doors.Length; i++) { known[i] = doors[i].open; SendDoor(i, known[i]); }
                bonnetKnown = BonnetState();
                SendBonnet(bonnetKnown);
            }

            for (int i = 0; i < doors.Length; i++)
            {
                var d = doors[i];
                if (d == null) continue;
                if (now < ignoreUntil[i]) continue;
                if (d.open == known[i]) continue;
                known[i] = d.open;
                SendDoor(i, d.open);
            }

            if (now >= bonnetIgnoreUntil)
            {
                byte b = BonnetState();
                if (b != bonnetKnown)
                {
                    bonnetKnown = b;
                    SendBonnet(b);
                }
            }
        }

        private void SendDoor(int i, bool open)
        {
            CoopNet.Instance.SendReliable(Msg.DoorState, w => { w.Write((byte)i); w.Write(open); });
        }

        private void SendBonnet(byte state)
        {
            CoopNet.Instance.SendReliable(Msg.BonnetState, w => w.Write(state));
        }

        // ================= rede -> local =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.DoorState && type != Msg.BonnetState) return;
            try
            {
                var car = GameRefs.Car;
                if (car == null) return;
                if (builtFor != car) Build(car);
                if (type == Msg.DoorState)
                {
                    int i = r.ReadByte();
                    bool open = r.ReadBoolean();
                    if (i >= doors.Length || doors[i] == null) return;
                    known[i] = open;
                    ignoreUntil[i] = Time.realtimeSinceStartup + ApplyWindow;
                    ApplyDoor(doors[i], open);
                }
                else
                {
                    byte state = r.ReadByte();
                    bonnetKnown = state;
                    bonnetIgnoreUntil = Time.realtimeSinceStartup + ApplyWindow + 0.8f;
                    ApplyBonnet(state);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("DoorSync " + type + ": " + e);
            }
        }

        private static void ApplyDoor(DoorLogicC d, bool open)
        {
            if (d.open == open) return;
            // Abre/fecha com a animação do jogo, sem a falha de porta quebrada nem tranca.
            bool broken = d.isBroken, locked = d.isLocked;
            d.isBroken = false;
            d.isLocked = false;
            try { d.StartCoroutine("Trigger"); }
            finally { d.isBroken = broken; d.isLocked = locked; }
            Plugin.Log.LogInfo("Portas: o outro jogador " + (open ? "abriu " : "fechou ") + d.name);
        }

        private void ApplyBonnet(byte state)
        {
            var cl = GameRefs.CarLogic;
            if (cl == null || bonnet == null) return;
            bool popped = (state & 1) != 0, open = (state & 2) != 0;

            if (open && !cl.bonnetOpen)
            {
                if (!cl.bonnetPopped) cl.bonnetPopped = true; // abre direto (a trava já foi puxada do outro lado)
                bonnet.StartCoroutine("Trigger");
                Plugin.Log.LogInfo("Capô: o outro jogador abriu.");
            }
            else if (!open && cl.bonnetOpen)
            {
                bonnet.StartCoroutine("Trigger"); // fecha (DoorClosed zera a trava)
                Plugin.Log.LogInfo("Capô: o outro jogador fechou.");
            }
            else if (!open && popped && !cl.bonnetPopped)
            {
                bonnet.PopBonnet(); // só a trava: capô levanta um pouco
                if (bonnetHandle != null) { try { bonnetHandle.MoveBack(); } catch { } }
                Plugin.Log.LogInfo("Capô: o outro jogador puxou a trava.");
            }
        }
    }
}
