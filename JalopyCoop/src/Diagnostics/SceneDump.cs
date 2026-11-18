using System;
using System.Collections;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Ferramenta de investigação: grava em BepInEx/JalopyCoop-dump.txt
    /// a hierarquia do jogador, do tio e todos os modelos animados (SkinnedMeshRenderer) da cena.
    /// Roda sozinho 15 s depois de cada cena carregar e quando se aperta F8.
    /// </summary>
    public class SceneDump : MonoBehaviour
    {
        private int dumpCount;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo("Cena carregada: " + scene.name + " (index " + scene.buildIndex + ")");
            StartCoroutine(DumpLater(scene.name));
        }

        private IEnumerator DumpLater(string sceneName)
        {
            yield return new WaitForSeconds(15f);
            Dump("auto após carregar " + sceneName);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                Dump("tecla F8");
            }
        }

        private void Dump(string reason)
        {
            dumpCount++;
            var sb = new StringBuilder();
            sb.AppendLine("==== JalopyCoop dump #" + dumpCount + " (" + reason + ") " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Cena: " + SceneManager.GetActiveScene().name);

            DumpTagged(sb, "Player");
            DumpTagged(sb, "Uncle");

            if (Camera.main != null)
            {
                sb.AppendLine();
                sb.AppendLine("--- Camera.main: " + PathOf(Camera.main.transform));
            }

            sb.AppendLine();
            sb.AppendLine("--- Todos os SkinnedMeshRenderer (modelos animados) da cena:");
            var skinned = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>();
            foreach (var smr in skinned)
            {
                if (smr == null || smr.gameObject.scene.name == null) continue; // ignora assets não instanciados
                var anim = smr.GetComponentInParent<Animator>();
                sb.AppendLine(string.Format("  [{0}] {1} | mesh={2} | mat={3} | animator={4} | ativo={5}",
                    smr.gameObject.scene.name,
                    PathOf(smr.transform),
                    smr.sharedMesh != null ? smr.sharedMesh.name : "-",
                    smr.sharedMaterial != null ? smr.sharedMaterial.name : "-",
                    anim != null ? (anim.name + " ctrl=" + (anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "-")) : "-",
                    smr.gameObject.activeInHierarchy));
            }

            sb.AppendLine();
            sb.AppendLine("--- Animators com controller (personagens possíveis):");
            foreach (var a in Resources.FindObjectsOfTypeAll<Animator>())
            {
                if (a == null || a.gameObject.scene.name == null || a.runtimeAnimatorController == null) continue;
                sb.AppendLine("  " + PathOf(a.transform) + " | ctrl=" + a.runtimeAnimatorController.name + " | avatar=" + (a.avatar != null ? a.avatar.name : "-") + " | ativo=" + a.gameObject.activeInHierarchy);
            }

            var path = Path.Combine(Paths.BepInExRootPath, "JalopyCoop-dump.txt");
            File.AppendAllText(path, sb.ToString() + Environment.NewLine);
            Plugin.Log.LogInfo("Dump #" + dumpCount + " gravado em " + path);
        }

        private static void DumpTagged(StringBuilder sb, string tag)
        {
            sb.AppendLine();
            GameObject go = null;
            try { go = GameObject.FindWithTag(tag); } catch (Exception e) { sb.AppendLine("Tag " + tag + " erro: " + e.Message); }
            if (go == null)
            {
                sb.AppendLine("--- Tag '" + tag + "': não encontrado");
                return;
            }
            sb.AppendLine("--- Tag '" + tag + "': " + PathOf(go.transform) + " pos=" + go.transform.position);
            DumpTree(sb, go.transform, 1);
        }

        private static void DumpTree(StringBuilder sb, Transform t, int depth)
        {
            var indent = new string(' ', depth * 2);
            var comps = t.GetComponents<Component>();
            var names = new StringBuilder();
            foreach (var c in comps)
            {
                if (c == null || c is Transform) continue;
                if (names.Length > 0) names.Append(", ");
                names.Append(c.GetType().Name);
                var r = c as Renderer;
                if (r != null) names.Append("(on=" + r.enabled + ")");
            }
            sb.AppendLine(indent + t.name + (t.gameObject.activeSelf ? "" : " [inativo]") + " : " + names);
            if (depth > 12) return;
            for (int i = 0; i < t.childCount; i++)
            {
                DumpTree(sb, t.GetChild(i), depth + 1);
            }
        }

        private static string PathOf(Transform t)
        {
            var s = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                s = t.name + "/" + s;
            }
            return s;
        }
    }
}
