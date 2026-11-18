using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace JalopyCoop.Net
{
    /// <summary>
    /// Salas na rede local (e em VPNs como Radmin/Hamachi, que passam broadcast).
    ///  - HOST com sala criada (Hospedando, sem tio): anuncia 1x/s por UDP na porta 7779:
    ///    "JALOPYCOOP|protocolo|porta|id|nome". Manda para 255.255.255.255, para o broadcast de cada IP da máquina
    ///    (/24; 26.x do Radmin = /8) e para 127.0.0.1 (dois jogos no mesmo PC).
    ///  - TIO desligado com a janela F7 aberta: escuta a porta 7779 e monta a lista (some se ficar 4 s sem anúncio).
    /// Pela internet sem VPN o anúncio não chega: aí o tio digita o IP (como antes).
    /// </summary>
    public class RoomDiscovery : MonoBehaviour
    {
        public const int DiscoveryPort = 7779;
        private const string Tag = "JALOPYCOOP";

        public static RoomDiscovery Instance { get; private set; }

        public class Room { public string Id; public string Ip; public int Port; public string Name; public int Protocol; public float Seen; }
        public readonly List<Room> Rooms = new List<Room>();

        /// <summary>A janela F7 liga isso enquanto está aberta (o tio só escuta com ela aberta).</summary>
        public bool Listening;
        public string RoomName = "Jalopy Co-op";

        private UdpClient sender;
        private UdpClient listener;
        private float sendTimer;
        private readonly string roomId = Guid.NewGuid().ToString("N").Substring(0, 8); // uma por jogo aberto
        private List<IPEndPoint> targets;
        private string listenError;
        private int received;
        private bool loggedSockErr;
        public string ListenError { get { return listenError; } }

        private void Awake() { Instance = this; }

        private void Update()
        {
            var net = CoopNet.Instance;
            if (net == null) return;
            bool announce = net.Mode == CoopMode.Host && !net.Connected;
            if (announce) Announce(net); else CloseSender();

            bool listen = Listening && net.Mode == CoopMode.Off;
            if (listen) Listen(); else CloseListener();
            for (int i = Rooms.Count - 1; i >= 0; i--)
                if (Time.realtimeSinceStartup - Rooms[i].Seen > 4f) Rooms.RemoveAt(i);
        }

        private void OnDestroy() { CloseSender(); CloseListener(); }

        // ================= host =================

        private void Announce(CoopNet net)
        {
            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer > 0f) return;
            sendTimer = 1f;
            try
            {
                if (sender == null)
                {
                    sender = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    sender.EnableBroadcast = true;
                    sender.Client.Blocking = false;
                    targets = Targets();
                    Plugin.Log.LogInfo("Sala: anunciando na rede (" + targets.Count + " destinos, porta " + DiscoveryPort + ").");
                }
                string name = (RoomName ?? "").Replace("|", "/");
                if (name.Length > 40) name = name.Substring(0, 40);
                var data = Encoding.UTF8.GetBytes(Tag + "|" + CoopNet.ProtocolVersion + "|" + net.Port + "|" + roomId + "|" + name);
                foreach (var t in targets)
                {
                    try { sender.Send(data, data.Length, t); } catch { } // uma placa sem rede não pode parar as outras
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Sala: não deu para anunciar: " + e.Message);
                CloseSender();
                sendTimer = 10f;
            }
        }

        private static List<IPEndPoint> Targets()
        {
            var list = new List<IPEndPoint>();
            var seen = new HashSet<string>();
            Action<IPAddress> add = ip => { if (seen.Add(ip.ToString())) list.Add(new IPEndPoint(ip, DiscoveryPort)); };
            add(IPAddress.Broadcast);
            add(IPAddress.Loopback);
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ip.GetAddressBytes();
                    if (b[0] == 26) add(new IPAddress(new byte[] { 26, 255, 255, 255 })); // Radmin VPN (26.0.0.0/8)
                    else add(new IPAddress(new byte[] { b[0], b[1], b[2], 255 }));
                }
            }
            catch { }
            return list;
        }

        private void CloseSender()
        {
            if (sender != null) { try { sender.Close(); } catch { } sender = null; }
        }

        // ================= tio =================

        private void Listen()
        {
            if (listener == null)
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); // dois jogos no mesmo PC
                    s.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    s.Blocking = false;
                    listener = new UdpClient();
                    listener.Client = s;
                    listenError = null;
                    received = 0;
                    Plugin.Log.LogInfo("Sala: procurando salas na porta " + DiscoveryPort + "...");
                }
                catch (Exception e)
                {
                    if (listenError == null) Plugin.Log.LogWarning("Sala: não deu para procurar salas na porta " + DiscoveryPort + ": " + e.Message);
                    listenError = e.Message;
                    CloseListener();
                    return;
                }
            }
            try
            {
                for (int i = 0; i < 20 && listener.Available > 0; i++)
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = listener.Receive(ref from);
                    if (received++ < 3) Plugin.Log.LogInfo("Sala: anúncio recebido de " + from + " (" + data.Length + " bytes).");
                    Parse(Encoding.UTF8.GetString(data), from);
                }
            }
            catch (SocketException e) { if (e.SocketErrorCode != SocketError.WouldBlock && !loggedSockErr) { loggedSockErr = true; Plugin.Log.LogWarning("Sala: erro na porta " + DiscoveryPort + ": " + e.SocketErrorCode + " " + e.Message); } }
            catch (Exception e) { Plugin.Log.LogWarning("Sala: erro lendo anúncio: " + e.Message); }
        }

        /// <summary>Melhor caminho para o mesmo host: rede de casa > VPN (Radmin 26.x) > outros > 127.0.0.1.</summary>
        private static int Rank(string ip)
        {
            if (ip.StartsWith("127.")) return 0;
            if (ip.StartsWith("192.168.") || ip.StartsWith("10.")) return 3;
            if (ip.StartsWith("172."))
            {
                var parts = ip.Split('.'); int b;
                if (parts.Length > 1 && int.TryParse(parts[1], out b) && b >= 16 && b <= 31) return 3;
            }
            if (ip.StartsWith("26.")) return 2;
            return 1;
        }

        private void Parse(string msg, IPEndPoint from)
        {
            // JALOPYCOOP|protocolo|porta|id da sala|nome — o id junta os caminhos do mesmo host (rede de casa, VPN, 127.0.0.1)
            var p = msg.Split(new[] { '|' }, 5);
            if (p.Length < 5 || p[0] != Tag) return;
            int proto, port;
            if (!int.TryParse(p[1], out proto) || !int.TryParse(p[2], out port)) return;
            string ip = from.Address.ToString(), id = p[3], name = p[4];
            foreach (var r in Rooms)
            {
                if (r.Id != id) continue;
                if (Rank(ip) > Rank(r.Ip)) r.Ip = ip;
                r.Seen = Time.realtimeSinceStartup;
                r.Protocol = proto; r.Port = port; r.Name = name;
                return;
            }
            Rooms.Add(new Room { Id = id, Ip = ip, Port = port, Name = name, Protocol = proto, Seen = Time.realtimeSinceStartup });
            Plugin.Log.LogInfo("Sala: achei \"" + name + "\" em " + ip + ":" + port + " (protocolo " + proto + ").");
        }

        private void CloseListener()
        {
            if (listener != null) { Plugin.Log.LogInfo("Sala: parei de procurar (" + received + " anúncios recebidos, " + Rooms.Count + " salas)."); try { listener.Close(); } catch { } listener = null; }
            if (!Listening) Rooms.Clear();
        }
    }
}
