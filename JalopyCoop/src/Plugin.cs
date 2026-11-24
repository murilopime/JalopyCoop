using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JalopyCoop.Diagnostics;
using JalopyCoop.Net;
using JalopyCoop.Sync;
using JalopyCoop.UI;
using UnityEngine;

namespace JalopyCoop
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "jalopycoop";
        public const string Name = "Jalopy Co-op";
        public const string Version = "0.1.2";

        internal static ManualLogSource Log;
        /// <summary>
        /// Atalhos de teclado (F5 volante, F6 sentar/levantar) só para testes: -coop-devkeys.
        /// No jogo normal tudo é pelas interações do jogo (portas, bancos, saídas). Pedido do usuário.
        /// </summary>
        public static bool DevKeys;
        /// <summary>-coop-traffic=N (0-4): trânsito usado pelo jogo nos trechos, sem mudar o save. -1 = o do save.</summary>
        public static int TrafficOverride = -1;

        private ConfigEntry<string> hostAddress;
        private ConfigEntry<int> port;
        private ConfigEntry<bool> diagnostics;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo(Name + " " + Version + " carregado.");

            hostAddress = Config.Bind("Rede", "IPDoHost", "127.0.0.1", "IP do host para o jogador-tio se conectar.");
            port = Config.Bind("Rede", "Porta", 7777, "Porta UDP usada pelo co-op.");
            diagnostics = Config.Bind("Debug", "Diagnostico", false, "Liga as ferramentas de investigação (F8 dump, F9 prévia de modelos).");

            // As duas janelas precisam continuar rodando mesmo sem foco (teste no mesmo PC e alt-tab).
            Application.runInBackground = true;

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            SeededRandom.Install(harmony); // 4d: mundo sorteado igual nos dois jogos

            var go = new GameObject("JalopyCoop");
            DontDestroyOnLoad(go);
            var net = go.AddComponent<CoopNet>();
            go.AddComponent<CarSync>();
            go.AddComponent<UncleSync>();
            var drive = go.AddComponent<DriveSync>();
            go.AddComponent<PassengerSeat>();
            go.AddComponent<ItemSync>();
            go.AddComponent<DoorSync>();
            go.AddComponent<TyreSync>();
            go.AddComponent<PartSync>();
            go.AddComponent<ControlSync>();
            go.AddComponent<RadioSync>();
            go.AddComponent<RouteSync>();
            go.AddComponent<TimeSync>();
            go.AddComponent<ShopSync>();
            go.AddComponent<BoxSync>();
            go.AddComponent<EnvDoorSync>();
            go.AddComponent<CarLookSync>();
            go.AddComponent<CityCheck>();
            go.AddComponent<MotelSync>();
            go.AddComponent<SaveTransfer>();
            go.AddComponent<InteractSync>();
            go.AddComponent<BorderSync>();
            go.AddComponent<EconomySync>();
            go.AddComponent<RoutePlanSync>();
            go.AddComponent<TrafficSync>();
            go.AddComponent<CarAmbienceFix>();
            go.AddComponent<UpgradeSync>();
            go.AddComponent<HomeStorageSync>();
            go.AddComponent<ReturnHomeSync>();
            go.AddComponent<HostLeaveSync>();
            go.AddComponent<CoopMouseTick>(); // mouse vale 1x por quadro (câmera "teleportando")
            drive.UncleDriverOffsetConfig = Config.Bind("Visual", "AjusteTioMotorista", "0,0,0",
                "Ajuste fino (x,y,z) do tio-NPC no volante na tela do host.");
            go.AddComponent<CarControlsSync>();
            var puppet = go.AddComponent<HostPuppet>();
            puppet.SeatOffsetConfig = Config.Bind("Visual", "AjusteMotorista", "-0.08,0.16,0.10", // valor ajustado pelo usuário no teste 3
                "Ajuste fino (x,y,z em metros, espaço do carro) da posição do modelo do host sentado.");
            puppet.SeatYawConfig = Config.Bind("Visual", "GiroMotorista", 0f,
                "Giro (graus) do corpo do modelo do host sentado.");
            puppet.PassengerOffsetConfig = Config.Bind("Visual", "AjustePassageiro", "-0.48,0.26,0.16", // valor ajustado pelo usuário no teste de 2026-11-10
                "Ajuste fino (x,y,z em metros, espaço do banco) do host no passageiro quando o tio dirige.");
            puppet.PassengerYawConfig = Config.Bind("Visual", "GiroPassageiro", 0f,
                "Giro (graus) do host no passageiro.");
            go.AddComponent<DevTools>(); // F4, só com -coop-devkeys no host
            var menu = go.AddComponent<CoopMenu>();
            menu.HostAddress = hostAddress;
            menu.Port = port;
            menu.RoomName = Config.Bind("Rede", "NomeDaSala", "Jalopy Co-op", "Nome da sala que o host anuncia na rede local.");
            menu.ShowStatus = Config.Bind("Visual", "MostrarLinhaCoop", true, "Mostra a linha do co-op no alto da tela (F3 liga/desliga no jogo).");
            go.AddComponent<RoomDiscovery>().RoomName = menu.RoomName.Value;

            if (diagnostics.Value)
            {
                go.AddComponent<SceneDump>();
                go.AddComponent<ModelPreview>();
            }

            // Linha de comando (para testes): -coop-host   ou   -coop-join=127.0.0.1
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-coop-lowgfx") go.AddComponent<LowGraphics>();
                else if (arg == "-coop-continue") go.AddComponent<AutoContinue>();
                else if (arg == "-coop-devkeys") DevKeys = true;
                else if (arg.StartsWith("-coop-traffic="))
                {
                    int tv;
                    if (int.TryParse(arg.Substring("-coop-traffic=".Length), out tv) && tv >= 0 && tv <= 4) TrafficOverride = tv;
                }
                else if (arg.StartsWith("-coop-lag="))
                {
                    int lag;
                    if (int.TryParse(arg.Substring("-coop-lag=".Length), out lag) && lag > 0 && lag <= 1000) { CoopNet.LagMs = lag; Log.LogInfo("Teste: ping simulado de " + lag + " ms."); }
                }
                else if (arg == "-coop-mute")go.AddComponent<MuteAudio>();
                else if (arg.StartsWith("-coop-autotest=")) go.AddComponent<AutoTest>().Scenario = arg.Substring("-coop-autotest=".Length);
                else if (arg == "-coop-host") net.StartHost(port.Value);
                else if (arg.StartsWith("-coop-join="))
                {
                    hostAddress.Value = arg.Substring("-coop-join=".Length);
                    net.StartClient(hostAddress.Value, port.Value);
                }
            }
        }
    }
}
