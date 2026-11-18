using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.UI
{
    /// <summary>
    /// Atalhos de TESTE (só com -coop-devkeys, só no host): janela F4 para teleportar o carro (cidade, loja,
    /// motel, posto, fronteira), dar dinheiro e mudar a hora. Não faz parte do jogo normal.
    ///
    /// A rota (cidades, motel, fronteira) só existe depois de escolher o caminho no mapa; antes disso só há a
    /// cidade/garagem atual. O tio sentado vai junto com o carro; a pé, o host manda ele para perto do carro.
    /// </summary>
    public class DevTools : MonoBehaviour
    {
        private bool open;
        private Rect window = new Rect(460, 60, 320, 420);
        private string lastResult = "";
        private bool cursorWasVisible;
        private CursorLockMode cursorWasLock;

        private static DevTools Instance;
        /// <summary>Teleporte de botão: só o jogador local vai (o carro só vem com "Trazer o carro até mim").</summary>
        private static bool movingPlayer;

        private void Start()
        {
            Instance = this;
            CoopNet.Instance.OnReliableData += OnReliableData;
        }

        /// <summary>Janela: host e tio (com -coop-devkeys). Teleporte/dinheiro/hora só no host.</summary>
        private static bool Allowed
        {
            get { return Plugin.DevKeys && CoopNet.Instance != null && GameRefs.InGame; }
        }

        private static bool IsHostSide
        {
            get { var n = CoopNet.Instance; return n != null && n.Mode != CoopMode.Client; }
        }

        private void Update()
        {
            if (!Allowed) { if (open) SetOpen(false); if (noclip) SetNoclip(false); return; }
            if (noclip) FlyNoclip();
            if (Input.GetKeyDown(KeyCode.F4)) SetOpen(!open);
            if (open) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        private void SetOpen(bool on)
        {
            open = on;
            if (on) { cursorWasVisible = Cursor.visible; cursorWasLock = Cursor.lockState; }
            else { Cursor.visible = cursorWasVisible; Cursor.lockState = cursorWasLock; }
        }

        private void OnGUI()
        {
            if (!Allowed) return;
            if (!open)
            {
                GUI.Label(new Rect(10, 26, 400, 22), "[F4 atalhos de teste]");
                return;
            }
            window = GUI.Window(0x4A44, window, Draw, IsHostSide ? "Atalhos de teste (host)" : "Atalhos de teste (tio)");
        }

        private void Draw(int id)
        {
            if (GUILayout.Button(noclip ? "Noclip: LIGADO (clique para desligar)" : "Noclip: desligado"))
                Do(() => SetNoclip(!noclip));
            GUILayout.Label("Noclip: WASD anda, Espaço sobe, Ctrl desce, Shift rápido.");
            GUILayout.Space(6);
            GUILayout.Label("Ir até (só você, o carro fica):");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cidade")) Act(1);
            if (GUILayout.Button("Loja")) Act(2);
            if (GUILayout.Button("Motel")) Act(3);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Posto")) Act(4);
            if (GUILayout.Button("Fronteira")) Act(5);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Ferro-velho")) Act(11);
            if (GUILayout.Button("Loja do Laika")) Act(12);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Sortear outras rotas")) Act(13);
            if (GUILayout.Button("Resetar motel")) Do(() => MotelSync.ResetMotelForTest(true));
            if (GUILayout.Button("Carro até a fronteira")) Act(16);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+100 dinheiro")) Act(6);
            if (GUILayout.Button("+1000")) Act(7);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1 hora")) Act(8);
            if (GUILayout.Button("8:00")) Act(9);
            if (GUILayout.Button("20:00")) Act(10);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label(lastResult);
            GUILayout.Label("F4 fecha. Teleporte só depois de escolher a rota no mapa (exceto a cidade atual).");
            GUI.DragWindow();
        }

        /// <summary>Host faz na hora; o tio pede ao host (o carro, o relógio e o dinheiro são do host).</summary>
        private void Act(int id)
        {
            if (id <= 5 || id == 11 || id == 12) { Do(() => TeleportMe(id)); return; } // cada um se teleporta sozinho
            if (IsHostSide) { Do(() => Run(id)); return; }
            CoopNet.Instance.SendReliable(Msg.DevRequest, w => w.Write((byte)id));
            lastResult = "Pedido mandado ao host.";
        }

        private static string TeleportMe(int id)
        {
            var player = GameRefs.Player;
            if (player == null) return "Sem jogador.";
            var ctl = player.GetComponent<RigidbodyControllerC>();
            if ((ctl != null && ctl.isSat) || UncleSync.ClientSeatedByCoop) return "Saia do carro antes de teleportar.";
            movingPlayer = true;
            try { return Run(id); }
            finally { movingPlayer = false; }
        }

        private static string Run(int id)
        {
            switch (id)
            {
                case 1: return TeleportTo(Target.Town);
                case 2: return TeleportTo(Target.Shop);
                case 3: return TeleportTo(Target.Motel);
                case 4: return TeleportTo(Target.Fuel);
                case 5: return TeleportTo(Target.Border);
                case 11: return TeleportNear<ScrapYardC>("ferro-velho");
                case 12: return TeleportLaika();
                case 13: return RouteSync.Instance != null ? RouteSync.Instance.HostReroll() : "Sem RouteSync.";
                case 16: return TeleportTo(Target.Border); // carro + os dois, 40 m antes da cancela
                case 6: return AddMoney(100f);
                case 7: return AddMoney(1000f);
                case 8: return AddHours(1f);
                case 9: return SetHour(8f);
                case 10: return SetHour(20f);
            }
            return "?";
        }

        private void Do(Func<string> action)
        {
            try { lastResult = action(); }
            catch (Exception e) { lastResult = "Erro: " + e.Message; Plugin.Log.LogError("DevTools: " + e); }
            Plugin.Log.LogInfo("Teste: " + lastResult);
        }

        // ================= trazer o carro até o jogador =================

        /// <summary>O carro vai para um lugar livre perto de quem apertou (o carro é do host: o tio pede com a posição dele).</summary>
        private void CarToMe()
        {
            var player = GameRefs.Player;
            if (player == null) { lastResult = "Sem jogador."; return; }
            var pos = player.transform.position;
            if (IsHostSide) { Do(() => CarTo(pos, "o host")); return; }
            CoopNet.Instance.SendReliable(Msg.DevRequest, w => { w.Write((byte)14); NetIO.Put(w, pos); });
            lastResult = "Pedido mandado ao host.";
        }

        private static string CarTo(Vector3 pos, string who)
        {
            var player = GameRefs.Player;
            var ctl = player != null ? player.GetComponent<RigidbodyControllerC>() : null;
            if (CoopNet.Instance.Mode != CoopMode.Client && ctl != null && ctl.isSat && who == "o host") return "Você já está no carro.";
            // chão sob o jogador (no noclip ele pode estar no ar): o carro vai para esse nível, nunca para baixo do terreno
            float baseY;
            if (!GroundBelow(pos + Vector3.up * 1.5f, 200f, out baseY)) return "Sem chão debaixo de " + who + ".";
            var at = new Vector3(pos.x, baseY, pos.z);
            Plugin.Log.LogInfo("Teste: trazer carro: jogador " + pos + ", chão " + baseY);
            // perto do jogador (3 a 10 m), sem mexer em ninguém, só chão com até 2,5 m de diferença de altura
            return PlaceNear(at, who, false, new[] { 3.5f, 5f, 7f, 10f }, 2.5f);
        }

        /// <summary>Diagnóstico: tudo o que o raio vertical acerta (nome, altura, camada).</summary>
        private static void LogHits(Vector3 spot, RaycastHit[] hits)
        {
            var sb = new System.Text.StringBuilder("Teste: raio em " + spot + ":");
            int n = 0;
            foreach (var h in hits)
            {
                if (n++ >= 8) break;
                sb.Append(" | " + h.collider.name + " y=" + h.point.y.ToString("F1") + " cam=" + LayerMask.LayerToName(h.collider.gameObject.layer) + " raiz=" + h.transform.root.name);
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>Primeiro chão de cima para baixo a partir de "from" (ignora carro e jogador).</summary>
        private static bool GroundBelow(Vector3 from, float range, out float y)
        {
            var car = GameRefs.Car;
            y = from.y;
            var hits = Physics.RaycastAll(from, Vector3.down, range, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (car != null && h.transform.IsChildOf(car.root)) continue;
                if (GameRefs.Player != null && h.transform.IsChildOf(GameRefs.Player.transform)) continue;
                y = h.point.y;
                return true;
            }
            return false;
        }

        // ================= noclip =================

        private bool noclip;
        private bool savedCc, savedKinematic, savedDetect, savedController;

        private string SetNoclip(bool on)
        {
            var player = GameRefs.Player;
            if (player == null) return "Sem jogador.";
            var cc = player.GetComponent<CharacterController>();
            var rb = player.GetComponent<Rigidbody>();
            var ctl = player.GetComponent<RigidbodyControllerC>();
            if (on)
            {
                if (ctl != null && ctl.isSat) return "Saia do carro antes de ligar o noclip.";
                if (UncleSync.ClientSeatedByCoop) return "Saia do carro antes de ligar o noclip.";
                savedCc = cc != null && cc.enabled;
                savedKinematic = rb != null && rb.isKinematic;
                savedDetect = rb == null || rb.detectCollisions;
                savedController = ctl != null && ctl.enabled;
                if (cc != null) cc.enabled = false;
                if (rb != null) { rb.velocity = Vector3.zero; rb.isKinematic = true; rb.detectCollisions = false; }
                if (ctl != null) ctl.enabled = false; // sem gravidade nem andar do jogo
                noclip = true;
                return "Noclip ligado.";
            }
            if (cc != null) cc.enabled = savedCc;
            if (rb != null) { rb.isKinematic = savedKinematic; rb.detectCollisions = savedDetect; rb.velocity = Vector3.zero; }
            if (ctl != null) ctl.enabled = savedController;
            noclip = false;
            return "Noclip desligado.";
        }

        /// <summary>Move o corpo inteiro (não só a câmera, como o noclip do jogo): o outro jogador vê voando.</summary>
        private void FlyNoclip()
        {
            var player = GameRefs.Player;
            var cam = GameRefs.Cam;
            if (player == null || cam == null) { noclip = false; return; }
            if (open) return; // com a janela aberta o teclado é da janela
            var dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += cam.transform.forward;
            if (Input.GetKey(KeyCode.S)) dir -= cam.transform.forward;
            if (Input.GetKey(KeyCode.D)) dir += cam.transform.right;
            if (Input.GetKey(KeyCode.A)) dir -= cam.transform.right;
            if (Input.GetKey(KeyCode.Space)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl)) dir -= Vector3.up;
            if (dir.sqrMagnitude < 0.001f) return;
            float speed = Input.GetKey(KeyCode.LeftShift) ? 400f : 15f;
            player.transform.position += dir.normalized * speed * Time.unscaledDeltaTime;
        }

        // ================= teleporte =================

        private enum Target { Town, Shop, Motel, Fuel, Border }

        private static string TeleportTo(Target target)
        {
            var car = GameRefs.Car;
            if (car == null) return "Sem carro.";
            Transform goal = null;
            Vector3 face = car.forward;
            string what = target.ToString();

            if (target == Target.Border)
            {
                BorderLogicC best = null; float bd = float.MaxValue;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<BorderLogicC>())
                {
                    float d = (b.transform.position - car.position).sqrMagnitude;
                    if (d < bd) { bd = d; best = b; }
                }
                if (best == null) return "Nenhuma fronteira criada (escolha a rota no mapa primeiro).";
                // 40 m antes da fronteira, do lado em que o carro está (a estrada corre no eixo Z do mundo)
                float side = Mathf.Sign(car.position.z - best.transform.position.z);
                if (side == 0f) side = -1f;
                var spot = best.transform.position + new Vector3(0f, 0f, 40f * side);
                return Place(spot, new Vector3(0f, 0f, -side), "fronteira " + best.name);
            }

            var hub = PickHub(car);
            if (hub == null) return "Nenhuma cidade criada (escolha a rota no mapa primeiro).";
            GameObject building = null;
            switch (target)
            {
                case Target.Shop: building = hub.market; what = "loja"; break;
                case Target.Motel: building = hub.spawnedMotel != null ? hub.spawnedMotel : hub.motel; what = "motel"; break;
                case Target.Fuel: building = hub.petrolStation; what = "posto"; break;
                default: what = "cidade"; break;
            }
            var reference = building != null ? building.transform.position : hub.transform.position;
            if (target == Target.Fuel && building == null) return TeleportNear<RefuelLogicC>("posto");
            if (target != Target.Town && building == null) return "Esta cidade não tem " + what + ".";

            // rua mais perto do prédio (os roadTargets são as ruas da cidade)
            float best2 = float.MaxValue;
            foreach (var rt in hub.roadTargets)
            {
                if (rt == null) continue;
                float d = (rt.position - reference).sqrMagnitude;
                if (d < best2) { best2 = d; goal = rt; }
            }
            if (goal == null) goal = hub.transform;
            if (building != null)
            {
                var toBuilding = reference - goal.position; toBuilding.y = 0f;
                if (toBuilding.sqrMagnitude > 0.01f) face = Vector3.Cross(Vector3.up, toBuilding.normalized); // de lado para o prédio
            }
            return Place(goal.position, face, what + " de " + hub.name);
        }

        /// <summary>
        /// Leva o carro para perto do objeto T mais perto (posto e ferro-velho da estrada nascem como "paradas" da rota,
        /// fora da cidade). Procura um lugar livre do tamanho do carro, com chão, em volta do objeto.
        /// </summary>
        private static string TeleportNear<T>(string what) where T : Component
        {
            var car = GameRefs.Car;
            T best = null; float bd = float.MaxValue;
            foreach (var o in UnityEngine.Object.FindObjectsOfType<T>())
            {
                if (o.transform.IsChildOf(car.root)) continue;
                float d = (o.transform.position - car.position).sqrMagnitude;
                if (d < bd) { bd = d; best = o; }
            }
            if (best == null) return "Nenhum " + what + " criado (escolha a rota no mapa primeiro; nem toda rota tem).";
            return PlaceNear(best.transform.position, what);
        }

        private static string TeleportLaika()
        {
            var car = GameRefs.Car;
            var hub = PickHub(car);
            if (hub != null && hub.laikaFactory != null) return PlaceNear(hub.laikaFactory.transform.position, "loja do Laika de " + hub.name);
            return TeleportNear<LaikaBuildingC>("loja do Laika");
        }

        private static string PlaceNear(Vector3 target, string what, bool movePlayers = true, float[] dists = null, float maxStep = 0f)
        {
            var car = GameRefs.Car;
            var half = new Vector3(1.1f, 0.8f, 2.4f);
            if (dists == null) dists = new[] { 9f, 13f, 17f, 22f };
            for (int di = 0; di < dists.Length; di++)
            {
                for (int a = 0; a < 12; a++)
                {
                    var dir = Quaternion.Euler(0f, a * 30f, 0f) * Vector3.forward;
                    var spot = target + dir * dists[di];
                    var hits = Physics.RaycastAll(spot + Vector3.up * (maxStep > 0f ? maxStep + 2f : 300f), Vector3.down, maxStep > 0f ? 80f : 600f, ~0, QueryTriggerInteraction.Ignore);
                    Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                    RaycastHit? ground = null;
                    foreach (var h in hits)
                    {
                        if (h.transform.IsChildOf(car.root)) continue;
                        if (GameRefs.Player != null && h.transform.IsChildOf(GameRefs.Player.transform)) continue;
                        ground = h; break;
                    }
                    if (di == 0 && a == 0) LogHits(spot, hits);
                    if (ground == null || Vector3.Angle(ground.Value.normal, Vector3.up) > 20f) continue;
                    if (maxStep > 0f && Mathf.Abs(ground.Value.point.y - target.y) > maxStep) continue;
                    var face = Vector3.Cross(Vector3.up, -dir); // de lado para o lugar
                    var rot = Quaternion.LookRotation(face, Vector3.up);
                    var center = ground.Value.point + Vector3.up * (half.y + 0.3f);
                    bool blocked = false;
                    foreach (var c in Physics.OverlapBox(center, half, rot, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (c.transform.IsChildOf(car.root)) continue;
                        if (GameRefs.Player != null && c.transform.IsChildOf(GameRefs.Player.transform)) continue;
                        blocked = true; break;
                    }
                    if (blocked) continue;
                    if (maxStep > 0f) Plugin.Log.LogInfo("Teste: lugar do carro " + ground.Value.point + " em " + ground.Value.collider.name);
                    return Place(ground.Value.point, face, what, movePlayers, maxStep > 0f);
                }
            }
            return "Sem lugar livre perto do " + what + ".";
        }

        /// <summary>Cidade de destino da rota, ou a mais perta se ainda não há rota.</summary>
        private static Hub_CitySpawnC PickHub(Transform car)
        {
            var rg = RouteGeneratorC.Global;
            if (rg != null && rg.spawnedHub != null)
            {
                var h = rg.spawnedHub.GetComponent<Hub_CitySpawnC>();
                if (h != null) return h;
            }
            Hub_CitySpawnC best = null; float bd = float.MaxValue;
            foreach (var h in UnityEngine.Object.FindObjectsOfType<Hub_CitySpawnC>())
            {
                float d = (h.transform.position - car.position).sqrMagnitude;
                if (d < bd) { bd = d; best = h; }
            }
            return best;
        }

        private static string Place(Vector3 spot, Vector3 forward, string label, bool movePlayers = true, bool spotIsGround = false)
        {
            var car = GameRefs.Car;
            var body = GameRefs.CarBody;
            // chão: primeiro objeto de cima para baixo que não seja o próprio carro nem jogador
            var from = spot + Vector3.up * (spotIsGround ? 0.5f : 300f);
            float y = spot.y;
            var hits = Physics.RaycastAll(from, Vector3.down, spotIsGround ? 80f : 600f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.transform.IsChildOf(car.root)) continue;
                if (GameRefs.Player != null && h.transform.IsChildOf(GameRefs.Player.transform)) continue;
                y = h.point.y;
                break;
            }
            if (movingPlayer)
            {
                var me = GameRefs.Player;
                var target = new Vector3(spot.x, y + 1.5f, spot.z);
                me.transform.position = target;
                var mrb = me.GetComponent<Rigidbody>();
                if (mrb != null) { mrb.velocity = Vector3.zero; if (!mrb.isKinematic) mrb.position = target; }
                return "Você foi até " + label + ".";
            }
            // mesma altura do chão que o carro tem agora (o pivô do carro não fica no chão)
            float clearance = 1.2f, gy;
            if (GroundBelow(car.position + Vector3.up * 3f, 10f, out gy))
            {
                float c = car.position.y - gy;
                if (c > 0.2f && c < 3f) clearance = c;
            }
            var pos = new Vector3(spot.x, y + clearance + 0.15f, spot.z);
            forward.y = 0f;
            var rot = forward.sqrMagnitude > 0.01f ? Quaternion.LookRotation(forward.normalized, Vector3.up) : car.rotation;

            var player = GameRefs.Player;
            bool hostInCar = player != null && player.transform.IsChildOf(car.root);
            if (body != null) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.position = pos; body.rotation = rot; }
            car.position = pos;
            car.rotation = rot;
            if (Instance != null && body != null) Instance.StartCoroutine(Settle(car, body));

            if (!movePlayers) return "Carro levado até " + label + ".";
            var side = rot * Vector3.left * 3f;
            if (!hostInCar && player != null) player.transform.position = pos + side + Vector3.up * 0.5f;
            // tio a pé: vai para o outro lado do carro
            var net = CoopNet.Instance;
            if (net != null && net.Connected)
            {
                var uncleSpot = pos - side + Vector3.up * 0.5f;
                net.SendReliable(Msg.DevTeleport, w => NetIO.Put(w, uncleSpot));
            }
            return "Carro levado até " + label + ".";
        }

        /// <summary>Segura o carro parado meio segundo depois de mover (sem isso a física joga ele longe).</summary>
        private static System.Collections.IEnumerator Settle(Transform car, Rigidbody body)
        {
            bool wasKinematic = body.isKinematic;
            var all = car.root.GetComponentsInChildren<Rigidbody>();
            body.isKinematic = true;
            float until = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < until)
            {
                foreach (var rb in all) if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                yield return null;
            }
            if (body != null)
            {
                body.isKinematic = wasKinematic;
                if (!body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
        }

        /// <summary>Tio: o host teleportou o carro; se o tio estiver a pé, vai junto.</summary>
        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type == Msg.DevRequest)
            {
                int id = r.ReadByte();
                var n = CoopNet.Instance;
                if (n != null && n.Mode == CoopMode.Host && Plugin.DevKeys)
                {
                    if (id == 14) { var at = NetIO.GetVector3(r); Do(() => "(pedido do tio) " + CarTo(at, "o tio")); }
                    else Do(() => "(pedido do tio) " + Run(id));
                }
                return;
            }
            if (type != Msg.DevTeleport) return;
            var spot = NetIO.GetVector3(r);
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Client) return;
            if (UncleSync.ClientSeatedByCoop) return; // sentado: vai com o carro
            var player = GameRefs.Player;
            if (player == null) return;
            player.transform.position = spot;
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null) rb.velocity = Vector3.zero;
            Plugin.Log.LogInfo("Teste: o host teleportou o carro; fui junto.");
        }

        // ================= dinheiro e hora =================

        private static DirectorC Director
        {
            get { var d = GameObject.FindWithTag("Director"); return d != null ? d.GetComponent<DirectorC>() : null; }
        }

        private static string AddMoney(float amount)
        {
            var d = Director;
            var wallet = d != null && d.wallet != null ? d.wallet.GetComponent<WalletC>() : null;
            if (wallet == null) return "Carteira não achada.";
            wallet.TotalWealth += amount;
            d.totalWealth += amount;
            wallet.UpdateWealth();
            return "+" + amount + " de dinheiro (total " + wallet.TotalWealth + ").";
        }

        private static DNC_DayNight DayNight
        {
            get { var d = Director; return d != null && d.dayNightCycle != null ? d.dayNightCycle.GetComponent<DNC_DayNight>() : null; }
        }

        private static string AddHours(float h)
        {
            var dn = DayNight;
            if (dn == null) return "Relógio não achado.";
            return SetHour(dn.timeInHours + h);
        }

        private static string SetHour(float hour)
        {
            var dn = DayNight;
            if (dn == null) return "Relógio não achado.";
            hour = Mathf.Clamp(hour, 0f, 22.5f); // 23h o jogo faz o jogador desmaiar
            dn._timeInSeconds = hour * 3600f;
            dn.timeInHours = hour;
            Traverse.Create(dn).Field("_currentTransition").SetValue(null); // recalcula luz/céu
            return "Hora: " + Mathf.FloorToInt(hour) + ":" + Mathf.FloorToInt((hour % 1f) * 60f).ToString("00") + (dn.isPaused ? " (relógio do jogo pausado)" : "");
        }
    }
}
