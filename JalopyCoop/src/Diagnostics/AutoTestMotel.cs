using System.Collections;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Roteiro "motel" (-coop-autotest=motel): dormir só com os dois deitados.
    ///  2 s host deita                  -> 6 s host confere: o dia NÃO passou (esperando)
    ///  7 s tio deita, 9 s tio levanta  -> 12 s host confere: tio de pé e o dia não passou
    /// 13 s tio deita de novo           -> os dois deitados: o dia passa nos dois
    /// 42 s os dois conferem: dia +1, manhã, ninguém deitado; escrevem as opções de rota do dia novo (comparar)
    /// </summary>
    public partial class AutoTest
    {
        private static BedLogicC TestBed()
        {
            var motel = Object.FindObjectOfType<MotelLogicC>();
            if (motel != null && motel.roomBeds != null && motel.roomNumber >= 0 && motel.roomNumber < motel.roomBeds.Length && motel.roomBeds[motel.roomNumber] != null)
                return motel.roomBeds[motel.roomNumber].GetComponent<BedLogicC>();
            return Object.FindObjectOfType<BedLogicC>();
        }

        private static int Days()
        {
            var d = GameObject.FindWithTag("Director");
            return d != null ? d.GetComponent<DirectorC>().daysPassed : -1;
        }

        private static float Hour() { var c = TimeSync.Clock; return c != null ? c._timeInSeconds / 3600f : -1f; }

        private IEnumerator HostMotelScript()
        {
            float t0 = Time.time;
            SteeringLook = false;
            int day0 = Days();
            var bed = TestBed();
            Line("dia " + day0 + ", hora " + Hour().ToString("0.00") + ", cama " + (bed != null ? bed.name + " " + bed.transform.position : "nenhuma"));
            yield return Until(t0, 2f);
            if (bed != null) { bed.StartCoroutine("Trigger"); Line("host deitou"); }
            yield return Until(t0, 6f);
            var ms = MotelSync.Instance;
            Check("host deitado e esperando (dia não passou)", ms != null && ms.LocalLying && !ms.Sleeping && Days() == day0, "deitado=" + (ms != null && ms.LocalLying) + " dia=" + Days());
            yield return Shot("01_host_deitado");
            yield return Until(t0, 8.5f);
            Check("host vê o tio deitado", ms != null && ms.RemoteLying, "tio deitado=" + (ms != null && ms.RemoteLying));
            var u = GameRefs.Uncle;
            var ua = u != null ? u.GetComponent<Animator>() : null;
            Check("host: corpo do tio deitado (animação bedTime)", ua != null && ua.GetBool("bedTime"), "bedTime=" + (ua != null && ua.GetBool("bedTime")) + " pos=" + (u != null ? u.transform.position.ToString() : "-"));
            yield return ObserverShot(UncleT(), "01b_tio_deitado_visto_pelo_host");
            yield return Until(t0, 12f);
            Check("tio levantou e o dia não passou", ms != null && !ms.RemoteLying && !ms.Sleeping && Days() == day0, "tio deitado=" + (ms != null && ms.RemoteLying) + " dia=" + Days());
            yield return Until(t0, 16f);
            Check("os dois deitados: dormindo", ms != null && ms.Sleeping, "dormindo=" + (ms != null && ms.Sleeping));
            yield return Until(t0, 42f);
            CheckNewDay(day0);
            yield return Shot("02_host_dia_novo");
            yield return Finish(t0, 46f);
        }

        private IEnumerator ClientMotelScript()
        {
            float t0 = Time.time;
            SteeringLook = false;
            int day0 = Days();
            var bed = TestBed();
            Line("dia " + day0 + ", hora " + Hour().ToString("0.00") + ", cama " + (bed != null ? bed.name + " " + bed.transform.position : "nenhuma"));
            yield return Until(t0, 5.5f);
            var pup = GameObject.Find("JalopyCoop_Host");
            var head = pup != null ? FindBone(pup.transform, "Head") : null;
            float dh = head != null && bed != null && bed.path.Length > 1 ? Vector3.Distance(head.position, bed.path[1].position) : 99f;
            Check("tio: corpo do host deitado na cama (cabeça no travesseiro)", dh < 1f, "cabeça-travesseiro=" + dh.ToString("0.00") + "m");
            yield return ObserverShot(HostPuppetT(), "00_host_deitado_visto_pelo_tio");
            yield return Until(t0, 7f);
            if (bed != null) { bed.StartCoroutine("Trigger"); Line("tio deitou"); }
            yield return Until(t0, 9.5f);
            var ms = MotelSync.Instance;
            if (ms != null) { ms.TestGetUp(); Line("tio levantou"); }
            yield return Until(t0, 13f);
            if (bed != null) { bed.StartCoroutine("Trigger"); Line("tio deitou de novo"); }
            yield return Until(t0, 16f);
            Check("tio: dormindo junto com o host", ms != null && ms.Sleeping, "dormindo=" + (ms != null && ms.Sleeping));
            yield return Until(t0, 42f);
            CheckNewDay(day0);
            yield return Shot("02_tio_dia_novo");
            yield return Finish(t0, 46f);
        }

        private void CheckNewDay(int day0)
        {
            var ms = MotelSync.Instance;
            float h = Hour();
            Check("dia passou (+1)", Days() == day0 + 1, "dia " + day0 + " -> " + Days());
            Check("é de manhã", h > 4.9f && h < 7f, "hora " + h.ToString("0.00"));
            Check("ninguém deitado depois de acordar", ms != null && !ms.LocalLying && !ms.Sleeping, "deitado=" + (ms != null && ms.LocalLying) + " dormindo=" + (ms != null && ms.Sleeping));
            var rg = RouteGeneratorC.Global;
            Line("opções de rota do dia novo: " + (rg != null ? RouteSync.OptionsFingerprint(rg) : "-"));
            Line("routeLevel " + (rg != null ? rg.routeLevel.ToString() : "-"));
        }
    }
}
