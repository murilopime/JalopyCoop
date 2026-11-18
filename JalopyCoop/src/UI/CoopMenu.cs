using BepInEx.Configuration;
using JalopyCoop.Net;
using UnityEngine;

namespace JalopyCoop.UI
{
    /// <summary>
    /// Janela F7: o host cria a sala dentro do save; o tio, no menu principal, vê as salas da rede e conecta (ou digita o IP).
    /// Também a linha de status no canto.
    /// </summary>
    public class CoopMenu : MonoBehaviour
    {
        public ConfigEntry<string> HostAddress;
        public ConfigEntry<int> Port;
        public ConfigEntry<string> RoomName;
        public ConfigEntry<bool> ShowStatus;

        private bool open;
        private Rect window = new Rect(20, 60, 460, 120);
        private string ipText;
        private string portText;
        private bool cursorWasVisible;
        private CursorLockMode cursorWasLock;

        private void Start()
        {
            ipText = HostAddress.Value;
            portText = Port.Value.ToString();
        }

        // FPS médio do último meio segundo (tempo real, não muda com a pausa do jogo)
        private int fps;
        private int frames;
        private float fpsTimer;
        private bool fpsLogged;

        private void Update()
        {
            frames++;
            fpsTimer += Time.unscaledDeltaTime;
            if (fpsTimer >= 0.5f)
            {
                fps = Mathf.RoundToInt(frames / fpsTimer); frames = 0; fpsTimer = 0f;
                if (!fpsLogged && JalopyCoop.Sync.GameRefs.InGame)
                {
                    fpsLogged = true;
                    Plugin.Log.LogInfo("FPS: " + fps + " (limite do jogo " + Application.targetFrameRate + ", vsync " + QualitySettings.vSyncCount
                        + ", tela " + Screen.currentResolution.refreshRate + " Hz).");
                }
            }
            if (Input.GetKeyDown(KeyCode.F3) && ShowStatus != null) ShowStatus.Value = !ShowStatus.Value; // esconde/mostra a linha do co-op (fica salvo)
            if (Input.GetKeyDown(KeyCode.F7))
            {
                open = !open;
                if (open)
                {
                    cursorWasVisible = Cursor.visible;
                    cursorWasLock = Cursor.lockState;
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                else
                {
                    Cursor.visible = cursorWasVisible;
                    Cursor.lockState = cursorWasLock;
                }
            }
            else if (open)
            {
                // O jogo esconde o cursor sozinho; mantém visível com a janela aberta.
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private void OnGUI()
        {
            var net = CoopNet.Instance;
            if (net == null) return;

            string line = "Co-op: " + net.Status;
            if (net.Connected) line += " | ping " + net.PingMs + " ms";
            line += " | " + fps + " fps";
            line += "   [F7 menu | F3 esconde esta linha";
            line += "]";
            if (ShowStatus == null || ShowStatus.Value) GUI.Label(new Rect(10, 6, 1400, 22), line);

            // Avisos (DriveSync.Hint) não aparecem mais na tela: pedido do usuário. Vão para o log.

            if (open) window = GUILayout.Window(0x4A43, window, DrawWindow, "Jalopy Co-op", GUILayout.Width(460));
            var disc = RoomDiscovery.Instance;
            if (disc != null) disc.Listening = open && !JalopyCoop.Sync.GameRefs.InGame;
        }

        /// <summary>
        /// Dentro do save: "Criar sala" (host). No menu principal: lista das salas da rede + IP (tio).
        /// O tio conectando no menu entra sozinho no mundo do host (SaveTransfer).
        /// </summary>
        private void DrawWindow(int id)
        {
            var net = CoopNet.Instance;
            bool inGame = JalopyCoop.Sync.GameRefs.InGame;
            GUILayout.Label("Status: " + net.Status);
            GUILayout.Space(6);

            if (net.Mode == CoopMode.Host)
            {
                GUILayout.Label("Sala \"" + RoomName.Value + "\" criada. O tio abre o F7 no menu do jogo e clica Conectar.");
                if (GUILayout.Button(net.Connected ? "Fechar sala (desconecta o tio)" : "Fechar sala")) net.Stop();
            }
            else if (net.Mode == CoopMode.Client)
            {
                if (GUILayout.Button("Desconectar")) net.Stop();
            }
            else if (inGame)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Nome da sala:", GUILayout.Width(95));
                RoomName.Value = GUILayout.TextField(RoomName.Value ?? "", 40, GUILayout.Width(200));
                GUILayout.EndHorizontal();
                PortField();
                if (GUILayout.Button("Criar sala (você é o motorista)"))
                {
                    SavePort();
                    var rd = RoomDiscovery.Instance;
                    if (rd != null) rd.RoomName = RoomName.Value;
                    net.StartHost(Port.Value);
                }
                GUILayout.Label("Para entrar como TIO, volte ao menu principal do jogo e abra o F7 lá.");
            }
            else
            {
                var rd = RoomDiscovery.Instance;
                GUILayout.Label("Salas na rede:");
                if (rd != null && rd.ListenError != null) GUILayout.Label("  Não deu para procurar salas: " + rd.ListenError);
                else if (rd == null || rd.Rooms.Count == 0) GUILayout.Label("  Procurando... (o host precisa entrar no save e criar a sala)");
                else
                {
                    foreach (var r in rd.Rooms.ToArray())
                    {
                        GUILayout.BeginHorizontal();
                        bool sameVersion = r.Protocol == CoopNet.ProtocolVersion;
                        GUILayout.Label("  " + r.Name + "  (" + r.Ip + ":" + r.Port + ")" + (sameVersion ? "" : "  — versão do mod diferente!"), GUILayout.Width(330));
                        GUI.enabled = sameVersion;
                        if (GUILayout.Button("Conectar", GUILayout.Width(80)))
                        {
                            HostAddress.Value = r.Ip;
                            ipText = r.Ip;
                            net.StartClient(r.Ip, r.Port);
                        }
                        GUI.enabled = true;
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.Space(8);
                GUILayout.Label("Pela internet (sem VPN): digite o IP do host.");
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP do host:", GUILayout.Width(80));
                ipText = GUILayout.TextField(ipText, GUILayout.Width(170));
                GUILayout.EndHorizontal();
                PortField();
                if (GUILayout.Button("Conectar por IP"))
                {
                    SavePort();
                    HostAddress.Value = ipText.Trim();
                    net.StartClient(HostAddress.Value, Port.Value);
                }
            }
            GUI.DragWindow();
        }

        private void PortField()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Porta:", GUILayout.Width(80));
            portText = GUILayout.TextField(portText, GUILayout.Width(60));
            GUILayout.EndHorizontal();
        }

        private void SavePort()
        {
            int p;
            if (int.TryParse(portText, out p) && p > 0 && p < 65536) Port.Value = p;
            portText = Port.Value.ToString();
        }
    }
}
