using System.Collections.Generic;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Host: lê o carro e o jogador e monta o pacote de estado.
    /// Cliente: guarda os pacotes e move o carro local suavemente (interpolação ~100 ms atrás do host).
    /// No cliente o carro fica "kinematic": a física é só do host.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class CarSync : MonoBehaviour
    {
        private const float InterpDelay = 0.1f;   // atraso de interpolação (s)
        private const float MaxExtrapolate = 0.15f;
        private const float MaxSaneSpeed = 80f;     // m/s (o Laika nem chega perto)
        private const float MaxSaneAngSpeed = 10f;  // rad/s

        private static bool IsFinite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        private static bool IsFinite(Vector3 v) { return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z); }
        private static bool IsFinite(Quaternion q) { return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w); }

        public static CarSync Instance { get; private set; }

        /// <summary>Último estado recebido do host (no cliente).</summary>
        public HostStateData Latest { get; private set; }
        public bool HasLatest { get; private set; }

        private struct Snap
        {
            public float Time;
            public HostStateData Data;
        }

        private readonly List<Snap> buffer = new List<Snap>();
        private float clockOffset;      // hostTime - tempo local
        private bool clockInit;
        private Rigidbody frozenBody;   // carro que deixamos kinematic no cliente
        private bool frozenWasKinematic;
        private readonly List<Behaviour> disabledLocal = new List<Behaviour>();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.BuildHostState = BuildHostState;
            net.OnHostState += OnHostState;
            net.OnConnectionChanged += connected =>
            {
                if (!connected) Release();
                buffer.Clear();
                clockInit = false;
                HasLatest = false;
            };
        }

        // ---------- HOST ----------

        private HostStateData BuildHostState()
        {
            var s = new HostStateData();
            s.HostTime = Time.realtimeSinceStartup;
            var car = GameRefs.Car;
            var body = GameRefs.CarBody;
            if (car != null)
            {
                s.HasCar = true;
                s.CarPos = car.position;
                s.CarRot = car.rotation;
                if (body != null)
                {
                    s.CarVel = body.velocity;
                    s.CarAngVel = body.angularVelocity;
                }
                foreach (var w in car.root.GetComponentsInChildren<WheelScriptPCC>())
                {
                    var wc = w.GetComponent<WheelCollider>();
                    if (wc == null) continue;
                    if (w.typeOfWheel == WheelScriptPCC.wheelType.Steer || w.typeOfWheel == WheelScriptPCC.wheelType.SteerAndMotor) s.SteerAngle = wc.steerAngle;
                    s.WheelRpm = wc.rpm;
                }
            }
            var cam = GameRefs.Cam;
            var player = GameRefs.Player;
            if (cam != null)
            {
                s.HostEye = cam.transform.position;
                s.HostLook = cam.transform.forward;
                if (car != null) s.HostEyeCar = car.InverseTransformPoint(cam.transform.position);
            }
            if (player != null)
            {
                s.HostBody = player.transform.position;
                var rc = player.GetComponent<RigidbodyControllerC>();
                if (rc != null)
                {
                    s.HostSat = rc.isSat;
                    s.HostDriving = rc.driving;
                }
            }
            return s;
        }

        // ---------- CLIENTE ----------

        private void OnHostState(HostStateData s)
        {
            float now = Time.realtimeSinceStartup;
            float offset = s.HostTime - now;
            // Usa o menor atraso visto (pacote mais rápido) e acompanha devagar mudanças de relógio.
            if (!clockInit) { clockOffset = offset; clockInit = true; }
            else if (offset > clockOffset) clockOffset = offset;
            else clockOffset = Mathf.Lerp(clockOffset, offset, 0.01f);

            if (buffer.Count > 0 && s.HostTime <= buffer[buffer.Count - 1].Time) return; // fora de ordem
            if (!IsFinite(s.CarPos) || !IsFinite(s.CarRot)) return;                      // pacote com física quebrada
            // Picos de velocidade (ex.: freio de mão com torque infinito) não podem arremessar o carro do tio.
            if (!IsFinite(s.CarVel) || s.CarVel.magnitude > MaxSaneSpeed) s.CarVel = Vector3.zero;
            if (!IsFinite(s.CarAngVel) || s.CarAngVel.magnitude > MaxSaneAngSpeed) s.CarAngVel = Vector3.zero;
            buffer.Add(new Snap { Time = s.HostTime, Data = s });
            if (buffer.Count > 40) buffer.RemoveAt(0);
            Latest = s;
            HasLatest = true;
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Client || !net.Connected || buffer.Count == 0) return;

            var car = GameRefs.Car;
            var body = GameRefs.CarBody;
            if (car == null || body == null) return;

            if (frozenBody != body)
            {
                Release();
                frozenBody = body;
                frozenWasKinematic = body.isKinematic;
                body.isKinematic = true;
                body.interpolation = RigidbodyInterpolation.None;
                DisableLocalCarSimulation(car);
                Plugin.Log.LogInfo("Carro do cliente agora segue o host (" + disabledLocal.Count + " componentes de física/dano desligados).");
            }

            float renderTime = Time.realtimeSinceStartup + clockOffset - InterpDelay;
            Vector3 pos;
            Quaternion rot;
            Sample(renderTime, out pos, out rot);
            if (!body.isKinematic) body.isKinematic = true; // o macaco (JackDown) do jogo liga a física de novo
            if (!Latest.HasCar || !IsFinite(pos) || !IsFinite(rot)) return;
            car.position = pos;
            car.rotation = rot;
            AnimateWheels();
        }

        /// <summary>
        /// No cliente o WheelScriptPCC fica desligado; aqui repetimos só a parte visual dele
        /// (rodas girando, rodas da frente virando, volante girando) com os valores do host.
        /// </summary>
        private void AnimateWheels()
        {
            float steer = Latest.SteerAngle;
            float rpm = Latest.WheelRpm;
            foreach (var b in disabledLocal)
            {
                var w = b as WheelScriptPCC;
                if (w == null) continue;
                bool steers = w.typeOfWheel == WheelScriptPCC.wheelType.Steer || w.typeOfWheel == WheelScriptPCC.wheelType.SteerAndMotor;
                if (w.wheelTransform != null)
                {
                    w.wheelTransform.Rotate(rpm / 60f * 360f * Time.deltaTime, 0f, 0f);
                    if (steers)
                    {
                        var e = w.wheelTransform.localEulerAngles;
                        var holding = w.wheelTransform.parent != null ? w.wheelTransform.parent.GetComponent<HoldingLogicC>() : null;
                        float baseY = (holding != null && holding.wheelID == 2) ? e.z - 180f : e.z; // igual ao jogo
                        w.wheelTransform.localEulerAngles = new Vector3(e.x, steer + baseY, e.z);
                    }
                }
                if (w.steeringwheelTransform != null)
                {
                    var e = w.steeringwheelTransform.localEulerAngles;
                    w.steeringwheelTransform.localEulerAngles = new Vector3(e.x, e.y, steer * 6f);
                }
            }
        }

        private void Sample(float t, out Vector3 pos, out Quaternion rot)
        {
            // Antes do primeiro pacote: usa o primeiro.
            if (t <= buffer[0].Time)
            {
                pos = buffer[0].Data.CarPos;
                rot = buffer[0].Data.CarRot;
                return;
            }
            for (int i = 0; i < buffer.Count - 1; i++)
            {
                var a = buffer[i];
                var b = buffer[i + 1];
                if (t >= a.Time && t <= b.Time)
                {
                    float k = (t - a.Time) / Mathf.Max(0.0001f, b.Time - a.Time);
                    pos = Vector3.Lerp(a.Data.CarPos, b.Data.CarPos, k);
                    rot = Quaternion.Slerp(a.Data.CarRot, b.Data.CarRot, k);
                    // Remove pacotes velhos que já passaram.
                    if (i > 2) buffer.RemoveRange(0, i - 2);
                    return;
                }
            }
            // Depois do último pacote: extrapola um pouco com a velocidade.
            var last = buffer[buffer.Count - 1].Data;
            float dt = Mathf.Min(t - buffer[buffer.Count - 1].Time, MaxExtrapolate);
            pos = last.CarPos + last.CarVel * dt;
            rot = Quaternion.Euler(last.CarAngVel * Mathf.Rad2Deg * dt) * last.CarRot;
        }

        /// <summary>
        /// No cliente o carro é movido "teletransportado" para seguir o host. Para o jogo local não achar
        /// que bateu (quebrando vidros/motor e amassando a lataria) nem tentar acelerar sozinho,
        /// desliga dano por batida, deformação, tração das rodas e derrapagem. Dano é só do host.
        /// </summary>
        private void DisableLocalCarSimulation(Transform car)
        {
            disabledLocal.Clear();
            var root = car.root;
            foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null || !b.enabled) continue;
                if (b is CarCollisionsC || b is CarDamage2 || b is WheelScriptPCC || b is SkiddingScriptC)
                {
                    b.enabled = false;
                    disabledLocal.Add(b);
                }
            }
            foreach (var wc in root.GetComponentsInChildren<WheelCollider>(true))
            {
                wc.motorTorque = 0f;
                wc.brakeTorque = 0f;
            }
        }

        /// <summary>Devolve a física do carro ao jogo local (ao desconectar).</summary>
        /// <summary>Cliente: o carro volta a ser só "seguir o host" (algo do jogo ligou a física dele).</summary>
        public void Refreeze()
        {
            if (frozenBody != null) frozenBody.isKinematic = true;
        }

        public void Release()
        {
            if (frozenBody != null)
            {
                frozenBody.isKinematic = frozenWasKinematic;
                Plugin.Log.LogInfo("Carro do cliente voltou à física local.");
            }
            foreach (var b in disabledLocal) if (b != null) b.enabled = true;
            disabledLocal.Clear();
            frozenBody = null;
        }
    }
}
