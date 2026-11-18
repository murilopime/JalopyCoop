using System.Collections;
using HarmonyLib;
using JalopyCoop.Sync;
using UnityEngine;

namespace JalopyCoop.Diagnostics
{
    /// <summary>
    /// Roteiro "porta" (-coop-autotest=porta): dinheiro por baixo da porta do motel.
    ///  2 s host "bate" numa porta que dá dinheiro (como o SketchResponse: SlideCashUnderDoor + já deu)
    ///  5 s tio confere: a mesma porta marcada e a nota aparece aqui; o tio pega a nota
    /// 10 s host confere: a nota dele sumiu e o dinheiro subiu uma vez só; tio confere o dinheiro igual
    /// </summary>
    public partial class AutoTest
    {
        private static float Money()
        {
            var d = GameObject.FindWithTag("Director");
            var w = d != null ? d.GetComponent<DirectorC>().wallet.GetComponent<WalletC>() : null;
            return w != null ? w.TotalWealth + w.incomingFunds : -1f;
        }

        private static bool Given(DoorKnockingC d) { return Traverse.Create(d).Field("hasGivenMoney").GetValue<bool>(); }

        private static int CashNear(Vector3 p)
        {
            int n = 0;
            foreach (var c in Object.FindObjectsOfType<LooseCashC>()) if ((c.transform.position - p).sqrMagnitude < 9f) n++;
            return n;
        }

        private DoorKnockingC cashDoor;

        /// <summary>
        /// Portas do motel e o tipo de hóspede (0 normal, 1 dá dinheiro, 2 bravo) — tem que ser igual nos dois.
        /// Se nenhuma dá dinheiro neste save, o teste marca a MESMA porta (a de menor posição) como "dá dinheiro"
        /// dos dois lados, para poder testar.
        /// </summary>
        private void PrepareDoors()
        {
            var doors = new System.Collections.Generic.List<DoorKnockingC>(Object.FindObjectsOfType<DoorKnockingC>());
            doors.Sort((a, b) => { var pa = a.transform.position; var pb = b.transform.position; int c = pa.x.CompareTo(pb.x); return c != 0 ? c : pa.z.CompareTo(pb.z); });
            var sb = new System.Text.StringBuilder();
            bool any = false;
            foreach (var d in doors) { sb.Append(d.personalityType); if (d.personalityType == 1) any = true; }
            Line("hóspedes das portas (ordem por posição): " + sb + " (" + doors.Count + " portas)");
            if (!any && doors.Count > 0) { doors[0].SetDoorToShady(); Line("nenhuma dá dinheiro neste save: marquei " + doors[0].name + " " + doors[0].transform.position); }
        }
        private float money0;

        private IEnumerator HostDoorCashScript()
        {
            float t0 = Time.time;
            money0 = Money();
            PrepareDoors();
            int shady = 0;
            foreach (var d in Object.FindObjectsOfType<DoorKnockingC>())
            {
                if (d.personalityType != 1) continue;
                shady++;
                if (cashDoor == null && !Given(d)) cashDoor = d;
            }
            Line("portas que dão dinheiro aqui: " + shady + ", dinheiro " + money0);
            yield return Until(t0, 2f);
            if (cashDoor == null) { Check("tem porta que dá dinheiro", false, "nenhuma"); yield return Finish(t0, 14f); yield break; }
            Traverse.Create(cashDoor).Field("director").SetValue(GameObject.FindWithTag("Director"));
            cashDoor.SlideCashUnderDoor();
            Traverse.Create(cashDoor).Field("hasGivenMoney").SetValue(true);
            Line("host bateu em " + cashDoor.name + " em " + cashDoor.transform.position + ": nota " + CashNear(cashDoor.transform.position));
            yield return Until(t0, 10f);
            Check("host: a nota sumiu (o tio pegou)", CashNear(cashDoor.transform.position) == 0, "notas perto da porta=" + CashNear(cashDoor.transform.position));
            float m = Money();
            Check("host: dinheiro subiu uma vez", m > money0 + 1f && m < money0 + 60f, money0 + " -> " + m);
            yield return Finish(t0, 14f);
        }

        private IEnumerator ClientDoorCashScript()
        {
            float t0 = Time.time;
            money0 = Money();
            PrepareDoors();
            int shady = 0;
            foreach (var d in Object.FindObjectsOfType<DoorKnockingC>()) if (d.personalityType == 1) shady++;
            Line("portas que dão dinheiro aqui: " + shady + ", dinheiro " + money0);
            yield return Until(t0, 5f);
            foreach (var d in Object.FindObjectsOfType<DoorKnockingC>()) if (d.personalityType == 1 && Given(d)) { cashDoor = d; break; }
            Check("tio: a mesma porta marcada como 'já deu dinheiro'", cashDoor != null, cashDoor != null ? cashDoor.name + " " + cashDoor.transform.position : "nenhuma");
            LooseCashC cash = null;
            if (cashDoor != null)
                foreach (var c in Object.FindObjectsOfType<LooseCashC>()) if ((c.transform.position - cashDoor.transform.position).sqrMagnitude < 9f) { cash = c; break; }
            Check("tio: a nota apareceu aqui", cash != null, cash != null ? "valor " + cash.cashValue : "sem nota");
            yield return Shot("01_tio_nota");
            if (cash != null) { cash.StartCoroutine(cash.PickUp()); Line("tio pegou a nota"); }
            yield return Until(t0, 12f);
            float m = Money();
            Check("tio: dinheiro subiu uma vez", m > money0 + 1f && m < money0 + 60f, money0 + " -> " + m);
            Check("tio: a porta não dá dinheiro de novo", cashDoor != null && Given(cashDoor), "já deu=" + (cashDoor != null && Given(cashDoor)));
            yield return Finish(t0, 14f);
        }
    }
}
