using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using JalopyCoop.Net;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Teste automático (-coop-autotest=mvp3). Roda nas DUAS janelas ao mesmo tempo, com o mesmo roteiro de tempo:
    /// o mod "joga" sozinho (anda, olha, senta, levanta), tira prints em cada ponto e confere números (OK/FALHOU).
    /// Tudo vai para a pasta autotest/ ao lado das cópias do jogo. No fim, o jogo fecha sozinho.
    /// </summary>
    public partial class AutoTest : MonoBehaviour
    {
        public static AutoTest Instance { get; private set; }
        /// <summary>true: o olhar é do roteiro (o MouseLook do jogo fica parado).</summary>
        public static bool SteeringLook;

        public string Scenario = "mvp3";

        private string outDir;
        private readonly StringBuilder report = new StringBuilder();
        private int ok, fail, errors;
        private bool started;
        private float readySince = -1f;
        private string side;

        // olhar e andar comandados
        private float yaw, pitch;
        private bool walking;
        private Vector3 walkCenter;
        private float walkRadius, walkSpeed, walkAngle;

        private void Awake()
        {
            Instance = this;
            Application.logMessageReceived += OnLog;
        }

        private void OnDestroy() { Application.logMessageReceived -= OnLog; }

        private void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
            {
                errors++;
                if (errors <= 10) Line("ERRO no log: " + msg);
            }
        }

        private bool startSignal;   // tio: o host mandou começar
        private bool subscribed;

        private void Update()
        {
            if (started) return;
            var net = CoopNet.Instance;
            if (net == null) return;
            if (!subscribed) { subscribed = true; net.OnReliable += m => { if (m == Msg.TestStart) startSignal = true; }; }
            bool ready = net.Connected && GameRefs.InGame && GameRefs.CarLogic != null;
            if (net.Mode == CoopMode.Client)
            {
                // O tio começa quando o host manda (mesmo relógio para os dois).
                if (!ready || !startSignal) return;
            }
            else
            {
                // O host espera o tio estar no jogo e sentado, mais 5 s, e manda o "começa".
                ready = ready && UncleSync.Instance != null && UncleSync.Instance.HasRemote
                    && (Scenario == "aparencia" || Scenario == "clima" || Scenario == "cidade" || Scenario == "motel" || Scenario == "porta" || Scenario == "chuva" || Scenario == "transito" ? ItemSync.Instance != null && ItemSync.Instance.Ready : UncleSync.Instance.RemoteSeated);
                if (!ready) { readySince = -1f; return; }
                if (readySince < 0f) { readySince = Time.realtimeSinceStartup; return; }
                if (Time.realtimeSinceStartup - readySince < 5f) return;
                net.SendReliable(Msg.TestStart);
            }
            started = true;
            errors = 0; // conta só os erros durante o roteiro
            side = net.Mode == CoopMode.Host ? "host" : "tio";
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(BepInEx.Paths.ExecutablePath), Path.Combine("..", "autotest")));
            outDir = Path.Combine(root, Scenario);
            Directory.CreateDirectory(outDir);
            Line("Teste automático '" + Scenario + "' começou (" + side + ") " + DateTime.Now.ToString("HH:mm:ss"));
            if (Scenario == "porta") StartCoroutine(net.Mode == CoopMode.Host ? HostDoorCashScript() : ClientDoorCashScript());
            else if (Scenario == "motel") StartCoroutine(net.Mode == CoopMode.Host ? HostMotelScript() : ClientMotelScript());
            else if (Scenario == "cidade") StartCoroutine(net.Mode == CoopMode.Host ? HostCityScript() : ClientCityScript());
            else if (Scenario == "chuva") StartCoroutine(net.Mode == CoopMode.Host ? HostRainScript() : ClientRainScript());
            else if (Scenario == "clima") StartCoroutine(net.Mode == CoopMode.Host ? HostWeatherScript() : ClientWeatherScript());
            else if (Scenario == "aparencia") StartCoroutine(net.Mode == CoopMode.Host ? HostLookScript() : ClientLookScript());
            else if (Scenario == "transito") StartCoroutine(net.Mode == CoopMode.Host ? HostTrafficScript() : ClientTrafficScript());
            else StartCoroutine(net.Mode == CoopMode.Host ? HostScript() : ClientScript());
        }

        // ================= roteiros (mesmo relógio nos dois lados) =================
        //  0 s  tio sentado, host no volante: prints dos dois
        //  6 s  tio levanta (lado do passageiro)
        //  9 s  host confere o tio-NPC de pé; 10-17 s tio anda em círculo; host confere andando
        // 18 s  tio para e gira o olhar
        // 21 s  host sai do carro; 25-32 s host anda em círculo; tio confere host andando
        // 33-36 s host gira no lugar; tio confere
        // 38 s  host senta no volante; 40 s tio senta; 44 s prints finais; 48 s fecha

        private IEnumerator ClientScript()
        {
            float t0 = Time.time;
            SteeringLook = true;
            yield return Until(t0, 2f); LookAtHost(); yield return Until(t0, 3f);
            yield return Shot("01_tio_sentado_ve_host"); yield return ObserverShot(HostPuppetT(), "01_host_volante");
            CheckHostPuppetSeated("tio sentado: host no volante");

            yield return Until(t0, 6f);
            UncleSync.Instance.SetWantSeat(false);
            yield return Until(t0, 9f);
            LookAtHost(); yield return Shot("02_tio_de_pe_ve_host"); yield return ObserverShot(HostPuppetT(), "02_host_volante");

            yield return Until(t0, 10f);
            var player = GameRefs.Player;
            var car = GameRefs.Car;
            if (player != null && car != null)
            {
                walkCenter = player.transform.position + (player.transform.position - car.position).normalized * 3f;
                walkCenter.y = player.transform.position.y;
                StartWalk(walkCenter, 2.5f, 2.5f);
            }
            yield return Until(t0, 17f);
            walking = false;
            yield return Until(t0, 18f);
            yield return SweepLook(3f);

            yield return Until(t0, 26f);
            LookAtHost(); yield return Shot("03_host_andando"); yield return ObserverShot(HostPuppetT(), "03_host_andando");
            CheckHostPuppetStanding("tio vê host andando");
            yield return Until(t0, 29f);
            LookAtHost(); CheckHostPuppetStanding("tio vê host andando (2)");
            yield return Until(t0, 34.5f);
            LookAtHost(); yield return Shot("04_host_girando"); yield return ObserverShot(HostPuppetT(), "04_host_girando");
            CheckHostPuppetStanding("tio vê host girando");

            yield return Until(t0, 40f);
            UncleSync.Instance.SetWantSeat(true);
            yield return Until(t0, 44f);
            LookAtHost(); yield return Shot("05_tio_sentado_host_volante"); yield return ObserverShot(HostPuppetT(), "05_host_volante");
            CheckHostPuppetSeated("final: host no volante");
            yield return Finish(t0);
        }

        private IEnumerator HostScript()
        {
            float t0 = Time.time;
            SteeringLook = true;
            yield return Until(t0, 2f); LookAtUncle(); yield return Until(t0, 3f);
            yield return Shot("01_host_ve_tio_sentado"); yield return ObserverShot(UncleT(), "01_tio_sentado");
            CheckUncleSeated(true, "host: tio-NPC sentado");

            yield return Until(t0, 9f);
            LookAtUncle(); yield return Shot("02_tio_npc_de_pe"); yield return ObserverShot(UncleT(), "02_tio_de_pe");
            CheckUncleSeated(false, "host: tio-NPC saiu do carro");
            CheckUncleFollow("host: tio-NPC no lugar do tio (de pé)");

            for (float t = 11f; t <= 16f; t += 2.5f)
            {
                yield return Until(t0, t);
                LookAtUncle();
                CheckUncleFollow("host: tio-NPC acompanha andando (" + t + " s)");
            }
            yield return Shot("03_tio_npc_andando"); yield return ObserverShot(UncleT(), "03_tio_andando");

            yield return Until(t0, 21f);
            var cl = GameRefs.CarLogic;
            if (cl != null && cl.doorExit != null)
            {
                // A saída pela porta só fica ligada com a porta aberta; ligada, o Start dela prepara as referências.
                if (!cl.doorExit.activeSelf) { cl.doorExit.SetActive(true); yield return null; yield return null; }
                var exit = cl.doorExit.GetComponent<DoorExitLogicC>();
                if (exit != null) exit.Trigger();
                Line("host saiu do carro (DoorExit)");
            }
            yield return Until(t0, 25f);
            var player = GameRefs.Player;
            var car = GameRefs.Car;
            if (player != null && car != null)
            {
                var away = (player.transform.position - car.position); away.y = 0f;
                walkCenter = player.transform.position + away.normalized * 3f;
                StartWalk(walkCenter, 2.5f, 2.5f);
            }
            yield return Until(t0, 32f);
            walking = false;
            yield return Until(t0, 33f);
            yield return SweepLook(3f);

            yield return Until(t0, 38f);
            cl = GameRefs.CarLogic;
            if (cl != null && cl.playerSeat != null)
            {
                var seat = cl.playerSeat.GetComponent<SeatLogicC>();
                if (seat != null) seat.Trigger();
                Line("host sentou no volante (SeatLogic)");
            }
            yield return Until(t0, 44f);
            LookAtUncle(); yield return Shot("04_host_ve_tio_sentado"); yield return ObserverShot(UncleT(), "04_tio_sentado");
            CheckUncleSeated(true, "host: tio-NPC sentou de novo");
            yield return Finish(t0);
        }

        private IEnumerator Finish(float t0, float end = 48f)
        {
            SteeringLook = false;
            walking = false;
            Line("Erros/exceções no log: " + errors);
            Line("RESULTADO (" + side + "): " + ok + " OK, " + fail + " FALHOU");
            File.WriteAllText(Path.Combine(outDir, side + "-resultado.txt"), report.ToString(), Encoding.UTF8);
            yield return Until(t0, end);
            Plugin.Log.LogInfo("[TESTE] fim, fechando o jogo.");
            Application.Quit();
        }

        // ================= checagens =================

        private void CheckUncleSeated(bool want, string name)
        {
            var u = GameRefs.Uncle;
            var logic = u != null ? u.GetComponent<UncleLogicC>() : null;
            Check(name, logic != null && logic.isSat == want, "isSat=" + (logic != null ? logic.isSat.ToString() : "-"));
        }

        private void CheckUncleFollow(string name)
        {
            var u = GameRefs.Uncle;
            var us = UncleSync.Instance;
            if (u == null || us == null || !us.HasRemote) { Check(name, false, "sem tio/estado"); return; }
            var body = FindBone(u.transform, "LowerBack");
            var p = body != null ? body.position : u.transform.position;
            var r = us.RemoteBody;
            float dx = Flat(p - r);
            float feet = LowestBone(u.transform);
            float ground; bool g = Ground(r, u.transform, out ground);
            string d = "dist=" + dx.ToString("0.00") + "m";
            if (g) d += " pés-chão=" + (feet - ground).ToString("0.00") + "m";
            Check(name, dx < 1.2f && (!g || Mathf.Abs(feet - ground) < 0.6f), d);
        }

        private void CheckHostPuppetSeated(string name)
        {
            var pup = GameObject.Find("JalopyCoop_Host");
            var s = CarSync.Instance != null ? CarSync.Instance.Latest : default(HostStateData);
            if (pup == null || !pup.activeInHierarchy) { Check(name, false, "modelo do host não existe"); return; }
            var head = FindBone(pup.transform, "Head");
            float d = head != null ? Vector3.Distance(head.position, s.HostEye) : 99f;
            Check(name, d < 1.5f, "cabeça-olhos do host=" + d.ToString("0.00") + "m");
        }

        private void CheckHostPuppetStanding(string name)
        {
            var pup = GameObject.Find("JalopyCoop_Host");
            var s = CarSync.Instance != null ? CarSync.Instance.Latest : default(HostStateData);
            if (pup == null || !pup.activeInHierarchy) { Check(name, false, "modelo do host não existe"); return; }
            if (s.HostSat) { Check(name, false, "o host ainda está sentado"); return; }
            var body = FindBone(pup.transform, "LowerBack");
            float dx = body != null ? Flat(body.position - s.HostBody) : 99f;
            float feet = LowestBone(pup.transform);
            float ground; bool g = Ground(s.HostBody, pup.transform, out ground);
            string d = "dist=" + dx.ToString("0.00") + "m";
            if (g) d += " pés-chão=" + (feet - ground).ToString("0.00") + "m";
            Check(name, dx < 1.2f && (!g || Mathf.Abs(feet - ground) < 0.6f), d);
        }

        private void Check(string name, bool pass, string detail)
        {
            if (pass) ok++; else fail++;
            Line((pass ? "OK      " : "FALHOU  ") + name + "  (" + detail + ")");
        }

        private void Line(string s)
        {
            report.AppendLine(s);
            Plugin.Log.LogInfo("[TESTE] " + s);
        }

        // ================= ações =================

        private IEnumerator Until(float t0, float t)
        {
            while (Time.time - t0 < t) yield return null;
        }

        private void LookAtHost()
        {
            if (CarSync.Instance == null || !CarSync.Instance.HasLatest) return;
            var s = CarSync.Instance.Latest;
            var target = s.HostSat ? s.HostEye : s.HostBody + Vector3.up * 0.5f;
            LookAt(target);
        }

        private void LookAtUncle()
        {
            var u = GameRefs.Uncle;
            if (u == null) return;
            var head = FindBone(u.transform, "Head");
            LookAt(head != null ? head.position + Vector3.down * 0.8f : u.transform.position + Vector3.up * 2f);
        }

        private void LookAt(Vector3 target)
        {
            var cam = GameRefs.Cam;
            if (cam == null) return;
            var d = target - cam.transform.position;
            if (d.sqrMagnitude < 0.0001f) return;
            yaw = Quaternion.LookRotation(d).eulerAngles.y;
            var flat = new Vector3(d.x, 0f, d.z).magnitude;
            pitch = Mathf.Clamp(Mathf.Atan2(d.y, flat) * Mathf.Rad2Deg, -60f, 60f);
            ApplyLook();
        }

        private IEnumerator SweepLook(float seconds)
        {
            float start = yaw, t = 0f;
            while (t < seconds)
            {
                t += Time.deltaTime;
                yaw = start + Mathf.Sin(t / seconds * Mathf.PI * 2f) * 90f;
                yield return null;
            }
            yaw = start;
        }

        private void ApplyLook()
        {
            var player = GameRefs.Player;
            var cam = GameRefs.Cam;
            if (player == null || cam == null) return;
            // Gira o corpo pela diferença entre para onde a câmera aponta e para onde deve apontar
            // (a câmera pode ter giro próprio em relação ao corpo; impor o ângulo no corpo errava a mira).
            float delta = Mathf.DeltaAngle(cam.transform.eulerAngles.y, yaw);
            if (Mathf.Abs(delta) > 0.01f) player.transform.Rotate(0f, delta, 0f, Space.World);
            cam.transform.localEulerAngles = new Vector3(-pitch, 0f, 0f);
            var camMl = cam.GetComponent<MouseLook>();
            if (camMl != null) camMl.rotationY = pitch;
        }

        private void StartWalk(Vector3 center, float radius, float speed)
        {
            var player = GameRefs.Player;
            if (player == null) return;
            walkCenter = center; walkRadius = radius; walkSpeed = speed;
            var off = player.transform.position - center; off.y = 0f;
            walkAngle = Mathf.Atan2(off.z, off.x);
            walking = true;
            Line(side + " andando em círculo (raio " + radius + " m)");
        }

        private void FixedUpdate()
        {
            if (!walking) return;
            var player = GameRefs.Player;
            if (player == null) { walking = false; return; }
            walkAngle += walkSpeed / walkRadius * Time.fixedDeltaTime;
            var p = player.transform.position;
            var want = walkCenter + new Vector3(Mathf.Cos(walkAngle), 0f, Mathf.Sin(walkAngle)) * walkRadius;
            var dir = new Vector3(-Mathf.Sin(walkAngle), 0f, Mathf.Cos(walkAngle));
            yaw = Quaternion.LookRotation(dir).eulerAngles.y;
            pitch = 0f;
            var rb = player.GetComponent<Rigidbody>();
            var next = new Vector3(want.x, p.y, want.z);
            if (rb != null && !rb.isKinematic) rb.MovePosition(next); else player.transform.position = next;
        }

        private void LateUpdate()
        {
            if (SteeringLook) ApplyLook();
        }

        private IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                var file = Path.Combine(outDir, side + "_" + name + ".png");
                File.WriteAllBytes(file, tex.EncodeToPNG());
                Destroy(tex);
                Line("print: " + Path.GetFileName(file));
            }
            catch (Exception e) { Line("print falhou: " + e.Message); }
        }

        /// <summary>
        /// Print de "observador": câmera extra a ~3,5 m do modelo testado, olhando direto para ele
        /// (a print em 1ª pessoa depende da mira e, sentado, fica colada no carro).
        /// </summary>
        private IEnumerator ObserverShot(Transform subject, string name, float dist = 3.5f)
        {
            yield return new WaitForEndOfFrame();
            if (subject == null) { Line("print observador: sem modelo (" + name + ")"); yield break; }
            var center = FindBone(subject, "UpperBack") ?? FindBone(subject, "LowerBack") ?? subject;
            var target = center.position;
            var main = GameRefs.Cam;
            var from = main != null ? main.transform.position : target + Vector3.forward * 4f;
            var dir = from - target; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            var camPos = target + dir.normalized * dist + Vector3.up * 0.8f;
            var go = new GameObject("JalopyCoop_TestCam");
            RenderTexture rt = null;
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.nearClipPlane = 0.05f;
                cam.fieldOfView = 55f;
                cam.transform.position = camPos;
                cam.transform.LookAt(target);
                rt = new RenderTexture(960, 540, 24);
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                var file = Path.Combine(outDir, side + "_" + name + "_obs.png");
                File.WriteAllBytes(file, tex.EncodeToPNG());
                Destroy(tex);
                Line("print observador: " + Path.GetFileName(file));
            }
            catch (Exception e) { Line("print observador falhou: " + e.Message); }
            finally
            {
                if (rt != null) { rt.Release(); Destroy(rt); }
                Destroy(go);
            }
        }

        private static Transform HostPuppetT() { var p = GameObject.Find("JalopyCoop_Host"); return p != null ? p.transform : null; }
        private static Transform UncleT() { var u = GameRefs.Uncle; return u != null ? u.transform : null; }

        // ================= utilidades =================

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        private static Transform FindBone(Transform root, string name) { return GameRefs.FindDeep(root, name); }

        private static float LowestBone(Transform root)
        {
            float min = float.MaxValue;
            foreach (var t in root.GetComponentsInChildren<Transform>())
            {
                if (t == root || t.GetComponent<Renderer>() != null) continue;
                if (t.name.Contains("Suit") || t.name.Contains("Letter") || t.name.Contains("envelope") || t.name.Contains("Envelope")) continue;
                if (t.position.y < min) min = t.position.y;
            }
            return min;
        }

        private static bool Ground(Vector3 from, Transform ignore, out float y)
        {
            y = 0f;
            float best = float.MaxValue;
            var player = GameRefs.Player;
            foreach (var h in Physics.RaycastAll(from + Vector3.up * 0.5f, Vector3.down, 10f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || h.collider.transform.IsChildOf(ignore)) continue;
                if (player != null && h.collider.transform.IsChildOf(player.transform)) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return best < float.MaxValue;
        }
    }

    /// <summary>Durante o teste automático, o olhar vem do roteiro (o MouseLook do jogo não mexe).</summary>
    [HarmonyPatch]
    public static class AutoTestLookPatch
    {
        [HarmonyPatch(typeof(MouseLook), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool Fixed() { return !AutoTest.SteeringLook; }

        [HarmonyPatch(typeof(MouseLook), "Update")]
        [HarmonyPrefix]
        private static bool Upd() { return !AutoTest.SteeringLook; }
    }
}
