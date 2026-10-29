# Overscan BR

Navegador para TVs Samsung de 2025 (Tizen 9), em português e pensado para usar do sofá. É uma versão do [Overscan](https://github.com/Apps2Samsung/Overscan), navegador de código aberto de Patrick Stel, com licença MIT.

Instala ao lado de qualquer outro app: aparece na TV como **Overscan BR**.

## O que ele tem

- **Tudo em português**, com letras cerca de um terço maiores no menu, nos avisos, no teclado e na tela inicial.
- **Menu curto:** segure OK. O que se usa todo dia fica na frente; o resto está em **Avançado…**.
- **Barra de favoritos no botão vermelho:** quadros grandes no topo da tela. Setas escolhem, OK abre, e o último quadro guarda a página que está aberta. Já vem com ge e Google.
- **Letra maior e menor por site:** **verde** aumenta, **amarelo** diminui. O navegador lembra o tamanho de cada site. Mudando na tela inicial, vira o padrão de todos.
- **Vídeo em tela cheia no botão azul:** some tudo da página que não é o vídeo (cabeçalho e barra lateral fixos incluídos). Depois de 3 segundos sem mexer, o ponteiro some e os controles do vídeo também. A primeira seta ou OK só traz o ponteiro de volta. Azul ou Voltar saem da tela cheia.
- **Ponteiro que avisa:** fica azul quando está em cima de algo que o OK aperta. Um OK que cai a poucos pixels de um botão vai para o botão.
- **Abrir só o player:** no menu. Quando o vídeo está num quadro de outro site, abre esse quadro como a página, onde o navegador consegue ajudar (tela cheia limpa e o vídeo pelo caminho que funciona na TV).
- **Controle pelo computador:** abra um site ou acione favoritos, letra, tela cheia e menu a partir do computador, na mesma rede de casa (veja abaixo).
- Bloqueio de anúncios ligado, como no original.

Se o controle não tiver os botões coloridos, tudo isso também está no menu (segurar OK).

## Como instalar

Você precisa de um computador na mesma rede da TV e do [Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung) (gratuito, Windows, Mac e Linux).

1. Na TV, abra **Apps**, digite **1 2 3 4 5** no controle (no controle sem números, use o botão **123**), ligue o **Developer Mode**, coloque o IP do computador e reinicie a TV.
2. No Apps2Samsung, escolha **Overscan BR** na lista (ou baixe o `OverscanBR-tizen9.tpk` na página de versões deste repositório e escolha **Custom WGT / TPK**).
3. Clique em **Baixar & Instalar**. Na primeira vez ele pede para entrar na sua conta Samsung, para criar o certificado.

## Controle pelo computador

Na TV, segure OK e vá em **Avançado… > Controle pelo computador**. Aparecem o **IP da TV** e uma **chave** que só existe na sua TV.

Com o Python 3 (já vem no Mac), use o `tools/overscan-br/tv.py` deste repositório. Na mesma pasta dele, crie dois arquivos de uma linha: `tv_ip.txt` com o IP e `tv_chave.txt` com a chave. Depois:

```bash
python3 tv.py site ge.globo.com
```

Por baixo, o Overscan BR responde na porta 8081:

- `GET /abrir?chave=CHAVE&url=ENDERECO` abre um site;
- `GET /acao?chave=CHAVE&id=ACAO` aciona `favbar`, `zoomin`, `zoomout`, `zoomreset`, `videofull`, `player`, `home`, `address`, `bookmark` ou `menu`.

Só aceita pedidos da rede de casa (endereços privados) e com a chave certa.

## Limitações conhecidas

- **Vídeo depende do site.** Isso vem do motor de navegador que a Samsung entrega para apps nas TVs 2025, e acontece igual no Overscan original. Arquivos MP4 simples dão erro de decodificação e o HLS nativo da TV não toca (erro 4); transmissões por hls.js tocam. O Overscan BR avisa os sites que não há HLS nativo, para eles usarem o caminho que funciona, mas isso não alcança players dentro de quadros de outro site (para esses, use Abrir só o player). Para vídeo, os apps oficiais e o navegador da própria Samsung são mais garantidos.
- Overscan e Overscan BR usam a mesma porta (8081): deixe só um aberto.
- Feito e testado numa TV Samsung de 2025 (QN55QEF1, Tizen 9.0). Os pacotes para TVs mais antigas do Overscan original não foram alterados.

## Compilar

Precisa do .NET 6 SDK com o workload Tizen, o mesmo do projeto original:

```bash
bash dotnet-install.sh --channel 6.0 --install-dir ~/.dotnet6 --no-path
bash workload-install.sh -d ~/.dotnet6
DOTNET_ROOT=~/.dotnet6 ~/.dotnet6/dotnet build OverscanNui/OverscanNui.csproj -c Release
```

O pacote sai em `OverscanNui/bin/Release/net6.0-tizen8.0/org.bnoads.overscanbr-<versão>.tpk`, assinado com o certificado padrão. O Apps2Samsung reassina com o seu na instalação.

## Créditos e licença

Overscan: Patrick Stel e colaboradores ([Apps2Samsung/Overscan](https://github.com/Apps2Samsung/Overscan)), licença MIT, veja `LICENSE`. As mudanças do Overscan BR são da BNOads e seguem a mesma licença.
