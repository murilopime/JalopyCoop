using System;
using System.Collections.Generic;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Aparência do carro igual nos dois:
    ///  - Pintura (PaintCanC): cor (_Color) e metálico (_SpecColor) de cada peça pintável — as mesmas que o save
    ///    guarda (MainMenuC.LoadCarPaintJob): capô, portas, porta-malas, teto, faróis, interior e a carroceria
    ///    (PaintCanRelayC.targets).
    ///  - Adesivo (ExtraComponentC.isCustomDecal): ExtraUpgradesC.installedDecalID + decalColour, aplicado com o
    ///    NewDecal do jogo (material do MainMenuC.decalMatCatalogue, como ao carregar o save).
    ///  - Sujeira (barro, esponja, limpador): transparência (alpha do _Color) das camadas de sujeira da lataria,
    ///    rodas e vidros (CarLogicC.*DirtObj etc.).
    /// Cada lado olha 4x por segundo; o que mudou aqui vai para o outro, que só copia o valor.
    /// </summary>
    public class CarLookSync : MonoBehaviour
    {
        private const float Interval = 0.25f;
        private const float ColorEps = 0.01f;
        private const float DirtEps = 0.02f;

        private float timer;
        private CarLogicC builtFor;
        private GameObject[] builtWheels; // a sujeira da roda é do pneu: troca quando o pneu é posto
        private readonly List<Renderer> paint = new List<Renderer>();
        private readonly List<Renderer> dirt = new List<Renderer>();
        private Color[] knownColor, knownSpec;
        private float[] knownDirt;
        private int knownDecal = int.MinValue;
        private Color knownDecalColor;

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => builtFor = null;
        }

        private static void Add(List<Renderer> list, GameObject go)
        {
            if (go == null) return;
            var r = go.GetComponent<Renderer>();
            if (r != null && !list.Contains(r)) list.Add(r);
        }

        private static GameObject Parent(GameObject go)
        {
            return go != null && go.transform.parent != null ? go.transform.parent.gameObject : null;
        }

        private bool Build()
        {
            var logic = GameRefs.CarLogic;
            if (logic == null) return false;
            var wheels = new[] { logic.flWheelDirt, logic.frWheelDirt, logic.rrWheelDirt, logic.rlWheelDirt };
            if (builtFor == logic && builtWheels != null && SameRefs(builtWheels, wheels)) return true;
            builtWheels = wheels;
            paint.Clear(); dirt.Clear();
            // mesma ordem nos dois jogos (vem dos campos do CarLogicC, não de busca)
            Add(paint, Parent(logic.bonnetObj));
            Add(paint, Parent(logic.leftDoor));
            Add(paint, Parent(logic.rightDoor));
            Add(paint, Parent(logic.bootLid));
            Add(paint, logic.roof);
            Add(paint, logic.lHLight);
            Add(paint, logic.rHLight);
            Add(paint, logic.carInterior);
            Add(paint, logic.lDoorInterior);
            Add(paint, logic.rDoorInterior);
            Add(paint, logic.trunkInterior);
            var relay = logic.GetComponent<PaintCanRelayC>();
            if (relay != null) foreach (var t in relay.targets) Add(paint, t);

            foreach (var go in new[]
            {
                logic.frntDirtObj, logic.rearDirtObj, logic.frntlsideDirtObj, logic.doorlsideDirtObj, logic.doorlsideDirtObj2,
                logic.rearlsideDirtObj, logic.frntrsideDirtObj, logic.doorrsideDirtObj, logic.doorrsideDirtObj2, logic.rearrsideDirtObj,
                logic.flWheelDirt, logic.frWheelDirt, logic.rrWheelDirt, logic.rlWheelDirt,
                logic.frontWindowDirtRim, logic.frontWindowDirtWipers, logic.frontWindowDirtSmudge,
                logic.frontLeftWindowObj, logic.frontRightWindowObj, logic.rearLeftWindowObj, logic.rearRightWindowObj, logic.rearWindowObject,
            }) Add(dirt, go);

            knownColor = new Color[paint.Count];
            knownSpec = new Color[paint.Count];
            for (int i = 0; i < paint.Count; i++) { knownColor[i] = Col(paint[i]); knownSpec[i] = Spec(paint[i]); }
            knownDirt = new float[dirt.Count];
            for (int i = 0; i < dirt.Count; i++) knownDirt[i] = Alpha(dirt[i]);
            var up = logic.GetComponent<ExtraUpgradesC>();
            knownDecal = up != null ? up.installedDecalID : 0;
            knownDecalColor = up != null ? up.decalColour : Color.clear;
            builtFor = logic;
            Plugin.Log.LogInfo("Aparência: " + paint.Count + " peças pintáveis, " + dirt.Count + " camadas de sujeira.");
            return true;
        }

        private static bool SameRefs(GameObject[] a, GameObject[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static Color Col(Renderer r) { var m = r.material; return m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white; }
        private static Color Spec(Renderer r) { var m = r.material; return m.HasProperty("_SpecColor") ? m.GetColor("_SpecColor") : Color.black; }
        private static float Alpha(Renderer r) { var m = r.material; return m.HasProperty("_Color") ? m.GetColor("_Color").a : 0f; }

        private static bool Differs(Color a, Color b, float eps)
        {
            return Mathf.Abs(a.r - b.r) > eps || Mathf.Abs(a.g - b.g) > eps || Mathf.Abs(a.b - b.b) > eps || Mathf.Abs(a.a - b.a) > eps;
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !GameRefs.InGame) return;
            timer += Time.unscaledDeltaTime;
            if (timer < Interval) return;
            timer = 0f;
            try
            {
                if (!Build()) return;
                // pintura
                var changed = new List<int>();
                for (int i = 0; i < paint.Count; i++)
                {
                    if (paint[i] == null) continue;
                    var c = Col(paint[i]); var s = Spec(paint[i]);
                    if (!Differs(c, knownColor[i], ColorEps) && !Differs(s, knownSpec[i], ColorEps)) continue;
                    knownColor[i] = c; knownSpec[i] = s;
                    changed.Add(i);
                }
                if (changed.Count > 0)
                {
                    var idx = changed.ToArray();
                    var cols = new Color[idx.Length]; var specs = new Color[idx.Length];
                    for (int k = 0; k < idx.Length; k++) { cols[k] = knownColor[idx[k]]; specs[k] = knownSpec[idx[k]]; }
                    net.SendReliable(Msg.CarLook, w =>
                    {
                        w.Write((byte)1); w.Write(idx.Length);
                        for (int k = 0; k < idx.Length; k++) { w.Write(idx[k]); PutColor(w, cols[k]); PutColor(w, specs[k]); }
                    });
                }
                // sujeira
                var dirtChanged = new List<int>();
                for (int i = 0; i < dirt.Count; i++)
                {
                    if (dirt[i] == null) continue;
                    float a = Alpha(dirt[i]);
                    if (Mathf.Abs(a - knownDirt[i]) < DirtEps && !(a == 0f && knownDirt[i] != 0f)) continue;
                    knownDirt[i] = a;
                    dirtChanged.Add(i);
                }
                if (dirtChanged.Count > 0)
                {
                    var idx = dirtChanged.ToArray();
                    var vals = new float[idx.Length];
                    for (int k = 0; k < idx.Length; k++) vals[k] = knownDirt[idx[k]];
                    net.SendReliable(Msg.CarLook, w =>
                    {
                        w.Write((byte)2); w.Write(idx.Length);
                        for (int k = 0; k < idx.Length; k++) { w.Write(idx[k]); w.Write(vals[k]); }
                    });
                }
                // adesivo
                var up = builtFor.GetComponent<ExtraUpgradesC>();
                if (up != null && (up.installedDecalID != knownDecal || Differs(up.decalColour, knownDecalColor, ColorEps)))
                {
                    knownDecal = up.installedDecalID; knownDecalColor = up.decalColour;
                    int id = knownDecal; var col = knownDecalColor;
                    net.SendReliable(Msg.CarLook, w => { w.Write((byte)3); w.Write(id); PutColor(w, col); });
                    Plugin.Log.LogInfo("Aparência: pus o adesivo " + id + ".");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("CarLookSync: " + e); }
        }

        private static void PutColor(BinaryWriter w, Color c) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
        private static Color GetColor(BinaryReader r) { return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.CarLook) return;
            try
            {
                byte kind = r.ReadByte();
                bool ok = GameRefs.InGame && Build();
                if (kind == 1)
                {
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        int i = r.ReadInt32(); var c = GetColor(r); var s = GetColor(r);
                        if (!ok || i < 0 || i >= paint.Count || paint[i] == null) continue;
                        var m = paint[i].material;
                        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
                        if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", s);
                        knownColor[i] = c; knownSpec[i] = s;
                    }
                }
                else if (kind == 2)
                {
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        int i = r.ReadInt32(); float a = r.ReadSingle();
                        if (!ok || i < 0 || i >= dirt.Count || dirt[i] == null) continue;
                        dirt[i].material.SetAlpha(a);
                        knownDirt[i] = a;
                    }
                }
                else if (kind == 3)
                {
                    int id = r.ReadInt32(); var col = GetColor(r);
                    if (!ok) return;
                    var up = builtFor.GetComponent<ExtraUpgradesC>();
                    var menu = MainMenuC.Global;
                    if (up == null || menu == null || menu.decalMatCatalogue == null || id < 0 || id >= menu.decalMatCatalogue.Length) return;
                    up.decalColour = id == 0 ? new Color(0f, 0f, 0f, 0f) : col;
                    up.installedDecalID = id;
                    up.NewDecal(menu.decalMatCatalogue[id]);
                    knownDecal = id; knownDecalColor = up.decalColour;
                    Plugin.Log.LogInfo("Aparência: o outro jogador pôs o adesivo " + id + ".");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("CarLookSync: " + e); }
        }
    }
}
