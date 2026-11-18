using System;
using HarmonyLib;
using UnityEngine;
using JalopyCoop.Net;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Volume "dentro do carro" (DNC_soundEffect.inCarVolume). O jogo só liga esse aviso quando uma porta ou um vidro
    /// FECHA e o banco do motorista já está ocupado; qualquer porta ou vidro abrindo desliga. No co-op a ordem muda
    /// (a porta do tio abre e fecha pelo DoorSync, o tio senta pelo mod) e o aviso ficava desligado com os dois dentro:
    /// a chuva de fora tocava a 100% em vez de 20%, recomeçando a cada passo físico ("chiado cortado").
    /// Aqui, só no co-op conectado: ligado = o jogador LOCAL está sentado no carro (motorista ou passageiro) e as duas
    /// portas e os dois vidros estão fechados. Recalcula 2x/s (só referências diretas, sem varrer a cena) e logo depois
    /// de cada porta/vidro fechar.
    /// </summary>
    public class CarAmbienceFix : MonoBehaviour
    {
        private float next;

        private void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            Recompute("2x/s");
            WatchSeat();
        }

        // Diagnóstico (só log): ao sentar/levantar, mostra o som da chuva na hora e 4 s depois.
        private bool? lastSeated;
        private float snapAt = -1f;

        private void WatchSeat()
        {
            var net = CoopNet.Instance;
            if (net == null || !net.Connected) { lastSeated = null; return; }
            bool now = LocalSeated(net);
            var rg = RouteGeneratorC.Global;
            bool raining = rg != null && rg.rainType > 0;
            if (lastSeated.HasValue && lastSeated.Value != now && raining)
            {
                Plugin.Log.LogInfo("Som: " + (now ? "sentei" : "levantei") + " com chuva; " + Describe() + ".");
                snapAt = Time.unscaledTime + 4f;
            }
            lastSeated = now;
            if (snapAt > 0f && Time.unscaledTime >= snapAt)
            {
                snapAt = -1f;
                Plugin.Log.LogInfo("Som: 4 s depois (" + (now ? "sentado" : "fora do carro") + "); " + Describe() + ".");
            }
        }

        internal static void Recompute(string why)
        {
            try
            {
                var net = CoopNet.Instance;
                if (net == null || !net.Connected) return; // jogo normal: o jogo cuida
                var rg = RouteGeneratorC.Global;
                var cl = GameRefs.CarLogic;
                if (rg == null || rg.uDayCycle == null || cl == null || cl.playerSeat == null) return;
                var sfx = rg.uDayCycle.GetComponent<DNC_soundEffect>();
                var seat = cl.playerSeat.GetComponent<SeatLogicC>();
                if (sfx == null || seat == null || seat.driverDoor == null) return;

                bool want = LocalSeated(net) && AllClosed(seat.driverDoor.GetComponent<DoorLogicC>());
                if (sfx.inCarVolume == want) return;
                sfx.inCarVolume = want;
                Plugin.Log.LogInfo("Som: volume de dentro do carro " + (want ? "LIGADO" : "desligado") + " (" + why + ").");
            }
            catch (Exception e) { Plugin.Log.LogError("CarAmbienceFix: " + e); }
        }

        /// <summary>Para o log: volume de dentro, fontes de chuva de fora, sentado e portas/vidros.</summary>
        internal static string Describe()
        {
            try
            {
                var rg = RouteGeneratorC.Global;
                var cl = GameRefs.CarLogic;
                var sfx = rg != null && rg.uDayCycle != null ? rg.uDayCycle.GetComponent<DNC_soundEffect>() : null;
                var seat = cl != null && cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
                var driver = seat != null && seat.driverDoor != null ? seat.driverDoor.GetComponent<DoorLogicC>() : null;
                var pass = PassengerSeat.PassengerDoor();
                var net = CoopNet.Instance;
                string s = "dentroDoCarro=" + (sfx != null && sfx.inCarVolume);
                if (sfx != null) s += " dia=" + Src(sfx.DaySource) + " noite=" + Src(sfx.NightSource);
                s += " sentado=" + (net != null && LocalSeated(net));
                s += " portaMot=" + (driver != null && driver.open) + " portaPas=" + (pass != null && pass.open);
                if (driver != null) s += " vidroMot=" + WindowOpen(driver.windowScriptTrgt) + " vidroPas=" + WindowOpen(driver.passengerWindowScriptTrgt);
                if (seat != null) s += " bancoChuva=" + seat.rainAudioPlay + "/" + Src(seat.GetComponent<AudioSource>());
                return s;
            }
            catch (Exception e) { return "(erro " + e.Message + ")"; }
        }

        private static string Src(AudioSource a)
        {
            if (a == null) return "-";
            string s = (a.clip != null ? a.clip.name : "sem som") + " vol " + a.volume.ToString("0.00") + (a.isPlaying ? " tocando" : " parado");
            var cam = Camera.main;
            float dist = cam != null ? Vector3.Distance(cam.transform.position, a.transform.position) : -1f;
            s += " [3D " + a.spatialBlend.ToString("0.0") + ", a " + dist.ToString("0") + " m (máx " + a.maxDistance.ToString("0") + ")"
                + (a.mute ? ", MUDO" : "") + (a.playOnAwake ? "" : ", sem playOnAwake") + ", t " + a.time.ToString("0.00") + "]";
            return s;
        }

        internal static bool LocalSeated(CoopNet net)
        {
            if (net.Mode == CoopMode.Host)
            {
                var ds = DriveSync.Instance;
                return ds != null && ds.HostSeatedNow; // corpo do host sentado (motorista ou passageiro)
            }
            var us = UncleSync.Instance;
            return us != null && us.IsSeated;
        }

        /// <summary>Portas do motorista e do passageiro e os dois vidros fechados (os mesmos que o jogo confere).</summary>
        private static bool AllClosed(DoorLogicC driver)
        {
            if (driver == null || driver.open) return false;
            var pass = PassengerSeat.PassengerDoor();
            if (pass == null && driver.passengerDoor != null) pass = driver.passengerDoor.GetComponent<DoorLogicC>();
            if (pass != null && pass.open) return false;
            if (WindowOpen(driver.windowScriptTrgt) || WindowOpen(driver.passengerWindowScriptTrgt)) return false;
            return true;
        }

        private static bool WindowOpen(GameObject go)
        {
            var w = go != null ? go.GetComponent<WindowLogicC>() : null;
            return w != null && w.isOpen;
        }
    }

    [HarmonyPatch(typeof(DoorLogicC), "DoorClosed")]
    internal static class DoorClosedAmbiencePatch
    {
        private static void Postfix(DoorLogicC __instance)
        {
            if (!__instance.isEnvironmentalDoor) CarAmbienceFix.Recompute("porta fechou");
        }
    }

    [HarmonyPatch(typeof(WindowLogicC), "WindowClosed")]
    internal static class WindowClosedAmbiencePatch
    {
        private static void Postfix() { CarAmbienceFix.Recompute("vidro fechou"); }
    }

    /// <summary>
    /// SetLight/HeavyRainAudio desliga e liga as duas fontes (o som recomeça do zero) e o RainCheck chama isso em todo
    /// passo físico. Pula SÓ quando a chuva já está tocando com o som certo nas duas fontes. Se algo trocou o som
    /// (SetFogAudio ao passar por um abrigo para a fonte) ou a fonte parou, o original roda como sempre.
    /// (Pular o setter inteiro, como no RainFix antigo, deixava a fonte parada e a chuva sumia.)
    /// </summary>
    [HarmonyPatch]
    internal static class RainAudioRestartPatch
    {
        /// <summary>
        /// DESLIGADO (2026-11-16, teste manual): sem o recomeço aparece o "loop" do som de água da chuva de fora, que o
        /// jogo normal nunca deixa tocar inteiro. O chiado era o volume de dentro do carro desligado (corrigido acima);
        /// com ele em 20% o recomeço fica como no jogo normal.
        /// </summary>
        internal const bool SkipRestart = false;

        [HarmonyPatch(typeof(DNC_soundEffect), "SetLightRainAudio")]
        [HarmonyPrefix]
        private static bool Light(DNC_soundEffect __instance)
        {
            return !AlreadyPlaying(__instance, __instance.LightRainEffect, __instance.LightRainEffect);
        }

        [HarmonyPatch(typeof(DNC_soundEffect), "SetHeavyRainAudio")]
        [HarmonyPrefix]
        private static bool Heavy(DNC_soundEffect __instance)
        {
            return !AlreadyPlaying(__instance, __instance.HeavyRainEffect, __instance.LightRainEffect);
        }

        private static bool AlreadyPlaying(DNC_soundEffect s, AudioClip day, AudioClip night)
        {
            try
            {
                if (!SkipRestart) return false; // DESLIGADO: o jogo normal recomeça a chuva de fora a cada passo (ver abaixo)
                var net = CoopNet.Instance;
                if (net == null || !net.Connected) return false; // jogo normal: como sempre
                return Playing(s.DaySource, day) && Playing(s.NightSource, night);
            }
            catch { return false; }
        }

        private static bool Playing(AudioSource a, AudioClip clip)
        {
            return a != null && clip != null && a.clip == clip && a.enabled && a.isActiveAndEnabled && a.isPlaying;
        }
    }

    /// <summary>
    /// Som da água batendo no carro (SeatLogicC, "rainIndoor") debaixo de túnel/abrigo. O CarLogicC só baixa esse som
    /// quando rainLvl > 0 e o raio dele acerta um teto, mas debaixo do teto o RainCheck já pôs rainLvl = 0
    /// (SetDryWindows), então o som nunca baixava (log: túnel com bancoChuva vol 1.00). Aqui, só no co-op: o RainCheck
    /// manda. Dentro (teto) → para de chover no carro, baixa em ~2 s; fora com chuva → volta (e toca de novo se tinha parado).
    /// </summary>
    [HarmonyPatch(typeof(RainCheck), "Outside", MethodType.Setter)]
    internal static class InteriorRainShelterPatch
    {
        internal static bool Sheltered;
        internal const float ExtraFade = 0.4f; // por segundo, além dos 0.1 do jogo

        private static void Postfix(RainCheck __instance, bool value)
        {
            try
            {
                var net = CoopNet.Instance;
                if (net == null || !net.Connected) { Sheltered = false; return; }
                var rg = __instance.route;
                if (rg == null || rg.seatLogic == null) return;
                var seat = rg.seatLogic.GetComponent<SeatLogicC>();
                if (seat == null) return;
                if (rg.rainType <= 0) { Sheltered = false; return; }
                if (!value)
                {
                    if (!Sheltered) Plugin.Log.LogInfo("Chuva: debaixo de teto, som da água no carro baixando.");
                    Sheltered = true;
                    seat.rainAudioPlay = false;
                    return;
                }
                if (Sheltered && CarAmbienceFix.LocalSeated(net))
                {
                    Sheltered = false;
                    seat.rainAudioPlay = true;
                    var a = seat.GetComponent<AudioSource>();
                    if (a != null && !a.isPlaying) a.Play();
                    Plugin.Log.LogInfo("Chuva: saiu do teto, som da água no carro voltando.");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("InteriorRainShelter: " + e); }
        }
    }

    /// <summary>Deixa o som da água no carro baixar/subir em ~2 s (o jogo leva 10 s) quando o teto muda.</summary>
    [HarmonyPatch(typeof(SeatLogicC), "Update")]
    internal static class InteriorRainFadePatch
    {
        private static void Postfix(SeatLogicC __instance)
        {
            if (!InteriorRainShelterPatch.Sheltered && __instance.rainAudioPlay == false) return;
            var a = __instance.GetComponent<AudioSource>();
            if (a == null) return;
            float d = InteriorRainShelterPatch.ExtraFade * Time.deltaTime;
            if (InteriorRainShelterPatch.Sheltered && !__instance.rainAudioPlay && a.volume > 0f) a.volume = Mathf.Max(0f, a.volume - d);
            else if (!InteriorRainShelterPatch.Sheltered && __instance.rainAudioPlay && a.volume < 1f && a.isPlaying) a.volume = Mathf.Min(1f, a.volume + d);

            // O jogo PARA esse som quando ele chega perto de 0 e só toca de novo quando alguém senta. Se o raio do
            // CarLogicC pegou a beira de um teto (posto), o som parava e nunca mais voltava (teste: chuva sumiu depois
            // do posto). No co-op: chovendo no carro e alguém daqui sentado → toca de novo, subindo do zero.
            if (!InteriorRainShelterPatch.Sheltered && __instance.rainAudioPlay && !a.isPlaying) Replay(__instance, a);
        }

        private static float nextTry;

        private static void Replay(SeatLogicC seat, AudioSource a)
        {
            if (Time.unscaledTime < nextTry) return;
            nextTry = Time.unscaledTime + 0.5f;
            var net = CoopNet.Instance;
            if (net == null || !net.Connected || !CarAmbienceFix.LocalSeated(net)) return;
            var cl = seat.transform.parent != null ? seat.transform.parent.GetComponent<CarLogicC>() : null;
            if (cl == null || cl.rainLvl <= 0) return;
            a.volume = 0.05f;
            a.Play();
            Plugin.Log.LogInfo("Chuva: som da água no carro tinha parado; tocando de novo.");
        }
    }
}
