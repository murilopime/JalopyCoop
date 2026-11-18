using System.Collections.Generic;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Empresta animações do tio para outro modelo (ex.: o host como "homem de óculos").
    ///
    /// Trocar o controller do modelo pelo do tio deu T-pose (os caminhos dos ossos não batem), mas os dois
    /// modelos usam o esqueleto "Dantes". Então: um clone do tio, sem scripts e invisível, toca o clip
    /// (SampleAnimation) e a rotação de cada osso é copiada para o osso de MESMO NOME do outro modelo.
    /// </summary>
    public class PoseDonor
    {
        private GameObject donor;
        private Transform donorRoot;
        private readonly Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>();
        private readonly List<KeyValuePair<Transform, Transform>> pairs = new List<KeyValuePair<Transform, Transform>>(); // (doador, alvo)
        private Transform donorTop, targetTop;   // osso mais alto que existe nos dois
        private Transform targetRootBound;
        private readonly List<Quaternion> rest = new List<Quaternion>();

        public bool Ready { get { return donor != null && pairs.Count > 0; } }
        public int Matched { get { return pairs.Count; } }
        public Transform DonorRoot { get { return donorRoot; } }

        /// <summary>Cria o clone do tio (sem scripts) e casa os ossos com os do alvo.</summary>
        public bool Build(Transform target)
        {
            Destroy();
            var uncle = GameRefs.Uncle;
            if (uncle == null) return false;
            var srcAnim = uncle.GetComponent<Animator>();
            if (srcAnim == null || srcAnim.runtimeAnimatorController == null) return false;
            foreach (var c in srcAnim.runtimeAnimatorController.animationClips)
            {
                if (c != null && !clips.ContainsKey(c.name)) clips[c.name] = c;
            }

            // Clonar dentro de um pai INATIVO: Awake/Start dos scripts do tio não rodam no clone.
            var holder = new GameObject("JalopyCoop_PoseDonorHolder");
            holder.SetActive(false);
            donor = (GameObject)Object.Instantiate(uncle, holder.transform);
            donor.name = "JalopyCoop_PoseDonor";
            for (int pass = 0; pass < 3; pass++)
            {
                // de trás para frente: scripts que dependem de outros (AIPath -> Seeker) vêm depois
                var mbs = donor.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = mbs.Length - 1; i >= 0; i--) if (mbs[i] != null) Object.DestroyImmediate(mbs[i]);
                foreach (var col in donor.GetComponentsInChildren<Collider>(true)) if (col != null) Object.DestroyImmediate(col);
            }
            foreach (var a in donor.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(a);
            foreach (var r in donor.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            var anim = donor.GetComponent<Animator>();
            if (anim != null) anim.enabled = false; // quem posa é o SampleAnimation
            donor.transform.parent = null;
            donor.transform.position = new Vector3(0f, -3000f, 0f);
            donor.transform.localScale = uncle.transform.lossyScale;
            // O jogo acha o tio por tag ("Uncle"): o clone NÃO pode ter essa tag.
            foreach (var t in donor.GetComponentsInChildren<Transform>(true)) if (t.tag != "Untagged") t.tag = "Untagged";
            Object.Destroy(holder);
            donor.SetActive(true);
            donorRoot = donor.transform;

            // Casa os ossos pelo nome.
            var byName = new Dictionary<string, Transform>();
            foreach (var t in donor.GetComponentsInChildren<Transform>(true))
            {
                if (t != donorRoot && !byName.ContainsKey(t.name)) byName[t.name] = t;
            }
            targetRootBound = target;
            int bestDepth = int.MaxValue;
            foreach (var t in target.GetComponentsInChildren<Transform>(true))
            {
                if (t == target) continue;
                Transform d;
                if (!byName.TryGetValue(t.name, out d)) continue;
                if (t.GetComponent<Renderer>() != null) continue; // só ossos
                pairs.Add(new KeyValuePair<Transform, Transform>(d, t));
                rest.Add(t.localRotation);
                int depth = Depth(t, target);
                if (depth < bestDepth) { bestDepth = depth; donorTop = d; targetTop = t; }
            }
            Plugin.Log.LogInfo("Pose do tio emprestada: " + pairs.Count + " ossos casados (topo: " + (targetTop != null ? targetTop.name : "-") + "), " + clips.Count + " clips.");
            if (pairs.Count == 0)
            {
                var sb = new System.Text.StringBuilder("Ossos do modelo: ");
                foreach (var t in target.GetComponentsInChildren<Transform>(true)) sb.Append(t.name + " ");
                Plugin.Log.LogWarning(sb.ToString());
            }
            return Ready;
        }

        public bool HasClip(string name) { return clips.ContainsKey(name); }

        public Transform[] TargetBones()
        {
            var a = new Transform[pairs.Count];
            for (int i = 0; i < pairs.Count; i++) a[i] = pairs[i].Value;
            return a;
        }

        /// <summary>Volta os ossos do alvo à pose original (o Animator do modelo pode não mexer em todos).</summary>
        public void RestoreRest()
        {
            for (int i = 0; i < pairs.Count && i < rest.Count; i++)
            {
                if (pairs[i].Value != null) pairs[i].Value.localRotation = rest[i];
            }
        }

        /// <summary>
        /// Posa o alvo com o clip. O osso do topo recebe a rotação relativa à raiz (os eixos das raízes
        /// dos dois modelos podem ser diferentes); os outros, a rotação local.
        /// </summary>
        public bool Apply(string clipName, float time)
        {
            if (!Ready) return false;
            AnimationClip clip;
            if (!clips.TryGetValue(clipName, out clip)) return false;
            var root = donorRoot;
            Vector3 p = root.position; Quaternion r = root.rotation;
            clip.SampleAnimation(donor, clip.length > 0f ? Mathf.Repeat(time, clip.length) : 0f);
            root.position = p; root.rotation = r; // ignora movimento da raiz do clip

            for (int i = 0; i < pairs.Count; i++)
            {
                var d = pairs[i].Key; var t = pairs[i].Value;
                if (d == null || t == null) continue;
                if (t == targetTop)
                {
                    t.rotation = targetRootBound.rotation * (Quaternion.Inverse(root.rotation) * d.rotation);
                }
                else
                {
                    t.localRotation = d.localRotation;
                }
            }
            return true;
        }

        /// <summary>
        /// Move o alvo para que o osso do topo (quadril) fique onde o quadril do tio estaria se a raiz
        /// do tio estivesse em <paramref name="seat"/> (o lugar do tio sentado no carro).
        /// </summary>
        public void PlaceLikeDonor(Transform target, Transform seat, Vector3 extraLocal)
        {
            if (donorTop == null || targetTop == null) return;
            var local = Quaternion.Inverse(donorRoot.rotation) * (donorTop.position - donorRoot.position);
            var want = seat.position + seat.rotation * (local + extraLocal);
            target.position += want - targetTop.position;
        }

        /// <summary>Move o alvo no plano para o quadril (osso do topo) ficar na vertical de <paramref name="point"/>.</summary>
        public void CenterTopOver(Transform target, Vector3 point)
        {
            // O osso do topo ("Bone") fica na raiz do modelo, ~2,8 m longe do corpo: centralizar por ele fazia
            // o corpo girar em volta do host. As costas (LowerBack) ficam no meio do corpo.
            if (centerBone == null || !centerBone.IsChildOf(target))
            {
                centerBone = GameRefs.FindDeep(target, "LowerBack") ?? GameRefs.FindDeep(target, "UpperBack") ?? targetTop;
            }
            if (centerBone == null) return;
            var d = point - centerBone.position; d.y = 0f;
            target.position += d;
        }
        private Transform centerBone;

        public void Destroy()
        {
            if (donor != null) Object.Destroy(donor);
            donor = null;
            donorRoot = null;
            pairs.Clear();
            rest.Clear();
            clips.Clear();
            donorTop = targetTop = null;
        }

        private static int Depth(Transform t, Transform root)
        {
            int d = 0;
            while (t != null && t != root) { t = t.parent; d++; }
            return d;
        }
    }
}
