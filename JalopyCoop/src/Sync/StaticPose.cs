using System.Collections.Generic;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Copia uma pose PARADA de um modelo da cena para outro, osso por osso pelo nome (esqueleto "Dantes").
    /// Uso: o tio-NPC no volante (tela do host) copia a pose de "mãos no volante" do motorista original do
    /// jogo (FrameHolder/Laika_Driver, que já está posicionado no banco do motorista).
    /// Cabeça e pescoço não são copiados: continuam seguindo o olhar do jogador-tio.
    /// </summary>
    public class StaticPose
    {
        private Transform source, target;
        private readonly List<KeyValuePair<Transform, Transform>> pairs = new List<KeyValuePair<Transform, Transform>>();
        private Transform srcTop, dstTop;

        public bool Ready { get { return source != null && target != null && pairs.Count > 0 && srcTop != null; } }
        public Transform Source { get { return source; } }

        public bool Build(Transform src, Transform dst)
        {
            pairs.Clear(); srcTop = dstTop = null;
            source = src; target = dst;
            if (src == null || dst == null) return false;
            var byName = new Dictionary<string, Transform>();
            foreach (var t in src.GetComponentsInChildren<Transform>(true))
            {
                if (t != src && t.GetComponent<Renderer>() == null && !byName.ContainsKey(t.name)) byName[t.name] = t;
            }
            int best = int.MaxValue;
            foreach (var t in dst.GetComponentsInChildren<Transform>(true))
            {
                if (t == dst || t.GetComponent<Renderer>() != null) continue;
                Transform s;
                if (!byName.TryGetValue(t.name, out s)) continue;
                int depth = 0; for (var p = t; p != null && p != dst; p = p.parent) depth++;
                if (depth < best) { best = depth; srcTop = s; dstTop = t; }
                if (t.name.Contains("Head") || t.name.Contains("Neck")) continue;
                pairs.Add(new KeyValuePair<Transform, Transform>(s, t));
            }
            Plugin.Log.LogInfo("Pose de motorista para o tio: " + pairs.Count + " ossos casados (topo: " + (dstTop != null ? dstTop.name : "-") + ").");
            return Ready;
        }

        /// <summary>Posa o alvo e põe o quadril dele no quadril da fonte (+ ajuste no espaço da fonte).</summary>
        public void Apply(Vector3 extraLocal)
        {
            if (!Ready) return;
            target.rotation = source.rotation;
            for (int i = 0; i < pairs.Count; i++)
            {
                var s = pairs[i].Key; var t = pairs[i].Value;
                if (s == null || t == null) continue;
                if (t == dstTop) t.rotation = target.rotation * (Quaternion.Inverse(source.rotation) * s.rotation);
                else t.localRotation = s.localRotation;
            }
            var want = srcTop.position + source.rotation * extraLocal;
            target.position += want - dstTop.position;
        }
    }
}
