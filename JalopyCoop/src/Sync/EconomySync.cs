using System;
using System.IO;
using System.Text;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Preços do país (mercadorias): a loja da cidade (StoreC.SetValues, pelo país = routeLevel) sorteia os preços e
    /// grava no DirectorC (supplyNumbers, supplyCatalogueSellPrices/BuyPrices). Com o sorteio fixo sai igual, mas o
    /// host confere: manda a tabela dele quando muda; se a do tio estiver diferente, o tio copia e refaz as etiquetas
    /// de preço (ObjectPickupC.SetPriceTag) e os preços do porta-malas (CarLogicC.UpdateBootPrices).
    /// </summary>
    public class EconomySync : MonoBehaviour
    {
        private float timer;
        private uint lastSent;

        private void Start()
        {
            var net = CoopNet.Instance;
            net.OnReliableData += OnReliableData;
            net.OnConnectionChanged += c => lastSent = 0;
        }

        private static DirectorC Director
        {
            get { var d = GameObject.FindWithTag("Director"); return d != null ? d.GetComponent<DirectorC>() : null; }
        }

        private static uint Hash(DirectorC d, int level)
        {
            var sb = new StringBuilder();
            sb.Append(level).Append('|');
            if (d.supplyNumbers != null) foreach (var n in d.supplyNumbers) sb.Append(n).Append(',');
            sb.Append('|');
            if (d.supplyCatalogueSellPrices != null) foreach (var v in d.supplyCatalogueSellPrices) sb.Append(Mathf.RoundToInt(v * 10f)).Append(',');
            sb.Append('|');
            if (d.supplyCatalogueBuyPrices != null) foreach (var v in d.supplyCatalogueBuyPrices) sb.Append(Mathf.RoundToInt(v * 10f)).Append(',');
            return SeededRandom.Hash(sb.ToString());
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || net.Mode != CoopMode.Host || !GameRefs.InGame) return;
            timer += Time.unscaledDeltaTime;
            if (timer < 2f) return;
            timer = 0f;
            try
            {
                var d = Director;
                var rg = RouteGeneratorC.Global;
                if (d == null || rg == null || !d.economyReady || d.supplyNumbers == null || d.supplyCatalogueSellPrices == null || d.supplyCatalogueBuyPrices == null) return;
                uint h = Hash(d, rg.routeLevel);
                if (h == lastSent) return;
                lastSent = h;
                int level = rg.routeLevel;
                var nums = (int[])d.supplyNumbers.Clone();
                var sell = (float[])d.supplyCatalogueSellPrices.Clone();
                var buy = (float[])d.supplyCatalogueBuyPrices.Clone();
                net.SendReliable(Msg.Economy, w =>
                {
                    w.Write(h); w.Write(level);
                    w.Write(nums.Length); foreach (var n in nums) w.Write(n);
                    w.Write(sell.Length); foreach (var v in sell) w.Write(v);
                    w.Write(buy.Length); foreach (var v in buy) w.Write(v);
                });
                Plugin.Log.LogInfo("Preços: tabela do país " + level + " mandada (#" + h.ToString("X8") + ").");
            }
            catch (Exception e) { Plugin.Log.LogError("EconomySync: " + e); }
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.Economy) return;
            uint h = r.ReadUInt32(); int level = r.ReadInt32();
            var nums = new int[r.ReadInt32()]; for (int i = 0; i < nums.Length; i++) nums[i] = r.ReadInt32();
            var sell = new float[r.ReadInt32()]; for (int i = 0; i < sell.Length; i++) sell[i] = r.ReadSingle();
            var buy = new float[r.ReadInt32()]; for (int i = 0; i < buy.Length; i++) buy[i] = r.ReadSingle();
            if (CoopNet.Instance.Mode != CoopMode.Client || !GameRefs.InGame) return;
            try
            {
                var d = Director;
                var rg = RouteGeneratorC.Global;
                if (d == null || rg == null) return;
                uint mine = Hash(d, rg.routeLevel);
                if (mine == h) { Plugin.Log.LogInfo("Preços: iguais ao host (país " + level + ", #" + h.ToString("X8") + ")."); return; }
                Plugin.Log.LogWarning("Preços: diferentes do host (meu #" + mine.ToString("X8") + ", host #" + h.ToString("X8") + "): copiando.");
                d.supplyNumbers = nums;
                d.supplyCatalogueSellPrices = sell;
                d.supplyCatalogueBuyPrices = buy;
                d.economyReady = true;
                foreach (var op in FindObjectsOfType<ObjectPickupC>()) { try { op.SetPriceTag(); } catch { } }
                var logic = GameRefs.CarLogic;
                if (logic != null) { try { logic.UpdateBootPrices(); } catch { } }
            }
            catch (Exception e) { Plugin.Log.LogError("EconomySync: " + e); }
        }
    }
}
