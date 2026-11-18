using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Troca de pneu: macaco (colocar no ponto do carro, levantar, baixar) e parafusos das rodas (soltar/apertar).
    /// Os pneus em si (tirar/colocar) e a chave de roda na mão já vão pelo ItemSync.
    ///
    /// Quem faz a ação manda; o outro lado repete o efeito do jogo, sem pôr nada na mão do jogador local.
    /// Levantar o carro mexe na física: só o host move o carro de verdade; o tio vê pelo CarSync.
    /// </summary>
    public class TyreSync : MonoBehaviour
    {
        public static TyreSync Instance { get; private set; }

        private const float ApplyWindow = 2f;

        private class JackState { public bool known; public float ignoreUntil; }
        private readonly Dictionary<CarJackC, JackState> jacks = new Dictionary<CarJackC, JackState>();
        private class BoltState { public bool known; public float ignoreUntil; }
        private readonly Dictionary<BoltLogicC, BoltState> bolts = new Dictionary<BoltLogicC, BoltState>();
        private float scanTimer;

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => { jacks.Clear(); bolts.Clear(); };
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            var items = ItemSync.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame || items == null || !items.Ready) return;
            try
            {
                scanTimer -= Time.unscaledDeltaTime;
                if (scanTimer <= 0f) { scanTimer = 1f; Scan(); }
                PollJacks();
                PollBolts();
            }
            catch (Exception e) { Plugin.Log.LogError("TyreSync: " + e); }
        }

        /// <summary>Acha macacos e parafusos de roda (pneus podem trocar, então reescaneia sempre).</summary>
        private void Scan()
        {
            foreach (var j in SceneCache.Get<CarJackC>())
            {
                if (j != null && !jacks.ContainsKey(j)) jacks[j] = new JackState { known = j.isJackedUp };
            }
            foreach (var ec in SceneCache.Get<EngineComponentC>())
            {
                if (ec == null || ec.bolt == null) continue;
                var b = ec.bolt.GetComponent<BoltLogicC>();
                if (b != null && !bolts.ContainsKey(b)) bolts[b] = new BoltState { known = b.boltStateLoose };
            }
        }

        // ================= macaco =================

        private static CarJackReceiverC[] Receivers()
        {
            var car = GameRefs.Car;
            return car != null ? car.root.GetComponentsInChildren<CarJackReceiverC>(true) : new CarJackReceiverC[0];
        }

        /// <summary>
        /// Chamado pelo ItemSync quando um item sai da mão do jogador local. true = o TyreSync cuidou dele.
        /// </summary>
        public bool OnLocalRelease(ObjectPickupC op)
        {
            // chave de roda voltou para o macaco (ao baixar o carro): o JackDown do outro lado faz o mesmo
            if (op.GetComponent<TyreIronC>() != null && op.transform.parent != null && op.transform.parent.GetComponentInParent<CarJackC>() != null) return true;

            var jack = op.GetComponent<CarJackC>();
            if (jack == null || op.transform.parent == null) return false;
            var rec = op.transform.parent.GetComponent<CarJackReceiverC>();
            if (rec == null) return false;
            int idx = Array.IndexOf(Receivers(), rec);
            int id;
            if (idx < 0 || !ItemSync.Instance.TryGetId(op, out id)) return false;
            CoopNet.Instance.SendReliable(Msg.JackPlace, w => { w.Write(id); w.Write(idx); });
            Plugin.Log.LogInfo("Macaco: eu coloquei no ponto " + rec.name + " (tipo " + rec.type + ")");
            return true;
        }

        private void PollJacks()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var kv in jacks)
            {
                var j = kv.Key;
                var st = kv.Value;
                if (j == null || now < st.ignoreUntil || j.isJackedUp == st.known) continue;
                st.known = j.isJackedUp;
                int id;
                if (!ItemSync.Instance.TryGetId(j.GetComponent<ObjectPickupC>(), out id)) continue;
                bool up = j.isJackedUp;
                CoopNet.Instance.SendReliable(up ? Msg.JackUp : Msg.JackDown, w => w.Write(id));
                Plugin.Log.LogInfo("Macaco: eu " + (up ? "levantei" : "baixei") + " o carro");
            }
        }

        private void RemoteJackPlace(CarJackC jack, int recIdx)
        {
            var recs = Receivers();
            if (recIdx < 0 || recIdx >= recs.Length) return;
            var rec = recs[recIdx];
            var op = jack.GetComponent<ObjectPickupC>();
            ItemSync.Instance.ForgetRemote(op, 0.8f);
            // Igual ao DragRigidbodyC (tag "CarJack") do jogo.
            jack.jackInstalledAt = rec.gameObject;
            op.targetDropOff = rec.gameObject;
            foreach (var t in jack.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = LayerMask.NameToLayer("Default");
            jack.SendMessage("JackEngaged", SendMessageOptions.DontRequireReceiver);
            Plugin.Log.LogInfo("Macaco: o outro jogador colocou no ponto " + rec.name);
        }

        /// <summary>Como CarJackC.JackUp, mas sem pôr a chave de roda na mão do jogador local.</summary>
        private void RemoteJackUp(CarJackC jack)
        {
            if (jack.isJackedUp) return;
            var cl = GameRefs.CarLogic;
            var op = jack.GetComponent<ObjectPickupC>();
            if (cl == null || jack.jackInstalledAt == null) { Plugin.Log.LogWarning("Macaco: levantar sem ponto do macaco."); return; }
            var rec = jack.jackInstalledAt.GetComponent<CarJackReceiverC>();
            jack.transform.parent = null;
            if (op.targetDropOff != null) jack.transform.position = op.targetDropOff.transform.position;
            var box = jack.GetComponent<BoxCollider>(); if (box != null) box.enabled = false;
            if (jack.tyreIron != null) { var tb = jack.tyreIron.GetComponent<BoxCollider>(); if (tb != null) tb.enabled = false; }
            jack.isJackedUp = true;
            var anim = jack.GetComponent<Animator>(); if (anim != null) anim.SetBool("isJackedUp", true);
            var audio = jack.GetComponent<AudioSource>(); if (audio != null && jack.audioCrank != null) audio.PlayOneShot(jack.audioCrank, 1f);
            if (jack.jackBar != null) iTween.RotateBy(jack.jackBar, iTween.Hash("amount", new Vector3(2f, 0f, 0f), "islocal", true, "time", 0.2, "easetype", "easeoutquint"));

            bool host = CoopNet.Instance.Mode == CoopMode.Host;
            if (jack.carParent == null) jack.carParent = cl.transform.parent.parent.gameObject;
            int type = rec != null ? rec.type : 0;
            if (type == 1 || type == 2)
            {
                if (host)
                {
                    // Só o host levanta o carro de verdade (física é dele); o tio recebe a posição pelo CarSync.
                    jack.carParent.GetComponent<Rigidbody>().isKinematic = true;
                    if (type == 1) jack.carParent.transform.MoveRotateBy(new Vector3(jack.leftXAmount, 0f, 0f), new Vector3(0f, 0f, jack.leftXRotation), 0.35f, 0.25f, true, jack);
                    else jack.carParent.transform.MoveRotateBy(new Vector3(jack.rightXAmount, 0f, 0f), new Vector3(0f, 0f, jack.rightXRotation), 0.35f, 0.25f, true, jack);
                }
                cl.sideJackedUp = type;
                if (type == 1) cl.LeftTyresReadyForRemoval(); else cl.RightTyresReadyForRemoval();
            }
            Plugin.Log.LogInfo("Macaco: o outro jogador levantou o carro (lado " + type + ")");
        }

        private void RemoteJackDown(CarJackC jack)
        {
            if (!jack.isJackedUp) return;
            // O outro lado já conferiu os parafusos; aqui baixa sem a trava de "parafuso solto".
            bool fl = jack.preventJackReleaseFL, fr = jack.preventJackReleaseFR, rl = jack.preventJackReleaseRL, rr = jack.preventJackReleaseRR;
            jack.preventJackReleaseFL = jack.preventJackReleaseFR = jack.preventJackReleaseRL = jack.preventJackReleaseRR = false;
            try { jack.JackDown(); }
            finally { jack.preventJackReleaseFL = fl; jack.preventJackReleaseFR = fr; jack.preventJackReleaseRL = rl; jack.preventJackReleaseRR = rr; }
            // a chave de roda volta para o macaco (ReturnToJack no JackDown): para de seguir a mão do outro
            if (jack.tyreIron != null) ItemSync.Instance.ForgetRemote(jack.tyreIron.GetComponent<ObjectPickupC>(), 0f);
            if (CoopNet.Instance.Mode == CoopMode.Client) CarSync.Instance.Refreeze(); // JackDown liga a física do carro
            Plugin.Log.LogInfo("Macaco: o outro jogador baixou o carro");
        }

        /// <summary>O outro jogador pegou o macaco do ponto: limpa o estado do macaco aqui (como CarJackC.PickUp).</summary>
        public void OnRemotePickUp(ObjectPickupC op)
        {
            var jack = op.GetComponent<CarJackC>();
            if (jack == null) return;
            var cl = GameRefs.CarLogic;
            if (cl != null && cl.jackInUse == jack.gameObject) cl.jackInUse = null;
            var anim = jack.GetComponent<Animator>(); if (anim != null) anim.SetBool("isJackedUp", false);
            if (jack.jackBar != null) { var bc = jack.jackBar.GetComponent<BoxCollider>(); if (bc != null) bc.enabled = false; }
        }

        // ================= parafusos =================

        private void PollBolts()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var kv in bolts)
            {
                var b = kv.Key;
                var st = kv.Value;
                if (b == null || now < st.ignoreUntil || b.boltStateLoose == st.known) continue;
                st.known = b.boltStateLoose;
                var wheel = b.transform.parent != null ? b.transform.parent.GetComponent<ObjectPickupC>() : null;
                int id;
                if (wheel == null || !ItemSync.Instance.TryGetId(wheel, out id)) continue;
                bool loose = b.boltStateLoose;
                CoopNet.Instance.SendReliable(Msg.BoltState, w => { w.Write(id); w.Write(loose); });
                Plugin.Log.LogInfo("Parafuso: eu " + (loose ? "soltei" : "apertei") + " " + wheel.name);
            }
        }

        /// <summary>Efeito do TyreIronC.ActionPart2 + WheelLoose/WheelTight, sem a chave de roda.</summary>
        private void RemoteBolt(ObjectPickupC wheel, bool loose)
        {
            var ec = wheel.GetComponent<EngineComponentC>();
            if (ec == null || ec.bolt == null) return;
            var b = ec.bolt.GetComponent<BoltLogicC>();
            if (b == null || b.boltStateLoose == loose) return;
            BoltState st;
            if (!bolts.TryGetValue(b, out st)) { st = new BoltState(); bolts[b] = st; }
            st.known = loose;
            st.ignoreUntil = Time.realtimeSinceStartup + ApplyWindow;

            iTween.Stop(ec.bolt);
            iTween.MoveBy(ec.bolt, iTween.Hash("amount", new Vector3(0f, loose ? -0.1f : 0.1f, 0f), "islocal", true, "time", 1.0));
            iTween.RotateBy(ec.bolt, iTween.Hash("amount", new Vector3(0f, loose ? 1f : -1f, 0f), "islocal", true, "time", 1.0));
            b.boltStateLoose = loose;
            b.isLoose = loose;

            var cl = GameRefs.CarLogic;
            var jack = cl != null && cl.jackInUse != null ? cl.jackInUse.GetComponent<CarJackC>() : null;
            if (jack != null)
            {
                switch (ec.wheelInstallID)
                {
                    case 1: jack.preventJackReleaseFL = loose; break;
                    case 2: jack.preventJackReleaseFR = loose; break;
                    case 3: jack.preventJackReleaseRL = loose; break;
                    case 4: jack.preventJackReleaseRR = loose; break;
                }
            }
            if (loose)
            {
                wheel.enabled = true;
                ec.enabled = true;
                wheel.gameObject.tag = "Pickup";
            }
            else
            {
                var hl = wheel.transform.parent != null ? wheel.transform.parent.GetComponent<HoldingLogicC>() : null;
                var rot = hl != null && (hl.wheelID == 1 || hl.wheelID == 3) ? Vector3.zero : new Vector3(0f, 180f, 0f);
                iTween.RotateTo(wheel.gameObject, iTween.Hash("rotation", rot, "islocal", true, "time", 0.3));
                wheel.enabled = false;
                wheel.gameObject.tag = "Interactor";
            }
            Plugin.Log.LogInfo("Parafuso: o outro jogador " + (loose ? "soltou" : "apertou") + " " + wheel.name);
        }

        // ================= rede =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.JackPlace && type != Msg.JackUp && type != Msg.JackDown && type != Msg.BoltState) return;
            var items = ItemSync.Instance;
            if (items == null || !items.Ready) return;
            try
            {
                int id = r.ReadInt32();
                var op = items.Item(id);
                if (op == null) { Plugin.Log.LogWarning("TyreSync " + type + ": item " + id + " sem par aqui."); return; }
                if (type == Msg.BoltState) { RemoteBolt(op, r.ReadBoolean()); return; }

                var jack = op.GetComponent<CarJackC>();
                if (jack == null) return;
                JackState st;
                if (!jacks.TryGetValue(jack, out st)) { st = new JackState(); jacks[jack] = st; }
                if (type == Msg.JackPlace) { RemoteJackPlace(jack, r.ReadInt32()); return; }
                st.known = type == Msg.JackUp;
                st.ignoreUntil = Time.realtimeSinceStartup + ApplyWindow;
                if (type == Msg.JackUp) RemoteJackUp(jack); else RemoteJackDown(jack);
            }
            catch (Exception e) { Plugin.Log.LogError("TyreSync " + type + ": " + e); }
        }
    }
}
