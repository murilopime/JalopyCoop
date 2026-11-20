using System.IO;
using UnityEngine;

namespace JalopyCoop.Net
{
    /// <summary>Tipos de mensagem. O primeiro byte de todo pacote é um destes.</summary>
    public enum Msg : byte
    {
        // TCP (confiável)
        Hello = 1,      // cliente -> host: versão do protocolo, nome, porta UDP do cliente
        Welcome = 2,    // host -> cliente: id do jogador
        // UDP (rápido, pode perder)
        HostState = 3,  // host -> cliente, ~20x/s: carro + jogador host
        ClientState = 4,// cliente -> host, ~20x/s: olhar + assento do jogador-tio
        Ping = 5,       // ida e volta para medir o ping
        Pong = 6,
        // TCP (confiável): volante
        DriveRequest = 7, // cliente -> host: tio pede o volante
        DriveGrant = 8,   // host -> cliente: tio agora dirige
        DriveReturn = 9,  // qualquer lado: volante volta para o host
        // TCP (confiável): cliques do tio que o host executa
        UseHandbrake = 10,
        UseIgnition = 11,
        TestStart = 12,  // teste automático: host -> tio "começa o roteiro agora"
        SitDriver = 13,  // tio -> host: clicou na porta do motorista, quer sentar no volante
        DriveDeny = 14,  // host -> tio: o host está no volante
        LeaveDriver = 15, // tio -> host: saiu do volante pela porta (o host NÃO volta para o motorista)
        // TCP (confiável): itens (MVP 4a). Quem mexe no item manda; o outro lado repete.
        ItemHello = 16,    // cliente -> host: estou no jogo, manda a lista de itens
        ItemManifest = 17, // host -> cliente: lista de itens e encaixes (id, nome, posição) para casar os ids
        ItemHold = 18,     // qualquer lado: peguei o item (id)
        ItemPlace = 19,    // qualquer lado: encaixei o item (id) no encaixe (id)
        ItemPose = 20,     // qualquer lado: posição do item na mão / voando (10x/s)
        ItemFree = 21,     // qualquer lado: item parou solto no mundo (posição final)
        ItemStore = 22,    // qualquer lado: item guardado no porta-malas (posição no carro)
        DoorState = 23,    // qualquer lado: porta/porta-malas (índice) aberta ou fechada
        BonnetState = 24,  // qualquer lado: capô (bit 1 trava puxada, bit 2 aberto)
        JackPlace = 25,    // qualquer lado: macaco (id do item) colocado no ponto (índice) do carro
        JackUp = 26,       // qualquer lado: macaco levantou o carro
        JackDown = 27,     // qualquer lado: macaco baixou o carro
        BoltState = 28,    // qualquer lado: parafuso da roda (id do pneu) solto/apertado
        ItemStats = 29,    // números das peças/galões (host: mudanças; tio: só ações do jogador)
        CarCondition = 30, // host -> tio: CarPerformanceC.carCondition (vidros quebrados)
        ControlState = 31, // qualquer lado: controle do carro (índice) mudou de estado (faróis, limpador, vidro...)
        RadioTrack = 32,   // host -> tio: faixa do rádio (nome do clip) e segundo atual
        DevTeleport = 33,  // host -> tio (teste): carro teleportado; tio a pé vai para esta posição
        RouteChosen = 34,  // host -> tio: rota escolhida no mapa (número, trechos, indo pra casa, nível)
        ItemAdd = 35,      // host -> tio: itens novos (rota, loja, caixas) com id, nome e posição
        RouteRequest = 36, // tio -> host: o tio escolheu a rota no mapa dele (número, trechos, indo pra casa)
        DevRequest = 37,   // tio -> host (teste): botão da janela F4 (teleporte, dinheiro, hora)
        TimeOfDay = 38,    // host -> tio: hora do dia (segundos do DNC_DayNight), pausado, manhã
        MoneyDelta = 39,   // qualquer lado: o dinheiro mudou por ação minha (+/-)
        MoneyTotal = 40,   // host -> tio: dinheiro certo (a cada 5 s)
        ItemPurchased = 41,// qualquer lado: item (id) pago / não pago
        ShopGate = 42,     // qualquer lado: portão da loja (posição) fechado/aberto
        ItemGone = 43,     // qualquer lado: item (id) sumiu aqui (vendido, gasto): apagar no outro
        ShopCounter = 44,  // qualquer lado: balcão (posição): 1 item p/ compra, 2 item p/ venda, 3 pagou, 4 cancelou
        WorldSeed = 45,    // host -> tio (teste): semente nova do mundo; os dois refazem as opções de rota
        BoxOpened = 46,    // qualquer lado: caixa aberta (id do item da caixa ou posição)
        LaikaOrder = 47,   // qualquer lado: compra no catálogo do Laika (ponto de entrega + nomes das peças)
        EnvDoor = 48,      // qualquer lado: porta de prédio (loja, posto, Laika, motel) aberta/fechada
        CarLook = 49,      // qualquer lado: pintura (1), sujeira (2) ou adesivo (3) do carro
        CityPrint = 50,    // host -> tio: impressão digital de uma cidade (conferência)
        BedState = 51,     // qualquer lado: deitei (true + posição da cama) / levantei (false)
        DayPass = 52,      // host -> tio: os dois deitados, dormir (passa o dia)
        MotelPaid = 53,    // qualquer lado: paguei o quarto do motel (posição da recepção)
        DoorCash = 54,     // qualquer lado: bati numa porta do motel e o hóspede passou dinheiro (posição da porta)
        CashPicked = 55,   // qualquer lado: peguei um dinheiro solto (posição) -> o outro apaga o dele
        MotelReset = 56,   // teste (F4): resetar o motel mais perto nos dois
        SaveRequest = 57,  // tio -> host: me manda o save (no menu, antes de Continuar)
        SaveData = 58,     // host -> tio: o save do host (bytes do SaveFile); o tio usa só na memória
        Interact = 59,     // qualquer lado: torneira/registro (1), tampa (2) ou descarga (3): posição + estado
        BorderState = 60,  // host -> tio: estado da fronteira (janela, pronto p/ passaporte/carteira, cancela, passaporte guardado)
        BorderSay = 61,    // host -> tio: fala do guarda da fronteira
        BorderAct = 62,    // tio -> host: entreguei o passaporte (1) / a carteira (2) na janela
        Economy = 63,      // host -> tio: tabela de preços do país (o tio confere e copia se diferente)
        BorderSkip = 64,   // tio -> host: cliquei para passar a fala do guarda
        RoutePlan = 65,    // host -> tio: plano das rotas do mapa (clima, estrada boa/ruim/lama, distâncias, trechos)
        TrafficSpawn = 66, // host -> tio: carro do trânsito nasceu (id, lugar, rotação, modelo)
        TrafficMove = 67,  // host -> tio, 10x/s: posição/rotação dos carros do trânsito
        TrafficGone = 68,  // host -> tio: carro do trânsito sumiu
        UpgradeFitted = 69, // os 2 lados: instalei um upgrade no carro (id do ExtraComponentC, peso)
        UpgradeAsk = 70,    // tio -> host: me manda os upgrades instalados (ao entrar no jogo)
        UpgradeState = 71,  // host -> tio: upgrades instalados (bagageiro, para-choque, para-lamas, painel, faróis, ferramentas+nível)
        HomeStorage = 72,   // os 2 lados: comprei prateleira(s) do depósito em casa (total de prateleiras agora)
        ReturnHome = 73,    // host -> tio: voltei para casa (save novo do host); o tio recarrega junto
        ReturnHomeAsk = 74, // tio -> host: o tio escolheu "voltar para casa" no menu
        HostLeft = 75       // host -> tio: saí do mundo (salvar e sair para o menu, ou fechei o jogo)
    }

    /// <summary>Teclas de direção do jogador-tio (índices de MainMenuC.assignedInputStrings).</summary>
    public static class DriveKeys
    {
        public const int First = 8;  // 8/9 acelerar, 10/11 ré/freio, 12/13 e 14/15 virar
        public const int Last = 15;
    }

    /// <summary>Estado que o host manda: carro e o próprio jogador host.</summary>
    public struct HostStateData
    {
        public float HostTime;      // relógio do host (Time.realtimeSinceStartup)
        public bool HasCar;
        public Vector3 CarPos;
        public Quaternion CarRot;
        public Vector3 CarVel;
        public Vector3 CarAngVel;
        public Vector3 HostEye;     // posição dos olhos do host (mundo)
        public Vector3 HostEyeCar;  // posição dos olhos no espaço local do carro (vale quando sentado)
        public Vector3 HostLook;    // direção do olhar (mundo)
        public Vector3 HostBody;    // posição do corpo do host (mundo). A câmera fica fora do centro e gira em volta dele.
        public bool HostSat;       // sentado no banco do motorista
        public bool HostDriving;
        public bool RemoteDriving;  // o volante está com o jogador-tio
        public bool HostPassenger;  // o host está no banco do passageiro
        public float SteerAngle;    // ângulo das rodas da frente (para girar o volante no cliente)
        public float WheelRpm;      // giro das rodas (visual)
        public byte IgnitionPos;    // IgnitionLogicC.currentPos (0 desligada, 1 virada)
        public bool EngineOn;       // CarLogicC.engineOn
        public byte HandbrakePos;   // HandbrakeLogicC.currentPos (0 puxado, 1 solto)
        public float Speed;         // CarControleScriptC.currentSpeed (velocímetro do tio)

        public void Write(BinaryWriter w)
        {
            w.Write(RemoteDriving);
            w.Write(SteerAngle);
            w.Write(WheelRpm);
            w.Write(IgnitionPos);
            w.Write(EngineOn);
            w.Write(HandbrakePos);
            w.Write(Speed);
            w.Write(HostTime);
            w.Write(HasCar);
            NetIO.Put(w, CarPos);
            NetIO.Put(w, CarRot);
            NetIO.Put(w, CarVel);
            NetIO.Put(w, CarAngVel);
            NetIO.Put(w, HostEye);
            NetIO.Put(w, HostEyeCar);
            NetIO.Put(w, HostLook);
            w.Write(HostSat);
            w.Write(HostDriving);
            NetIO.Put(w, HostBody);
            w.Write(HostPassenger);
        }

        public static HostStateData Read(BinaryReader r)
        {
            var s = new HostStateData();
            s.RemoteDriving = r.ReadBoolean();
            s.SteerAngle = r.ReadSingle();
            s.WheelRpm = r.ReadSingle();
            s.IgnitionPos = r.ReadByte();
            s.EngineOn = r.ReadBoolean();
            s.HandbrakePos = r.ReadByte();
            s.Speed = r.ReadSingle();
            s.HostTime = r.ReadSingle();
            s.HasCar = r.ReadBoolean();
            s.CarPos = NetIO.GetVector3(r);
            s.CarRot = NetIO.GetQuaternion(r);
            s.CarVel = NetIO.GetVector3(r);
            s.CarAngVel = NetIO.GetVector3(r);
            s.HostEye = NetIO.GetVector3(r);
            s.HostEyeCar = NetIO.GetVector3(r);
            s.HostLook = NetIO.GetVector3(r);
            s.HostSat = r.ReadBoolean();
            s.HostDriving = r.ReadBoolean();
            s.HostBody = NetIO.GetVector3(r);
            s.HostPassenger = r.ReadBoolean();
            return s;
        }
    }

    /// <summary>Estado que o cliente (jogador-tio) manda.</summary>
    public struct ClientStateData
    {
        public bool Seated;        // sentado (passageiro, ou motorista quando está dirigindo)
        public Vector3 LookDirCar; // direção do olhar no espaço local do carro
        public ushort DriveBits;   // bit i = tecla assignedInputStrings[8 + i] apertada
        public float JoyX, JoyAccelerate, JoyReverse; // controle (se usar)
        public Vector3 BodyPos;    // a pé: posição do corpo do jogador (mundo)
        public Vector3 LookDir;    // a pé: direção do olhar (mundo)

        public void Write(BinaryWriter w)
        {
            w.Write(Seated);
            NetIO.Put(w, LookDirCar);
            w.Write(DriveBits);
            w.Write(JoyX);
            w.Write(JoyAccelerate);
            w.Write(JoyReverse);
            NetIO.Put(w, BodyPos);
            NetIO.Put(w, LookDir);
        }

        public static ClientStateData Read(BinaryReader r)
        {
            var s = new ClientStateData();
            s.Seated = r.ReadBoolean();
            s.LookDirCar = NetIO.GetVector3(r);
            s.DriveBits = r.ReadUInt16();
            s.JoyX = r.ReadSingle();
            s.JoyAccelerate = r.ReadSingle();
            s.JoyReverse = r.ReadSingle();
            s.BodyPos = NetIO.GetVector3(r);
            s.LookDir = NetIO.GetVector3(r);
            return s;
        }
    }

    public static class NetIO
    {
        public static void Put(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static void Put(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        public static Vector3 GetVector3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        public static Quaternion GetQuaternion(BinaryReader r) { return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    }
}
