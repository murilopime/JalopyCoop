using System;
using System.IO;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4c: rádio igual para os dois.
    ///
    /// No jogo a música toca sempre (lista sorteada em cada jogo, RadioFreqLogicC); ligar/desligar e o botão de
    /// volume só sobem/descem o volume. Os botões vão pelo ControlSync (como os faróis).
    /// Aqui: o host manda qual faixa toca e em que segundo (1x/s e na troca); o tio toca a mesma faixa e
    /// corrige se ficar mais de 0,5 s fora.
    /// </summary>
    public class RadioSync : MonoBehaviour
    {
        private const float SendInterval = 1f;
        private const float MaxDrift = 0.5f;

        private float timer;
        private string lastClip;

        private void Start()
        {
            CoopNet.Instance.OnReliableData += OnReliableData;
        }

        private static RadioFreqLogicC Radio
        {
            get
            {
                var cl = GameRefs.CarLogic;
                if (cl != null && cl.radioFreq != null)
                {
                    var r = cl.radioFreq.GetComponent<RadioFreqLogicC>();
                    if (r != null) return r;
                }
                var car = GameRefs.Car;
                return car != null ? car.root.GetComponentInChildren<RadioFreqLogicC>(true) : null;
            }
        }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null || net.Mode != CoopMode.Host || !net.Connected || !GameRefs.InGame) return;
            var radio = Radio;
            var src = radio != null ? radio.GetComponent<AudioSource>() : null;
            if (src == null || src.clip == null) return;

            timer += Time.unscaledDeltaTime;
            bool changed = src.clip.name != lastClip;
            if (!changed && timer < SendInterval) return;
            timer = 0f;
            lastClip = src.clip.name;
            string name = src.clip.name;
            float time = src.time;
            net.SendReliable(Msg.RadioTrack, w => { w.Write(name); w.Write(time); });
            if (changed) Plugin.Log.LogInfo("Rádio: faixa " + name);
        }

        private void OnReliableData(Msg type, BinaryReader r)
        {
            if (type != Msg.RadioTrack) return;
            try
            {
                string name = r.ReadString();
                float time = r.ReadSingle();
                if (CoopNet.Instance.Mode != CoopMode.Client || !GameRefs.InGame) return;
                var radio = Radio;
                var src = radio != null ? radio.GetComponent<AudioSource>() : null;
                if (src == null) return;
                time += Mathf.Max(0, CoopNet.Instance.PingMs) / 2000f; // o host já andou meio ping

                if (src.clip == null || src.clip.name != name)
                {
                    var clip = FindClip(radio, name);
                    if (clip == null) { Plugin.Log.LogWarning("Rádio: faixa " + name + " não achada no tio."); return; }
                    src.clip = clip;
                    src.Play();
                    src.time = Mathf.Clamp(time, 0f, Mathf.Max(0f, clip.length - 0.1f));
                    // a próxima da lista do tio vira a mesma do host
                    if (radio.songShuffle.Count > 0) radio.songShuffle[0] = clip;
                    Plugin.Log.LogInfo("Rádio: tocando a faixa do host " + name);
                    return;
                }
                if (!src.isPlaying) src.Play();
                if (Mathf.Abs(src.time - time) > MaxDrift && time < src.clip.length - 0.1f) src.time = time;
            }
            catch (Exception e) { Plugin.Log.LogError("RadioSync: " + e); }
        }

        private static AudioClip FindClip(RadioFreqLogicC radio, string name)
        {
            foreach (var list in new[] { radio.songListings, radio.countrySpecificIndents, radio.intermissionListings })
            {
                if (list == null) continue;
                foreach (var c in list) if (c != null && c.name == name) return c;
            }
            foreach (var c in radio.songShuffle) if (c != null && c.name == name) return c;
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>()) if (c != null && c.name == name) return c;
            return null;
        }
    }
}
