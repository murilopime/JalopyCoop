using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Volante compartilhado (MVP 2).
    ///
    /// Regra: o HOST é sempre quem roda a física. Quando o tio "dirige", o jogo do host continua no
    /// modo dirigindo normal (combustível, motor, reclamações do tio etc.), mas as teclas de direção
    /// vêm do tio pela rede (CoopInput). Visualmente os dois trocam de banco:
    ///  - host: câmera vai para o banco do passageiro e o tio-NPC aparece no banco do motorista;
    ///  - tio: câmera vai para o banco do motorista e o modelo do host vai para o passageiro.
    ///
    /// (O antigo F5 de trocar o volante foi tirado em 2026-11-17: a troca é só pelas interações do jogo.)
    /// Para aceitar, o host precisa estar dirigindo (motor ligado e freio de mão solto).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class DriveSync : MonoBehaviour
    {
        public static DriveSync Instance { get; private set; }

        /// <summary>HOST: o tio está no volante.</summary>
        public bool RemoteDriving { get; private set; }
        /// <summary>CLIENTE: eu (tio) estou no volante.</summary>
        public bool ClientDriving { get; private set; }
        /// <summary>
        /// HOST: o host está (ou vai sentar) no banco do PASSAGEIRO. Independe de quem está no volante:
        /// se o tio sai do volante, o host continua no passageiro (o carro fica sem motorista).
        /// Por dentro do jogo o host continua no banco do motorista; a câmera vai para o passageiro e
        /// as teclas de direção do host não mexem no carro.
        /// </summary>
        public bool HostPassenger { get; private set; }

        private void SetHostPassenger(bool on)
        {
            if (HostPassenger == on) return;
            HostPassenger = on;
            if (on) hostWasSat = false;
            CoopInput.RemoteDriving = RemoteDriving || HostPassenger;
            if (!on)
            {
                var cam = GameRefs.Cam;
                if (cam != null && hostWasSat) cam.transform.localPosition = Vector3.up * 0.8f;
                hostWasSat = false;
            }
            Plugin.Log.LogInfo("Host no passageiro: " + on);
        }

        /// <summary>HOST: o host sentou no motorista pelo jogo (porta do motorista): sai do modo passageiro.</summary>
        public void OnHostSatDriverByGame() { SetHostPassenger(false); }
        /// <summary>HOST: saiu do carro pela saída do jogo (lado do motorista): deixa de estar no passageiro.</summary>
        public void OnHostExitByGame() { SetHostPassenger(false); }
        /// <summary>Texto curto para a linha de status.</summary>
        public string Hint { get; private set; }

        private bool pendingRequest;
        private float hintUntil;
        private float lastSwap = -100f;
        private Transform uncleHead;
        private bool hostWasSat;         // host sentado no frame anterior (com o tio no volante)

        /// <summary>HOST: o tio está no volante e o host está fora do carro (pode entrar pela porta do passageiro).</summary>
        public bool HostOutsideWhileUncleDrives { get { return RemoteDriving && !HostInDriverSeat(); } }
        public bool HostSeatedWhileUncleDrives { get { return RemoteDriving && HostInDriverSeat(); } }
        private bool uncleShiftKnown;
        private Vector3 uncleLocalOnDriver;
        private readonly StaticPose driverPose = new StaticPose();
        private bool driverPoseTried;
        private GameObject poseModel;
        private Transform anchor;
        private bool uncleRotSaved;
        private Quaternion uncleLocalRot;
        private Vector3 uncleDriverOffset;
        public BepInEx.Configuration.ConfigEntry<string> UncleDriverOffsetConfig;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            if (UncleDriverOffsetConfig != null)
            {
                var p = UncleDriverOffsetConfig.Value.Split(',');
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                float x, y, z;
                if (p.Length == 3 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out x)
                    && float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out y)
                    && float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out z))
                {
                    uncleDriverOffset = new Vector3(x, y, z);
                }
            }
            net.OnReliable += OnReliable;
            net.OnClientState += s => { if (RemoteDriving) CoopInput.SetRemote(s); };
            net.OnHostState += s =>
            {
                // O host manda a verdade a cada pacote: corrige se uma mensagem se perdeu.
                // (ignora pacotes atrasados logo depois de uma troca, que fariam o banco "piscar")
                if (net.Mode == CoopMode.Client && s.RemoteDriving != ClientDriving && Time.realtimeSinceStartup - lastSwap > 1f)
                {
                    SetClientDriving(s.RemoteDriving);
                }
            };
            net.OnConnectionChanged += connected =>
            {
                if (!connected)
                {
                    SetHostPassenger(false);
                    SetRemoteDriving(false);
                    SetClientDriving(false);
                    pendingRequest = false;
                }
            };
            var previous = net.BuildHostStateExtra;
            net.BuildHostStateExtra = s =>
            {
                if (previous != null) s = previous(s);
                s.RemoteDriving = RemoteDriving;
                s.HostPassenger = HostPassenger;
                return s;
            };
        }

        private void OnReliable(Msg type)
        {
            var net = CoopNet.Instance;
            if (net.Mode == CoopMode.Host)
            {
                if (type == Msg.DriveRequest)
                {
                    pendingRequest = true;
                    Say("O tio quer dirigir", 15f);
                    Plugin.Log.LogInfo("Tio pediu o volante.");
                }
                else if (type == Msg.DriveReturn)
                {
                    // F5: devolve o volante e o host volta para o motorista.
                    Plugin.Log.LogInfo("Tio devolveu o volante.");
                    SetRemoteDriving(false);
                    SetHostPassenger(false);
                    Say("O tio devolveu o volante", 4f);
                }
                else if (type == Msg.LeaveDriver)
                {
                    // Porta do motorista: o tio saiu. O host continua onde está (passageiro ou fora).
                    Plugin.Log.LogInfo("Tio saiu do volante pela porta.");
                    SetRemoteDriving(false);
                    Say("O tio saiu do volante", 4f);
                }
                else if (type == Msg.SitDriver)
                {
                    // Troca de banco de verdade (MVP 3): o tio clicou na porta do motorista.
                    if (!RemoteDriving && HostInDriverSeat() && !HostPassenger)
                    {
                        pendingRequest = true;
                        net.SendReliable(Msg.DriveDeny);
                        Say("O tio quer o volante: saia do carro", 10f);
                        return;
                    }
                    pendingRequest = false;
                    SetRemoteDriving(true);
                    net.SendReliable(Msg.DriveGrant);
                    Say("O tio está no volante: entre pela porta do passageiro", 8f);
                }
            }
            else if (net.Mode == CoopMode.Client)
            {
                if (type == Msg.DriveGrant)
                {
                    SetClientDriving(true);
                    if (UncleSync.Instance != null) UncleSync.Instance.SetWantSeat(true); // entrou pela porta: senta no volante
                    Say("Você está no volante! (a porta do motorista devolve)", 5f);
                }
                else if (type == Msg.DriveReturn) { SetClientDriving(false); Say("O volante voltou para o host", 4f); }
                else if (type == Msg.DriveDeny) Say("O host está no volante: ele precisa sair do carro", 6f);
            }
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            UpdateVirtualDriver();
            if (net == null || !net.Connected) return;
            if (Time.realtimeSinceStartup > hintUntil) Hint = null;
            // F5 (trocar o volante) tirado a pedido do usuário: a troca é pelas interações do jogo (portas/bancos).
        }

        private void LateUpdate()
        {
            CoopInput.EndFrame();
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host || (!RemoteDriving && !HostPassenger)) return;

            var car = GameRefs.Car;
            var cam = GameRefs.Cam;
            var seat = GameRefs.DriverSeatLoc;
            if (car == null || cam == null || seat == null) return;

            // Host no passageiro: por dentro, o jogo do host continua "sentado no motorista" (é quem roda a
            // física); a câmera do host vai para o passageiro enquanto ele está sentado.
            bool hostSat = HostInDriverSeat() && HostPassenger;
            var player = cam.transform.parent;
            if (hostSat && player != null)
            {
                if (!hostWasSat) cam.transform.localPosition = Vector3.up * 0.8f; // mesma altura que o jogo usa ao sentar
                // Olhos no banco do motorista = banco + (câmera - corpo), com a escala do jogador
                // (a escala não é 1: "banco + 0,8" ficava baixo). Não usa a posição atual da câmera (que o mod move).
                var eyeWorld = seat.position + (player.TransformPoint(Vector3.up * 0.8f) - player.position);
                var eye = car.InverseTransformPoint(eyeWorld);
                eye.x = -eye.x;
                cam.transform.position = car.TransformPoint(eye);
            }
            else if (hostWasSat)
            {
                cam.transform.localPosition = Vector3.up * 0.8f; // saiu do carro: câmera volta para o corpo
            }
            hostWasSat = hostSat;
            if (!RemoteDriving) return; // o tio-NPC só vai para o volante quando o tio dirige

            // Tio-NPC no banco do motorista: espelha a CABEÇA dele (a raiz do modelo fica longe do corpo,
            // espelhar a raiz jogava o tio para fora do carro) e move o corpo junto.
            var uncle = GameRefs.Uncle;
            var logic = uncle != null ? uncle.GetComponent<UncleLogicC>() : null;
            if (logic != null && logic.isSat && uncle.transform.parent != null && DriverPose(car, uncle))
            {
                // pose de mãos no volante, copiada do motorista original do jogo
            }
            else if (logic != null && logic.isSat && uncle.transform.parent != null)
            {
                // O deslocamento é calculado UMA vez por troca: recalcular todo frame a partir da cabeça
                // (que mexe com a animação e com o olhar) fazia o corpo inteiro balançar.
                if (!uncleShiftKnown)
                {
                    uncle.transform.localPosition = Vector3.zero; // lugar normal dele (passageiro)
                    if (uncleHead == null || !uncleHead.IsChildOf(uncle.transform)) uncleHead = GameRefs.FindDeep(uncle.transform, "Head");
                    if (uncleHead == null) return;
                    var h = car.InverseTransformPoint(uncleHead.position);
                    var m = h; m.x = -m.x;
                    var parent = uncle.transform.parent;
                    uncleLocalOnDriver = parent.InverseTransformPoint(uncle.transform.position + car.TransformVector(m - h));
                    uncleShiftKnown = true;
                }
                uncle.transform.localPosition = uncleLocalOnDriver;
            }
        }

        /// <summary>
        /// HOST: tio-NPC no volante com a pose do motorista original do jogo (mãos no volante), em vez da
        /// pose de passageiro espelhada. false = não deu (usa o espelho).
        /// </summary>
        private bool DriverPose(Transform car, GameObject uncle)
        {
            // O Laika_Driver do carro não tem animação (fica em T-pose). A pose de mãos no volante vem do
            // Animator do Laika_Driver_02 (o mesmo modelo do host na tela do tio): um clone INVISÍVEL dele
            // fica no banco do motorista e o tio copia os ossos.
            if (anchor == null || !anchor.IsChildOf(car)) anchor = GameRefs.FindDeep(car, "Laika_Driver");
            if (anchor == null) return false;
            if (poseModel == null)
            {
                if (driverPoseTried) return false;
                driverPoseTried = true;
                poseModel = CreatePoseModel();
                if (poseModel == null) return false;
                driverPose.Build(poseModel.transform, uncle.transform);
            }
            if (!driverPose.Ready) return false;

            // Sem o AjusteMotorista: ele é do boneco do host na tela do tio. Com o valor padrão (-0.08,0.16,0.10)
            // o tio-NPC ficava alto e para frente (teste online); nos testes locais o host tinha 0,0,0.
            // Ajuste fino do tio é o AjusteTioMotorista (uncleDriverOffset).
            var hp = GetComponent<HostPuppet>();
            var off = Vector3.zero;
            var yaw = hp != null ? hp.DriverSeatYaw : 0f;
            poseModel.transform.rotation = anchor.rotation * Quaternion.Euler(0f, yaw, 0f);
            poseModel.transform.position = anchor.position + car.TransformVector(off);

            if (!uncleRotSaved) { uncleLocalRot = uncle.transform.localRotation; uncleRotSaved = true; }
            driverPose.Apply(uncleDriverOffset);
            return true;
        }

        private GameObject CreatePoseModel()
        {
            var src = GameRefs.FindSceneAnimator("Laika_Driver_02");
            if (src == null) { Plugin.Log.LogWarning("Laika_Driver_02 não achado: tio no volante fica com a pose espelhada."); return null; }
            var go = (GameObject)Instantiate(src.gameObject);
            go.name = "JalopyCoop_DriverPose";
            go.transform.parent = null;
            go.transform.localScale = src.transform.lossyScale;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            go.SetActive(true);
            var a = go.GetComponent<Animator>();
            if (a != null) { a.cullingMode = AnimatorCullingMode.AlwaysAnimate; a.applyRootMotion = false; }
            return go;
        }

        // ================= troca de banco de verdade (MVP 3, passo 5) =================

        /// <summary>HOST: libera o SeatLogicC.Trigger do mod (o host entrando pelo passageiro).</summary>
        public static bool AllowHostSeat;
        private float lastDoorUse = -10f;

        public void SayHint(string text, float seconds) { Say(text, seconds); }

        /// <summary>HOST está sentado (no banco do motorista do jogo, que pode ser o "passageiro" do co-op).</summary>
        public bool HostSeatedNow { get { return HostInDriverSeat(); } }

        /// <summary>HOST: clicou no banco do passageiro (PassengerSeat).</summary>
        public void HostEnterPassenger()
        {
            var cl = GameRefs.CarLogic;
            if (cl != null && !HostInDriverSeat()) HostEnterPassenger(cl);
        }

        /// <summary>HOST: clicou na saída do passageiro (PassengerSeat).</summary>
        public void HostExitPassenger()
        {
            var cl = GameRefs.CarLogic;
            if (cl == null || !HostPassenger || !HostInDriverSeat()) return;
            if (cl.isDriving) { Say("Puxe o freio de mão antes de sair", 4f); return; }
            StartCoroutine(HostExitPassengerSide(cl));
        }

        private void HostEnterPassenger(CarLogicC cl)
        {
            var us = UncleSync.Instance;
            if (us != null && us.HasRemote && us.RemoteSeated && !RemoteDriving)
            {
                Say("O tio está no banco do passageiro", 4f);
                return;
            }
            var seat = cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
            if (seat == null) return;
            SetHostPassenger(true);
            AllowHostSeat = true;
            try { seat.Trigger(); } finally { AllowHostSeat = false; }
            Plugin.Log.LogInfo("Host sentou no passageiro.");
        }

        /// <summary>
        /// CLIENTE: clicou na porta do motorista. De pé: pede o volante (o host precisa estar fora do carro,
        /// ou aceitar com F5). No volante: sai pelo lado do motorista e devolve o volante.
        /// </summary>
        public void ClientDriverDoor()
        {
            var us = UncleSync.Instance;
            if (us == null) return;
            if (ClientDriving && us.IsSeated) ClientLeaveDriver(); else if (!us.IsSeated) ClientRequestDriver();
        }

        /// <summary>CLIENTE: clicou no banco do motorista (de fora): pede para sentar no volante.</summary>
        public void ClientRequestDriver()
        {
            if (Time.realtimeSinceStartup - lastDoorUse < 0.7f) return;
            lastDoorUse = Time.realtimeSinceStartup;
            var net = CoopNet.Instance;
            var us = UncleSync.Instance;
            if (net == null || us == null || us.IsSeated) return;
            net.SendReliable(Msg.SitDriver);
            Say("Entrando no volante...", 3f);
            Plugin.Log.LogInfo("Banco do motorista: pediu o volante.");
        }

        /// <summary>CLIENTE: clicou na saída do motorista (no volante): sai pelo lado do motorista.</summary>
        public void ClientLeaveDriver()
        {
            if (Time.realtimeSinceStartup - lastDoorUse < 0.7f) return;
            lastDoorUse = Time.realtimeSinceStartup;
            var net = CoopNet.Instance;
            var us = UncleSync.Instance;
            if (net == null || us == null) return;
            if (ClientDriving && us.IsSeated)
            {
                var cs = CarSync.Instance;
                if (cs != null && cs.HasLatest && cs.Latest.HandbrakePos == 1)
                {
                    Say("Puxe o freio de mão antes de sair", 4f);
                    return;
                }
                us.LeaveOnDriverSide();
                net.SendReliable(Msg.LeaveDriver); // o host continua onde está (passageiro ou fora)
                SetClientDriving(false);
                Say("Você saiu do volante", 3f);
                Plugin.Log.LogInfo("Porta do motorista: saiu do volante.");
            }
            else if (!us.IsSeated)
            {
                net.SendReliable(Msg.SitDriver);
                Say("Entrando no volante...", 3f);
                Plugin.Log.LogInfo("Porta do motorista: pediu o volante.");
            }
            // sentado no passageiro: a porta do motorista não faz nada
        }

        private System.Collections.IEnumerator HostExitPassengerSide(CarLogicC cl)
        {
            if (cl.doorExit == null) yield break;
            if (!cl.doorExit.activeSelf) { cl.doorExit.SetActive(true); yield return null; yield return null; }
            var exit = cl.doorExit.GetComponent<DoorExitLogicC>();
            if (exit == null) yield break;
            var car = GameRefs.Car;
            var player = GameRefs.Player;
            var seatLoc = GameRefs.DriverSeatLoc;

            // A saída do jogo leva o corpo de onde ele está até o ponto de saída (a posição do objeto de saída
            // NA HORA do clique). Para sair pelo passageiro na mesma animação:
            //  1) o corpo começa no banco do PASSAGEIRO (espelho do banco do motorista);
            //  2) o ponto de saída vai, só durante o clique, para o lado do PASSAGEIRO.
            var exitT = cl.doorExit.transform;
            var savedExitPos = exitT.position;
            if (car != null)
            {
                var e = car.InverseTransformPoint(savedExitPos); e.x = -e.x;
                exitT.position = car.TransformPoint(e);
                if (player != null && seatLoc != null)
                {
                    var s = car.InverseTransformPoint(seatLoc.position); s.x = -s.x;
                    player.transform.position = car.TransformPoint(s);
                }
            }
            SetHostPassenger(false); // câmera volta para o corpo (que agora está no banco do passageiro)
            try { exit.Trigger(); }
            finally { exitT.position = savedExitPos; }
            Plugin.Log.LogInfo("Host saiu pela porta do passageiro.");
        }

        /// <summary>HOST: o jogador host está sentado no banco do motorista (SeatLogicC do jogo).</summary>
        private static bool HostInDriverSeat()
        {
            // O CORPO do host (o banco pode estar "ocupado" pelo motorista virtual, ver VirtualDriver).
            var player = GameRefs.Player;
            var rc = player != null ? player.GetComponent<RigidbodyControllerC>() : null;
            return rc != null && rc.isSat;
        }

        // ================= motorista virtual =================
        // Com o tio no volante e o host FORA do carro, o jogo do host precisa achar que tem alguém no banco
        // do motorista (freio de mão e chave só funcionam assim). O mod marca o banco como ocupado e, enquanto
        // o host está fora, impede o jogo de prender o corpo dele no banco quando o carro anda.

        /// <summary>HOST: o banco do motorista está ocupado só "de mentira" (o tio dirige, o host está fora).</summary>
        public static bool VirtualDriver { get; private set; }

        private void UpdateVirtualDriver()
        {
            var net = CoopNet.Instance;
            bool want = net != null && net.Mode == CoopMode.Host && net.Connected && RemoteDriving && !HostInDriverSeat();
            var cl = GameRefs.CarLogic;
            var seat = cl != null && cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
            if (want)
            {
                if (seat != null && !seat.isSat) seat.isSat = true;
                if (!VirtualDriver) Plugin.Log.LogInfo("Motorista virtual ligado (tio no volante, host fora).");
                VirtualDriver = true;
            }
            else if (VirtualDriver)
            {
                VirtualDriver = false;
                // Volta ao normal só se o host não sentou de verdade.
                if (seat != null && !HostInDriverSeat())
                {
                    if (cl.isDriving) cl.DrivingStop();   // carro sem ninguém: freio de mão
                    seat.isSat = false;
                    var player = GameRefs.Player;
                    var rc = player != null ? player.GetComponent<RigidbodyControllerC>() : null;
                    if (rc != null) rc.driving = false;
                }
                Plugin.Log.LogInfo("Motorista virtual desligado.");
            }
        }

        private void SetRemoteDriving(bool on)
        {
            if (RemoteDriving == on) return;
            var cam = GameRefs.Cam;
            if (on) hostWasSat = false; // no 1º frame sentado a câmera é posta na altura padrão
            RemoteDriving = on;
            uncleShiftKnown = false;
            CoopInput.RemoteDriving = on || HostPassenger; // host no passageiro: as teclas dele não dirigem
            CoopInput.ClearRemote();
            if (!on)
            {
                // Devolve a câmera (se o host não ficou no passageiro) e o tio aos lugares normais.
                if (!HostPassenger)
                {
                    if (cam != null && hostWasSat) cam.transform.localPosition = Vector3.up * 0.8f;
                    hostWasSat = false;
                }
                var uncle = GameRefs.Uncle;
                var logic = uncle != null ? uncle.GetComponent<UncleLogicC>() : null;
                if (logic != null && logic.isSat)
                {
                    uncle.transform.localPosition = Vector3.zero;
                    if (uncleRotSaved) uncle.transform.localRotation = uncleLocalRot;
                }
                uncleRotSaved = false;
            }
            Plugin.Log.LogInfo("Volante com o tio: " + on);
        }

        private void SetClientDriving(bool on)
        {
            if (ClientDriving == on) return;
            ClientDriving = on;
            lastSwap = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo("Eu (tio) estou dirigindo: " + on);
            if (UncleSync.Instance != null) UncleSync.Instance.RefreshSeat();
        }

        private void Say(string text, float seconds)
        {
            if (text != Hint) Plugin.Log.LogInfo("Aviso: " + text);
            Hint = text;
            hintUntil = Time.realtimeSinceStartup + seconds;
        }
    }
}
