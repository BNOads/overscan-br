import Foundation

/// Aceita o certificado proprio da TV, e so o dela. Qualquer outro host segue a
/// verificacao normal do sistema.
final class ConfiancaTV: NSObject, URLSessionDelegate, Sendable {
    let host: String

    init(host: String) {
        self.host = host
    }

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge
    ) async -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        let espaco = challenge.protectionSpace
        guard espaco.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              espaco.host == host,
              let confianca = espaco.serverTrust else {
            return (.performDefaultHandling, nil)
        }
        return (.useCredential, URLCredential(trust: confianca))
    }
}

/// Uma conexao WebSocket com o controle remoto da TV (porta 8002), aberta
/// enquanto o painel esta aberto. As teclas saem sem esperar resposta, para o
/// toque parecer instantaneo; se a conexao cair, ela volta sozinha.
@MainActor
final class CanalRemoto {
    enum Estado: Equatable {
        case parado
        case conectando
        case conectado
        case recusado
    }

    /// O nome tem que ser exatamente este, senao a TV pede permissao de novo.
    static let nome = "Mac do JP"

    var aoMudar: ((Estado) -> Void)?
    private(set) var estado: Estado = .parado {
        didSet {
            if estado != oldValue { aoMudar?(estado) }
        }
    }

    private var ip: String
    private var sessao: URLSession?
    private var tarefa: URLSessionWebSocketTask?
    private var leitura: Task<Void, Never>?
    private var vigia: Task<Void, Never>?
    private var religar: Task<Void, Never>?
    private var pingar: Task<Void, Never>?
    private var geracao = 0
    private var pronto = false
    private var ativo = false
    private var falhas = 0
    private var fila: [(tecla: String, quando: Date)] = []

    init(ip: String) {
        self.ip = ip
    }

    /// Painel aberto: conecta e mantem conectado.
    func ligar() {
        ativo = true
        religar?.cancel()
        if tarefa == nil { conectar() }
    }

    /// Painel fechado: solta a conexao.
    func desligar() {
        ativo = false
        religar?.cancel()
        fila.removeAll()
        fechar()
        estado = .parado
    }

    func trocar(ip novo: String) {
        ip = novo
        falhas = 0
        fechar()
        if ativo { conectar() } else { estado = .parado }
    }

    func enviar(_ tecla: String) {
        guard tecla.range(of: "^KEY_[A-Z0-9_]+$", options: .regularExpression) != nil else { return }
        if pronto, let tarefa {
            mandar(tecla, por: tarefa)
            return
        }
        // Ainda conectando: guarda as ultimas teclas e manda assim que a TV liberar.
        fila.append((tecla, Date()))
        if fila.count > 6 { fila.removeFirst(fila.count - 6) }
        if tarefa == nil {
            religar?.cancel()
            conectar()
        }
    }

    // MARK: - Conexao

    private func conectar() {
        fechar()
        let nome = Data(Self.nome.utf8).base64EncodedString()
        var endereco = "wss://\(ip):8002/api/v2/channels/samsung.remote.control?name=\(Rede.codificar(nome))"
        if let token = Arquivos.ler(Arquivos.token) {
            endereco += "&token=\(Rede.codificar(token))"
        }
        guard let url = URL(string: endereco) else {
            estado = .parado
            return
        }

        geracao += 1
        let g = geracao
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 8
        config.waitsForConnectivity = false
        let novaSessao = URLSession(configuration: config, delegate: ConfiancaTV(host: ip), delegateQueue: nil)
        let novaTarefa = novaSessao.webSocketTask(with: url)
        sessao = novaSessao
        tarefa = novaTarefa
        pronto = false
        estado = .conectando
        novaTarefa.resume()

        leitura = Task { [weak self] in
            do {
                while true {
                    let mensagem = try await novaTarefa.receive()
                    guard let self, self.geracao == g else { return }
                    self.tratar(mensagem)
                }
            } catch {
                guard let self, self.geracao == g else { return }
                self.caiu(geracao: g)
            }
        }

        // Se a TV aceitar a conexao mas nunca liberar o controle, tenta de novo.
        vigia = Task { [weak self] in
            try? await Task.sleep(for: .seconds(30))
            guard !Task.isCancelled, let self, self.geracao == g, !self.pronto else { return }
            self.caiu(geracao: g)
        }
    }

    private func tratar(_ mensagem: URLSessionWebSocketTask.Message) {
        let texto: String
        switch mensagem {
        case .string(let s): texto = s
        case .data(let d): texto = String(decoding: d, as: UTF8.self)
        @unknown default: return
        }
        guard let json = (try? JSONSerialization.jsonObject(with: Data(texto.utf8))) as? [String: Any],
              let evento = json["event"] as? String else { return }

        switch evento {
        case "ms.channel.connect":
            pronto = true
            falhas = 0
            vigia?.cancel()
            if let dados = json["data"] as? [String: Any] {
                let novo = (dados["token"] as? String) ?? (dados["token"] as? NSNumber)?.stringValue ?? ""
                if !novo.isEmpty, novo != Arquivos.ler(Arquivos.token) {
                    Arquivos.gravarToken(novo)
                }
            }
            estado = .conectado
            esvaziarFila()
            iniciarPing()
        case "ms.channel.unauthorized":
            fechar()
            fila.removeAll()
            estado = .recusado
            agendarReconexao(em: 30)
        case "ms.channel.timeOut":
            caiu(geracao: geracao)
        default:
            break
        }
    }

    private func mandar(_ tecla: String, por t: URLSessionWebSocketTask) {
        let texto = "{\"method\":\"ms.remote.control\",\"params\":{\"Cmd\":\"Click\",\"DataOfCmd\":\"\(tecla)\",\"Option\":\"false\",\"TypeOfRemote\":\"SendRemoteKey\"}}"
        let g = geracao
        t.send(.string(texto)) { [weak self] erro in
            guard erro != nil else { return }
            Task { @MainActor in self?.caiu(geracao: g) }
        }
    }

    private func esvaziarFila() {
        guard let tarefa else { return }
        let agora = Date()
        let validas = fila.filter { agora.timeIntervalSince($0.quando) < 5 }
        fila.removeAll()
        for item in validas { mandar(item.tecla, por: tarefa) }
    }

    private func iniciarPing() {
        pingar?.cancel()
        let g = geracao
        pingar = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(20))
                guard !Task.isCancelled, let self, self.geracao == g, let t = self.tarefa else { return }
                t.sendPing { [weak self] erro in
                    guard erro != nil else { return }
                    Task { @MainActor in self?.caiu(geracao: g) }
                }
            }
        }
    }

    private func caiu(geracao g: Int) {
        guard g == geracao else { return }
        fechar()
        guard ativo else {
            estado = .parado
            return
        }
        falhas += 1
        estado = .conectando
        let esperas: [Double] = [1, 2, 4, 8, 12]
        agendarReconexao(em: esperas[min(falhas - 1, esperas.count - 1)])
    }

    private func agendarReconexao(em segundos: Double) {
        religar?.cancel()
        religar = Task { [weak self] in
            try? await Task.sleep(for: .seconds(segundos))
            guard !Task.isCancelled, let self, self.ativo, self.tarefa == nil else { return }
            self.conectar()
        }
    }

    private func fechar() {
        geracao += 1
        leitura?.cancel()
        vigia?.cancel()
        pingar?.cancel()
        tarefa?.cancel(with: .goingAway, reason: nil)
        sessao?.invalidateAndCancel()
        tarefa = nil
        sessao = nil
        pronto = false
    }
}
