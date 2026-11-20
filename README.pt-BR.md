# Jalopy Co-op

[English](README.md) | **Português**

Mod de **co-op para 2 jogadores** no [Jalopy](https://store.steampowered.com/app/446020/Jalopy/).
Um jogador é o **motorista** (o host, dono do save) e o outro vira o **tio**, sentado no banco do passageiro.
Os dois viajam no mesmo carro, no mesmo mundo, e qualquer um pode dirigir.

> **Versão de teste (alfa).** Já dá para jogar uma viagem inteira, mas ainda tem bugs. Faça backup do seu save.

---

## Instalação

Os **dois jogadores** precisam do mod, na mesma versão.

1. Baixe o `.zip` da última versão em **[Releases](../../releases)**.
2. Feche o Jalopy.
3. Na Steam, clique com o botão direito em Jalopy e vá em **Gerenciar → Procurar arquivos locais**.
4. Extraia o conteúdo do zip para essa pasta, ao lado do `Jalopy.exe`. São três itens: `winhttp.dll`, `doorstop_config.ini` e a pasta `BepInEx`.
5. Abra o jogo pela Steam normalmente.

O zip já traz o [BepInEx 5](https://github.com/BepInEx/BepInEx), que é o carregador de mods.

**Para desinstalar:** apague o `winhttp.dll`. Para tirar tudo, apague também o `doorstop_config.ini` e a pasta `BepInEx`.

## Como jogar

### Host (motorista)
1. Abra o jogo e entre no seu save (**Continuar**).
2. Aperte **F7** e clique em **Criar sala**.
3. Espere o tio conectar.

### Tio (passageiro)
1. Abra o jogo e fique no **menu principal**. Não clique em Continuar.
2. Aperte **F7**. A sala do host aparece em "Salas na rede".
3. Clique em **Conectar**. Você entra sozinho no mundo do host.

### Jogando pela internet

O modo mais fácil é com o [Radmin VPN](https://www.radmin-vpn.com/): os dois entram na mesma rede e a sala aparece normalmente.

Sem VPN:
- O host libera a porta **7777** (TCP e UDP) no roteador.
- O tio digita o IP do host em **Conectar por IP**.

### Teclas

| Tecla | O que faz |
|---|---|
| F7 | Abre e fecha a janela do co-op |
| F3 | Esconde ou mostra a linha do co-op no alto da tela |

Todas as ações no jogo usam as interações normais do Jalopy: portas, bancos, alavancas e cliques. O mod não cria atalhos de teclado para essas ações.

## Configuração

Depois da primeira vez que o jogo abre, o arquivo `BepInEx/config/jalopycoop.cfg` é criado. Nele dá para mudar:

- `NomeDaSala`: o nome da sala.
- `Porta`: padrão 7777.
- `MostrarLinhaCoop`: mostra ou esconde a linha do co-op.
- Ajustes finos de posição dos modelos dos jogadores.

## Problemas conhecidos

- Ainda não foi testado no co-op: os países depois do primeiro e o final do jogo.

Achou um bug? Abra uma **[Issue](../../issues)** e anexe o `BepInEx/LogOutput.log` **dos dois jogadores**. Esse arquivo fica na pasta do Jalopy.

## Compilar o código

Você precisa de:
- o [.NET SDK](https://dotnet.microsoft.com/download);
- uma cópia do Jalopy com o BepInEx 5 instalado.

As DLLs do jogo **não** vêm neste repositório.

```bash
cd JalopyCoop
dotnet build -c Release -p:GameDir="C:\caminho\da\copia\do\Jalopy"
```

O plugin é gerado em `JalopyCoop/bin/Release/JalopyCoop.dll` e também é copiado para `GameDir\BepInEx\plugins\JalopyCoop`.

Como o mod funciona:
- O host manda em tudo: a física do carro roda só no host.
- As mensagens importantes vão por **TCP**. A posição do carro e dos jogadores vai por **UDP**.
- A busca de salas usa um broadcast UDP na porta **7779**.

## Planos Futuros

- Chat usando o sistema de diálogo do próprio jogo: as mensagens aparecem como as falas do tio no Jalopy
- 3 a 4 jogadores
- Roupas e skins para os jogadores

## Créditos

- Jalopy é da **MinskWorks**. Este é um mod de fãs, sem ligação com eles. É preciso ter o jogo original.
- Feito com [BepInEx](https://github.com/BepInEx/BepInEx) e [HarmonyX](https://github.com/BepInEx/HarmonyX).
- **Luciana Tavo** - Apoio moral e beta testes.
