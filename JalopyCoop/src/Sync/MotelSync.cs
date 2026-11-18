using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4g: motel e dormir.
    ///  - Cama (BedLogicC.Trigger): no co-op, clicar na cama só DEITA (a câmera vai para a cama, como no jogo) e avisa o
    ///    outro. Deitado, um clique levanta. O dia só passa quando os dois estão deitados: o host manda "dormir" e cada
    ///    lado roda a passagem de dia do próprio jogo (BedLogicC.Trigger original). O sorteio fixo faz o dia novo sair
    ///    igual; só o host salva (o save do tio é bloqueado).
    ///  - Recepção (MotelLogicC.Payed): quem paga manda; o outro marca pago e recebe a chave igual (sem cobrar de novo:
    ///    o dinheiro vai pelo ShopSync). O quarto já é o mesmo (sorteio fixo).
    ///  - A porta do quarto destrancada com a chave vai pelo EnvDoorSync.
    /// </summary>
    public class MotelSync : MonoBehaviour
    {
        public static MotelSync Instance { get; private set; }

        /// <summary>true: deixa o BedLogicC.Trigger original rodar (a passagem de dia de verdade).</summary>
        internal static bool AllowOriginal;

        public bool LocalLying { get; private set; }
        public bool RemoteLying { get; private set; }
        public bool Sleeping { get; private set; }
        /// <summary>Cama em que o OUTRO jogador está deitado (null = ele está de pé). Os bonecos usam isso.</summary>
        public BedLogicC RemoteBed { get; private set; }
        /// <summary>O outro deitou na SEGUNDA cama do quarto (a "cama do tio"), não na cama principal.</summary>
        public bool RemoteSecond { get; private set; }
        private BedLogicC localBed;
        private float lieTime;
        private Vector3 camLocalPos;
        private Quaternion camLocalRot;

        private void Start()
        {
            Instance = this;
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { RemoteLying = false; RemoteBed = null; if (!c && LocalLying) GetUp(); };
        }

        internal static bool CoopActive
        {
            get { var n = CoopNet.Instance; return n != null && n.Connected; }
        }

        // ================= deitar / levantar =================

        internal IEnumerator LieDown(BedLogicC bed) { return LieDown(bed, false); }

        internal IEnumerator LieDown(BedLogicC bed, bool second)
        {
            if (LocalLying || Sleeping) yield break;
            var twin = second ? TwinOf(bed) : null;
            if (second && twin == null) yield break;
            LocalLying = true;
            localBed = bed;
            lieTime = Time.realtimeSinceStartup;
            var cam = Camera.main;
            camLocalPos = cam.transform.localPosition;
            camLocalRot = cam.transform.localRotation;
            try { MainMenuC.Global.ReticuleToggle(); } catch { }
            MainMenuC.Global.DisableLookAndWalk();
            iTween.MoveTo(cam.gameObject, iTween.Hash("position", HeadSpot(bed, second), "time", 1.5, "islocal", false, "easetype", "easeinoutexpo"));
            iTween.RotateTo(cam.gameObject, iTween.Hash("rotation", HeadRot(bed, second).eulerAngles, "delay", 0.25f, "time", 2f, "islocal", false, "easetype", "easeinoutexpo"));
            var pos = bed.transform.position;
            CoopNet.Instance.SendReliable(Msg.BedState, w => { w.Write(true); NetIO.Put(w, pos); w.Write(second); });
            Plugin.Log.LogInfo("Motel: deitei (" + bed.name + (second ? ", segunda cama" : "") + "). Esperando o outro jogador.");
            CheckBothLying();
        }

        private void GetUp()
        {
            if (!LocalLying || Sleeping) return;
            LocalLying = false;
            var cam = Camera.main;
            if (cam != null)
            {
                iTween.Stop(cam.gameObject);
                iTween.MoveTo(cam.gameObject, iTween.Hash("position", camLocalPos, "time", 1.0, "islocal", true, "easetype", "easeinoutexpo"));
                iTween.RotateTo(cam.gameObject, iTween.Hash("rotation", camLocalRot.eulerAngles, "time", 1.0, "islocal", true, "easetype", "easeinoutexpo"));
            }
            MainMenuC.Global.EnableLookAndWalk();
            try { MainMenuC.Global.ReticuleToggle(); } catch { }
            if (CoopActive) CoopNet.Instance.SendReliable(Msg.BedState, w => { w.Write(false); NetIO.Put(w, Vector3.zero); w.Write(false); });
            Plugin.Log.LogInfo("Motel: levantei.");
        }

        /// <summary>Teste automático: levantar como se tivesse clicado.</summary>
        internal void TestGetUp() { GetUp(); }

        // ================= segunda cama (a "cama do tio") =================
        // O quarto tem a cama do jogador (BedLogicC, com o ponto da cabeça path[1]) e a cama do tio (mesmo modelo,
        // sem clique). A segunda cama ganha um ponto de clique; deitar nela usa a mesma posição da cabeça, levada da
        // cama principal para a segunda (mesmo modelo = mesma forma).

        private static readonly System.Collections.Generic.Dictionary<BedLogicC, Transform> twins = new System.Collections.Generic.Dictionary<BedLogicC, Transform>();

        /// <summary>A outra cama do quarto: objeto com o mesmo modelo (malha) da cama principal, a até 8 m.</summary>
        internal static Transform TwinOf(BedLogicC bed)
        {
            if (bed == null) return null;
            Transform t;
            if (twins.TryGetValue(bed, out t) && t != null) return t;
            t = null;
            var mf = bed.GetComponent<MeshFilter>();
            var names = new System.Collections.Generic.HashSet<string>();
            if (mf != null && mf.sharedMesh != null) names.Add(mf.sharedMesh.name);
            if (bed.messyBed != null) names.Add(bed.messyBed.name);
            float bd = 64f;
            foreach (var f in FindObjectsOfType<MeshFilter>())
            {
                if (f.gameObject == bed.gameObject || f.sharedMesh == null || !names.Contains(f.sharedMesh.name)) continue;
                if (f.GetComponent<BedLogicC>() != null) continue; // cama de jogador de outro quarto
                float d = (f.transform.position - bed.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; t = f.transform; }
            }
            if (!twins.ContainsKey(bed))
                Plugin.Log.LogInfo("Motel: segunda cama de " + bed.name + " = " + (t != null ? t.name + " a " + Mathf.Sqrt(bd).ToString("0.0") + " m" : "nenhuma") + ".");
            twins[bed] = t;
            return t;
        }

        internal static Vector3 HeadSpot(BedLogicC bed, bool second)
        {
            var p = bed.path[1].position;
            var twin = second ? TwinOf(bed) : null;
            return twin != null ? twin.TransformPoint(bed.transform.InverseTransformPoint(p)) : p;
        }

        internal static Quaternion HeadRot(BedLogicC bed, bool second)
        {
            var r = bed.path[1].rotation;
            var twin = second ? TwinOf(bed) : null;
            return twin != null ? twin.rotation * Quaternion.Inverse(bed.transform.rotation) * r : r;
        }

        /// <summary>Centro da cama (para onde os pés apontam).</summary>
        internal static Vector3 BedCenter(BedLogicC bed, bool second)
        {
            var twin = second ? TwinOf(bed) : null;
            return twin != null ? twin.position : bed.transform.position;
        }

        private float twinTimer;
        private readonly System.Collections.Generic.HashSet<BedLogicC> twinDone = new System.Collections.Generic.HashSet<BedLogicC>();

        /// <summary>Põe o ponto de clique na segunda cama de cada quarto (uma vez por cama).</summary>
        private void AddSecondBeds()
        {
            twinTimer += Time.unscaledDeltaTime;
            if (twinTimer < 2f) return;
            twinTimer = 0f;
            foreach (var bed in FindObjectsOfType<BedLogicC>())
            {
                if (twinDone.Contains(bed)) continue;
                twinDone.Add(bed);
                if (bed.path == null || bed.path.Length < 2 || bed.path[1] == null) continue;
                var twin = TwinOf(bed);
                if (twin == null || twin.Find("JalopyCoop_SecondBed") != null) continue;
                var go = new GameObject("JalopyCoop_SecondBed");
                go.layer = bed.gameObject.layer;
                try { go.tag = bed.gameObject.tag; } catch { }
                go.transform.SetParent(twin, false);
                var src = bed.GetComponent<BoxCollider>();
                var col = go.AddComponent<BoxCollider>();
                if (src != null) { col.center = src.center; col.size = src.size; col.isTrigger = src.isTrigger; }
                else
                {
                    var tm = twin.GetComponent<MeshFilter>();
                    if (tm != null && tm.sharedMesh != null) { col.center = tm.sharedMesh.bounds.center; col.size = tm.sharedMesh.bounds.size; }
                    col.isTrigger = true;
                }
                go.AddComponent<CoopSecondBed>().Main = bed;
                Plugin.Log.LogInfo("Motel: dá para deitar também na segunda cama (" + twin.name + ", camada " + LayerMask.LayerToName(go.layer) + ", tag " + go.tag + ").");
            }
        }

        private static BedLogicC BedAt(Vector3 pos)
        {
            BedLogicC best = null; float bd = 9f;
            foreach (var b in FindObjectsOfType<BedLogicC>())
            {
                float d = (b.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        private float uncleTimer;

        /// <summary>
        /// O tio "de verdade" (NPC) não age sozinho no co-op: o tio é o jogador. Igual ao "não" do começo do jogo no
        /// efeito (ele não fala, não anda sozinho pelo motel), mas sem o uncleAtHome do jogo, que tiraria o corpo do
        /// tio da cena (no host esse corpo é o boneco do jogador-tio). O host já liga o debugStopUncleWorking; aqui
        /// vale para os dois lados e ainda solta o tio de destinos do motel que o jogo dá por fora (porta do quarto).
        /// </summary>
        private void KeepRealUncleOff()
        {
            uncleTimer += Time.unscaledDeltaTime;
            if (uncleTimer < 0.5f) return;
            uncleTimer = 0f;
            var uncle = GameRefs.Uncle;
            var logic = uncle != null ? uncle.GetComponent<UncleLogicC>() : null;
            if (logic == null) return;
            logic.debugStopUncleWorking = true;
            logic.restrictTalk = true;
            var ai = uncle.GetComponent("AIPath");
            if (ai == null) return;
            var tr = Traverse.Create(ai).Field("target");
            var target = tr.GetValue<Transform>();
            if (target == null) return;
            foreach (var m in FindObjectsOfType<MotelLogicC>())
            {
                bool motelNode = target == m.lobbyNode;
                if (m.roomNodes != null) foreach (var n in m.roomNodes) if (n == target) motelNode = true;
                if (m.doorNodes != null) foreach (var n in m.doorNodes) if (n == target) motelNode = true;
                if (motelNode) { tr.SetValue(null); Plugin.Log.LogInfo("Motel: o tio-NPC não anda sozinho no co-op."); return; }
            }
        }

        private void Update()
        {
            if (CoopActive && GameRefs.InGame) { KeepRealUncleOff(); AddSecondBeds(); }
            if (!LocalLying || Sleeping) return;
            // um clique (ou a tecla de usar) levanta; espera a câmera chegar na cama primeiro
            if (Time.realtimeSinceStartup - lieTime < 2.5f) return;
            bool click = Input.GetButtonDown("Fire1");
            try { click = click || Input.GetKeyDown(MainMenuC.Global.assignedInputStrings[16]) || Input.GetKeyDown(MainMenuC.Global.assignedInputStrings[17]); } catch { }
            if (click) GetUp();
        }

        private void OnGUI()
        {
            if (!LocalLying || Sleeping) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = Color.white;
            string text = RemoteLying ? "Os dois deitados..." : "Esperando o outro jogador deitar.\nClique para levantar.";
            GUI.Label(new Rect(0, Screen.height * 0.75f, Screen.width, 60), text, style);
        }

        // ================= os dois deitados: dormir =================

        private void CheckBothLying()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host || Sleeping) return;
            if (!LocalLying || !RemoteLying) return;
            Plugin.Log.LogInfo("Motel: os dois deitados, passando o dia.");
            net.SendReliable(Msg.DayPass);
            Sleep();
        }

        private void Sleep()
        {
            if (Sleeping) return;
            var bed = localBed;
            if (bed == null) { Plugin.Log.LogWarning("Motel: sem cama para dormir."); return; }
            Sleeping = true;
            LocalLying = false; RemoteLying = false; RemoteBed = null;
            try { MainMenuC.Global.ReticuleToggle(); } catch { } // o Trigger original liga/desliga a mira de novo
            MainMenuC.Global.EnableLookAndWalk(); // o original desliga de novo logo no começo
            AllowOriginal = true;
            try { bed.StartCoroutine("Trigger"); }
            finally { AllowOriginal = false; }
            StartCoroutine(SleepDone());
        }

        private IEnumerator SleepDone()
        {
            // a passagem de dia do jogo dura uns 17 s (olhos fechados, dia novo, acordar)
            yield return new WaitForSeconds(20f);
            Sleeping = false;
            Plugin.Log.LogInfo("Motel: dia novo.");
            try { InteractSync.OpenUncleBriefcaseForMorning(); }
            catch (Exception e) { Plugin.Log.LogError("Carta: " + e); }
        }

        // ================= recepção (pagar o quarto) =================

        internal static void LocalPaid(MotelLogicC motel)
        {
            if (applyingPaid || !CoopActive) return;
            var pos = motel.transform.position;
            CoopNet.Instance.SendReliable(Msg.MotelPaid, w => NetIO.Put(w, pos));
            Plugin.Log.LogInfo("Motel: paguei o quarto " + motel.roomNumber + ".");
        }

        private static bool applyingPaid;

        private static void ApplyPaid(Vector3 pos)
        {
            MotelLogicC motel = null; float bd = 25f;
            foreach (var m in FindObjectsOfType<MotelLogicC>())
            {
                float d = (m.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; motel = m; }
            }
            if (motel == null) { Plugin.Log.LogWarning("Motel: recepção sem par aqui."); return; }
            if (motel.hasPaid) return;
            // como o Payed, sem cobrar
            motel.hasPaid = true;
            var col = motel.GetComponent<BoxCollider>();
            if (col != null) col.enabled = false;
            if (motel.shopKeeper != null) motel.shopKeeper.GetComponent<Animator>().SetTrigger("GiveKey");
            var give = AccessTools.Method(typeof(MotelLogicC), "GiveRoomKey");
            if (give != null) motel.StartCoroutine((IEnumerator)give.Invoke(motel, null));
            Plugin.Log.LogInfo("Motel: o outro jogador pagou o quarto " + motel.roomNumber + ".");
        }

        // ================= bater na porta (dinheiro por baixo da porta) =================
        // Quais quartos dão dinheiro já é igual nos dois (MotelLogicC.SetGuests usa o sorteio fixo). Cada porta dá
        // dinheiro uma vez só: quem bateu primeiro avisa, o outro marca a porta como "já deu" e mostra a mesma nota.
        // Pegar a nota (LooseCashC.PickUp) soma na carteira (ShopSync manda ao outro) e o outro apaga a nota dele.

        internal static bool applyingCash;

        internal static void LocalDoorCash(DoorKnockingC door)
        {
            if (applyingCash || !CoopActive) return;
            var pos = door.transform.position;
            CoopNet.Instance.SendReliable(Msg.DoorCash, w => NetIO.Put(w, pos));
            Plugin.Log.LogInfo("Motel: bati em " + door.name + " e ganhei dinheiro por baixo da porta.");
        }

        private static void ApplyDoorCash(Vector3 pos)
        {
            DoorKnockingC door = null; float bd = 4f;
            foreach (var d in FindObjectsOfType<DoorKnockingC>())
            {
                float dist = (d.transform.position - pos).sqrMagnitude;
                if (dist < bd) { bd = dist; door = d; }
            }
            if (door == null) { Plugin.Log.LogWarning("Motel: porta do dinheiro sem par aqui."); return; }
            var given = Traverse.Create(door).Field("hasGivenMoney");
            if (given.GetValue<bool>()) return; // já deu aqui (os dois bateram juntos)
            given.SetValue(true);
            if (Traverse.Create(door).Field("director").GetValue<GameObject>() == null)
                Traverse.Create(door).Field("director").SetValue(GameObject.FindWithTag("Director"));
            applyingCash = true;
            try { door.SlideCashUnderDoor(); }
            finally { applyingCash = false; }
            Plugin.Log.LogInfo("Motel: o outro jogador bateu em " + door.name + " e ganhou dinheiro (a nota aparece aqui também).");
        }

        internal static void LocalCashPicked(LooseCashC cash)
        {
            if (applyingCash || !CoopActive) return;
            var pos = cash.transform.position;
            CoopNet.Instance.SendReliable(Msg.CashPicked, w => NetIO.Put(w, pos));
            Plugin.Log.LogInfo("Dinheiro solto: peguei " + cash.cashValue + ".");
        }

        private static void ApplyCashPicked(Vector3 pos)
        {
            LooseCashC best = null; float bd = 2.25f;
            foreach (var c in FindObjectsOfType<LooseCashC>())
            {
                float d = (c.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = c; }
            }
            if (best == null) return;
            Destroy(best.gameObject); // sem somar: o dinheiro já veio pela carteira
            Plugin.Log.LogInfo("Dinheiro solto: o outro jogador pegou " + best.cashValue + ".");
        }

        // ================= botão de teste: resetar o motel =================

        /// <summary>
        /// TESTE (F4): deixa o motel mais perto como antes de pagar: quarto sem pagar, sem chave, porta do quarto
        /// trancada, cama clicável, portas de hóspede sem ter dado dinheiro (e pelo menos uma que dá). Roda igual nos
        /// dois (quem clica manda MotelReset para o outro).
        /// </summary>
        internal static string ResetMotelForTest(bool send)
        {
            var player = GameRefs.Player;
            MotelLogicC m = null; float bd = float.MaxValue;
            foreach (var x in FindObjectsOfType<MotelLogicC>())
            {
                float d = player != null ? (x.transform.position - player.transform.position).sqrMagnitude : 0f;
                if (d < bd) { bd = d; m = x; }
            }
            if (m == null) return "Nenhum motel aqui.";
            m.hasPaid = false;
            m.hasSlept = false;
            var col = m.GetComponent<BoxCollider>();
            if (col != null) col.enabled = true;
            var key = Traverse.Create(m).Field("spawnedRoomKey").GetValue<GameObject>();
            if (key != null) Destroy(key);
            int room = m.roomNumber;
            if (m.roomDoors != null && room >= 0 && room < m.roomDoors.Length && m.roomDoors[room] != null)
            {
                var door = m.roomDoors[room].GetComponent<EnvironmentDoorsC>();
                if (door != null)
                {
                    if (door.open) { door.isLocked = false; door.StartCoroutine(door.Trigger()); }
                    door.isLocked = true;
                }
            }
            if (m.roomBeds != null && room >= 0 && room < m.roomBeds.Length && m.roomBeds[room] != null)
            {
                var bed = m.roomBeds[room];
                if (bed.GetComponent<BoxCollider>() == null) bed.AddComponent<BoxCollider>();
                var bl = bed.GetComponent<BedLogicC>();
                if (bl != null) Traverse.Create(bl).Field("block").SetValue(false);
            }
            int shady = 0; DoorKnockingC firstOther = null;
            if (m.roomDoors != null)
                for (int i = 0; i < m.roomDoors.Length; i++)
                {
                    if (m.roomDoors[i] == null) continue;
                    var p = m.roomDoors[i].transform.parent != null ? m.roomDoors[i].transform.parent.parent : null;
                    var k = p != null ? p.GetComponent<DoorKnockingC>() : null;
                    if (k == null) continue;
                    Traverse.Create(k).Field("hasGivenMoney").SetValue(false);
                    if (i == room) continue;
                    if (k.personalityType == 1) shady++;
                    if (firstOther == null) firstOther = k;
                }
            if (shady == 0 && firstOther != null) { firstOther.SetDoorToShady(); shady = 1; }
            if (send && CoopActive) CoopNet.Instance.SendReliable(Msg.MotelReset);
            Plugin.Log.LogInfo("Teste: motel resetado (quarto " + room + ", " + shady + " porta(s) que dão dinheiro).");
            return "Motel resetado: quarto " + (room + 1) + " sem pagar, " + shady + " porta(s) dão dinheiro.";
        }

        // ================= rede =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            try
            {
                switch (type)
                {
                    case Msg.BedState:
                    {
                        bool lying = r.ReadBoolean();
                        var pos = NetIO.GetVector3(r);
                        bool second = r.ReadBoolean();
                        RemoteSecond = lying && second;
                        RemoteLying = lying;
                        RemoteBed = lying ? BedAt(pos) : null;
                        Plugin.Log.LogInfo("Motel: o outro jogador " + (lying ? "deitou." : "levantou."));
                        CheckBothLying();
                        break;
                    }
                    case Msg.DayPass:
                        if (CoopNet.Instance.Mode != CoopMode.Client) return;
                        if (localBed == null) { Plugin.Log.LogWarning("Motel: o host mandou dormir mas não deitei aqui."); return; }
                        Plugin.Log.LogInfo("Motel: o host mandou dormir.");
                        Sleep();
                        break;
                    case Msg.MotelReset:
                        ResetMotelForTest(false);
                        break;
                    case Msg.DoorCash:
                        ApplyDoorCash(NetIO.GetVector3(r));
                        break;
                    case Msg.CashPicked:
                        ApplyCashPicked(NetIO.GetVector3(r));
                        break;
                    case Msg.MotelPaid:
                    {
                        var pos = NetIO.GetVector3(r);
                        applyingPaid = true;
                        try { ApplyPaid(pos); }
                        finally { applyingPaid = false; }
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("MotelSync " + type + ": " + e); }
        }
    }

    /// <summary>Ponto de clique da segunda cama: o clique do jogo (SendMessage "Trigger") deita nela.</summary>
    public class CoopSecondBed : MonoBehaviour
    {
        public BedLogicC Main;

        public void Trigger()
        {
            if (Main == null || MotelSync.Instance == null || !MotelSync.CoopActive) return;
            MotelSync.Instance.StartCoroutine(MotelSync.Instance.LieDown(Main, true));
        }
    }

    /// <summary>O tio-NPC não faz as rotinas do motel sozinho no co-op (o tio é o jogador).</summary>
    [HarmonyPatch]
    internal static class UncleMotelRoutinesPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var n in new[] { "UncleToMotelLobby", "UncleWalkingToRoom", "MotelSatOnBed" })
            {
                var m = AccessTools.Method(typeof(UncleLogicC), n);
                if (m != null) yield return m;
            }
        }

        private static bool Prefix(ref IEnumerator __result)
        {
            if (!MotelSync.CoopActive) return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty() { yield break; }
    }

    /// <summary>A cama do tio puxava o boneco do jogador-tio quando ele passava perto (no co-op ele deita pela cama do quarto).</summary>
    [HarmonyPatch(typeof(UncleBedTriggerC), "OnTriggerEnter")]
    internal static class UncleBedTriggerPatch
    {
        private static bool Prefix() { return !MotelSync.CoopActive; }
    }

    /// <summary>Clicar na cama no co-op: deita e espera (o dia só passa com os dois deitados).</summary>
    [HarmonyPatch(typeof(BedLogicC), "Trigger")]
    internal static class BedTriggerPatch
    {
        private static bool Prefix(BedLogicC __instance, ref IEnumerator __result)
        {
            if (MotelSync.AllowOriginal || !MotelSync.CoopActive || MotelSync.Instance == null) return true;
            __result = MotelSync.Instance.LieDown(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(DoorKnockingC), "SlideCashUnderDoor")]
    internal static class DoorCashPatch
    {
        private static void Postfix(DoorKnockingC __instance)
        {
            try { MotelSync.LocalDoorCash(__instance); }
            catch (Exception e) { Plugin.Log.LogError("MotelSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(LooseCashC), "PickUp")]
    internal static class LooseCashPickPatch
    {
        private static void Prefix(LooseCashC __instance)
        {
            try { MotelSync.LocalCashPicked(__instance); }
            catch (Exception e) { Plugin.Log.LogError("MotelSync: " + e); }
        }
    }

    [HarmonyPatch(typeof(MotelLogicC), "Payed")]
    internal static class MotelPayedPatch
    {
        private static void Postfix(MotelLogicC __instance)
        {
            try { MotelSync.LocalPaid(__instance); }
            catch (Exception e) { Plugin.Log.LogError("MotelSync: " + e); }
        }
    }
}
