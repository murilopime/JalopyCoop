using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Portas dos prédios (loja, posto, Laika, motel...): EnvironmentDoorsC. "open" vira true quando abre e
    /// false quando termina de fechar. Cada lado olha as portas perto de si; quando uma muda pela mão do jogador,
    /// manda (posição + aberta) e o outro chama o mesmo Trigger do jogo.
    /// </summary>
    public class EnvDoorSync : MonoBehaviour
    {
        private float timer;
        private readonly Dictionary<EnvironmentDoorsC, bool> known = new Dictionary<EnvironmentDoorsC, bool>();
        private readonly Dictionary<EnvironmentDoorsC, bool> knownLocked = new Dictionary<EnvironmentDoorsC, bool>();
        private readonly Dictionary<EnvironmentDoorsC, float> ignoreUntil = new Dictionary<EnvironmentDoorsC, float>();

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => known.Clear();
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            timer += Time.unscaledDeltaTime;
            if (timer < 0.15f) return;
            timer = 0f;
            try
            {
                foreach (var d in SceneCache.Get<EnvironmentDoorsC>())
                {
                    if (d == null) continue;
                    bool open = d.open;
                    bool locked = d.isLocked;
                    bool last, lastLocked;
                    if (!known.TryGetValue(d, out last) || !knownLocked.TryGetValue(d, out lastLocked)) { known[d] = open; knownLocked[d] = locked; continue; }
                    float until;
                    if (ignoreUntil.TryGetValue(d, out until) && Time.realtimeSinceStartup < until) continue; // ainda repetindo o do outro
                    if (open == last && locked == lastLocked) continue;
                    known[d] = open; knownLocked[d] = locked;
                    var pos = Hinge(d);
                    net.SendReliable(Msg.EnvDoor, w => { NetIO.Put(w, pos); w.Write(open); w.Write(locked); });
                    Plugin.Log.LogInfo("Porta: " + d.transform.parent.parent.name + (open ? " aberta" : " fechada") + (locked ? ", trancada" : ""));
                }
            }
            catch (Exception e) { Plugin.Log.LogError("EnvDoorSync: " + e); }
        }

        /// <summary>Dobradiça (a maçaneta gira com a porta; a dobradiça não sai do lugar).</summary>
        private static Vector3 Hinge(EnvironmentDoorsC d)
        {
            var t = d.transform.parent != null && d.transform.parent.parent != null ? d.transform.parent.parent : d.transform;
            return t.position;
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.EnvDoor) return;
            var pos = NetIO.GetVector3(r);
            bool open = r.ReadBoolean();
            bool lockedNow = r.ReadBoolean();
            try
            {
                EnvironmentDoorsC door = null; float bd = 1f;
                foreach (var d in FindObjectsOfType<EnvironmentDoorsC>())
                {
                    float dist = (Hinge(d) - pos).sqrMagnitude;
                    if (dist < bd) { bd = dist; door = d; }
                }
                if (door == null) { Plugin.Log.LogWarning("Porta sem par aqui."); return; }
                known[door] = open;
                knownLocked[door] = lockedNow;
                ignoreUntil[door] = Time.realtimeSinceStartup + 1.6f;
                if (!lockedNow && door.isLocked)
                {
                    door.isLocked = false; // o outro destrancou com a chave (quarto do motel)
                    Plugin.Log.LogInfo("Porta: o outro jogador destrancou " + door.transform.parent.parent.name);
                }
                if (door.open == open) return;
                bool locked = door.isLocked;
                door.isLocked = false; // o outro já destrancou/abriu: aqui só repete o movimento
                try { door.StartCoroutine(door.Trigger()); }
                finally { door.isLocked = locked; }
                Plugin.Log.LogInfo("Porta: o outro jogador " + (open ? "abriu " : "fechou ") + door.transform.parent.parent.name);
            }
            catch (Exception e) { Plugin.Log.LogError("EnvDoorSync: " + e); }
        }
    }
}
