using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Entrar e sair do PASSAGEIRO igual ao motorista do jogo (pedido do usuário), para o host e para o tio:
    ///  - abre a porta (clique normal do jogo);
    ///  - mira no banco (ele acende) e clica: senta;
    ///  - sentado, com a porta aberta, aparece o ícone de saída: clica e sai.
    /// O jogo só tem esses objetos no motorista; o mod cria cópias espelhadas no passageiro
    /// (mesmo formato, mesmo brilho, mesmo ícone), com o comportamento do co-op.
    /// </summary>
    [DefaultExecutionOrder(900)]
    public class PassengerSeat : MonoBehaviour
    {
        public static PassengerSeat Instance { get; private set; }

        private GameObject seatClone, exitClone;
        private Transform builtFor;
        private Renderer seatRenderer;
        private Material seatStart, seatGlow;
        private bool seatGlowing;
        private SpriteRenderer exitSprite;

        private void Awake() { Instance = this; }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) { SetActive(false, false); return; }
            var cl = GameRefs.CarLogic;
            var car = GameRefs.Car;
            if (cl == null || car == null || cl.playerSeat == null || cl.doorExit == null) return;
            if (builtFor != cl.transform || seatClone == null || exitClone == null) Build(cl, car);
            if (seatClone == null) return;
            if (!seatClone.activeSelf) seatClone.SetActive(true);

            var uncleDoor = PassengerDoor();
            bool doorOpen = uncleDoor != null && uncleDoor.open;
            KeepDoorUsable(uncleDoor);

            bool seatFree, meInPassenger;
            if (net.Mode == CoopMode.Host)
            {
                var ds = DriveSync.Instance;
                var us = UncleSync.Instance;
                bool uncleInPassenger = us != null && us.HasRemote && us.RemoteSeated && (ds == null || !ds.RemoteDriving);
                meInPassenger = ds != null && ds.HostPassenger && ds.HostSeatedNow;
                bool meSeatedAnywhere = ds != null && ds.HostSeatedNow;
                seatFree = !uncleInPassenger && !meSeatedAnywhere;
            }
            else
            {
                var us = UncleSync.Instance;
                var ds = DriveSync.Instance;
                var cs = CarSync.Instance;
                bool hostInPassenger = cs != null && cs.HasLatest && cs.Latest.HostSat && cs.Latest.HostPassenger && !cs.Latest.RemoteDriving;
                bool seated = us != null && us.IsSeated;
                meInPassenger = seated && (ds == null || !ds.ClientDriving);
                seatFree = !hostInPassenger && !seated;
                ClientDriverExit(cl, ds, us);
            }

            seatClone.tag = seatFree ? "Interactor" : "Untagged";
            if (!seatFree && seatGlowing) RaycastExitSeat();
            bool exitOn = meInPassenger && doorOpen;
            if (exitClone.activeSelf != exitOn) exitClone.SetActive(exitOn);
            if (exitOn && exitSprite != null && GameRefs.Cam != null)
                exitSprite.transform.LookAt(GameRefs.Cam.transform.position, -Vector3.up);
            if (seatGlowing && seatRenderer != null)
                seatRenderer.material.SetFloat("_RimPower", Mathf.PingPong(Time.time, 0.75f) + 1.25f);
        }

        // ---------- cliques (chegam do jogo por SendMessage, ver PassengerClick) ----------

        public void TriggerSeat()
        {
            var net = CoopNet.Instance;
            if (net == null) return;
            RaycastExitSeat();
            if (net.Mode == CoopMode.Host)
            {
                if (DriveSync.Instance != null) DriveSync.Instance.HostEnterPassenger();
            }
            else if (UncleSync.Instance != null) UncleSync.Instance.SitPassenger();
        }

        public void TriggerExit()
        {
            var net = CoopNet.Instance;
            if (net == null) return;
            if (net.Mode == CoopMode.Host)
            {
                if (DriveSync.Instance != null) DriveSync.Instance.HostExitPassenger();
            }
            else if (UncleSync.Instance != null) UncleSync.Instance.SetWantSeat(false);
        }

        public void RaycastEnterSeat()
        {
            if (seatClone == null || seatClone.tag != "Interactor" || seatRenderer == null) return;
            seatGlowing = true;
            seatRenderer.enabled = true;
            if (seatGlow != null) seatRenderer.material = seatGlow;
        }

        public void RaycastExitSeat()
        {
            seatGlowing = false;
            if (seatRenderer == null) return;
            if (seatStart != null) seatRenderer.material = seatStart;
            seatRenderer.enabled = false; // a cópia só aparece como brilho (o banco de verdade já está lá)
        }

        // ---------- montagem ----------

        private void Build(CarLogicC cl, Transform car)
        {
            Clear();
            builtFor = cl.transform;
            var seatLogic = cl.playerSeat.GetComponent<SeatLogicC>();
            seatClone = MirrorClone(cl.playerSeat, car, "JalopyCoop_PassengerSeat", true);
            exitClone = MirrorClone(cl.doorExit, car, "JalopyCoop_PassengerExit", false); // sem escala negativa: o ícone ficava de ponta-cabeça e o clique falhava
            if (seatClone == null || exitClone == null) { Clear(); return; }

            seatRenderer = seatClone.GetComponent<Renderer>();
            var origRenderer = cl.playerSeat.GetComponent<Renderer>();
            seatStart = seatLogic != null && seatLogic.startMaterial != null ? seatLogic.startMaterial : (origRenderer != null ? origRenderer.sharedMaterial : null);
            seatGlow = seatLogic != null ? seatLogic.glowMaterial : null;
            if (seatRenderer != null) seatRenderer.enabled = false;
            seatClone.AddComponent<PassengerClick>().IsSeat = true;
            seatClone.SetActive(true);

            exitClone.AddComponent<PassengerClick>().IsSeat = false;
            exitSprite = exitClone.GetComponentInChildren<SpriteRenderer>(true);
            exitClone.SetActive(false);
            Plugin.Log.LogInfo("Banco e saída do passageiro criados (cópias espelhadas do motorista).");
        }

        /// <summary>Cópia sem os scripts do jogo, espelhada no eixo X do carro.</summary>
        private static GameObject MirrorClone(GameObject original, Transform car, string name, bool flipScale)
        {
            var go = (GameObject)Instantiate(original, original.transform.parent);
            go.name = name;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(mb);
            foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) DestroyImmediate(a);

            var p = car.InverseTransformPoint(original.transform.position);
            p.x = -p.x;
            go.transform.position = car.TransformPoint(p);
            var q = Quaternion.Inverse(car.rotation) * original.transform.rotation;
            q = new Quaternion(q.x, -q.y, -q.z, q.w);   // espelho no plano X
            go.transform.rotation = car.rotation * q;
            if (flipScale)
            {
                var s = go.transform.localScale; s.x = -s.x;
                go.transform.localScale = s;
            }
            return go;
        }

        private void Clear()
        {
            if (seatClone != null) Destroy(seatClone);
            if (exitClone != null) Destroy(exitClone);
            seatClone = exitClone = null;
            builtFor = null;
            seatGlowing = false;
        }

        private void SetActive(bool seat, bool exit)
        {
            if (seatClone != null && seatClone.activeSelf != seat) seatClone.SetActive(seat);
            if (exitClone != null && exitClone.activeSelf != exit) exitClone.SetActive(exit);
        }

        // ---------- portas ----------

        internal static DoorLogicC PassengerDoor()
        {
            var u = GameRefs.Uncle;
            var logic = u != null ? u.GetComponent<UncleLogicC>() : null;
            return logic != null && logic.uncleDoorTrigger != null ? logic.uncleDoorTrigger.GetComponent<DoorLogicC>() : null;
        }

        /// <summary>
        /// O jogo desliga o clique da porta do tio (e às vezes a deixa "emperrada") nas rotinas do tio-NPC.
        /// No co-op essa porta é de jogador: sempre clicável e abre de primeira.
        /// </summary>
        private static void KeepDoorUsable(DoorLogicC door)
        {
            if (door == null) return;
            var bc = door.GetComponent<BoxCollider>();
            if (bc != null && !bc.enabled) bc.enabled = true;
            if (door.isBroken) door.isBroken = false;
        }

        /// <summary>
        /// CLIENTE no volante: a saída do motorista do jogo (o ícone) aparece com a porta do motorista aberta
        /// e o freio de mão puxado, como no jogo. O clique dela é tratado em ClientSeatPatches.
        /// </summary>
        private static void ClientDriverExit(CarLogicC cl, DriveSync ds, UncleSync us)
        {
            bool driving = ds != null && ds.ClientDriving && us != null && us.IsSeated;
            var leftDoor = cl.leftDoor != null ? cl.leftDoor.GetComponent<DoorLogicC>() : null;
            var cs = CarSync.Instance;
            bool handbrakeOn = cs == null || !cs.HasLatest || cs.Latest.HandbrakePos == 0;
            bool want = driving && leftDoor != null && leftDoor.open && handbrakeOn;
            if (driving || cl.doorExit.activeSelf) { if (cl.doorExit.activeSelf != want) cl.doorExit.SetActive(want); }
        }
    }

    /// <summary>Recebe o clique e o brilho do jogo (DragRigidbodyC manda "Trigger"/"RaycastEnter"/"RaycastExit").</summary>
    public class PassengerClick : MonoBehaviour
    {
        public bool IsSeat;

        public void Trigger()
        {
            var ps = PassengerSeat.Instance;
            if (ps == null) return;
            Plugin.Log.LogInfo("Clique no " + (IsSeat ? "banco" : "ícone de saída") + " do passageiro.");
            if (IsSeat) ps.TriggerSeat(); else ps.TriggerExit();
        }

        public void RaycastEnter() { if (IsSeat && PassengerSeat.Instance != null) PassengerSeat.Instance.RaycastEnterSeat(); }
        public void RaycastExit() { if (IsSeat && PassengerSeat.Instance != null) PassengerSeat.Instance.RaycastExitSeat(); }
    }
}
