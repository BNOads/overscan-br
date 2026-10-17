# Overscan BR

Versão brasileira do [Overscan](https://github.com/Apps2Samsung/Overscan), o navegador de código aberto para TVs Samsung com Tizen, de Patrick Stel (licença MIT). Feita para TVs de 2025 (Tizen 9.0), pacote `OverscanNui`.

Instala ao lado do original: o app se chama **Overscan BR** (`org.bnoads.overscanbr`).

## O que muda em relação ao original

- **Tudo em português:** menu, avisos, teclado, tela inicial e cartão do controle.
- **Letras cerca de um terço maiores** em todo o navegador (`NuiTheme.TextScale`).
- **Menu curto:** o que se usa todo dia fica no primeiro menu; o resto foi para **Avançado…**.
- **Barra de favoritos** no botão **vermelho** (ou no menu): quadros grandes no topo, setas escolhem, OK abre, o último quadro guarda a página aberta.
- **Zoom por site:** **verde** aumenta e **amarelo** diminui a letra; o navegador lembra o tamanho de cada site. Mudando na tela inicial, vira o padrão de todos os sites. O zoom só é aplicado entre carregamentos, nunca dentro dos avisos do motor (aplicar ali travava a TV).
- **Vídeo em tela cheia** no botão **azul**: esconde tudo da página que não é o vídeo (topo e barra lateral fixos incluídos). Depois de 3 segundos sem mexer, o ponteiro some, os controles do vídeo da página também, e em players de outros sites o mouse sai de cima da barra de controles. A primeira seta ou OK só traz o ponteiro de volta. Azul ou Voltar saem.
- **Botão Info não liga/desliga mais as imagens** (era fácil apertar sem querer, e imagens desligadas quebram players de vídeo). Imagens ficam em Avançado.
- **Abrir site pelo computador:** `GET http://<IP-da-TV>:8081/abrir?chave=...&url=...`, aceito só da rede de casa e com a chave. O script `tools/overscan-br/tv.py` faz isso (`python3 tv.py site ge.globo.com`).
- **Favoritos iniciais:** ge e Google, só na primeira abertura.

## Compilar

Precisa do .NET 6 SDK com o workload Tizen (o mesmo do projeto original):

```bash
bash dotnet-install.sh --channel 6.0 --install-dir ~/.dotnet6 --no-path
bash workload-install.sh -d ~/.dotnet6
DOTNET_ROOT=~/.dotnet6 ~/.dotnet6/dotnet build OverscanNui/OverscanNui.csproj -c Release
```

O pacote sai em `OverscanNui/bin/Release/net6.0-tizen8.0/org.bnoads.overscanbr-<versão>.tpk`, assinado com o certificado padrão.

## Instalar na TV

Com o [Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung): Modo Desenvolvedor da TV ligado com o IP do computador, Release **"Custom WGT / TPK"** (último item da lista), botão da pastinha para escolher o `.tpk`, **Baixar & Instalar**. Ele assina com o certificado da sua conta Samsung. Atenção: ele apaga o arquivo escolhido depois de instalar.

## Limitações conhecidas

- **Vídeo:** herdado do original. Nas TVs 2025, vídeo comum (MP4) dá erro de decodificação dentro do navegador, e algumas transmissões são recusadas por falta de espaço do buffer da TV. Para vídeo, os apps oficiais ou o navegador da Samsung são mais confiáveis.
- O Overscan original e o Overscan BR usam a mesma porta de diagnóstico (8081); deixe só um aberto.

## Créditos

Overscan: Patrick Stel e colaboradores, licença MIT (arquivo `LICENSE`). As mudanças desta versão seguem a mesma licença.
