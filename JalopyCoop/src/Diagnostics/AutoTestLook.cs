using System.Collections;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Roteiro "aparencia" (-coop-autotest=aparencia): pintura, adesivo e sujeira do carro.
    /// Cada lado faz no carro o mesmo que o jogo faz (PaintCanC muda a cor do material; o adesivo usa
    /// ExtraUpgradesC.NewDecal; o barro/esponja mudam o alpha da camada de sujeira) e o outro lado confere.
    ///  3 s host pinta o capô de vermelho        ->  6 s tio confere
    ///  7 s tio pinta a porta esq. de azul metálico -> 10 s host confere
    /// 11 s tio põe o adesivo 2 (verde)           -> 14 s host confere
    /// 15 s host suja a frente e uma roda (barro) -> 18 s tio confere
    /// 19 s tio limpa a frente (esponja)          -> 22 s host confere (e a roda continua suja)
    /// 25 s fim
    /// </summary>
    public partial class AutoTest
    {
        private static readonly Color TestRed = new Color(0.85f, 0.1f, 0.1f, 1f);
        private static readonly Color TestBlue = new Color(0.1f, 0.2f, 0.9f, 1f);
        private static readonly Color TestGreen = new Color(0.1f, 0.8f, 0.2f, 1f);

        private static Renderer R(GameObject go) { return go != null ? go.GetComponent<Renderer>() : null; }
        private static Renderer Bonnet { get { var l = GameRefs.CarLogic; return l != null && l.bonnetObj != null ? R(l.bonnetObj.transform.parent.gameObject) : null; } }
        private static Renderer LDoor { get { var l = GameRefs.CarLogic; return l != null && l.leftDoor != null ? R(l.leftDoor.transform.parent.gameObject) : null; } }
        private static Renderer FrontDirt { get { var l = GameRefs.CarLogic; return l != null ? R(l.frntDirtObj) : null; } }
        private static Renderer WheelDirt { get { var l = GameRefs.CarLogic; return l != null ? R(l.flWheelDirt) : null; } }

        private static bool Near(Color a, Color b) { return Mathf.Abs(a.r - b.r) < 0.03f && Mathf.Abs(a.g - b.g) < 0.03f && Mathf.Abs(a.b - b.b) < 0.03f; }
        private static string Txt(Color c) { return "(" + c.r.ToString("0.00") + "," + c.g.ToString("0.00") + "," + c.b.ToString("0.00") + "," + c.a.ToString("0.00") + ")"; }

        private void PaintFlat(Renderer r, Color c, string what)
        {
            if (r == null) { Line("sem " + what); return; }
            r.material.color = c;
            r.material.SetColor("_SpecColor", Color.black);
            Line("pintei " + what + " " + Txt(c));
        }

        private void PaintMetal(Renderer r, Color c, string what)
        {
            if (r == null) { Line("sem " + what); return; }
            r.material.SetColor("_SpecColor", c);
            Line("pintei " + what + " (metálico) " + Txt(c));
        }

        private void SetDirt(Renderer r, float a, string what)
        {
            if (r == null) { Line("sem " + what); return; }
            r.material.SetAlpha(a);
            Line("sujeira " + what + " = " + a.ToString("0.00"));
        }

        private void CheckColor(Renderer r, Color want, string name)
        {
            if (r == null) { Check(name, false, "sem peça"); return; }
            var c = r.material.color;
            Check(name, Near(c, want), "cor=" + Txt(c) + " esperado=" + Txt(want));
        }

        private void CheckSpec(Renderer r, Color want, string name)
        {
            if (r == null) { Check(name, false, "sem peça"); return; }
            var c = r.material.GetColor("_SpecColor");
            Check(name, Near(c, want), "metálico=" + Txt(c) + " esperado=" + Txt(want));
        }

        private void CheckDirt(Renderer r, float want, string name)
        {
            if (r == null) { Check(name, false, "sem camada"); return; }
            float a = r.material.color.a;
            Check(name, Mathf.Abs(a - want) < 0.03f, "alpha=" + a.ToString("0.00") + " esperado=" + want.ToString("0.00"));
        }

        private void CheckDecal(int want, Color col, string name)
        {
            var l = GameRefs.CarLogic;
            var up = l != null ? l.GetComponent<ExtraUpgradesC>() : null;
            if (up == null) { Check(name, false, "sem ExtraUpgradesC"); return; }
            Check(name, up.installedDecalID == want && Near(up.decalColour, col), "id=" + up.installedDecalID + " cor=" + Txt(up.decalColour));
        }

        private IEnumerator CarShot(string name)
        {
            yield return ObserverShot(GameRefs.Car, name, 7f);
        }

        private IEnumerator HostLookScript()
        {
            float t0 = Time.time;
            var startBonnet = Bonnet != null ? Bonnet.material.color : Color.white;
            Line("capô no começo " + Txt(startBonnet));
            yield return Until(t0, 3f);
            PaintFlat(Bonnet, TestRed, "capô");

            yield return Until(t0, 10f);
            CheckSpec(LDoor, TestBlue, "host vê a porta esquerda azul metálica (tio pintou)");
            yield return Until(t0, 14f);
            CheckDecal(2, TestGreen, "host vê o adesivo 2 verde (tio pôs)");
            yield return CarShot("01_host_adesivo_porta");

            yield return Until(t0, 15f);
            SetDirt(FrontDirt, 0.85f, "frente");
            SetDirt(WheelDirt, 0.7f, "roda dianteira esq.");

            yield return Until(t0, 22f);
            CheckDirt(FrontDirt, 0f, "host vê a frente limpa (tio limpou)");
            CheckDirt(WheelDirt, 0.7f, "host: roda continua suja");
            yield return CarShot("02_host_final");
            yield return Finish(t0, 25f);
        }

        private IEnumerator ClientLookScript()
        {
            float t0 = Time.time;
            yield return Until(t0, 6f);
            CheckColor(Bonnet, TestRed, "tio vê o capô vermelho (host pintou)");
            CheckSpec(Bonnet, Color.black, "tio: capô sem metálico");
            yield return CarShot("01_tio_capo");

            yield return Until(t0, 7f);
            PaintMetal(LDoor, TestBlue, "porta esquerda");

            yield return Until(t0, 11f);
            var l = GameRefs.CarLogic;
            var up = l != null ? l.GetComponent<ExtraUpgradesC>() : null;
            var menu = MainMenuC.Global;
            if (up != null && menu != null && menu.decalMatCatalogue != null && menu.decalMatCatalogue.Length > 2)
            {
                up.decalColour = TestGreen;
                up.installedDecalID = 2;
                up.NewDecal(menu.decalMatCatalogue[2]);
                Line("pus o adesivo 2 verde");
            }
            else Line("não deu para pôr o adesivo (catálogo de adesivos " + (menu != null && menu.decalMatCatalogue != null ? menu.decalMatCatalogue.Length.ToString() : "null") + ")");

            yield return Until(t0, 18f);
            CheckDirt(FrontDirt, 0.85f, "tio vê a frente suja (host sujou)");
            CheckDirt(WheelDirt, 0.7f, "tio vê a roda suja (host sujou)");
            yield return CarShot("02_tio_sujo");

            yield return Until(t0, 19f);
            SetDirt(FrontDirt, 0f, "frente (limpei)");

            yield return Until(t0, 22f);
            yield return CarShot("03_tio_final");
            yield return Finish(t0, 25f);
        }
    }
}
