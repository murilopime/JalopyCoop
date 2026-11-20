using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace JalopyCoop.Net
{
    public enum CoopMode { Off, Host, Client }

    /// <summary>
    /// Conexão host/cliente por IP direto, sem bibliotecas externas
    /// (o LiteNetLib não roda no Mono antigo do Jalopy: falta System.Runtime.Serialization).
    ///
    ///  - TCP na porta escolhida: aperto de mão (Hello/Welcome) e mensagens que não podem se perder.
    ///  - UDP na mesma porta: estado contínuo (carro, olhar), 20x/s, com número de sequência
    ///    para descartar pacotes velhos.
    ///
    /// Tudo roda na thread principal da Unity (sockets não bloqueantes lidos no Update).
    /// O host é a autoridade: roda o jogo de verdade e manda o estado.
    /// </summary>
    public class CoopNet : MonoBehaviour
    {
        public const int ProtocolVersion = 40;
        private const float SendInterval = 0.05f;   // 20 pacotes por segundo
        private const float TimeoutSeconds = 10f;
        private const float ConnectTimeoutSeconds = 8f;
        private const int SIO_UDP_CONNRESET = -1744830452;

        public static CoopNet Instance { get; private set; }

        public CoopMode Mode { get; private set; }
        public bool Connected { get; private set; }
        public string Status { get; private set; }
        public int PingMs { get; private set; }

        public event Action<HostStateData> OnHostState;
        public event Action<ClientStateData> OnClientState;
        public Func<HostStateData> BuildHostState;
        public Func<HostStateData, HostStateData> BuildHostStateExtra; // outros sistemas completam o pacote
        public Func<ClientStateData> BuildClientState;
        public event Action<bool> OnConnectionChanged;
        /// <summary>Mensagens confiáveis simples (só o tipo), ex.: pedido/entrega do volante.</summary>
        public event Action<Msg> OnReliable;
        /// <summary>Mensagem confiável com dados (o leitor já passou do byte de tipo).</summary>
        public event Action<Msg, BinaryReader> OnReliableData;

        private TcpListener listener;
        private TcpClient tcp;
        private IAsyncResult connecting;
        private float connectStarted;
        private UdpClient udp;
        private IPEndPoint remoteUdp;          // para onde mando UDP
        private IPAddress remoteIp;
        private int port;
        public int Port { get { return port; } }

        private readonly List<byte> tcpBuffer = new List<byte>();
        private readonly byte[] readChunk = new byte[8192];
        private readonly Dictionary<byte, uint> lastSeqIn = new Dictionary<byte, uint>();
        private uint seqOut;
        private float sendTimer;
        private float pingTimer;
        private float lastHeard;

        private void Awake()
        {
            Instance = this;
            Status = "Desligado";
            PingMs = -1;
        }

        // ================= início / fim =================

        public void StartHost(int port)
        {
            Stop();
            try
            {
                this.port = port;
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                udp = MakeUdp(port);
                Mode = CoopMode.Host;
                Status = "Hospedando na porta " + port + " | IPs: " + LocalIps() + " | aguardando o tio...";
                Plugin.Log.LogInfo(Status);
            }
            catch (Exception e)
            {
                Fail("Falha ao hospedar na porta " + port + ": " + e.Message);
            }
        }

        public void StartClient(string address, int port)
        {
            Stop();
            try
            {
                this.port = port;
                remoteIp = Resolve(address);
                udp = MakeUdp(0);
                tcp = new TcpClient();
                tcp.NoDelay = true;
                connecting = tcp.BeginConnect(remoteIp, port, null, null);
                connectStarted = Time.realtimeSinceStartup;
                remoteUdp = new IPEndPoint(remoteIp, port);
                Mode = CoopMode.Client;
                Status = "Conectando em " + remoteIp + ":" + port + "...";
                Plugin.Log.LogInfo(Status);
            }
            catch (Exception e)
            {
                Fail("Falha ao conectar em " + address + ":" + port + ": " + e.Message);
            }
        }

        public void Stop()
        {
            bool was = Connected;
            Connected = false;
            CloseTcp();
            if (listener != null) { try { listener.Stop(); } catch { } listener = null; }
            if (udp != null) { try { udp.Close(); } catch { } udp = null; }
            connecting = null;
            remoteUdp = null;
            lastSeqIn.Clear();
            Mode = CoopMode.Off;
            Status = "Desligado";
            PingMs = -1;
            if (was && OnConnectionChanged != null) OnConnectionChanged(false);
        }

        private void Fail(string msg)
        {
            Plugin.Log.LogError(msg);
            Stop();
            Status = msg;
        }

        /// <summary>Perdeu o outro lado. O host volta a esperar; o cliente desliga.</summary>
        private void Drop(string reason)
        {
            Plugin.Log.LogInfo("Desconectado: " + reason);
            bool was = Connected;
            Connected = false;
            CloseTcp();
            remoteUdp = null;
            lastSeqIn.Clear();
            PingMs = -1;
            if (Mode == CoopMode.Host)
            {
                Status = "Tio saiu (" + reason + "). Aguardando...";
            }
            else
            {
                if (udp != null) { try { udp.Close(); } catch { } udp = null; }
                Mode = CoopMode.Off;
                Status = "Desconectado (" + reason + ")";
            }
            if (was && OnConnectionChanged != null) OnConnectionChanged(false);
        }

        private void CloseTcp()
        {
            if (tcp != null) { try { tcp.Close(); } catch { } tcp = null; }
            tcpBuffer.Clear();
            lagTcp.Clear();
            lagUdp.Clear();
        }

        private static UdpClient MakeUdp(int port)
        {
            var u = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            // No Windows, um "porta inalcançável" faz o próximo Receive dar erro. Desliga isso.
            try { u.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
            u.Client.Blocking = false;
            return u;
        }

        private static IPAddress Resolve(string address)
        {
            IPAddress ip;
            if (IPAddress.TryParse(address, out ip)) return ip;
            foreach (var a in Dns.GetHostAddresses(address))
            {
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            }
            throw new Exception("não achei o IP de " + address);
        }

        // ================= laço principal =================

        private void Update()
        {
            if (Mode == CoopMode.Off) return;
            try
            {
                if (Mode == CoopMode.Host) AcceptClient();
                else FinishConnect();
                if (Mode == CoopMode.Off) return;

                ReadTcp();
                ReadUdp();
                FlushLag();
                if (Mode == CoopMode.Off) return;

                if (Connected && Time.realtimeSinceStartup - lastHeard > TimeoutSeconds)
                {
                    Drop("sem resposta há " + (int)TimeoutSeconds + " s");
                    return;
                }
                if (Connected) SendPeriodic();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Erro de rede: " + e);
                if (Mode == CoopMode.Host) Drop(e.Message);
                else Fail("Erro de rede: " + e.Message);
            }
        }

        private void AcceptClient()
        {
            if (listener == null || !listener.Pending()) return;
            var incoming = listener.AcceptTcpClient();
            if (tcp != null)
            {
                incoming.Close(); // MVP: só um tio por vez
                return;
            }
            tcp = incoming;
            tcp.NoDelay = true;
            remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
            lastHeard = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo("Conexão TCP de " + tcp.Client.RemoteEndPoint + ", aguardando Hello...");
        }

        private void FinishConnect()
        {
            if (connecting == null) return;
            if (!connecting.IsCompleted)
            {
                if (Time.realtimeSinceStartup - connectStarted > ConnectTimeoutSeconds)
                {
                    Fail("Host não respondeu em " + remoteIp + ":" + port + " (IP certo? host clicou Hospedar? firewall?)");
                }
                return;
            }
            try { tcp.EndConnect(connecting); }
            catch (Exception e) { connecting = null; Fail("Não conectou em " + remoteIp + ":" + port + ": " + e.Message); return; }
            connecting = null;
            lastHeard = Time.realtimeSinceStartup;

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((byte)Msg.Hello);
            w.Write(ProtocolVersion);
            w.Write(Environment.UserName ?? "tio");
            w.Write(((IPEndPoint)udp.Client.LocalEndPoint).Port);
            SendTcp(ms.ToArray());
            Status = "Conectado, aguardando o host aceitar...";
        }

        // ================= TCP (mensagens com tamanho) =================

        private void SendTcp(byte[] payload)
        {
            if (tcp == null) return;
            var frame = new byte[payload.Length + 4];
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
            payload.CopyTo(frame, 4);
            tcp.GetStream().Write(frame, 0, frame.Length);
        }

        private void ReadTcp()
        {
            if (tcp == null || connecting != null) return;
            var sock = tcp.Client;
            if (sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0)
            {
                Drop("conexão fechada");
                return;
            }
            while (tcp != null && sock.Available > 0)
            {
                int n = tcp.GetStream().Read(readChunk, 0, Math.Min(readChunk.Length, sock.Available));
                if (n <= 0) { Drop("conexão fechada"); return; }
                for (int i = 0; i < n; i++) tcpBuffer.Add(readChunk[i]);
            }
            while (tcpBuffer.Count >= 4)
            {
                int len = BitConverter.ToInt32(tcpBuffer.GetRange(0, 4).ToArray(), 0);
                if (len < 0 || len > 1 << 20) { Drop("pacote TCP inválido"); return; }
                if (tcpBuffer.Count < 4 + len) break;
                var payload = tcpBuffer.GetRange(4, len).ToArray();
                tcpBuffer.RemoveRange(0, 4 + len);
                lastHeard = Time.realtimeSinceStartup;
                if (LagMs > 0) lagTcp.Enqueue(new KeyValuePair<float, byte[]>(Time.realtimeSinceStartup + LagMs / 2000f, payload));
                else HandleTcp(new BinaryReader(new MemoryStream(payload)));
            }
        }

        // ================= atraso simulado (só para teste local: -coop-lag=ms) =================
        // Cada lado segura o que chega por metade do ping pedido: ping total ~ LagMs, como no online.
        // TCP sai na mesma ordem; UDP tem uma variação pequena (pode chegar fora de ordem, como na internet).

        /// <summary>Ping simulado em ms (0 = desligado). Só pela linha de comando de teste.</summary>
        public static int LagMs;
        private readonly Queue<KeyValuePair<float, byte[]>> lagTcp = new Queue<KeyValuePair<float, byte[]>>();
        private readonly List<LagPacket> lagUdp = new List<LagPacket>();
        private struct LagPacket { public float due; public byte[] data; public IPEndPoint from; }

        private void FlushLag()
        {
            if (LagMs <= 0) return;
            float now = Time.realtimeSinceStartup;
            while (lagTcp.Count > 0 && lagTcp.Peek().Key <= now && Mode != CoopMode.Off)
            {
                var payload = lagTcp.Dequeue().Value;
                HandleTcp(new BinaryReader(new MemoryStream(payload)));
            }
            for (int i = 0; i < lagUdp.Count && Mode != CoopMode.Off; )
            {
                if (lagUdp[i].due <= now) { var pk = lagUdp[i]; lagUdp.RemoveAt(i); HandleUdp(pk.data, pk.from); }
                else i++;
            }
        }

        private void HandleTcp(BinaryReader r)
        {
            var type = (Msg)r.ReadByte();
            switch (type)
            {
                case Msg.Hello:
                {
                    int version = r.ReadInt32();
                    string name = r.ReadString();
                    int clientUdpPort = r.ReadInt32();
                    remoteUdp = new IPEndPoint(remoteIp, clientUdpPort);
                    Plugin.Log.LogInfo("Hello de " + name + " (protocolo " + version + "), UDP " + remoteUdp);
                    if (version != ProtocolVersion)
                    {
                        Plugin.Log.LogWarning("Versão do mod diferente no tio: " + version + " (host " + ProtocolVersion + ")");
                    }
                    var ms = new MemoryStream();
                    var w = new BinaryWriter(ms);
                    w.Write((byte)Msg.Welcome);
                    w.Write(1);
                    SendTcp(ms.ToArray());
                    SetConnected("Tio conectado: " + name + " (" + remoteIp + ")");
                    break;
                }
                case Msg.Welcome:
                {
                    int id = r.ReadInt32();
                    SetConnected("Conectado ao host " + remoteIp + " (id " + id + ")");
                    break;
                }
                default:
                    if (Connected && OnReliable != null) OnReliable(type);
                    if (Connected && OnReliableData != null) OnReliableData(type, r);
                    break;
            }
        }

        /// <summary>Manda uma mensagem confiável (TCP) que é só o tipo.</summary>
        public void SendReliable(Msg type)
        {
            if (!Connected || tcp == null) return;
            try { SendTcp(new[] { (byte)type }); }
            catch (Exception e) { Plugin.Log.LogWarning("Falha ao enviar " + type + ": " + e.Message); }
        }

        /// <summary>Manda uma mensagem confiável (TCP) com dados.</summary>
        public void SendReliable(Msg type, Action<BinaryWriter> body)
        {
            if (!Connected || tcp == null) return;
            try
            {
                var ms = new MemoryStream();
                var w = new BinaryWriter(ms);
                w.Write((byte)type);
                body(w);
                SendTcp(ms.ToArray());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Falha ao enviar " + type + ": " + e.Message); }
        }

        /// <summary>
        /// A cena recarregou com os dois conectados (voltar para casa): avisa as sincronias como numa reconexão
        /// (false antes de recarregar, true depois), sem fechar a conexão.
        /// </summary>
        public void SessionReset(bool connected)
        {
            if (!Connected || OnConnectionChanged == null) return;
            try { OnConnectionChanged(connected); }
            catch (Exception e) { Plugin.Log.LogError("SessionReset: " + e); }
        }

        private void SetConnected(string status)
        {
            Connected = true;
            Status = status;
            lastHeard = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo(status);
            if (OnConnectionChanged != null) OnConnectionChanged(true);
        }

        // ================= UDP (estado contínuo) =================

        private void SendUdp(Msg type, Action<BinaryWriter> body)
        {
            if (udp == null || remoteUdp == null) return;
            var ms = new MemoryStream(128);
            var w = new BinaryWriter(ms);
            w.Write((byte)type);
            w.Write(++seqOut);
            body(w);
            var data = ms.ToArray();
            try { udp.Send(data, data.Length, remoteUdp); }
            catch (SocketException) { /* fila cheia ou rede caiu: o timeout cuida */ }
        }

        private void ReadUdp()
        {
            if (udp == null) return;
            while (udp != null && udp.Available > 0)
            {
                IPEndPoint from = null;
                byte[] data;
                try { data = udp.Receive(ref from); }
                catch (SocketException) { break; }
                if (remoteIp == null || !from.Address.Equals(remoteIp)) continue; // pacote de outro lugar
                if (data.Length < 5) continue;
                if (LagMs > 0)
                {
                    float d = LagMs / 2000f * UnityEngine.Random.Range(0.85f, 1.15f);
                    lagUdp.Add(new LagPacket { due = Time.realtimeSinceStartup + d, data = data, from = from });
                    lastHeard = Time.realtimeSinceStartup;
                    continue;
                }
                HandleUdp(data, from);
            }
        }

        private void HandleUdp(byte[] data, IPEndPoint from)
        {
            {

                var r = new BinaryReader(new MemoryStream(data));
                var type = (Msg)r.ReadByte();
                uint seq = r.ReadUInt32();
                uint last;
                if (lastSeqIn.TryGetValue((byte)type, out last) && seq <= last) return; // velho/duplicado
                lastSeqIn[(byte)type] = seq;
                lastHeard = Time.realtimeSinceStartup;
                if (Mode == CoopMode.Host && Connected) remoteUdp = from; // segue a porta real (NAT/VPN)
                if (!Connected) return;

                try
                {
                    switch (type)
                    {
                        case Msg.HostState:
                            if (OnHostState != null) OnHostState(HostStateData.Read(r));
                            break;
                        case Msg.ClientState:
                            if (OnClientState != null) OnClientState(ClientStateData.Read(r));
                            break;
                        case Msg.Ping:
                        {
                            float t = r.ReadSingle();
                            SendUdp(Msg.Pong, w => w.Write(t));
                            break;
                        }
                        case Msg.Pong:
                            PingMs = (int)((Time.realtimeSinceStartup - r.ReadSingle()) * 1000f);
                            break;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Pacote UDP inválido (" + type + "): " + e.Message);
                }
            }
        }

        private void SendPeriodic()
        {
            pingTimer += Time.unscaledDeltaTime;
            if (pingTimer >= 1f)
            {
                pingTimer = 0f;
                float now = Time.realtimeSinceStartup;
                SendUdp(Msg.Ping, w => w.Write(now));
            }

            sendTimer += Time.unscaledDeltaTime;
            if (sendTimer < SendInterval) return;
            sendTimer = 0f;

            if (Mode == CoopMode.Host && BuildHostState != null)
            {
                var s = BuildHostState();
                if (BuildHostStateExtra != null) s = BuildHostStateExtra(s);
                SendUdp(Msg.HostState, w => s.Write(w));
            }
            else if (Mode == CoopMode.Client && BuildClientState != null)
            {
                var s = BuildClientState();
                SendUdp(Msg.ClientState, w => s.Write(w));
            }
        }

        private void OnDestroy() { Stop(); }
        private void OnApplicationQuit()
        {
            // Host fechando o jogo: avisa o tio antes de fechar (se não chegar, a conexão caindo resolve igual)
            if (Mode == CoopMode.Host && Connected) { try { SendReliable(Msg.HostLeft); } catch { } }
            Stop();
        }

        public static string LocalIps()
        {
            try
            {
                var list = new List<string>();
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) list.Add(ip.ToString());
                }
                return list.Count > 0 ? string.Join(", ", list.ToArray()) : "?";
            }
            catch
            {
                return "?";
            }
        }
    }
}
