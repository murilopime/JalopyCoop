using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Caixas (BoxContentsC) abertas iguais nos dois. No jogo: segurar o clique 0,7 s na tampa (BoxLogicC) ou
    /// cortar o cadeado (PadLockC -> BoxContentsC.Open). Aberta = animação "Open" e sem Rigidbody.
    /// O conteúdo já nasce igual (sorteio fixo) e os itens têm id (ItemSync).
    /// </summary>
    public class BoxSync : MonoBehaviour
    {
        private float timer;
        private readonly HashSet<BoxContentsC> known = new HashSet<BoxContentsC>(); // caixas já abertas que conheço

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => known.Clear();
        }

        private static bool IsOpen(BoxContentsC box)
        {
            var anim = box.GetComponent<Animator>();
            return anim != null && anim.GetBool("Open") && box.GetComponent<Rigidbody>() == null;
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            var items = ItemSync.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame || items == null || !items.Ready) return;
            timer += Time.unscaledDeltaTime;
            if (timer < 0.5f) return;
            timer = 0f;
            try
            {
                foreach (var box in SceneCache.Get<BoxContentsC>())
                {
                    if (box == null) continue;
                    if (known.Contains(box) || !IsOpen(box)) continue;
                    known.Add(box);
                    int id = -1;
                    var op = box.GetComponent<ObjectPickupC>();
                    if (op != null) items.TryGetId(op, out id);
                    var pos = box.transform.position;
                    net.SendReliable(Msg.BoxOpened, w => { w.Write(id); NetIO.Put(w, pos); });
                    Plugin.Log.LogInfo("Caixa: abri " + box.name);
                }
            }
            catch (Exception e) { Plugin.Log.LogError("BoxSync: " + e); }
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.BoxOpened) return;
            int id = r.ReadInt32();
            var pos = NetIO.GetVector3(r);
            try
            {
                BoxContentsC box = null;
                var items = ItemSync.Instance;
                if (id >= 0 && items != null)
                {
                    var op = items.Item(id);
                    if (op != null) box = op.GetComponent<BoxContentsC>();
                }
                if (box == null)
                {
                    float bd = 4f;
                    foreach (var b in FindObjectsOfType<BoxContentsC>())
                    {
                        float d = (b.transform.position - pos).sqrMagnitude;
                        if (d < bd) { bd = d; box = b; }
                    }
                }
                if (box == null) { Plugin.Log.LogWarning("Caixa: sem par aqui."); return; }
                known.Add(box);
                if (IsOpen(box)) return;
                // como o fim do BoxLogicC.Update (tampa aberta) + BoxContentsC.Open
                if (box.boxOpener != null)
                {
                    var opener = box.boxOpener.GetComponent<BoxLogicC>();
                    if (opener != null) { opener.debugOpen = true; opener.readyToOpen = false; }
                    var col = box.boxOpener.GetComponent<BoxCollider>();
                    if (col != null) Destroy(col);
                }
                if (box.padLock != null) box.padLock.SetActive(false);
                box.Open();
                var audio = box.GetComponent<AudioSource>();
                var opener2 = box.boxOpener != null ? box.boxOpener.GetComponent<BoxLogicC>() : null;
                if (audio != null && opener2 != null && opener2.audioOpen != null) audio.PlayOneShot(opener2.audioOpen, 1f);
                Plugin.Log.LogInfo("Caixa: o outro jogador abriu " + box.name);
            }
            catch (Exception e) { Plugin.Log.LogError("BoxSync: " + e); }
        }
    }
}
