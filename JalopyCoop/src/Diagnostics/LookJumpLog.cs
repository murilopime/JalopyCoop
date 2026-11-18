using JalopyCoop.Net;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Teste online: "a câmera pula de repente para frente". Só no co-op conectado: se a direção da câmera muda
    /// muito num quadro sem o mouse ter mexido tanto, anota no log o que estava acontecendo (sentado, pai, mouse
    /// suave...). Só lê a câmera local; nada de varrer a cena.
    /// </summary>
    public class LookJumpLog : MonoBehaviour
    {
        private Vector3 lastFwd;
        private bool has;
        private float nextLog;
        private int logged;

        private void LateUpdate()
        {
            var net = CoopNet.Instance;
            var cam = GameRefs.Cam;
            if (net == null || !net.Connected || cam == null || !GameRefs.InGame) { has = false; return; }
            var fwd = cam.transform.forward;
            if (!has) { lastFwd = fwd; has = true; return; }
            float ang = Vector3.Angle(lastFwd, fwd);
            lastFwd = fwd;
            float mouse = Mathf.Abs(Input.GetAxis("Mouse X")) + Mathf.Abs(Input.GetAxis("Mouse Y"));
            if (ang < 40f || mouse > 8f) return;
            if (Time.realtimeSinceStartup < nextLog || logged >= 30) return;
            nextLog = Time.realtimeSinceStartup + 1f;
            logged++;

            var player = GameRefs.Player;
            var rc = player != null ? player.GetComponent<RigidbodyControllerC>() : null;
            var pml = player != null ? player.GetComponent<MouseLook>() : null;
            var cml = cam.GetComponent<MouseLook>();
            bool smooth = MainMenuC.Global != null && MainMenuC.Global.mouseSmooth;
            var ds = DriveSync.Instance;
            Plugin.Log.LogWarning("Câmera: pulou " + ang.ToString("0") + "° num quadro (mouse " + mouse.ToString("0.0") + ")"
                + " | sentado=" + (rc != null && rc.isSat)
                + " pai=" + (player != null && player.transform.parent != null ? player.transform.parent.name : "-")
                + " mouseSuave=" + smooth
                + " olhar(corpo)=" + (pml != null ? (pml.enabled ? "on" : "off") + " alvoX=" + pml.xTargetRotation.ToString("0") : "-")
                + " olhar(câmera)=" + (cml != null ? (cml.enabled ? "on" : "off") + " alvoY=" + cml.yTargetRotation.ToString("0") : "-")
                + " | " + net.Mode
                + (ds != null ? " hostPassageiro=" + ds.HostPassenger + " tioNoVolante=" + ds.RemoteDriving + " clienteNoVolante=" + ds.ClientDriving : "")
                + " fps=" + (1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime)).ToString("0"));
        }
    }
}
