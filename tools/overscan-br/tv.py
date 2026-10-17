#!/usr/bin/env python3
"""Controla a TV Samsung (e o Overscan BR) pela rede, sem instalar nada.

Uso:
  python3 tv.py site ge.globo.com          abre o site no Overscan BR (precisa estar aberto)
  python3 tv.py digitar "texto"            digita no teclado que estiver aberto na TV
  python3 tv.py app org.tizen.browser      abre um app pelo id completo
  python3 tv.py tecla KEY_HOME             aperta uma tecla do controle

Configuracao, em arquivos de uma linha ao lado deste script:
  tv_ip.txt     IP da TV (ou a variavel de ambiente TV_IP)
  tv_chave.txt  chave do Overscan BR (na TV: segurar OK > Avancado >
                Controle pelo computador)
  tv_nome.txt   opcional, o nome com que este computador aparece na TV

O "site" manda o endereco direto pro Overscan BR, pela porta 8081, com a chave.
Se o Overscan BR estiver fechado, ele cai no plano B: abre o navegador da
Samsung e espera voce selecionar a barra de endereco.

Na primeira vez que usa o controle pela rede, a TV pergunta se pode deixar o Mac
controlar. Aceite no controle.
"""
import urllib.parse
import urllib.request
import base64
import json
import os
import socket
import ssl
import struct
import sys

PASTA = os.path.dirname(os.path.abspath(__file__))
ARQ_TOKEN = os.path.join(PASTA, "tv_token.txt")
ARQ_CHAVE = os.path.join(PASTA, "tv_chave.txt")


def ler_arquivo(nome, padrao=""):
    try:
        with open(os.path.join(PASTA, nome)) as f:
            return f.read().strip() or padrao
    except OSError:
        return padrao


TV_IP = os.environ.get("TV_IP") or ler_arquivo("tv_ip.txt")
NOME = ler_arquivo("tv_nome.txt", "Computador")


def ler_token():
    try:
        with open(ARQ_TOKEN) as f:
            return f.read().strip()
    except OSError:
        return ""


def conectar():
    nome = base64.b64encode(NOME.encode()).decode()
    caminho = f"/api/v2/channels/samsung.remote.control?name={nome}"
    token = ler_token()
    if token:
        caminho += f"&token={token}"

    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    sock = ctx.wrap_socket(socket.create_connection((TV_IP, 8002), timeout=10))

    chave = base64.b64encode(os.urandom(16)).decode()
    pedido = (
        f"GET {caminho} HTTP/1.1\r\nHost: {TV_IP}:8002\r\nUpgrade: websocket\r\n"
        f"Connection: Upgrade\r\nSec-WebSocket-Key: {chave}\r\nSec-WebSocket-Version: 13\r\n\r\n"
    )
    sock.sendall(pedido.encode())
    resposta = b""
    while b"\r\n\r\n" not in resposta:
        resposta += sock.recv(1024)
    if b" 101 " not in resposta.split(b"\r\n")[0]:
        sys.exit(f"A TV recusou a conexao: {resposta.split(chr(13).encode())[0].decode()}")
    return sock


def receber(sock):
    def ler(n):
        dados = b""
        while len(dados) < n:
            parte = sock.recv(n - len(dados))
            if not parte:
                raise ConnectionError("conexao fechada pela TV")
            dados += parte
        return dados

    while True:
        cab = ler(2)
        tipo = cab[0] & 0x0F
        tamanho = cab[1] & 0x7F
        if tamanho == 126:
            tamanho = struct.unpack(">H", ler(2))[0]
        elif tamanho == 127:
            tamanho = struct.unpack(">Q", ler(8))[0]
        dados = ler(tamanho)
        if tipo == 0x8:
            codigo = struct.unpack(">H", dados[:2])[0] if len(dados) >= 2 else 0
            raise ConnectionError(f"a TV fechou a conexao (codigo {codigo} {dados[2:].decode(errors='ignore')})")
        if tipo == 0x1:
            return json.loads(dados.decode() or "{}")


def enviar(sock, msg):
    dados = json.dumps(msg).encode()
    mascara = os.urandom(4)
    cab = bytes([0x81])
    if len(dados) < 126:
        cab += bytes([0x80 | len(dados)])
    elif len(dados) < 65536:
        cab += bytes([0x80 | 126]) + struct.pack(">H", len(dados))
    else:
        cab += bytes([0x80 | 127]) + struct.pack(">Q", len(dados))
    sock.sendall(cab + mascara + bytes(b ^ mascara[i % 4] for i, b in enumerate(dados)))


def autorizar(sock):
    sock.settimeout(40)
    print("Esperando a TV liberar (na primeira vez, aceite no controle)...")
    msg = receber(sock)
    if msg.get("event") != "ms.channel.connect":
        sys.exit(f"A TV nao liberou: {msg.get('event')}")
    token = msg.get("data", {}).get("token")
    if token and token != ler_token():
        with open(ARQ_TOKEN, "w") as f:
            f.write(token)
    sock.settimeout(10)


def abrir_app(app_id):
    pedido = urllib.request.Request(
        f"http://{TV_IP}:8001/api/v2/applications/{app_id}", method="POST")
    try:
        with urllib.request.urlopen(pedido, timeout=8) as r:
            print("App aberto." if r.read().strip() == b"true" else "A TV nao confirmou.")
    except urllib.error.HTTPError as erro:
        print(f"A TV nao achou esse app ({erro.code}). Use o id completo.")


def digitar(texto):
    sock = conectar()
    autorizar(sock)
    enviar(sock, {"method": "ms.remote.control",
                  "params": {"Cmd": base64.b64encode(texto.encode()).decode(),
                             "DataOfCmd": "base64", "TypeOfRemote": "SendInputString"}})
    enviar(sock, {"method": "ms.remote.control", "params": {"TypeOfRemote": "SendInputEnd"}})
    sock.close()
    print("Texto enviado.")


def tecla(nome):
    sock = conectar()
    autorizar(sock)
    enviar(sock, {"method": "ms.remote.control",
                  "params": {"Cmd": "Click", "DataOfCmd": nome,
                             "Option": "false", "TypeOfRemote": "SendRemoteKey"}})
    sock.close()
    print("Tecla enviada.")


def abrir_no_overscan(endereco):
    """Manda o endereco pro Overscan BR. Devolve False se ele nao estiver aberto."""
    try:
        with open(ARQ_CHAVE) as f:
            chave = f.read().strip()
    except OSError:
        return False
    consulta = urllib.parse.urlencode({"chave": chave, "url": endereco})
    try:
        with urllib.request.urlopen(f"http://{TV_IP}:8081/abrir?{consulta}", timeout=5) as r:
            resposta = r.read().decode("utf-8", "replace").strip()
    except OSError:
        return False
    if resposta.startswith("ok"):
        print("Overscan BR:", resposta)
        return True
    if "diagnostics" in resposta or "Overscan diagnostics" in resposta:
        # Respondeu o Overscan original, que nao sabe abrir pela rede.
        return False
    print("Overscan BR recusou:", resposta)
    return True


def main():
    if not TV_IP:
        sys.exit("Falta o IP da TV: crie tv_ip.txt ao lado do tv.py (ou use TV_IP=...).")
    if len(sys.argv) != 3 or sys.argv[1] not in ("site", "digitar", "app", "tecla"):
        sys.exit(__doc__)
    acao, valor = sys.argv[1], sys.argv[2]
    if acao == "site":
        if abrir_no_overscan(valor):
            return
        print("O Overscan BR nao respondeu. Plano B: navegador da Samsung.")
        abrir_app("org.tizen.browser")
        input("Na TV, selecione a barra de endereco ate o teclado aparecer e aperte Enter aqui...")
        digitar(valor)
    elif acao == "digitar":
        digitar(valor)
    elif acao == "app":
        abrir_app(valor)
    else:
        tecla(valor)


if __name__ == "__main__":
    main()
