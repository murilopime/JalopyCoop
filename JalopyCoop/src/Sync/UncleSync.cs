using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Papel do tio no co-op.
    ///
    /// CLIENTE (o jogador que é o tio):
    ///  - esconde o tio-NPC local (ele "é" o tio) e cala as falas dele;
    ///  - senta o jogador no banco do passageiro (espelho do banco do motorista) e trava o andar;
    ///  - manda para o host para onde está olhando.
    ///  Senta/levanta pelo banco e pela porta do passageiro (as interações do jogo).
    ///
    /// HOST:
    ///  - o tio-NPC continua existindo (história, diálogos), mas quando o jogador-tio está sentado
    ///    a cabeça do tio segue o olhar do jogador-tio (HeadLookController do próprio jogo).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class UncleSync : MonoBehaviour
    {
        public static UncleSync Instance { get; private set; }

        /// <summary>CLIENTE: o co-op sentou o jogador-tio (banco controlado pelo mod).</summary>
        public static bool ClientSeatedByCoop
        {
            get
            {
                var n = CoopNet.Instance;
                return Instance != null && Instance.seated && n != null && n.Mode == CoopMode.Client && n.Connected;
            }
        }
        private void Awake() { Instance = this; }

        /// <summary>CLIENTE: sentar (true) ou levantar (false), usado pelo teste automático.</summary>
        public void SetWantSeat(bool on) { wantSeat = on; }

        private float lastDoorToggle = -10f;
        private bool exitDriverSide;

        public bool IsSeated { get { return seated; } }

        /// <summary>CLIENTE: clicou no banco do passageiro (PassengerSeat).</summary>
        public void SitPassenger()
        {
            if (seated) return;
            var cs = CarSync.Instance;
            if (cs != null && cs.HasLatest && cs.Latest.HostSat && cs.Latest.HostPassenger && !cs.Latest.RemoteDriving)
            {
                if (DriveSync.Instance != null) DriveSync.Instance.SayHint("O host está no banco do passageiro", 4f);
                return;
            }
            wantSeat = true;
            Plugin.Log.LogInfo("Banco do passageiro: sentar.");
        }

        /// <summary>CLIENTE: levanta saindo pelo lado do MOTORISTA (saiu do volante pela porta).</summary>
        public void LeaveOnDriverSide()
        {
            exitDriverSide = true;
            wantSeat = false;
        }

        /// <summary>CLIENTE: o jogo desliga o clique da porta do tio em várias rotinas dele; aqui ela fica sempre clicável.</summary>
        private void KeepPassengerDoorClickable()
        {
            var u = GameRefs.Uncle;
            var logic = u != null ? u.GetComponent<UncleLogicC>() : null;
            if (logic == null || logic.uncleDoorTrigger == null) return;
            var bc = logic.uncleDoorTrigger.GetComponent<BoxCollider>();
            if (bc != null && !bc.enabled) bc.enabled = true;
        }

        /// <summary>CLIENTE: clique na porta do passageiro. Sentado: sai. De pé (perto do carro): entra.</summary>
        public void ToggleSeatByDoor()
        {
            if (Time.realtimeSinceStartup - lastDoorToggle < 0.7f) return; // um clique só
            lastDoorToggle = Time.realtimeSinceStartup;
            if (DriveSync.Instance != null && DriveSync.Instance.ClientDriving)
            {
                Plugin.Log.LogInfo("Porta do passageiro: você está no volante (saia pela porta do motorista antes).");
                return;
            }
            if (!seated)
            {
                var cs = CarSync.Instance;
                if (cs != null && cs.HasLatest && cs.Latest.HostSat && cs.Latest.HostPassenger && !cs.Latest.RemoteDriving)
                {
                    if (DriveSync.Instance != null) DriveSync.Instance.SayHint("O host está no banco do passageiro", 4f);
                    return;
                }
            }
            wantSeat = !seated;
            Plugin.Log.LogInfo("Porta do passageiro: " + (wantSeat ? "entrar" : "sair"));
        }
        /// <summary>HOST: já chegou estado do jogador-tio.</summary>
        public bool HasRemote { get { return hasRemote; } }
        /// <summary>HOST: posição do corpo do jogador-tio (último pacote).</summary>
        public Vector3 RemoteBody { get { return remote.BodyPos; } }
        public bool RemoteSeated { get { return remote.Seated; } }

        // ---- cliente ----
        private bool seated;
        private bool wantSeat = false;          // começa em pé na garagem, como o host (pedido do usuário)
        private GameObject hiddenUncle;
        private Transform savedPlayerParent;

        // ---- host ----
        private ClientStateData remote;
        private bool hasRemote;
        private Transform uncleHead;
        private float lastBoardTry = -100f;
        private Vector3 smoothLookCar;

        /// <summary>HOST: true enquanto a cabeça do tio segue o jogador-tio (o CursorHit do jogo fica bloqueado).</summary>
        public static bool OverridingUncleLook { get; private set; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.BuildClientState = BuildClientState;
            net.OnClientState += s => { remote = s; hasRemote = true; };
            net.OnConnectionChanged += connected =>
            {
                hasRemote = false;
                if (!connected)
                {
                    RestoreHostUncle();
                    if (following)
                    {
                        following = false;
                        var u = GameRefs.Uncle;
                        var cc = u != null ? u.GetComponent<CharacterController>() : null;
                        if (cc != null) cc.enabled = true;
                    }
                    StandUp();
                    ShowLocalUncle();
                    ShowSuitcase();
                    wantSeat = false;
                    spawnChecked = false;
                }
            };
        }

        private ClientStateData BuildClientState()
        {
            var s = new ClientStateData();
            s.Seated = seated;
            var car = GameRefs.Car;
            var cam = GameRefs.Cam;
            if (car != null && cam != null) s.LookDirCar = car.InverseTransformDirection(cam.transform.forward);
            var player = GameRefs.Player;
            if (player != null) s.BodyPos = player.transform.position;
            if (cam != null) s.LookDir = cam.transform.forward;
            if (DriveSync.Instance != null && DriveSync.Instance.ClientDriving)
            {
                s.DriveBits = CoopInput.ReadLocalDriveBits();
                s.JoyX = CoopInput.SafeAxis("JoypadX");
                s.JoyAccelerate = CoopInput.SafeAxis("JoypadAccelerate");
                s.JoyReverse = CoopInput.SafeAxis("JoypadReverse");
            }
            return s;
        }

        /// <summary>CLIENTE: troca de banco (motorista quando dirige, passageiro quando não).</summary>
        public void RefreshSeat()
        {
            if (!seated) return;
            var car = GameRefs.Car;
            var player = GameRefs.Player;
            if (car == null || player == null) return;
            player.transform.localPosition = SeatLocal(car);
            Plugin.Log.LogInfo("Trocou de banco: " + (IsDriverSide ? "motorista" : "passageiro"));
        }

        private static bool IsDriverSide
        {
            get { return DriveSync.Instance != null && DriveSync.Instance.ClientDriving; }
        }

        /// <summary>Lugar do jogador no espaço do carro: o banco do motorista ou o espelho dele (passageiro).</summary>
        private static Vector3 SeatLocal(Transform car)
        {
            var seatLoc = GameRefs.DriverSeatLoc;
            var local = car.InverseTransformPoint(seatLoc.position);
            if (!IsDriverSide) local.x = -local.x;
            return local;
        }

        private bool spawnChecked;

        /// <summary>
        /// Os dois lados: o tio-NPC carrega a maleta até a 1ª vez que entra no carro (UncleEnterCar a guarda).
        /// Como no co-op ele começa a pé, a maleta é guardada no carro do mesmo jeito que o jogo faz.
        /// </summary>
        private static bool suitcaseLogged;

        private static void StowSuitcase()
        {
            var u = GameRefs.Uncle;
            var logic = u != null ? u.GetComponent<UncleLogicC>() : null;
            if (logic == null) return;
            var cl = logic.carLogic != null ? logic.carLogic.GetComponent<CarLogicC>() : (GameRefs.CarLogic);
            var sc = logic.suitcase;
            if (!suitcaseLogged)
            {
                suitcaseLogged = true;
                Plugin.Log.LogInfo("Maleta: suitcase=" + (sc != null ? PathOf(sc.transform) : "null") +
                                   " | carLogic=" + (cl != null) + " | suitcaseTransform=" + (cl != null && cl.suitcaseTransform != null ? PathOf(cl.suitcaseTransform) : "null") +
                                   " | na mão=" + (GameRefs.FindDeep(u.transform, "SuitCase1") != null));
            }
            // Guarda no porta-maleta do carro (suitcaseHolder), do jeito que o jogo faz quando o tio vai embora
            // (UncleGoneForeverLogic). O "suitcaseTransform" que a rotina de entrar usa NÃO existe nesta cena:
            // quando o tio-NPC entra no carro, o jogo solta a maleta no nada. Por isso ela é guardada de novo
            // sempre que estiver na mão do tio ou solta (sem pai).
            if (sc == null || cl == null || cl.suitcaseHolder == null) return;
            var parent = sc.transform.parent;
            if (parent == cl.suitcaseHolder) return;
            if (parent != null && !sc.transform.IsChildOf(logic.transform)) return; // o jogador pegou/guardou em outro lugar
            sc.transform.parent = cl.suitcaseHolder;
            sc.transform.localPosition = Vector3.zero;
            sc.transform.localEulerAngles = Vector3.zero;
            var relay = sc.GetComponent<SuitcaseRelayC>();
            if (relay != null)
            {
                if (relay._lock != null && relay._lock.GetComponent<OpenBriefcaseLogicC>() != null) relay._lock.GetComponent<OpenBriefcaseLogicC>().CarTrigger();
                relay.ChangeToCarMesh();
            }
            Plugin.Log.LogInfo("Maleta do tio guardada no porta-maleta do carro.");
        }

        private static GameObject hiddenSuitcase;

        /// <summary>
        /// Pedido do usuário: no co-op a maleta do tio não aparece (nem na mão, nem no carro).
        /// Ela fica só ESCONDIDA (a história do tio usa a maleta e as cartas dela; apagar poderia quebrar o save)
        /// e volta ao desligar o co-op.
        /// </summary>
        private static void HideSuitcase()
        {
            var u = GameRefs.Uncle;
            var logic = u != null ? u.GetComponent<UncleLogicC>() : null;
            if (logic == null || logic.suitcase == null) return;
            if (logic.suitcase.activeSelf)
            {
                logic.suitcase.SetActive(false);
                hiddenSuitcase = logic.suitcase;
                Plugin.Log.LogInfo("Maleta do tio escondida (co-op).");
            }
        }

        private static void ShowSuitcase()
        {
            if (hiddenSuitcase != null) hiddenSuitcase.SetActive(true);
            hiddenSuitcase = null;
        }

        private static string PathOf(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        /// <summary>
        /// CLIENTE: o tio começa em pé na garagem (mesmo save do host = mesmo lugar). Se nasceu em cima do host,
        /// vai uns passos para o lado, uma vez só.
        /// </summary>
        private bool fakeSeatFlag;

        /// <summary>
        /// CLIENTE: o jogo só deixa usar o mapa (e o manual) com o banco do carro "ocupado" (SeatLogicC.isSat).
        /// O tio senta pelo mod, então marcamos o banco como ocupado enquanto ele está sentado. Os outros usos
        /// desse sinal (saída pela porta, freio, ignição) já são tratados pelo mod no tio.
        /// </summary>
        private void KeepGameSeatFlag()
        {
            var cl = GameRefs.CarLogic;
            var seat = cl != null && cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
            if (seat == null) return;
            if (seated) { if (!seat.isSat) seat.isSat = true; fakeSeatFlag = true; }
            else if (fakeSeatFlag) { seat.isSat = false; fakeSeatFlag = false; }
        }

        private void SpreadFromHostOnSpawn()
        {
            if (spawnChecked || seated) return;
            var cs = CarSync.Instance;
            var player = GameRefs.Player;
            if (cs == null || !cs.HasLatest || player == null) return;
            spawnChecked = true;
            var s = cs.Latest;
            if (s.HostSat || s.HostBody.sqrMagnitude < 0.001f) return;
            var d = player.transform.position - s.HostBody; d.y = 0f;
            if (d.magnitude > 2.5f) return;
            var side = Vector3.Cross(Vector3.up, s.HostLook); side.y = 0f;
            if (side.sqrMagnitude < 0.001f) side = Vector3.right;
            side.Normalize();
            var back = Vector3.Cross(side, Vector3.up);

            // Tenta vários lugares em volta do host e fica no primeiro livre com chão (antes ia às cegas e
            // podia cair dentro de uma prateleira).
            Vector3[] dirs = { side, -side, back, -back, (side + back).normalized, (back - side).normalized, (side - back).normalized, (-side - back).normalized };
            float[] dists = { 2.5f, 3.5f, 2f };
            foreach (var dist in dists)
            {
                foreach (var dir in dirs)
                {
                    Vector3 spot;
                    if (!FreeSpot(player, s.HostBody + dir * dist, out spot)) continue;
                    player.transform.position = spot;
                    Plugin.Log.LogInfo("Tio nasceu em cima do host: foi para o lado (" + dist + " m, lugar livre).");
                    return;
                }
            }
            Plugin.Log.LogInfo("Tio nasceu em cima do host: sem lugar livre por perto, ficou onde estava.");
        }

        /// <summary>Lugar livre para o corpo do jogador (cápsula do tamanho dele) com chão embaixo.</summary>
        internal static bool FreeSpot(GameObject player, Vector3 around, out Vector3 spot)
        {
            spot = around;
            float radius = 0.4f, height = 1.8f;
            var cc = player.GetComponent<CharacterController>();
            var cap = player.GetComponent<CapsuleCollider>();
            float scale = player.transform.lossyScale.y;
            if (cc != null) { radius = cc.radius * scale; height = cc.height * scale; }
            else if (cap != null) { radius = cap.radius * scale; height = cap.height * scale; }

            // chão: raio de cima para baixo
            RaycastHit ground;
            var from = new Vector3(around.x, around.y + height, around.z);
            if (!Physics.Raycast(from, Vector3.down, out ground, height * 2f, ~0, QueryTriggerInteraction.Ignore)) return false;
            if (ground.transform.IsChildOf(player.transform)) return false;
            if (Vector3.Angle(ground.normal, Vector3.up) > 30f) return false; // em cima de algo torto/inclinado

            var bottom = ground.point + Vector3.up * (radius + 0.05f);
            var top = ground.point + Vector3.up * Mathf.Max(radius + 0.06f, height - radius);
            foreach (var c in Physics.OverlapCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c.transform.IsChildOf(player.transform)) continue;
                return false; // tem parede, prateleira, carro...
            }
            spot = ground.point + Vector3.up * 0.1f;
            return true;
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) return;

            if (net.Mode == CoopMode.Client)
            {
                if (!GameRefs.InGame) return;
                HideLocalUncle();
                SilenceHostUncle(); // o tio-NPC escondido também não fala nem mostra o "!" no cliente
                KeepPassengerDoorClickable();
                SpreadFromHostOnSpawn();
                StowSuitcase(); HideSuitcase();
                // Só senta depois que o carro já está seguindo o host (para não sentar no carro errado).
                if (wantSeat && !seated && CarSync.Instance != null && CarSync.Instance.HasLatest) SitAsPassenger();
                else if (!wantSeat && seated) StandUp();
                KeepGameSeatFlag();
                if (seated && (DriveSync.Instance == null || !DriveSync.Instance.ClientDriving))
                {
                    // No passageiro: a saída do motorista (na frente da alavanca do freio) fica desligada.
                    // No volante quem liga/desliga essa saída é o PassengerSeat.ClientDriverExit (porta aberta + freio puxado);
                    // antes este trecho desligava todo quadro e o ícone piscava sem aceitar o clique.
                    var cl = GameRefs.CarLogic;
                    if (cl != null && cl.doorExit != null && cl.doorExit.activeSelf) cl.doorExit.SetActive(false);
                }
            }
            else if (net.Mode == CoopMode.Host)
            {
                SilenceHostUncle();
                if (GameRefs.InGame) StowSuitcase(); HideSuitcase();
                if (hasRemote && remote.Seated) { following = false; BoardUncleOnHost(); }
                else if (hasRemote && !remote.Seated) FollowOnFoot();
            }
        }

        // ---------- host: tio-NPC calado (ele é só o corpo do jogador-tio) ----------

        private UncleLogicC silenced;
        private bool savedRestrictTalk, savedTutorialBool, savedDebugStop;

        /// <summary>
        /// HOST: com o jogador-tio conectado, o tio-NPC não fala nem mostra o "!" (pedido do usuário).
        /// Os valores originais voltam quando o tio desconecta.
        /// </summary>
        private void SilenceHostUncle()
        {
            var uncle = GameRefs.Uncle;
            var logic = uncle != null ? uncle.GetComponent<UncleLogicC>() : null;
            if (logic == null) return;
            if (silenced != logic)
            {
                RestoreHostUncle();
                silenced = logic;
                savedRestrictTalk = logic.restrictTalk;
                savedTutorialBool = logic.tutorialBool;
                savedDebugStop = logic.debugStopUncleWorking;
                logic.ClearDialogueInstant();
                var dir = GameObject.FindWithTag("Director");
                if (dir != null && dir.GetComponent<DirectorC>() != null) dir.GetComponent<DirectorC>().BubbleOff();
                Plugin.Log.LogInfo("Tio-NPC do host calado (o jogador-tio é o tio).");
            }
            logic.restrictTalk = true;
            logic.tutorialBool = false;
            // Interruptor de desenvolvedor do próprio jogo: desliga o "cérebro" do tio (apresentação em casa
            // com "Take Uncle?", o "!", falas aleatórias). Reaplicado todo frame (o motel volta para false).
            logic.debugStopUncleWorking = true;
            if (logic.spriteTarget != null && logic.spriteTarget.activeSelf) logic.spriteTarget.SetActive(false);
        }

        private void RestoreHostUncle()
        {
            if (silenced == null) return;
            silenced.restrictTalk = savedRestrictTalk;
            silenced.tutorialBool = savedTutorialBool;
            silenced.debugStopUncleWorking = savedDebugStop;
            silenced = null;
        }

        /// <summary>
        /// HOST: o jogador-tio sentou no passageiro, então o tio-NPC do host também tem que estar sentado
        /// (é o "corpo" do jogador-tio na tela do host). Usa a rotina do próprio jogo: UncleEnterCar().
        /// </summary>
        private void BoardUncleOnHost()
        {
            if (Time.realtimeSinceStartup - lastBoardTry < 5f) return;
            var uncle = GameRefs.Uncle;
            if (uncle == null) return;
            var logic = uncle.GetComponent<UncleLogicC>();
            if (logic == null || logic.isSat || logic.uncleGoneForever || logic.uncleCarTransform == null) return;
            lastBoardTry = Time.realtimeSinceStartup + 10f; // dá tempo da rotina terminar antes de tentar de novo
            Plugin.Log.LogInfo("Jogador-tio sentou: colocando o tio-NPC no banco do passageiro.");
            StartCoroutine(BoardRoutine(logic));
        }

        /// <summary>
        /// Mesma ordem do jogo (UncleLogicC.Update): primeiro abre a porta do tio
        /// (a porta avisa o tio -> animação "doorOpened"), depois UncleEnterCar (animação "enterCar").
        /// Chamar UncleEnterCar com a porta fechada deixa o tio em pé do lado de fora.
        /// </summary>
        private System.Collections.IEnumerator BoardRoutine(UncleLogicC logic)
        {
            // Sem animação de entrar (pedido do usuário: o host entra na hora, o tio também):
            // só o sinal de "porta aberta" para o Animator, a rotina do jogo e o Animator adiantado até o fim.
            var anim = logic.GetComponent<Animator>();
            if (anim != null) anim.SetFloat("walkSpeed", 0f);
            logic.CarDoorOpen();
            logic.StartCoroutine(logic.UncleEnterCar());
            yield return null;
            FastForward(anim);
            Plugin.Log.LogInfo("Tio-NPC sentou, animação: " + ClipNames(anim));
            yield return new WaitForSeconds(5f);
            if (logic != null) Plugin.Log.LogInfo("Tio-NPC no carro: isSat=" + logic.isSat + " pai=" + (logic.transform.parent != null ? logic.transform.parent.name : "-"));
        }

        // ---------- host: tio-NPC a pé seguindo o jogador-tio (MVP 3) ----------

        private bool following;      // o tio-NPC está seguindo a posição do jogador-tio
        private bool exiting;        // rotina de sair do carro rodando
        private float feetOffset = float.NaN; // altura dos pés em relação à raiz do tio
        private Vector3 smoothLookWorld;
        private float followStart;

        /// <summary>HOST, a pé: a cabeça do tio-NPC segue o olhar do jogador-tio (o CursorHit do jogo fica bloqueado).</summary>
        private void LookOnFoot()
        {
            var uncle = GameRefs.Uncle;
            if (uncle == null) return;
            var hlc = uncle.GetComponent<HeadLookController>();
            if (hlc == null) return;
            OverridingUncleLook = true;
            if (uncleHead == null || !uncleHead.IsChildOf(uncle.transform)) uncleHead = GameRefs.FindDeep(uncle.transform, "Head");
            var from = uncleHead != null ? uncleHead.position : uncle.transform.position + Vector3.up * 3f;
            var want = remote.LookDir.sqrMagnitude > 0.001f ? remote.LookDir.normalized : uncle.transform.forward;
            if (smoothLookWorld.sqrMagnitude < 0.5f) smoothLookWorld = want;
            smoothLookWorld = Vector3.Slerp(smoothLookWorld, want, 1f - Mathf.Exp(-12f * Time.deltaTime));
            hlc.headLookOff = false;
            hlc.target = from + smoothLookWorld * 50f;
        }

        private bool lyingShown;

        /// <summary>
        /// HOST: mostra o jogador-tio deitado. No quarto do motel o jogo tem o lugar da cama do tio (UncleBedTriggerC):
        /// o tio vai para lá com a animação "bedTime", como no jogo. Sem esse lugar, deita na cama que ele clicou.
        /// </summary>
        private void LieUncle(GameObject uncle, BedLogicC bed, bool second)
        {
            if (lyingShown) return;
            lyingShown = true;
            following = false;
            var ai = uncle.GetComponent("AIPath");
            if (ai != null) { var tr = HarmonyLib.Traverse.Create(ai); tr.Field("target").SetValue(null); tr.Field("canMove").SetValue(false); }
            var cc = uncle.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            UncleBedTriggerC spot = null; float bd = 64f;
            foreach (var t in FindObjectsOfType<UncleBedTriggerC>())
            {
                float d = (t.transform.position - bed.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; spot = t; }
            }
            var twin = MotelSync.TwinOf(bed);
            if (spot != null && !second && twin != null)
            {
                // deitou na cama principal: o lugar da cama do tio, levado para a cama principal
                uncle.transform.position = bed.transform.TransformPoint(twin.InverseTransformPoint(spot.transform.position));
                uncle.transform.rotation = bed.transform.rotation * Quaternion.Inverse(twin.rotation) * spot.transform.rotation;
            }
            else if (spot != null)
            {
                uncle.transform.position = spot.transform.position;
                uncle.transform.rotation = spot.transform.rotation;
            }
            else
            {
                uncle.transform.position = bed.transform.position;
                uncle.transform.rotation = bed.transform.rotation;
            }
            var anim = uncle.GetComponent<Animator>();
            if (anim != null) anim.SetBool("bedTime", true);
            Plugin.Log.LogInfo("Tio-NPC deitado (jogador-tio deitou na " + (second ? "segunda cama" : "cama principal") + ").");
        }

        /// <summary>HOST: o jogador-tio está a pé. Se o tio-NPC está sentado, sai do carro; depois segue.</summary>
        private void FollowOnFoot()
        {
            var uncle = GameRefs.Uncle;
            if (uncle == null) return;
            var logic = uncle.GetComponent<UncleLogicC>();
            if (logic == null || logic.uncleGoneForever) return;
            if (logic.isSat)
            {
                if (!exiting) StartCoroutine(ExitRoutine(logic));
                return;
            }
            if (exiting) return;

            // Jogador-tio deitado na cama do motel: o tio-NPC aparece deitado (cama do tio, animação do jogo).
            var ms = MotelSync.Instance;
            if (ms != null && ms.RemoteBed != null)
            {
                LieUncle(uncle, ms.RemoteBed, ms.RemoteSecond);
                return;
            }
            if (lyingShown)
            {
                lyingShown = false;
                var a = uncle.GetComponent<Animator>();
                if (a != null) a.SetBool("bedTime", false);
                following = false; // refaz o "seguir" (posição volta para o jogador-tio)
                Plugin.Log.LogInfo("Tio-NPC levantou da cama (jogador-tio levantou).");
            }
            if (!following)
            {
                following = true;
                var ai = logic.GetComponent("AIPath"); // classe de outro assembly: mexe por nome
                if (ai != null)
                {
                    var tr = HarmonyLib.Traverse.Create(ai);
                    tr.Field("target").SetValue(null);
                    tr.Field("canMove").SetValue(false);
                }
                var cc = logic.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false; // o mod move o tio direto
                feetOffset = float.NaN;
                followStart = Time.time;
                Plugin.Log.LogInfo("Tio-NPC seguindo o jogador-tio a pé.");
            }

            // Posição: chão embaixo do corpo do jogador-tio (raio no mundo do host).
            float ground;
            if (!GroundBelow(remote.BodyPos, uncle.transform, out ground)) ground = remote.BodyPos.y - 1.8f;
            // A altura dos pés é medida no primeiro segundo (transição para o "em pé").
            if (float.IsNaN(feetOffset) || Time.time - followStart < 1f)
            {
                float f = FeetOffset(uncle);
                if (!float.IsNaN(f)) feetOffset = f;
            }
            var target = new Vector3(remote.BodyPos.x, ground - (float.IsNaN(feetOffset) ? 0f : feetOffset), remote.BodyPos.z);
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);   // chega 20x/s: suaviza
            var pos = uncle.transform.position;
            if ((target - pos).sqrMagnitude > 25f) pos = target; // longe demais: teleporta
            else pos = Vector3.Lerp(pos, target, k);
            uncle.transform.position = pos;

            // Corpo virado para onde o jogador-tio olha (só no plano).
            var flat = remote.LookDir; flat.y = 0f;
            if (flat.sqrMagnitude > 0.001f)
            {
                var rot = Quaternion.LookRotation(flat.normalized, Vector3.up);
                uncle.transform.rotation = Quaternion.Slerp(uncle.transform.rotation, rot, k);
            }
        }

        /// <summary>
        /// Sai do carro NA HORA (pedido do usuário: esperar a animação era chato). Mesmos sinais do jogo
        /// (UncleLogicC.HeadTowardsBonnet), com o Animator adiantado até o fim da animação de sair.
        /// </summary>
        private System.Collections.IEnumerator ExitRoutine(UncleLogicC logic)
        {
            exiting = true;
            following = false;
            Plugin.Log.LogInfo("Jogador-tio levantou: tio-NPC sai do carro.");
            var anim = logic.GetComponent<Animator>();
            logic.transform.parent = null;
            // A transição "sair" do Animator precisa da porta aberta (no jogo a porta avisa o tio: doorOpened).
            if (anim != null) { anim.SetBool("enterCar", false); anim.SetBool("doorOpened", true); anim.SetBool("exitCar", true); }
            logic.isSat = false;
            yield return null;
            FastForward(anim);
            if (anim != null) { anim.SetBool("exitCar", false); anim.SetBool("doorOpened", false); FastForward(anim); }
            Plugin.Log.LogInfo("Tio-NPC fora do carro, animação: " + ClipNames(anim));
            logic.carDoorOpen = false;
            exiting = false;
            lastBoardTry = -100f;   // pode sentar de novo logo
        }

        private static string ClipNames(Animator anim)
        {
            if (anim == null) return "-";
            var sb = new System.Text.StringBuilder();
            foreach (var ci in anim.GetCurrentAnimatorClipInfo(0)) if (ci.clip != null) sb.Append(ci.clip.name + " ");
            return sb.Length > 0 ? sb.ToString() : "-";
        }

        /// <summary>Adianta o Animator alguns segundos de uma vez: termina transições e animações curtas.</summary>
        private static void FastForward(Animator anim)
        {
            if (anim == null) return;
            for (int i = 0; i < 8; i++) anim.Update(0.75f);
        }

        private static bool GroundBelow(Vector3 from, Transform ignore, out float y)
        {
            y = 0f;
            var hits = Physics.RaycastAll(from + Vector3.up * 0.3f, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.IsChildOf(ignore)) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return best < float.MaxValue;
        }

        private static float FeetOffset(GameObject uncle)
        {
            float min = float.NaN;
            foreach (var sk in uncle.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (sk == null || !sk.enabled) continue;
                float y = sk.bounds.min.y;
                if (float.IsNaN(min) || y < min) min = y;
            }
            return float.IsNaN(min) ? float.NaN : min - uncle.transform.position.y;
        }

        private void LateUpdate()
        {
            OverridingUncleLook = false;
            if (following && hasRemote && !remote.Seated) { LookOnFoot(); return; }
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host || !net.Connected || !hasRemote || !remote.Seated) return;

            var uncle = GameRefs.Uncle;
            var car = GameRefs.Car;
            if (uncle == null || car == null) return;
            var logic = uncle.GetComponent<UncleLogicC>();
            var hlc = uncle.GetComponent<HeadLookController>();
            if (logic == null || hlc == null || !logic.isSat) return;
            OverridingUncleLook = true;

            if (uncleHead == null || !uncleHead.IsChildOf(uncle.transform)) uncleHead = GameRefs.FindDeep(uncle.transform, "Head");
            var from = uncleHead != null ? uncleHead.position : uncle.transform.position + Vector3.up * 1.5f;
            hlc.headLookOff = false;                        // o jogo desliga quando o tio está sentado; aqui religamos
            // O olhar chega 20x/s: suaviza (no espaço do carro) e mira longe, para a cabeça não tremer
            // (alvo perto + cabeça que se mexe = alvo que se mexe = balanço).
            var want = remote.LookDirCar.sqrMagnitude > 0.001f ? remote.LookDirCar.normalized : Vector3.forward;
            if (smoothLookCar.sqrMagnitude < 0.5f) smoothLookCar = want;
            smoothLookCar = Vector3.Slerp(smoothLookCar, want, 1f - Mathf.Exp(-12f * Time.deltaTime));
            hlc.target = from + car.TransformDirection(smoothLookCar) * 50f;
        }

        // ---------- cliente: tio local ----------

        private readonly System.Collections.Generic.List<Renderer> hiddenProps = new System.Collections.Generic.List<Renderer>();
        private float nextPropCheck;

        /// <summary>
        /// CLIENTE: a mala e as cartas ficam escondidas enquanto estão na MÃO do tio-NPC escondido
        /// (senão a mala "flutua" sozinha). Quando o jogo as passa para o carro, aparecem de novo.
        /// </summary>
        private void HideUncleProps()
        {
            if (hiddenUncle == null || Time.time < nextPropCheck) return;
            nextPropCheck = Time.time + 0.5f;
            var root = hiddenUncle.transform;
            foreach (var r in hiddenUncle.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer || !r.enabled) continue;
                r.enabled = false;
                hiddenProps.Add(r);
            }
            for (int i = hiddenProps.Count - 1; i >= 0; i--)
            {
                var r = hiddenProps[i];
                if (r == null) { hiddenProps.RemoveAt(i); continue; }
                if (!r.transform.IsChildOf(root)) { r.enabled = true; hiddenProps.RemoveAt(i); }
            }
        }

        private void ShowUncleProps()
        {
            foreach (var r in hiddenProps) if (r != null) r.enabled = true;
            hiddenProps.Clear();
        }

        private void HideLocalUncle()
        {
            HideUncleProps();
            if (hiddenUncle != null) return;
            var uncle = GameRefs.Uncle;
            if (uncle == null) return;
            foreach (var r in uncle.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer) r.enabled = false;
            }
            var logic = uncle.GetComponent<UncleLogicC>();
            if (logic != null) logic.restrictTalk = true;   // o tio local não fala: o jogador é o tio
            hiddenUncle = uncle;
            Plugin.Log.LogInfo("Tio-NPC local escondido (você é o tio).");
        }

        private void ShowLocalUncle()
        {
            if (hiddenUncle == null) return;
            ShowUncleProps();
            foreach (var r in hiddenUncle.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.enabled = true;
            var logic = hiddenUncle.GetComponent<UncleLogicC>();
            if (logic != null) logic.restrictTalk = false;
            hiddenUncle = null;
        }

        // ---------- cliente: banco do passageiro ----------

        private void SitAsPassenger()
        {
            var car = GameRefs.Car;
            var seatLoc = GameRefs.DriverSeatLoc;
            var player = GameRefs.Player;
            var cam = GameRefs.Cam;
            if (car == null || seatLoc == null || player == null || cam == null) return;

            // Banco do passageiro = espelho do banco do motorista no eixo X do carro.
            var local = SeatLocal(car);
            var localRot = Quaternion.Inverse(car.rotation) * seatLoc.rotation;

            var drag = cam.GetComponent<DragRigidbodyC>();
            if (drag != null) drag.StartCoroutine("DropAllItems");

            SetPlayerPhysics(player, false);
            savedPlayerParent = player.transform.parent;
            player.transform.parent = car;
            player.transform.localPosition = local;
            player.transform.localRotation = localRot;
            cam.transform.localPosition = Vector3.up * 0.8f;   // mesma altura que o jogo usa ao sentar

            seated = true;
            Plugin.Log.LogInfo("Sentado no banco do passageiro. Local " + local);
        }

        private void StandUp()
        {
            if (!seated) return;
            seated = false;
            var player = GameRefs.Player;
            var car = GameRefs.Car;
            if (player == null) return;
            player.transform.parent = savedPlayerParent;
            if (car != null)
            {
                // Sai pelo lado do passageiro (ou do motorista, se saiu do volante pela porta).
                var seatLoc = GameRefs.DriverSeatLoc;
                float side = seatLoc != null && car.InverseTransformPoint(seatLoc.position).x > 0 ? -1f : 1f;
                if (exitDriverSide) side = -side;
                exitDriverSide = false;
                player.transform.position = car.position + car.right * side * 2.2f + Vector3.up * 1.2f;
                player.transform.rotation = Quaternion.Euler(0f, car.eulerAngles.y, 0f);
            }
            SetPlayerPhysics(player, true);
            Plugin.Log.LogInfo("Levantou do banco do passageiro.");
        }

        private static void SetPlayerPhysics(GameObject player, bool on)
        {
            var rc = player.GetComponent<RigidbodyControllerC>();
            if (rc != null) rc.isSat = !on;
            var ml = player.GetComponent<MouseLook>();
            if (ml != null) ml.isSat = !on;
            var camMl = GameRefs.Cam != null ? GameRefs.Cam.GetComponent<MouseLook>() : null;
            if (camMl != null) camMl.isSat = !on;
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = !on;
                rb.detectCollisions = on;
            }
            var cap = player.GetComponent<CapsuleCollider>();
            if (cap != null) cap.enabled = on;
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = on;
            var steps = player.GetComponent<FootstepsC>();
            if (steps != null)
            {
                steps.isPlayerWalking = 0f;
                steps.enabled = on;
            }
        }
    }
}
