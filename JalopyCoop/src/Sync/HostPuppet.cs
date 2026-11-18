using System.Text;
using BepInEx.Configuration;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// CLIENTE: mostra o jogador host com o modelo do "homem de óculos" (Laika_Driver_02),
    /// escolhido pelo usuário. O modelo é um clone do NPC escondido na garagem.
    ///
    ///  - Sentado: a pose padrão do modelo já é "mãos no volante". Ele é ancorado no mesmo lugar
    ///    em que o jogo põe o motorista original (Laika_Driver, filho do carro) + ajuste fino.
    ///  - A pé: pés no chão (raio para baixo a partir dos olhos do host) e animação de pé.
    ///    (Animação de andar ainda não existe para este modelo: ver NOTES.md.)
    ///  - A cabeça gira para onde o host está olhando.
    ///
    /// Ajuste fino da posição sentada: BepInEx/config/jalopycoop.cfg.
    /// </summary>
    public class HostPuppet : MonoBehaviour
    {
        private const string ModelName = "Laika_Driver_02";
        private const string AnchorModelName = "Laika_Driver";   // motorista original, já posicionado no carro
        private const string BlackPlaneChild = "Plane_2478";     // plano preto que vem preso ao modelo
        private const string StandingState = "Standing_Idle_02";
        private const float MaxHeadTurn = 75f;

        public ConfigEntry<string> SeatOffsetConfig;
        public ConfigEntry<float> SeatYawConfig;
        private float seatYaw;

        private GameObject puppet;
        private Animator anim;
        private Transform head;
        private SkinnedMeshRenderer[] skins;
        private int seatedStateHash;
        private bool stateKnown;
        private bool showingSeated = true;
        private bool triedCreate;

        private bool anchorKnown;
        private Vector3 anchorLocalPos;     // raiz do motorista original no espaço do carro
        private Quaternion anchorLocalRot;
        private Vector3 seatOffset;         // ajuste fino (espaço do carro)
        public Vector3 DriverSeatOffset { get { return seatOffset; } }
        public float DriverSeatYaw { get { return seatYaw; } }

        // ---- passageiro (quando o tio dirige) ----
        private const string PassengerClip = "Uncle_car_Stationary_idle";
        private const string UncleSeatName = "UncleCarTransform";  // onde o tio fica sentado no carro
        public ConfigEntry<string> PassengerOffsetConfig;
        public ConfigEntry<float> PassengerYawConfig;
        private Vector3 passengerOffset;
        private float passengerYaw;
        private readonly PoseDonor donor = new PoseDonor();
        private bool donorTried;
        private Transform passengerSeat;
        private bool wasPassenger;
        private bool mirrorKnown;
        private Vector3 mirrorShiftLocal;
        private Vector3 smoothLookCar;

        private void Start()
        {
            CoopNet.Instance.OnConnectionChanged += connected => { if (!connected) DestroyPuppet(); };
            seatOffset = ParseVector(SeatOffsetConfig != null ? SeatOffsetConfig.Value : "0,0,0");
            seatYaw = SeatYawConfig != null ? SeatYawConfig.Value : 0f;
            passengerOffset = ParseVector(PassengerOffsetConfig != null ? PassengerOffsetConfig.Value : "0,0,0");
            passengerYaw = PassengerYawConfig != null ? PassengerYawConfig.Value : 0f;
        }

        /// <summary>
        /// Host no banco do passageiro: usa a pose de passageiro do tio (o modelo do host só tem a pose
        /// de dirigir, com os braços no volante) e o lugar onde o tio senta. false = não deu, usa o plano B.
        /// </summary>
        private bool PassengerPose(Transform car)
        {
            if (!donorTried)
            {
                donorTried = true;
                donor.Build(puppet.transform);
                if (!donor.HasClip(PassengerClip)) Plugin.Log.LogWarning("Clip " + PassengerClip + " não encontrado no tio.");
            }
            if (passengerSeat == null || !passengerSeat.IsChildOf(car)) passengerSeat = GameRefs.FindDeep(car, UncleSeatName);
            if (passengerSeat == null || !donor.Ready) return false;
            puppet.transform.rotation = passengerSeat.rotation * Quaternion.Euler(0f, passengerYaw, 0f);
            if (!donor.Apply(PassengerClip, Time.time)) return false;
            donor.PlaceLikeDonor(puppet.transform, passengerSeat, passengerOffset);
            return true;
        }

        // ---- a pé (MVP 3): animações do tio emprestadas ----
        private const string IdleClip = "Uncle_Standing_Idle";
        private const string WalkClip = "Uncle_Walking";
        private const float WalkRefSpeed = 3f;   // m/s em que o clip de andar fica na velocidade normal (ajustável)
        private bool standingLast;
        private Vector3 smoothPos;
        private bool smoothPosKnown;
        private Vector3 lastEye;
        private float speed;
        private float walkPhase;
        private float footFix = float.NaN;

        /// <summary>
        /// Host de pé/andando: o corpo segue os olhos do host (suavizado, chega 20x/s), vira para onde ele olha,
        /// toca o "parado" ou o "andando" do tio conforme a velocidade e põe os pés no chão.
        /// </summary>
        private void Standing(HostStateData s)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            // Usa o CORPO do host (a câmera fica fora do centro: girar o mouse fazia o corpo andar em círculo).
            var eye = s.HostBody.sqrMagnitude > 0.001f ? new Vector3(s.HostBody.x, s.HostEye.y, s.HostBody.z) : s.HostEye;
            if (!smoothPosKnown || !standingLast || (eye - smoothPos).sqrMagnitude > 25f)
            {
                smoothPos = eye; lastEye = eye; speed = 0f; smoothPosKnown = true; footFix = float.NaN;
            }
            float k = 1f - Mathf.Exp(-12f * dt);
            var prev = smoothPos;
            smoothPos = Vector3.Lerp(smoothPos, eye, k);
            var d = smoothPos - prev; d.y = 0f;
            speed = Mathf.Lerp(speed, d.magnitude / dt, 1f - Mathf.Exp(-6f * dt));

            var flat = s.HostLook; flat.y = 0f;
            if (flat.sqrMagnitude > 0.001f)
            {
                var want = Quaternion.LookRotation(flat.normalized, Vector3.up);
                puppet.transform.rotation = Quaternion.Slerp(puppet.transform.rotation, want, k);
            }

            // Pose do tio (se deu certo casar os ossos).
            if (!donorTried) { donorTried = true; donor.Build(puppet.transform); }
            bool posed = false;
            if (donor.Ready)
            {
                if (speed > 0.4f)
                {
                    walkPhase += dt * Mathf.Clamp(speed / WalkRefSpeed, 0.5f, 2f);
                    posed = donor.Apply(WalkClip, walkPhase);
                }
                else posed = donor.Apply(IdleClip, Time.time);
            }

            // Corpo embaixo dos olhos: o quadril (osso do topo) fica na vertical dos olhos.
            float groundY = smoothPos.y - 3.6f;
            float g;
            if (GroundBelow(smoothPos, out g)) groundY = g;
            puppet.transform.position = new Vector3(smoothPos.x, groundY, smoothPos.z);
            if (posed) donor.CenterTopOver(puppet.transform, smoothPos);
            // Pés no chão: pelo osso mais baixo (ossos já estão na pose deste frame; a caixa do mesh pode
            // estar 1 frame atrasada e a correção acumulava, o host sumia). Suavizado: os pés sobem e descem andando.
            float feet = LowestBone();
            if (!float.IsNaN(feet))
            {
                float fix = groundY - feet + SoleBelowBone;
                footFix = float.IsNaN(footFix) ? fix : Mathf.Lerp(footFix, fix, 1f - Mathf.Exp(-8f * dt));
                footFix = Mathf.Clamp(footFix, -6f, 6f);
            }
            if (!float.IsNaN(footFix)) puppet.transform.position += Vector3.up * footFix;

            if (Time.time > nextStandDiag)
            {
                nextStandDiag = Time.time + 5f;
                LogDiagnostics("a pé, vel " + speed.ToString("0.0") + ", chão " + groundY.ToString("0.00") + ", correção " + footFix.ToString("0.00"));
            }
        }
        private float nextStandDiag;

        private const float SoleBelowBone = 0.08f; // sola do sapato abaixo do osso mais baixo (aprox.)
        private Transform[] bones;

        private float LowestBone()
        {
            if (bones == null || bones.Length == 0)
            {
                bones = donor.TargetBones(); // só os ossos do esqueleto (casados com o tio)
                if (bones.Length == 0) return float.NaN;
            }
            float min = float.NaN;
            foreach (var b in bones)
            {
                if (b == null) continue;
                float y = b.position.y;
                if (float.IsNaN(min) || y < min) min = y;
            }
            return min;
        }

        private static bool GroundBelow(Vector3 from, out float y)
        {
            y = 0f;
            var player = GameRefs.Player;
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(from, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null) continue;
                if (player != null && h.collider.transform.IsChildOf(player.transform)) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return best < float.MaxValue;
        }

        private static bool PassengerNow
        {
            get { var c = CarSync.Instance; return c != null && c.HasLatest && c.Latest.HostSat && (c.Latest.RemoteDriving || c.Latest.HostPassenger); }
        }

        private void LateUpdate()
        {
            var net = CoopNet.Instance;
            var sync = CarSync.Instance;
            if (net == null || net.Mode != CoopMode.Client || !net.Connected || sync == null || !sync.HasLatest || !GameRefs.InGame)
            {
                return;
            }
            if (puppet == null)
            {
                if (triedCreate && Time.frameCount % 300 != 0) return; // tenta de novo de vez em quando
                triedCreate = true;
                Create();
                if (puppet == null) return;
            }

            var s = sync.Latest;
            var car = GameRefs.Car;
            if (!anchorKnown && car != null) FindAnchor(car);

            // Pose: sentado (pose padrão do modelo) ou de pé.
            if (stateKnown && s.HostSat != showingSeated)
            {
                showingSeated = s.HostSat;
                if (showingSeated) anim.Play(seatedStateHash, 0, 0f);
                else anim.Play(StandingState, 0, 0f);
                LogDiagnostics(showingSeated ? "host sentou" : "host levantou");
            }

            // Host deitado na cama do motel: o boneco deita na cama (cabeça no travesseiro, onde fica a câmera dele).
            var ms = MotelSync.Instance;
            if (ms != null && ms.RemoteBed != null && !s.HostSat)
            {
                LieOnBed(ms.RemoteBed, ms.RemoteSecond);
                return;
            }
            if (lying) { lying = false; anim.Play(StandingState, 0, 0f); LogDiagnostics("host levantou da cama"); }

            bool passenger = s.HostSat && (s.RemoteDriving || s.HostPassenger) && car != null;
            if (!passenger && wasPassenger) { donor.RestoreRest(); mirrorKnown = false; }
            if (s.HostSat && standingLast) donor.RestoreRest(); // voltou a sentar: tira a pose em pé do tio
            wasPassenger = passenger;

            if (passenger && PassengerPose(car))
            {
                // pose e lugar do tio no passageiro (ver PassengerPose)
            }
            else if (s.HostSat && car != null && anchorKnown)
            {
                // Testado (print do usuário): ancorar no motorista original do jogo + ajuste fino fica certo no volante.
                puppet.transform.rotation = car.rotation * anchorLocalRot * Quaternion.Euler(0f, seatYaw, 0f);
                puppet.transform.position = car.TransformPoint(anchorLocalPos + seatOffset);
                if (passenger && head != null)
                {
                    // Plano B (sem a pose do tio): espelha a CABEÇA para o passageiro. O deslocamento é
                    // calculado UMA vez; recalcular todo frame fazia o corpo balançar junto com a animação.
                    if (!mirrorKnown)
                    {
                        var h = car.InverseTransformPoint(head.position);
                        var m = h; m.x = -m.x;
                        mirrorShiftLocal = m - h;
                        mirrorKnown = true;
                    }
                    puppet.transform.position = car.TransformPoint(anchorLocalPos + seatOffset + mirrorShiftLocal);
                }
            }
            else
            {
                Standing(s);
            }
            standingLast = !s.HostSat;

            // Cabeça segue o olhar (depois do Animator, que roda antes do LateUpdate).
            // O olhar chega 20x/s em coordenadas do mundo, mas o carro é mostrado 100 ms atrasado: passa para
            // o espaço do carro (com a rotação do MESMO pacote) e suaviza, senão a cabeça treme nas curvas.
            if (head != null && s.HostLook.sqrMagnitude > 0.001f)
            {
                Vector3 look = s.HostLook.normalized;
                if (s.HostSat && car != null)
                {
                    var lookCar = Quaternion.Inverse(s.CarRot) * look;
                    if (smoothLookCar.sqrMagnitude < 0.5f) smoothLookCar = lookCar;
                    smoothLookCar = Vector3.Slerp(smoothLookCar, lookCar, 1f - Mathf.Exp(-12f * Time.deltaTime));
                    look = car.rotation * smoothLookCar;
                }
                else smoothLookCar = Vector3.zero;
                var forward = passenger ? car.forward : puppet.transform.forward;
                var desired = Quaternion.FromToRotation(forward, look);
                desired = Quaternion.RotateTowards(Quaternion.identity, desired, MaxHeadTurn);
                head.rotation = desired * head.rotation;
            }
        }

        private bool lying;

        /// <summary>
        /// Deitado: pose em pé girada para ficar de costas na cama. A cabeça vai para o ponto onde o jogo põe a câmera
        /// de quem deita (BedLogicC.path[1]) e os pés apontam para o meio da cama.
        /// </summary>
        private void LieOnBed(BedLogicC bed, bool second)
        {
            if (!lying) { lying = true; anim.Play(StandingState, 0, 0f); LogDiagnostics("host deitou"); }
            if (bed.path == null || bed.path.Length < 2 || bed.path[1] == null) return;
            var headSpot = MotelSync.HeadSpot(bed, second) + Vector3.down * 0.25f;
            var toFeet = MotelSync.BedCenter(bed, second) - headSpot; toFeet.y = 0f;
            if (toFeet.sqrMagnitude < 0.01f) toFeet = bed.transform.forward;
            toFeet.Normalize();
            puppet.transform.rotation = Quaternion.LookRotation(Vector3.up, -toFeet); // de barriga para cima, cabeça no travesseiro
            if (head != null) puppet.transform.position += headSpot - head.position;
            else puppet.transform.position = headSpot + toFeet * 1.6f;
        }

        private void FindAnchor(Transform car)
        {
            var original = GameRefs.FindSceneAnimator(AnchorModelName);
            if (original != null && original.transform.IsChildOf(car))
            {
                anchorLocalPos = car.InverseTransformPoint(original.transform.position);
                anchorLocalRot = Quaternion.Inverse(car.rotation) * original.transform.rotation;
                Plugin.Log.LogInfo("Âncora do motorista (do jogo): " + Fmt(anchorLocalPos));
            }
            else
            {
                // Plano B: banco do motorista do próprio jogador.
                var seat = GameRefs.DriverSeatLoc;
                if (seat == null) return;
                anchorLocalPos = car.InverseTransformPoint(seat.position) + Vector3.down * 0.5f;
                anchorLocalRot = Quaternion.Inverse(car.rotation) * seat.rotation;
                Plugin.Log.LogWarning("Laika_Driver não está no carro; usando o banco do motorista: " + Fmt(anchorLocalPos));
            }
            anchorKnown = true;
        }

        private void Create()
        {
            var src = GameRefs.FindSceneAnimator(ModelName);
            if (src == null)
            {
                Plugin.Log.LogWarning("Modelo do host (" + ModelName + ") não encontrado nesta cena.");
                return;
            }
            puppet = (GameObject)Instantiate(src.gameObject);
            puppet.name = "JalopyCoop_Host";
            puppet.transform.parent = null;
            puppet.transform.localScale = src.transform.lossyScale;
            var plane = GameRefs.FindDeep(puppet.transform, BlackPlaneChild);
            if (plane != null) DestroyImmediate(plane.gameObject);
            foreach (var c in puppet.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            skins = puppet.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var sk in skins) sk.updateWhenOffscreen = true; // bounds reais a cada frame (para achar os pés)
            puppet.SetActive(true);
            anim = puppet.GetComponent<Animator>();
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.applyRootMotion = false;
            head = GameRefs.FindDeep(puppet.transform, "Head");
            stateKnown = false;
            showingSeated = true;
            StartCoroutine(LearnSeatedState());
            Plugin.Log.LogInfo("Modelo do host criado (" + ModelName + "), escala " + puppet.transform.localScale + ", cabeça " + (head != null ? "ok" : "não achada"));
        }

        /// <summary>A pose inicial do modelo é a sentada: guarda o estado para voltar a ela depois.</summary>
        private System.Collections.IEnumerator LearnSeatedState()
        {
            for (int i = 0; i < 3; i++) yield return null;
            if (puppet == null) yield break;
            seatedStateHash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
            stateKnown = true;
            LogDiagnostics("modelo criado");
        }

        private float LowestPoint()
        {
            float min = float.NaN;
            if (skins == null) return min;
            foreach (var sk in skins)
            {
                if (sk == null || !sk.enabled) continue;
                float y = sk.bounds.min.y;
                if (float.IsNaN(min) || y < min) min = y;
            }
            return min;
        }

        private void LogDiagnostics(string why)
        {
            if (puppet == null) return;
            var sb = new StringBuilder("Modelo do host (" + why + "): ");
            var info = anim.GetCurrentAnimatorClipInfo(0);
            sb.Append("clip=");
            foreach (var ci in info) if (ci.clip != null) sb.Append(ci.clip.name + " ");
            sb.Append("| raiz=" + Fmt(puppet.transform.position));
            if (head != null) sb.Append(" cabeça=" + Fmt(head.position) + " (altura " + (head.position.y - puppet.transform.position.y).ToString("0.00") + ")");
            sb.Append(" pés=" + LowestPoint().ToString("0.00"));
            sb.Append(" | ajuste=" + Fmt(seatOffset));
            if (CarSync.Instance != null && CarSync.Instance.HasLatest) sb.Append(" | olhos do host=" + Fmt(CarSync.Instance.Latest.HostEye) + " sentado=" + CarSync.Instance.Latest.HostSat);
            Plugin.Log.LogInfo(sb.ToString());
        }

        private void DestroyPuppet()
        {
            if (puppet != null) Destroy(puppet);
            puppet = null;
            bones = null;
            triedCreate = false;
            anchorKnown = false;
            donor.Destroy();
            donorTried = false;
            wasPassenger = false;
            mirrorKnown = false;
        }

        private static string Fmt(Vector3 v)
        {
            return v.x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   v.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   v.z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Vector3 ParseVector(string s)
        {
            var p = (s ?? "").Split(',');
            float x = 0, y = 0, z = 0;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (p.Length == 3)
            {
                float.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out x);
                float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out y);
                float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out z);
            }
            return new Vector3(x, y, z);
        }
    }
}
