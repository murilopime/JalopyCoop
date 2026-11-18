using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Ferramenta de investigação: mostra os modelos de motorista escondidos no jogo (Laika_Driver)
    /// na frente do jogador e tira fotos deles com uma câmera auxiliar (sem mexer na tela do jogador).
    /// F9 liga/desliga a prévia. Também roda sozinho uma vez, 20 s depois de entrar no jogo.
    /// Fotos: BepInEx/JalopyCoop-shots/*.png
    /// </summary>
    public class ModelPreview : MonoBehaviour
    {
        private static readonly string[] ModelNames = { "Laika_Driver", "Laika_Driver_02", "Uncle" };

        private readonly List<GameObject> clones = new List<GameObject>();
        private bool autoDone;

        private void OnEnable() { SceneManager.sceneLoaded += OnSceneLoaded; }
        private void OnDisable() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!autoDone && scene.name.StartsWith("Scn"))
            {
                autoDone = true;
                StartCoroutine(AutoPreview());
            }
        }

        private IEnumerator AutoPreview()
        {
            yield return new WaitForSeconds(20f);
            yield return StartCoroutine(ShowAndShoot());
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (clones.Count > 0) ClearClones();
                else StartCoroutine(ShowAndShoot());
            }
        }

        private void ClearClones()
        {
            foreach (var c in clones) if (c != null) Destroy(c);
            clones.Clear();
            Plugin.Log.LogInfo("Prévia de modelos removida.");
        }

        private static Animator FindSceneAnimator(string name)
        {
            foreach (var a in Resources.FindObjectsOfTypeAll<Animator>())
            {
                if (a != null && a.name == name && a.gameObject.scene.name != null) return a;
            }
            return null;
        }

        private IEnumerator ShowAndShoot()
        {
            var cam = Camera.main;
            if (cam == null) { Plugin.Log.LogWarning("Prévia: sem câmera principal."); yield break; }

            ClearClones();
            var log = new StringBuilder();
            log.AppendLine("==== Prévia de modelos " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // Lugar no chão à frente do jogador.
            var fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            var basePos = cam.transform.position + fwd * 3f;
            RaycastHit hit;
            if (Physics.Raycast(basePos + Vector3.up * 2f, Vector3.down, out hit, 10f)) basePos = hit.point;
            else basePos.y -= 1.6f;

            foreach (var name in ModelNames)
            {
                var a = FindSceneAnimator(name);
                if (a == null) log.AppendLine(name + ": não encontrado");
                else LogAnimator(log, a);
            }

            var uncleSrc = FindSceneAnimator("Uncle");

            // Para cada motorista: pose própria (parado), andando e sentado dirigindo (animações do tio).
            // Fileira de trás: Laika_Driver (capacete?). Fileira da frente: Laika_Driver_02 (óculos?).
            var models = new[] { "Laika_Driver", "Laika_Driver_02" };
            var poses = new[] { "parado", "andando", "dirigindo" };
            var variants = new List<string>();
            var dir = Path.Combine(Paths.BepInExRootPath, "JalopyCoop-shots");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("HHmmss");

            for (int m = 0; m < models.Length; m++)
            for (int p = 0; p < poses.Length; p++)
            {
                var driverSrc = FindSceneAnimator(models[m]);
                if (driverSrc == null) continue;
                int i = variants.Count;
                variants.Add(models[m] + "_" + poses[p]);
                var clone = (GameObject)Instantiate(driverSrc.gameObject);
                clone.name = "JalopyCoop_Preview_" + variants[i];
                clone.transform.parent = null;
                clone.transform.localScale = driverSrc.transform.lossyScale;
                clone.transform.position = basePos + right * ((p - 1) * 1.6f) + fwd * (m == 0 ? 1.8f : 0f);
                clone.transform.rotation = Quaternion.LookRotation(-fwd, Vector3.up);
                clone.SetActive(true);
                foreach (var col in clone.GetComponentsInChildren<Collider>()) col.enabled = false;
                var anim = clone.GetComponent<Animator>();
                if (p > 0 && uncleSrc != null && anim != null)
                {
                    anim.runtimeAnimatorController = uncleSrc.runtimeAnimatorController;
                    anim.Rebind();
                    if (poses[p] == "andando") anim.SetFloat("walkSpeed", 1f);
                    if (poses[p] == "dirigindo") { anim.SetBool("enterCar", true); anim.SetFloat("carSpeed", 40f); }
                }
                clones.Add(clone);
                log.AppendLine("Clone " + variants[i] + " em " + clone.transform.position);
            }

            // Espera as animações chegarem na pose (entrar no carro leva alguns segundos).
            yield return new WaitForSeconds(5f);

            // Foto de cada clone, enquadrada pelo tamanho real do modelo.
            for (int i = 0; i < clones.Count; i++)
            {
                for (int k = 0; k < clones.Count; k++) SetVisible(clones[k], k == i); // só o clone da foto aparece (sem reiniciar a animação)
                yield return null;
                var b = BoundsOf(clones[i]);
                ShootBounds(b, -fwd, Path.Combine(dir, stamp + "_" + variants[i] + "_frente.png"));
                ShootBounds(b, right, Path.Combine(dir, stamp + "_" + variants[i] + "_lado.png"));
                log.AppendLine(variants[i] + ": tamanho " + b.size);
            }
            // Foto do grupo.
            foreach (var c in clones) SetVisible(c, true);
            yield return null;
            if (clones.Count == 0) yield break;
            var all = BoundsOf(clones[0]);
            for (int i = 1; i < clones.Count; i++) all.Encapsulate(BoundsOf(clones[i]));
            ShootBounds(all, -fwd, Path.Combine(dir, stamp + "_grupo.png"));

            // Também mostra o motorista original no lugar dele (dentro do carro), se existir.
            var driver = FindSceneAnimator("Laika_Driver");
            if (driver != null)
            {
                bool wasActive = driver.gameObject.activeSelf;
                driver.gameObject.SetActive(true);
                for (int f = 0; f < 10; f++) yield return null;
                var db = BoundsOf(driver.gameObject);
                ShootBounds(db, driver.transform.right, Path.Combine(dir, stamp + "_no_carro_lado.png"));
                ShootBounds(db, driver.transform.forward, Path.Combine(dir, stamp + "_no_carro_frente.png"));
                log.AppendLine("Laika_Driver original: tamanho " + db.size);
                log.AppendLine("Laika_Driver original: pos " + driver.transform.position + " pai " + (driver.transform.parent != null ? driver.transform.parent.name : "-") + " ativo antes=" + wasActive);
                driver.gameObject.SetActive(wasActive);
            }

            log.AppendLine("Fotos em " + dir + " (prefixo " + stamp + ")");
            File.AppendAllText(Path.Combine(Paths.BepInExRootPath, "JalopyCoop-dump.txt"), log.ToString() + Environment.NewLine);
            Plugin.Log.LogInfo("Prévia pronta: fotos " + stamp + " em " + dir + ". F9 remove.");
        }

        private static void LogAnimator(StringBuilder log, Animator a)
        {
            log.Append(a.name + " | ctrl=" + (a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "-") + " | params: ");
            try
            {
                foreach (var p in a.parameters) log.Append(p.name + ":" + p.type + " ");
            }
            catch (Exception e) { log.Append("(erro " + e.Message + ")"); }
            log.AppendLine();
            if (a.runtimeAnimatorController != null)
            {
                log.Append("   clips: ");
                foreach (var c in a.runtimeAnimatorController.animationClips) if (c != null) log.Append(c.name + " ");
                log.AppendLine();
            }
        }

        private static void SetVisible(GameObject go, bool on)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
        }

        private static Bounds BoundsOf(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = new Bounds(go.transform.position + Vector3.up, Vector3.one * 0.5f);
            bool first = true;
            foreach (var r in rs)
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue; // ignora partículas etc.
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        /// <summary>Fotografa a caixa inteira, olhando na direção oposta a 'side' (câmera fica do lado 'side').</summary>
        private static void ShootBounds(Bounds b, Vector3 side, string file)
        {
            side.y = 0;
            side.Normalize();
            const float fov = 40f;
            float size = Mathf.Max(b.size.y, Mathf.Max(b.size.x, b.size.z) * 0.75f);
            float dist = (size * 0.5f) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.35f + b.extents.magnitude * 0.3f;
            var from = b.center + side * dist + Vector3.up * size * 0.1f;
            Shoot(from, b.center, file, fov);
        }

        private static void Shoot(Vector3 from, Vector3 lookAt, string file, float fov = 50f)
        {
            var go = new GameObject("JalopyCoop_PreviewCam");
            var cam = go.AddComponent<Camera>();
            go.transform.position = from;
            go.transform.LookAt(lookAt);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.enabled = false;
            var rt = new RenderTexture(960, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(960, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 960, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Destroy(tex);
            Destroy(rt);
            Destroy(go);
        }
    }
}
