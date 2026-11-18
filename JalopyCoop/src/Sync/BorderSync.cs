using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4h: fronteira com o HOST decidindo (BorderLogicC).
    /// No jogo: o carro entra na área → guarda fala → janela abre → alguém entrega o passaporte (PassportStuff) na
    /// janela → o guarda lê e SORTEIA revista → guarda vai ao porta-malas, tira o proibido/roubado → multa paga com a
    /// carteira na janela → a cancela abre. As falas esperam clique, então se os dois rodassem isso sozinhos a
    /// revista sairia diferente e o porta-malas abriria duas vezes. Por isso:
    ///  - Só o host roda a fronteira. Ele manda o estado (janela, pronto p/ passaporte/carteira, cancela, passado) e
    ///    cada fala do guarda; o porta-malas e os itens tirados já vão pelo DoorSync/ItemSync; a multa pelo ShopSync.
    ///  - O tio vê a mesma janela, cancela e falas. Se ele entregar o passaporte ou a carteira na janela, o pedido vai
    ///    para o host, que faz a entrega com as coisas dele.
    /// </summary>
    public class BorderSync : MonoBehaviour
    {
        public static BorderSync Instance { get; private set; }

        private const byte ActPapers = 1, ActWallet = 2;

        private class Snap
        {
            public bool papers, wallet, crossed, passportHome;
            public Quaternion window;
            public Vector3 gate;
        }

        private float timer;
        private readonly Dictionary<BorderLogicC, Snap> sent = new Dictionary<BorderLogicC, Snap>();
        // tio: o último estado do host de cada fronteira
        private readonly Dictionary<BorderLogicC, Snap> mirror = new Dictionary<BorderLogicC, Snap>();
        private float sayClearAt = -1f;
        private float lastSkipSent;
        /// <summary>Host: o tio clicou para passar a fala do guarda (vale até este horário).</summary>
        internal static float RemoteSkipUntil = -1f;

        private void Start()
        {
            Instance = this;
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { sent.Clear(); mirror.Clear(); };
        }

        internal static bool IsClient
        {
            get { var n = CoopNet.Instance; return n != null && n.Connected && n.Mode == CoopMode.Client; }
        }

        internal static bool IsHost
        {
            get { var n = CoopNet.Instance; return n != null && n.Connected && n.Mode == CoopMode.Host; }
        }

        private static GameObject Passport(BorderLogicC b)
        {
            var t = Traverse.Create(b).Field("theObject").GetValue<GameObject>();
            if (t != null) return t;
            foreach (var op in FindObjectsOfType<ObjectPickupC>()) if (op.name == "PassportStuff") return op.gameObject;
            return null;
        }

        // ================= host: manda o estado =================

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            if (net.Mode == CoopMode.Client) { ClientUpdate(); return; }
            timer += Time.unscaledDeltaTime;
            if (timer < 0.2f) return;
            timer = 0f;
            try
            {
                foreach (var b in SceneCache.Get<BorderLogicC>())
                {
                    if (b == null) continue;
                    var pass = Traverse.Create(b).Field("theObject").GetValue<GameObject>();
                    var holder = pass != null ? pass.GetComponent<PassportLogicC>() : null;
                    var s = new Snap
                    {
                        papers = b.readyToTakePapers,
                        wallet = b.readyToTakeWallet,
                        crossed = b.borderCrossed,
                        passportHome = holder != null && holder.holdingPosition != null && pass.transform.parent == holder.holdingPosition,
                        window = b.window != null ? b.window.transform.rotation : Quaternion.identity,
                        gate = b.gate != null ? b.gate.transform.localPosition : Vector3.zero,
                    };
                    Snap last;
                    if (sent.TryGetValue(b, out last) && last.papers == s.papers && last.wallet == s.wallet && last.crossed == s.crossed
                        && last.passportHome == s.passportHome && Quaternion.Angle(last.window, s.window) < 0.5f && (last.gate - s.gate).sqrMagnitude < 0.0004f)
                        continue;
                    sent[b] = s;
                    var pos = b.transform.position;
                    net.SendReliable(Msg.BorderState, w =>
                    {
                        NetIO.Put(w, pos);
                        w.Write(s.papers); w.Write(s.wallet); w.Write(s.crossed); w.Write(s.passportHome);
                        w.Write(s.window.x); w.Write(s.window.y); w.Write(s.window.z); w.Write(s.window.w);
                        NetIO.Put(w, s.gate);
                    });
                }
            }
            catch (Exception e) { Plugin.Log.LogError("BorderSync: " + e); }
        }

        /// <summary>Host: uma fala do guarda da fronteira → o tio mostra a mesma.</summary>
        internal static void HostSaid(BorderLogicC b, string text)
        {
            if (!IsHost || string.IsNullOrEmpty(text)) return;
            var pos = b.transform.position;
            CoopNet.Instance.SendReliable(Msg.BorderSay, w => { NetIO.Put(w, pos); w.Write(text); });
        }

        // ================= tio: espelha =================

        private void ClientUpdate()
        {
            float k = 1f - Mathf.Exp(-8f * Time.deltaTime);
            foreach (var kv in mirror)
            {
                var b = kv.Key; var s = kv.Value;
                if (b == null) continue;
                if (b.window != null) b.window.transform.rotation = Quaternion.Slerp(b.window.transform.rotation, s.window, k);
                if (b.gate != null) b.gate.transform.localPosition = Vector3.Lerp(b.gate.transform.localPosition, s.gate, k);
            }
            // tio também passa a fala do guarda: o clique (mesmas teclas do jogo) vai para o host
            if (sayClearAt > 0f && Time.realtimeSinceStartup - lastSkipSent > 0.4f && SkipPressed())
            {
                lastSkipSent = Time.realtimeSinceStartup;
                CoopNet.Instance.SendReliable(Msg.BorderSkip, w => w.Write((byte)1));
                Plugin.Log.LogInfo("Fronteira: cliquei para passar a fala do guarda.");
            }
            if (sayClearAt > 0f && Time.realtimeSinceStartup > sayClearAt)
            {
                sayClearAt = -1f;
                var d = GameObject.FindWithTag("Director");
                var dc = d != null ? d.GetComponent<DirectorC>() : null;
                if (dc != null)
                {
                    dc.dialogueHolder.GetComponent<UILabel>().text = string.Empty;
                    dc.BubbleOff();
                }
                Unlock();
            }
        }

        private static BorderLogicC BorderAt(Vector3 pos)
        {
            BorderLogicC best = null; float bd = 25f;
            foreach (var b in FindObjectsOfType<BorderLogicC>())
            {
                float d = (b.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        private void ApplyState(BorderLogicC b, Snap s)
        {
            Snap last;
            mirror.TryGetValue(b, out last);
            mirror[b] = s;
            if (b.window != null) iTween.Stop(b.window);
            if (b.gate != null) iTween.Stop(b.gate);
            b.readyToTakePapers = s.papers;
            b.readyToTakeWallet = s.wallet;
            if (b.windowRelay != null && b.windowRelay.GetComponent<Collider>() != null)
                b.windowRelay.GetComponent<Collider>().enabled = s.papers || s.wallet;
            if (s.crossed && !b.borderCrossed)
            {
                b.borderCrossed = true;
                var rg = RouteGeneratorC.Global;
                if (rg != null) rg.borderOpen = false;
                Plugin.Log.LogInfo("Fronteira: a cancela abriu (host).");
            }
            // passaporte de volta ao lugar dele (o host fez isso ao abrir a cancela ou ao esperar a multa)
            if (s.passportHome && (last == null || !last.passportHome))
            {
                var pass = Passport(b);
                var holder = pass != null ? pass.GetComponent<PassportLogicC>() : null;
                if (holder != null && holder.holdingPosition != null)
                {
                    iTween.Stop(pass);
                    pass.transform.parent = holder.holdingPosition;
                    pass.transform.localPosition = Vector3.zero;
                    pass.transform.localEulerAngles = Vector3.zero;
                    var col = pass.GetComponent<Collider>();
                    if (col != null) col.enabled = true;
                    pass.layer = 8;
                }
            }
        }

        private void ShowSay(BorderLogicC b, string text)
        {
            var d = GameObject.FindWithTag("Director");
            var dc = d != null ? d.GetComponent<DirectorC>() : null;
            if (dc == null) return;
            var mouth = b.shopCharacterMouth != null ? b.shopCharacterMouth.transform : b.transform;
            dc.BubbleOn(mouth, Traverse.Create(b).Field("characterName").GetValue<string>() ?? "ui_name_05");
            var ds = dc.dialogueHolder.GetComponent<DialogueStuffsC>();
            ds.word = text;
            ds.StartTypeText(b.gameObject);
            // StartTypeText trava o jogador (DragRigidbodyC.inputRestrict) até a fala do guarda terminar; quem destrava
            // é a corrotina do BorderLogicC, que no tio não roda (as falas avançam pelo clique do host). Sem isto o
            // tio ficava sem mira, sem abrir o vidro nem pegar o passaporte.
            Unlock();
            sayClearAt = Time.realtimeSinceStartup + 2.5f + text.Length * 0.06f;
        }

        private static bool SkipPressed()
        {
            var keys = MainMenuC.Global != null ? MainMenuC.Global.assignedInputStrings : null;
            if (Input.GetButtonDown("Fire1")) return true;
            return keys != null && keys.Length > 17 && (Input.GetKeyDown(keys[16]) || Input.GetKeyDown(keys[17]));
        }

        private static void Unlock()
        {
            var cam = Camera.main;
            var drag = cam != null ? cam.GetComponent<DragRigidbodyC>() : null;
            if (drag != null && drag.inputRestrict) drag.inputRestrict = false;
        }

        // ================= tio: entregar passaporte / carteira =================

        /// <summary>Tio clicou na janela com o passaporte ou a carteira na mão.</summary>
        internal static bool ClientAction(BorderLogicC b)
        {
            var cam = Camera.main;
            var drag = cam != null ? cam.GetComponent<DragRigidbodyC>() : null;
            var held = drag != null ? drag.isHolding1 : null;
            if (held == null) return false;
            var pos = b.transform.position;
            if (b.readyToTakePapers && held.name == "PassportStuff")
            {
                // o passaporte sai da mão e vai até o guarda (só a imagem; quem decide é o host)
                held.transform.parent = null;
                if (b.windowRelay != null) b.windowRelay.GetComponent<Collider>().enabled = false;
                iTween.MoveTo(held, iTween.Hash("position", b.path[1], "time", 1.0, "islocal", false, "easetype", "easeinoutexpo"));
                held.layer = LayerMask.NameToLayer("Default");
                foreach (var t in held.GetComponentsInChildren<Transform>()) t.gameObject.layer = LayerMask.NameToLayer("Default");
                drag.isHolding1 = null;
                b.readyToTakePapers = false;
                CoopNet.Instance.SendReliable(Msg.BorderAct, w => { NetIO.Put(w, pos); w.Write(ActPapers); });
                Plugin.Log.LogInfo("Fronteira: entreguei o passaporte (o host decide).");
                return true;
            }
            if (b.readyToTakeWallet && held.name == "Wallet")
            {
                b.readyToTakeWallet = false;
                CoopNet.Instance.SendReliable(Msg.BorderAct, w => { NetIO.Put(w, pos); w.Write(ActWallet); });
                Plugin.Log.LogInfo("Fronteira: paguei a multa (o host cobra).");
                return true;
            }
            return false;
        }

        /// <summary>Host: faz a entrega pedida pelo tio, com o passaporte/carteira do host.</summary>
        private static void HostAct(BorderLogicC b, byte act)
        {
            if (act == ActPapers && b.readyToTakePapers)
            {
                var pass = Passport(b);
                if (pass == null) { Plugin.Log.LogWarning("Fronteira: passaporte não achado no host."); return; }
                Traverse.Create(b).Field("theObject").SetValue(pass);
                pass.transform.parent = null;
                if (b.windowRelay != null) b.windowRelay.GetComponent<Collider>().enabled = false;
                var op = pass.GetComponent<ObjectPickupC>();
                iTween.MoveTo(pass, iTween.Hash("position", b.path[1], "time", 1.0, "islocal", false, "easetype", "easeinoutexpo", "oncomplete", "ReadThroughPapers", "oncompletetarget", b.gameObject));
                iTween.RotateTo(pass, iTween.Hash("rotation", b.path[0].eulerAngles + (op != null ? op.inventoryAdjustRotation : Vector3.zero), "time", 0.5, "islocal", false, "easetype", "easeinoutexpo"));
                pass.layer = LayerMask.NameToLayer("Default");
                foreach (var t in pass.GetComponentsInChildren<Transform>()) t.gameObject.layer = LayerMask.NameToLayer("Default");
                var drag = Camera.main != null ? Camera.main.GetComponent<DragRigidbodyC>() : null;
                if (drag != null && drag.isHolding1 == pass) drag.isHolding1 = null;
                Plugin.Log.LogInfo("Fronteira: o tio entregou o passaporte.");
            }
            else if (act == ActWallet && b.readyToTakeWallet)
            {
                var d = GameObject.FindWithTag("Director");
                var wallet = d != null ? d.GetComponent<DirectorC>().wallet : null;
                if (wallet == null) return;
                Traverse.Create(b).Field("walletObj").SetValue(wallet);
                b.FinePaid();
                Plugin.Log.LogInfo("Fronteira: o tio pagou a multa.");
            }
        }

        // ================= rede =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            try
            {
                switch (type)
                {
                    case Msg.BorderState:
                    {
                        var pos = NetIO.GetVector3(r);
                        var s = new Snap { papers = r.ReadBoolean(), wallet = r.ReadBoolean(), crossed = r.ReadBoolean(), passportHome = r.ReadBoolean() };
                        s.window = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        s.gate = NetIO.GetVector3(r);
                        if (!IsClient) return;
                        var b = BorderAt(pos);
                        if (b == null) { Plugin.Log.LogWarning("Fronteira: sem par aqui."); return; }
                        ApplyState(b, s);
                        break;
                    }
                    case Msg.BorderSay:
                    {
                        var pos = NetIO.GetVector3(r);
                        var text = r.ReadString();
                        if (!IsClient) return;
                        var b = BorderAt(pos);
                        if (b != null) ShowSay(b, text);
                        break;
                    }
                    case Msg.BorderSkip:
                    {
                        r.ReadByte();
                        if (!IsHost) return;
                        RemoteSkipUntil = Time.realtimeSinceStartup + 1.2f;
                        Plugin.Log.LogInfo("Fronteira: o tio passou a fala do guarda.");
                        break;
                    }
                    case Msg.BorderAct:
                    {
                        var pos = NetIO.GetVector3(r);
                        byte act = r.ReadByte();
                        if (!IsHost) return;
                        var b = BorderAt(pos);
                        if (b != null) HostAct(b, act);
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("BorderSync " + type + ": " + e); }
        }
    }

    /// <summary>Tio: a fronteira não roda sozinha (o host decide e manda).</summary>
    [HarmonyPatch(typeof(BorderLogicC), "OnTriggerEnter")]
    internal static class BorderTriggerPatch
    {
        private static bool Prefix() { return !BorderSync.IsClient; }
    }

    [HarmonyPatch(typeof(BorderLogicC), "Update")]
    internal static class BorderUpdatePatch
    {
        private static bool Prefix() { return !BorderSync.IsClient; }
    }

    /// <summary>Tio: clicar na janela com passaporte/carteira vira pedido ao host.</summary>
    [HarmonyPatch(typeof(BorderLogicC), "ActionRelay")]
    internal static class BorderActionPatch
    {
        private static bool Prefix(BorderLogicC __instance)
        {
            if (!BorderSync.IsClient) return true;
            try { BorderSync.ClientAction(__instance); }
            catch (Exception e) { Plugin.Log.LogError("BorderSync: " + e); }
            return false;
        }
    }

    /// <summary>
    /// Host: a fala do guarda espera um clique (WaitWhile com uma lambda que olha o Input). Se o tio clicou, a lambda
    /// responde "não espera mais" uma vez — igual ao clique do host.
    /// </summary>
    [HarmonyPatch]
    internal static class BorderSkipWaitPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // compilador antigo do Unity: a lambda é o static '<>m__0' dentro de '<DialogueSpeech>c__IteratorF'
            var found = new List<MethodBase>();
            foreach (var t in typeof(BorderLogicC).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!t.Name.StartsWith("<DialogueSpeech>")) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (m.Name.StartsWith("<>m__") && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0) found.Add(m);
            }
            Plugin.Log.LogInfo("Fronteira: esperas de clique do guarda achadas: " + found.Count);
            return found;
        }

        private static void Postfix(ref bool __result)
        {
            if (!__result || BorderSync.RemoteSkipUntil < 0f) return;
            if (Time.realtimeSinceStartup <= BorderSync.RemoteSkipUntil) __result = false;
            BorderSync.RemoteSkipUntil = -1f;
        }
    }

    /// <summary>Host: cada fala do guarda da fronteira vai para o tio.</summary>
    [HarmonyPatch(typeof(DialogueStuffsC), "StartTypeText")]
    internal static class BorderSayPatch
    {
        private static void Postfix(DialogueStuffsC __instance, GameObject target1)
        {
            try
            {
                if (target1 == null || !BorderSync.IsHost) return;
                var b = target1.GetComponent<BorderLogicC>();
                if (b != null) BorderSync.HostSaid(b, __instance.word);
            }
            catch (Exception e) { Plugin.Log.LogError("BorderSync: " + e); }
        }
    }
}
