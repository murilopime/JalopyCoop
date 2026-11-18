using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4a: itens (chave, peças do motor, objetos soltos, porta-malas).
    ///
    /// Ids: os dois jogos carregam o mesmo save, então os itens nascem nos mesmos lugares. O host manda a lista
    /// (id, nome, posição) e o tio casa cada id com o item dele de mesmo nome mais perto. Mesmo para os encaixes
    /// (HoldingLogicC: ignição, motor, espelho...).
    ///
    /// Quem mexe no item é o dono naquele momento: manda "peguei", a posição na mão e onde o item foi parar
    /// (encaixe, porta-malas ou solto no chão). O outro lado repete: tira o item do encaixe, segue a posição e,
    /// no fim, encaixa com a lógica do próprio jogo (ObjectPickupC.DropOff), então a chave na ignição liga o carro
    /// e a peça no motor conta no desempenho.
    ///
    /// Limitações (por enquanto): rodas/pneus (parafusos), itens que nascem depois (loja, caixas da rota),
    /// dois jogadores pegando o mesmo item ao mesmo tempo.
    /// </summary>
    public class ItemSync : MonoBehaviour
    {
        public static ItemSync Instance { get; private set; }

        private const float PoseInterval = 0.1f;   // 10x/s
        private const float MatchDistance = 0.6f;  // metros

        // ids casados
        private readonly Dictionary<int, ObjectPickupC> items = new Dictionary<int, ObjectPickupC>();
        private readonly Dictionary<ObjectPickupC, int> itemIds = new Dictionary<ObjectPickupC, int>();
        private readonly Dictionary<int, HoldingLogicC> drops = new Dictionary<int, HoldingLogicC>();
        private readonly Dictionary<HoldingLogicC, int> dropIds = new Dictionary<HoldingLogicC, int>();
        private bool ready;
        /// <summary>Ids casados (host: lista enviada; tio: lista recebida).</summary>
        public bool Ready { get { return ready; } }

        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>(); // nome/posição de cada id (para recasar)
        private float inGameFor;
        private float addTimer;
        private float goneTimer;
        private readonly HashSet<int> gone = new HashSet<int>();
        private bool hostPendingManifest;
        private float helloTimer;
        private float poseTimer;

        // itens do jogador local
        private readonly HashSet<ObjectPickupC> heldLocal = new HashSet<ObjectPickupC>();
        private readonly HashSet<ObjectPickupC> warnedNoId = new HashSet<ObjectPickupC>();
        private class Released { public float t; public float still; }
        private readonly Dictionary<ObjectPickupC, Released> released = new Dictionary<ObjectPickupC, Released>();

        // itens que o outro jogador está mexendo
        private class RemoteItem
        {
            public bool hasPose;
            public bool carSpace;
            public Vector3 pos;
            public Quaternion rot;
            public readonly List<Collider> disabled = new List<Collider>();
        }
        private readonly Dictionary<ObjectPickupC, RemoteItem> remote = new Dictionary<ObjectPickupC, RemoteItem>();

        private void Awake() { Instance = this; }

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += connected => { if (!connected) ResetAll(); };
        }

        // ================= laço =================

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) { inGameFor = 0f; return; }
            inGameFor += Time.unscaledDeltaTime;

            // Espera o save terminar de carregar: o jogo recria peças do motor logo depois de entrar.
            if (net.Mode == CoopMode.Client && !ready && inGameFor > 4f)
            {
                helloTimer -= Time.unscaledDeltaTime;
                if (helloTimer <= 0f)
                {
                    helloTimer = 5f;
                    net.SendReliable(Msg.ItemHello, w => { });
                }
            }
            if (net.Mode == CoopMode.Host && hostPendingManifest)
            {
                hostPendingManifest = false;
                SendManifest();
            }
            if (ready)
            {
                goneTimer += Time.unscaledDeltaTime;
                if (goneTimer >= 1f) { goneTimer = 0f; CheckGone(); }
            }
            if (net.Mode == CoopMode.Host && ready)
            {
                addTimer += Time.unscaledDeltaTime;
                if (addTimer >= 3f) { addTimer = 0f; SendNewItems(); }
            }
            if (!ready) return;

            try
            {
                TrackLocal();
                UpdateRemote();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("ItemSync: " + e);
            }
        }

        // ================= ids (lista do host) =================

        private static Transform Car { get { return GameRefs.Car; } }

        private static List<T> SceneObjects<T>() where T : Component
        {
            var list = new List<T>();
            foreach (var o in Resources.FindObjectsOfTypeAll<T>())
            {
                if (o == null || o.hideFlags != HideFlags.None) continue;
                if (!o.gameObject.scene.IsValid()) continue; // prefab, não está na cena
                list.Add(o);
            }
            return list;
        }

        /// <summary>Posição para casar ids: no espaço do carro se o objeto está no carro, senão no mundo.</summary>
        private static void KeyPos(Transform t, out bool carSpace, out Vector3 pos)
        {
            var car = Car;
            if (car != null && t.IsChildOf(car)) { carSpace = true; pos = car.InverseTransformPoint(t.position); }
            else { carSpace = false; pos = t.position; }
        }

        /// <summary>
        /// HOST: itens que nasceram depois da lista (rota, cidade, loja, caixas). Ganham ids novos (os antigos
        /// não mudam) e vão para o tio, que casa pelo nome e posição (o mundo é sorteado igual: SeededRandom).
        /// </summary>
        private void SendNewItems()
        {
            var fresh = new List<ObjectPickupC>();
            foreach (var op in SceneObjects<ObjectPickupC>()) if (!itemIds.ContainsKey(op)) fresh.Add(op);
            var freshDrops = new List<HoldingLogicC>();
            foreach (var hl in SceneObjects<HoldingLogicC>()) if (!dropIds.ContainsKey(hl)) freshDrops.Add(hl);
            if (fresh.Count == 0 && freshDrops.Count == 0) return;

            int next = 0; foreach (var k in items.Keys) next = Math.Max(next, k + 1);
            int nextDrop = 0; foreach (var k in drops.Keys) nextDrop = Math.Max(nextDrop, k + 1);
            var itemEntries = new List<Entry>();
            foreach (var op in fresh)
            {
                var e = new Entry { id = next++, name = op.name };
                KeyPos(op.transform, out e.carSpace, out e.pos);
                items[e.id] = op; itemIds[op] = e.id; entries[e.id] = e;
                itemEntries.Add(e);
            }
            var dropEntries = new List<Entry>();
            foreach (var hl in freshDrops)
            {
                var e = new Entry { id = nextDrop++, name = hl.name };
                KeyPos(hl.transform, out e.carSpace, out e.pos);
                drops[e.id] = hl; dropIds[hl] = e.id;
                dropEntries.Add(e);
            }
            CoopNet.Instance.SendReliable(Msg.ItemAdd, w => { WriteEntryList(w, itemEntries); WriteEntryList(w, dropEntries); });
            Plugin.Log.LogInfo("Itens: " + itemEntries.Count + " itens e " + dropEntries.Count + " encaixes novos mandados ao tio.");
        }

        private static void WriteEntryList(BinaryWriter w, List<Entry> list)
        {
            w.Write(list.Count);
            foreach (var e in list) { w.Write(e.id); w.Write(e.name); w.Write(e.carSpace); NetIO.Put(w, e.pos); }
        }

        /// <summary>TIO: itens novos do host. Casa agora o que já existe aqui; o resto casa quando for usado (GetItem).</summary>
        private void ReceiveNewItems(BinaryReader r)
        {
            var newItems = ReadEntries(r);
            var newDrops = ReadEntries(r);
            foreach (var e in newItems) entries[e.id] = e;
            var unusedItems = new List<ObjectPickupC>();
            foreach (var op in SceneObjects<ObjectPickupC>()) if (!itemIds.ContainsKey(op)) unusedItems.Add(op);
            var unusedDrops = new List<HoldingLogicC>();
            foreach (var hl in SceneObjects<HoldingLogicC>()) if (!dropIds.ContainsKey(hl)) unusedDrops.Add(hl);
            int a = Match(newItems, unusedItems, items, itemIds);
            int b = Match(newDrops, unusedDrops, drops, dropIds);
            Plugin.Log.LogInfo("Itens: novos do host casados " + a + "/" + newItems.Count + " itens e " + b + "/" + newDrops.Count + " encaixes.");
        }

        /// <summary>Item com id que foi destruído aqui (vendido, gasto): o outro lado apaga o mesmo.</summary>
        private void CheckGone()
        {
            // logo depois de casar a lista o jogo ainda recria peças (save carregando): isso não é "item vendido"
            if (inGameFor < 15f) return;
            List<int> dead = null;
            foreach (var kv in items)
            {
                if (kv.Value != null || gone.Contains(kv.Key)) continue;
                (dead ?? (dead = new List<int>())).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var id in dead)
            {
                gone.Add(id);
                items.Remove(id);
                entries.Remove(id); // não recasar com outro objeto
                int gid = id;
                CoopNet.Instance.SendReliable(Msg.ItemGone, w => w.Write(gid));
            }
            Plugin.Log.LogInfo("Itens: " + dead.Count + " item(ns) sumiram aqui; avisei o outro jogador.");
        }

        private void ApplyGone(int id)
        {
            if (!gone.Add(id)) return;
            ObjectPickupC op;
            if (items.TryGetValue(id, out op) && op != null)
            {
                itemIds.Remove(op);
                remote.Remove(op);
                Plugin.Log.LogInfo("Itens: " + op.name + " sumiu no outro jogo (vendido/gasto); apagando aqui.");
                Destroy(op.gameObject);
            }
            items.Remove(id);
            entries.Remove(id);
        }

        private void SendManifest()
        {
            if (!GameRefs.InGame) { hostPendingManifest = true; return; }
            ClearIds();
            var localItems = SceneObjects<ObjectPickupC>();
            var localDrops = SceneObjects<HoldingLogicC>();
            for (int i = 0; i < localItems.Count; i++)
            {
                items[i] = localItems[i]; itemIds[localItems[i]] = i;
                var e = new Entry { id = i, name = localItems[i].name };
                KeyPos(localItems[i].transform, out e.carSpace, out e.pos);
                entries[i] = e;
            }
            for (int i = 0; i < localDrops.Count; i++) { drops[i] = localDrops[i]; dropIds[localDrops[i]] = i; }

            CoopNet.Instance.SendReliable(Msg.ItemManifest, w =>
            {
                WriteEntries(w, localItems);
                WriteEntries(w, localDrops);
            });
            ready = true;
            Plugin.Log.LogInfo("Itens: lista enviada ao tio (" + localItems.Count + " itens, " + localDrops.Count + " encaixes).");
        }

        private static void WriteEntries<T>(BinaryWriter w, List<T> list) where T : Component
        {
            w.Write(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                bool carSpace; Vector3 pos;
                KeyPos(list[i].transform, out carSpace, out pos);
                w.Write(i);
                w.Write(list[i].name);
                w.Write(carSpace);
                NetIO.Put(w, pos);
            }
        }

        private struct Entry { public int id; public string name; public bool carSpace; public Vector3 pos; }

        private static List<Entry> ReadEntries(BinaryReader r)
        {
            int n = r.ReadInt32();
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new Entry();
                e.id = r.ReadInt32();
                e.name = r.ReadString();
                e.carSpace = r.ReadBoolean();
                e.pos = NetIO.GetVector3(r);
                list.Add(e);
            }
            return list;
        }

        private void ReceiveManifest(BinaryReader r)
        {
            var hostItems = ReadEntries(r);
            var hostDrops = ReadEntries(r);
            ClearIds();
            foreach (var e in hostItems) entries[e.id] = e;
            int okItems = Match(hostItems, SceneObjects<ObjectPickupC>(), items, itemIds);
            int okDrops = Match(hostDrops, SceneObjects<HoldingLogicC>(), drops, dropIds);
            ready = true;
            Plugin.Log.LogInfo("Itens: casados " + okItems + "/" + hostItems.Count + " itens e " + okDrops + "/" + hostDrops.Count + " encaixes.");
        }

        private static int Match<T>(List<Entry> host, List<T> local, Dictionary<int, T> byId, Dictionary<T, int> idOf) where T : Component
        {
            var byName = new Dictionary<string, List<T>>();
            foreach (var o in local)
            {
                List<T> l;
                if (!byName.TryGetValue(o.name, out l)) { l = new List<T>(); byName[o.name] = l; }
                l.Add(o);
            }
            var used = new HashSet<T>();
            var missing = new List<string>();
            int ok = 0;
            foreach (var e in host)
            {
                List<T> cands;
                T best = null;
                float bestD = MatchDistance;
                if (byName.TryGetValue(e.name, out cands))
                {
                    foreach (var c in cands)
                    {
                        if (used.Contains(c)) continue;
                        bool carSpace; Vector3 pos;
                        KeyPos(c.transform, out carSpace, out pos);
                        if (carSpace != e.carSpace) continue;
                        float d = (pos - e.pos).magnitude;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                }
                if (best == null) { if (missing.Count < 12) missing.Add(e.name); continue; }
                used.Add(best);
                byId[e.id] = best;
                idOf[best] = e.id;
                ok++;
            }
            if (missing.Count > 0) Plugin.Log.LogInfo("  sem par (" + typeof(T).Name + "): " + string.Join(", ", missing.ToArray()));
            return ok;
        }

        /// <summary>Item do id. Se o objeto foi destruído/recriado pelo jogo (peças ao carregar o save), recasa pelo nome e posição.</summary>
        private ObjectPickupC GetItem(int id)
        {
            ObjectPickupC op;
            if (items.TryGetValue(id, out op) && op != null) return op;
            Entry e;
            if (!entries.TryGetValue(id, out e)) return null;
            op = Nearest(e, 2.5f);
            if (op != null)
            {
                items[id] = op; itemIds[op] = id;
                Plugin.Log.LogInfo("Itens: recasado " + e.name + " (id " + id + ")");
            }
            return op;
        }

        /// <summary>Id de um item local. Sem id: usa um id do mesmo nome que perdeu o objeto ou nunca casou.</summary>
        private bool IdOf(ObjectPickupC op, out int id)
        {
            if (itemIds.TryGetValue(op, out id)) return true;
            bool carSpace; Vector3 pos;
            KeyPos(op.transform, out carSpace, out pos);
            float best = 2.5f; int bestId = -1;
            foreach (var kv in entries)
            {
                var e = kv.Value;
                if (e.name != op.name) continue;
                ObjectPickupC cur;
                if (items.TryGetValue(e.id, out cur) && cur != null) continue; // id já tem objeto vivo
                float d = e.carSpace == carSpace ? (e.pos - pos).magnitude : 1f; // já está na mão: espaço pode mudar
                if (d < best) { best = d; bestId = e.id; }
            }
            if (bestId < 0) return false;
            id = bestId;
            items[id] = op; itemIds[op] = id;
            Plugin.Log.LogInfo("Itens: recasado " + op.name + " na mão (id " + id + ")");
            return true;
        }

        private ObjectPickupC Nearest(Entry e, float maxDist)
        {
            ObjectPickupC best = null;
            foreach (var c in SceneObjects<ObjectPickupC>())
            {
                if (c.name != e.name || itemIds.ContainsKey(c)) continue;
                bool carSpace; Vector3 pos;
                KeyPos(c.transform, out carSpace, out pos);
                if (carSpace != e.carSpace) continue;
                float d = (pos - e.pos).magnitude;
                if (d < maxDist) { maxDist = d; best = c; }
            }
            return best;
        }

        // ---------- usado pelo TyreSync ----------

        public bool TryGetId(ObjectPickupC op, out int id)
        {
            id = -1;
            return op != null && IdOf(op, out id);
        }

        public ObjectPickupC Item(int id) { return GetItem(id); }

        /// <summary>Todos os itens com id (cópia: pode iterar à vontade).</summary>
        public List<KeyValuePair<int, ObjectPickupC>> AllItems() { return new List<KeyValuePair<int, ObjectPickupC>>(items); }

        /// <summary>Para de seguir a mão do outro jogador (o jogo assumiu o item: macaco, chave de roda).</summary>
        public void ForgetRemote(ObjectPickupC op, float restoreDelay)
        {
            RemoteItem ri;
            if (op == null || !remote.TryGetValue(op, out ri)) return;
            remote.Remove(op);
            if (restoreDelay <= 0f) RestoreColliders(ri);
            else StartCoroutine(RestoreCollidersLater(ri, restoreDelay));
        }

        private void ClearIds()
        {
            items.Clear(); itemIds.Clear(); drops.Clear(); dropIds.Clear(); entries.Clear(); gone.Clear();
        }

        private void ResetAll()
        {
            foreach (var kv in remote)
            {
                if (kv.Key == null) continue;
                RestoreColliders(kv.Value);
            }
            remote.Clear();
            heldLocal.Clear();
            released.Clear();
            ClearIds();
            ready = false;
            hostPendingManifest = false;
            helloTimer = 0f;
        }

        // ================= jogador local =================

        private void TrackLocal()
        {
            var cam = GameRefs.Cam;
            var drag = cam != null ? cam.GetComponent<DragRigidbodyC>() : null;
            if (drag == null) return;

            var now = new HashSet<ObjectPickupC>();
            AddHeld(now, drag.isHolding1);
            AddHeld(now, drag.isHolding2);
            AddHeld(now, drag.isHolding3);

            foreach (var op in now)
            {
                if (heldLocal.Contains(op)) continue;
                released.Remove(op);
                remote.Remove(op); // se o outro estava mexendo, agora é meu
                Send(Msg.ItemHold, op, w => { });
                Plugin.Log.LogInfo("Itens: eu peguei " + op.name);
            }
            foreach (var op in heldLocal)
            {
                if (op == null || now.Contains(op)) continue;
                released[op] = new Released();
            }
            heldLocal.Clear();
            foreach (var op in now) heldLocal.Add(op);

            poseTimer += Time.unscaledDeltaTime;
            if (poseTimer < PoseInterval) return;
            poseTimer = 0f;

            foreach (var op in heldLocal) Send(Msg.ItemPose, op, w => WritePose(w, op.transform));

            var done = new List<ObjectPickupC>();
            foreach (var kv in released)
            {
                var op = kv.Key;
                var rel = kv.Value;
                if (op == null) { done.Add(op); continue; }
                rel.t += PoseInterval;

                if (TyreSync.Instance != null && TyreSync.Instance.OnLocalRelease(op)) { done.Add(op); continue; }

                var parent = op.transform.parent;
                var hl = parent != null ? parent.GetComponent<HoldingLogicC>() : null;
                int dropId;
                if (hl != null && dropIds.TryGetValue(hl, out dropId))
                {
                    Send(Msg.ItemPlace, op, w => w.Write(dropId));
                    Plugin.Log.LogInfo("Itens: eu encaixei " + op.name + " em " + hl.name);
                    done.Add(op);
                    continue;
                }
                if (op.inventoryPlacedAt != null)
                {
                    if (rel.t < 0.5f) continue; // espera a animação de guardar
                    Send(Msg.ItemStore, op, w => WriteCarPose(w, op.transform));
                    Plugin.Log.LogInfo("Itens: eu guardei " + op.name + " (" + op.inventoryPlacedAt.name + ")");
                    done.Add(op);
                    continue;
                }
                var rb = op.GetComponent<Rigidbody>();
                bool moving = rb != null && !rb.isKinematic && (rb.velocity.sqrMagnitude > 0.01f || rb.angularVelocity.sqrMagnitude > 0.05f);
                rel.still = moving ? 0f : rel.still + PoseInterval;
                if (rel.still >= 1f || rel.t > 15f)
                {
                    bool inCar = RestsOnCar(op.transform);
                    int layer = op.gameObject.layer;
                    Send(Msg.ItemFree, op, w => { WritePose(w, op.transform); w.Write(inCar); w.Write(layer); });
                    Plugin.Log.LogInfo("Itens: eu soltei " + op.name + " (pai " + (op.transform.parent != null ? op.transform.parent.name : "-") + ")");
                    done.Add(op);
                }
                else
                {
                    Send(Msg.ItemPose, op, w => WritePose(w, op.transform));
                }
            }
            foreach (var op in done) released.Remove(op);
        }

        private void AddHeld(HashSet<ObjectPickupC> set, GameObject go)
        {
            if (go == null) return;
            var op = go.GetComponent<ObjectPickupC>();
            if (op == null) return;
            int id;
            if (IdOf(op, out id)) { set.Add(op); return; }
            if (warnedNoId.Add(op)) Plugin.Log.LogWarning("Itens: " + go.name + " está na mão mas não tem id (não sincroniza).");
        }

        private void Send(Msg type, ObjectPickupC op, Action<BinaryWriter> body)
        {
            int id;
            if (!IdOf(op, out id)) return;
            CoopNet.Instance.SendReliable(type, w => { w.Write(id); body(w); });
        }

        // pose: perto do carro vai no espaço do carro (o carro anda e a mensagem chega depois)
        private static void WritePose(BinaryWriter w, Transform t)
        {
            var car = Car;
            bool carSpace = car != null && (t.position - car.position).sqrMagnitude < 12f * 12f;
            w.Write(carSpace);
            if (carSpace)
            {
                NetIO.Put(w, car.InverseTransformPoint(t.position));
                NetIO.Put(w, Quaternion.Inverse(car.rotation) * t.rotation);
            }
            else
            {
                NetIO.Put(w, t.position);
                NetIO.Put(w, t.rotation);
            }
        }

        /// <summary>O item parou em cima do carro (banco, chão do carro, porta-malas)?</summary>
        private static bool RestsOnCar(Transform t)
        {
            var car = Car;
            if (car == null) return false;
            var root = car.root;
            var hits = Physics.RaycastAll(t.position + Vector3.up * 0.05f, Vector3.down, 0.6f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.transform.IsChildOf(t)) continue; // o próprio item
                return hit.transform.IsChildOf(root);
            }
            return false;
        }

        private static void WriteCarPose(BinaryWriter w, Transform t)
        {
            var car = Car;
            NetIO.Put(w, car.InverseTransformPoint(t.position));
            NetIO.Put(w, Quaternion.Inverse(car.rotation) * t.rotation);
        }

        // ================= mensagens =================

        private void OnReliableData(Msg type, BinaryReader r)
        {
            try
            {
                switch (type)
                {
                    case Msg.ItemHello:
                        if (CoopNet.Instance.Mode == CoopMode.Host) SendManifest();
                        return;
                    case Msg.ItemManifest:
                        if (CoopNet.Instance.Mode == CoopMode.Client) ReceiveManifest(r);
                        return;
                    case Msg.ItemGone:
                        if (ready) ApplyGone(r.ReadInt32());
                        return;
                    case Msg.ItemAdd:
                        if (CoopNet.Instance.Mode == CoopMode.Client && ready) ReceiveNewItems(r);
                        return;
                    case Msg.ItemHold:
                    case Msg.ItemPlace:
                    case Msg.ItemPose:
                    case Msg.ItemFree:
                    case Msg.ItemStore:
                        break;
                    default:
                        return;
                }
                if (!ready) return;
                int id = r.ReadInt32();
                var op = GetItem(id);
                if (op == null) { Plugin.Log.LogWarning("Itens: " + type + " para id " + id + " sem item aqui."); return; }
                if (heldLocal.Contains(op)) return; // está na minha mão: o meu vale

                switch (type)
                {
                    case Msg.ItemHold: RemoteHold(op); break;
                    case Msg.ItemPose: RemotePose(op, r); break;
                    case Msg.ItemPlace:
                    {
                        int dropId = r.ReadInt32();
                        HoldingLogicC hl;
                        if (drops.TryGetValue(dropId, out hl) && hl != null) RemotePlace(op, hl);
                        else Plugin.Log.LogWarning("Itens: encaixe " + dropId + " sem par para " + op.name);
                        break;
                    }
                    case Msg.ItemFree: RemoteFree(op, r); break;
                    case Msg.ItemStore: RemoteStore(op, r); break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("ItemSync " + type + ": " + e);
            }
        }

        // ================= o outro jogador =================

        private RemoteItem GetRemote(ObjectPickupC op)
        {
            RemoteItem ri;
            if (!remote.TryGetValue(op, out ri)) { ri = new RemoteItem(); remote[op] = ri; }
            return ri;
        }

        private void RemoteHold(ObjectPickupC op)
        {
            released.Remove(op);
            var ri = GetRemote(op);
            if (TyreSync.Instance != null) TyreSync.Instance.OnRemotePickUp(op);
            ReleaseFromPlacement(op);
            op.transform.parent = null;
            // Como o jogo faz ao pegar (ObjectPickupC.MoveToSlot1): sem física na mão.
            var rb = op.GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (op.connectedTo == null) DestroyImmediate(rb);
                else { rb.isKinematic = true; rb.detectCollisions = false; }
            }
            if (ri.disabled.Count == 0)
            {
                foreach (var c in op.GetComponentsInChildren<Collider>(true))
                {
                    if (c.enabled) { c.enabled = false; ri.disabled.Add(c); }
                }
            }
            SetLayer(op.gameObject, LayerMask.NameToLayer("Default"));
            Plugin.Log.LogInfo("Itens: o outro jogador pegou " + op.name);
        }

        private void RemotePose(ObjectPickupC op, BinaryReader r)
        {
            var ri = GetRemote(op);
            if (ri.disabled.Count == 0 && !ri.hasPose) RemoteHold(op); // perdeu o "peguei"
            ReadPose(r, out ri.carSpace, out ri.pos, out ri.rot);
            if (!ri.hasPose) { ri.hasPose = true; ApplyPose(op.transform, ri, 1f); }
        }

        private void RemotePlace(ObjectPickupC op, HoldingLogicC hl)
        {
            RemoteItem ri;
            // Colisão só volta depois da animação de encaixe: uma peça sólida entrando no motor empurrava o carro.
            if (remote.TryGetValue(op, out ri)) { remote.Remove(op); StartCoroutine(RestoreCollidersLater(ri, 0.6f)); }
            if (op.placedAt != null || op.inventoryPlacedAt != null) ReleaseFromPlacement(op);
            var rb = op.GetComponent<Rigidbody>();
            if (rb != null && op.connectedTo == null) DestroyImmediate(rb);
            if (hl.isOccupied && hl.targetObject != null && hl.targetObject != op.gameObject)
            {
                Plugin.Log.LogWarning("Itens: encaixe " + hl.name + " já ocupado por " + hl.targetObject.name);
            }
            // Igual ao DragRigidbodyC.DropOffCheck do jogo.
            op.targetDropOff = hl.gameObject;
            hl.targetObject = op.gameObject;
            SetLayer(op.gameObject, LayerMask.NameToLayer("Default"));
            op.SendMessage("DropOff", SendMessageOptions.DontRequireReceiver);
            Plugin.Log.LogInfo("Itens: o outro jogador encaixou " + op.name + " em " + hl.name);
        }

        private void RemoteFree(ObjectPickupC op, BinaryReader r)
        {
            bool carSpace; Vector3 pos; Quaternion rot;
            ReadPose(r, out carSpace, out pos, out rot);
            bool inCar = r.ReadBoolean();
            int layer = r.ReadInt32();
            Plugin.Log.LogInfo("Itens: o outro jogador soltou " + op.name);

            RemoteItem ri;
            if (remote.TryGetValue(op, out ri)) { RestoreColliders(ri); remote.Remove(op); }
            var tmp = new RemoteItem { carSpace = carSpace, pos = pos, rot = rot };
            op.transform.parent = null;
            ApplyPose(op.transform, tmp, 1f);
            SetLayer(op.gameObject, layer);
            // Parado onde o outro deixou, sem física aqui (antes caía pelo chão). Se pegar, o jogo liga a física.
            var rb = op.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; }
            var car = Car;
            if (inCar && car != null) op.transform.parent = car; // dentro do carro: anda junto
        }

        private void RemoteStore(ObjectPickupC op, BinaryReader r)
        {
            var pos = NetIO.GetVector3(r);
            var rot = NetIO.GetQuaternion(r);
            RemoteItem ri;
            if (remote.TryGetValue(op, out ri)) { RestoreColliders(ri); remote.Remove(op); }
            var car = Car;
            if (car == null) return;
            op.transform.parent = car;
            op.transform.localPosition = pos;
            op.transform.localRotation = rot;
            var rb = op.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            Plugin.Log.LogInfo("Itens: o outro jogador guardou " + op.name + " no porta-malas");
        }

        private void UpdateRemote()
        {
            float k = 1f - Mathf.Exp(-15f * Time.deltaTime);
            List<ObjectPickupC> dead = null;
            foreach (var kv in remote)
            {
                if (kv.Key == null) { (dead ?? (dead = new List<ObjectPickupC>())).Add(kv.Key); continue; }
                if (kv.Value.hasPose) ApplyPose(kv.Key.transform, kv.Value, k);
            }
            if (dead != null) foreach (var d in dead) remote.Remove(d);
        }

        private static void ApplyPose(Transform t, RemoteItem ri, float k)
        {
            Vector3 pos = ri.pos;
            Quaternion rot = ri.rot;
            if (ri.carSpace)
            {
                var car = Car;
                if (car == null) return;
                pos = car.TransformPoint(pos);
                rot = car.rotation * rot;
            }
            if (k >= 1f || (t.position - pos).sqrMagnitude > 9f) { t.position = pos; t.rotation = rot; return; }
            t.position = Vector3.Lerp(t.position, pos, k);
            t.rotation = Quaternion.Slerp(t.rotation, rot, k);
        }

        private static void ReadPose(BinaryReader r, out bool carSpace, out Vector3 pos, out Quaternion rot)
        {
            carSpace = r.ReadBoolean();
            pos = NetIO.GetVector3(r);
            rot = NetIO.GetQuaternion(r);
        }

        private static System.Collections.IEnumerator RestoreCollidersLater(RemoteItem ri, float delay)
        {
            yield return new WaitForSeconds(delay);
            RestoreColliders(ri);
        }

        private static void RestoreColliders(RemoteItem ri)
        {
            foreach (var c in ri.disabled) if (c != null) c.enabled = true;
            ri.disabled.Clear();
        }

        private static void SetLayer(GameObject go, int layer)
        {
            if (layer < 0) return;
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        /// <summary>
        /// Tira o item do encaixe/porta-malas como o ObjectPickupC.PickUp do jogo faz, mas sem pôr na mão do
        /// jogador local (o item está na mão do outro).
        /// </summary>
        private static void ReleaseFromPlacement(ObjectPickupC op)
        {
            iTween.Stop(op.gameObject);
            if (op.placedAt != null)
            {
                var hl = op.placedAt.GetComponent<HoldingLogicC>();
                if (op.isInEngine)
                {
                    op.isInEngine = false;
                    var ec = op.GetComponent<EngineComponentC>();
                    if (ec != null)
                    {
                        try
                        {
                            if (ec.acceleration > 0f && ec.carLogic != null) ec.carLogic.SendMessage("EngineDamageNone", SendMessageOptions.DontRequireReceiver);
                            ec.RemoveStatsFromCarPerf();
                            if (ec.bolt != null && ec.carLogic != null)
                            {
                                // pneu: como o fim do ObjectPickupC.RemoveFromEngine do jogo
                                var logic = ec.carLogic.GetComponent<CarLogicC>();
                                int n = ec.wheelInstallID - 1;
                                if (logic != null && n >= 0 && n < logic.wheelObjects.Length)
                                {
                                    var ws = logic.wheelObjects[n].GetComponent<WheelScriptPCC>();
                                    if (ws != null) ws.wheelTransform = null;
                                }
                                var perf = ec.carLogic.GetComponent<CarPerformanceC>();
                                if (perf != null) perf.RemoveTyreRefs();
                            }
                        }
                        catch (Exception e) { Plugin.Log.LogWarning("Itens: tirar peça do motor: " + e.Message); }
                    }
                }
                if (hl != null)
                {
                    hl.isOccupied = false;
                    try { hl.StopFunctionalLogic(); } catch { }
                    if (hl.targetObject == op.gameObject) hl.targetObject = null;
                }
                op.placedAt = null;
            }

            var p = op.transform.parent;
            if (p != null && p.parent != null)
            {
                var mirror = p.parent.GetComponent<MirrorLogicC>();
                if (mirror != null) mirror.keyHolder = null;
            }

            if (op.inventoryPlacedAt != null)
            {
                var relay = op.inventoryPlacedAt.GetComponent<InventoryRelayC>();
                if (relay != null)
                {
                    relay.isOccupied = false;
                    if (op.dimensionY == 2 && op.dimensionX == 1 && op.dimensionZ == 1 && relay.spaceAbove != null)
                    {
                        var above = relay.spaceAbove.GetComponent<InventoryRelayC>();
                        if (above != null) above.isOccupied = false;
                    }
                    if (Array.IndexOf(MultiSlots, relay.name) >= 0) { try { relay.UnOccupy(); } catch { } }
                    var inv = relay.inventoryBase != null ? relay.inventoryBase.GetComponent<InventoryLogicC>() : null;
                    if (inv != null)
                    {
                        try
                        {
                            if (!inv.isHomeInventory && !inv.isCrateInventory)
                            {
                                var cl = GameRefs.CarLogic;
                                var perf = cl != null ? cl.GetComponent<CarPerformanceC>() : null;
                                if (perf != null)
                                {
                                    perf.carBootWeight -= op.rigidMass;
                                    perf.totalCarWeight -= op.rigidMass;
                                    perf.UpdateWeight();
                                }
                            }
                            inv.AvailableSlotCount();
                            inv.UpdateInventory();
                        }
                        catch (Exception e) { Plugin.Log.LogWarning("Itens: tirar do porta-malas: " + e.Message); }
                    }
                }
                op.inventoryPlacedAt = null;
            }
        }

        private static readonly string[] MultiSlots =
        {
            "2x2x1", "3x2x1", "2x1x2", "2x2x2", "2x2x3", "2x1x3", "3x2x3", "4x2x2", "4x2x3", "4x1x1", "4x2x1"
        };
    }
}
