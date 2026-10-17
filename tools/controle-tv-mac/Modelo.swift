import AppKit
import Foundation
import Observation

enum EstadoTV: Equatable {
    case verificando
    case ligada
    case emEspera
    case foraDaRede
}

struct Aviso: Equatable, Identifiable {
    enum Tipo { case ok, info, alerta }
    let id = UUID()
    let texto: String
    let tipo: Tipo
}

/// Tudo que o painel mostra e tudo que ele manda para a TV.
@MainActor
@Observable
final class ModeloTV {
    static let ipPadrao = "192.168.0.106"
    private static let chaveIP = "ipDaTV"

    /// Tecla do controle usada quando o Overscan BR da TV ainda nao entende /acao.
    private static let teclaReserva: [String: String] = [
        "favbar": "KEY_RED",
        "zoomin": "KEY_GREEN",
        "zoomout": "KEY_YELLOW",
        "videofull": "KEY_CYAN",
        "home": "KEY_9",
        "address": "KEY_0",
        "bookmark": "KEY_8",
    ]

    private(set) var ip: String
    var estadoTV: EstadoTV = .verificando
    var nomeTV = ""
    var modeloTV = ""
    var navegador: EstadoNavegador = .verificando
    var canal: CanalRemoto.Estado = .parado
    var aviso: Aviso?
    var nivelLetra = 0
    var teclaAcesa: String?
    var ajustesAbertos = false

    @ObservationIgnored private var remoto: CanalRemoto?
    @ObservationIgnored private var tarefaStatus: Task<Void, Never>?
    @ObservationIgnored private var tarefaAviso: Task<Void, Never>?
    @ObservationIgnored private var tarefaSoltar: Task<Void, Never>?
    @ObservationIgnored private var tarefaAcesa: Task<Void, Never>?
    @ObservationIgnored private var acaoPelaRede: Bool?
    @ObservationIgnored private var visivel = false
    /// Janelas com o painel na tela agora (o da barra de menus e a janela normal).
    @ObservationIgnored private var janelasVisiveis: [ObjectIdentifier: NSWindow] = [:]
    @ObservationIgnored private let offline: Bool
    @ObservationIgnored let teclado = TecladoDoPainel()

    init(offline: Bool = false) {
        self.offline = offline
        let salvo = UserDefaults.standard.string(forKey: Self.chaveIP) ?? ""
        ip = salvo.isEmpty ? Self.ipPadrao : salvo
        guard !offline else { return }
        let canal = CanalRemoto(ip: ip)
        canal.aoMudar = { [weak self] novo in self?.canalMudou(novo) }
        remoto = canal
    }

    /// Estado de exemplo para a imagem de conferencia (--snapshot).
    static func amostra() -> ModeloTV {
        let m = ModeloTV(offline: true)
        m.estadoTV = .ligada
        m.nomeTV = "55\" QLED"
        m.modeloTV = "QN55QEF1AGXZD"
        m.navegador = .site("ge.globo.com")
        m.canal = .conectado
        return m
    }

    var chaveEncontrada: Bool { Arquivos.ler(Arquivos.chave) != nil }
    var tokenEncontrado: Bool { Arquivos.ler(Arquivos.token) != nil }

    // MARK: - Painel aberto e fechado

    /// Chamado por cada painel (barra de menus ou janela) quando aparece ou some.
    /// Os dois dividem este modelo: uma conexao so, aberta enquanto algum estiver na tela.
    func janela(_ janela: NSWindow?, visivel: Bool, origem: ObjectIdentifier) {
        if let janela, visivel {
            janelasVisiveis[origem] = janela
        } else {
            janelasVisiveis[origem] = nil
        }
        let agora = !janelasVisiveis.isEmpty
        if agora {
            teclado.ligar(janelas: Array(janelasVisiveis.values), modelo: self)
        } else {
            teclado.desligar()
        }
        guard agora != self.visivel else { return }
        self.visivel = agora
        guard !offline else { return }

        if agora {
            tarefaSoltar?.cancel()
            remoto?.ligar()
            iniciarStatus()
        } else {
            tarefaStatus?.cancel()
            tarefaStatus = nil
            ajustesAbertos = false
            // Uma folga curta: reabrir logo em seguida encontra a conexao pronta.
            tarefaSoltar = Task { [weak self] in
                try? await Task.sleep(for: .seconds(20))
                guard !Task.isCancelled, let self, !self.visivel else { return }
                self.remoto?.desligar()
            }
        }
    }

    private func iniciarStatus() {
        tarefaStatus?.cancel()
        tarefaStatus = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.atualizar()
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    func atualizar() async {
        guard !offline else { return }
        let ip = self.ip
        async let tv = Rede.lerTV(ip: ip)
        async let nav = Rede.lerNavegador(ip: ip)
        let (info, estadoNav) = await (tv, nav)
        guard ip == self.ip else { return }

        if let info {
            estadoTV = info.emEspera ? .emEspera : .ligada
            if !info.nome.isEmpty { nomeTV = info.nome }
            if !info.modelo.isEmpty { modeloTV = info.modelo }
        } else {
            estadoTV = .foraDaRede
        }
        definirNavegador(estadoNav ?? .fechado)
    }

    private func definirNavegador(_ novo: EstadoNavegador) {
        // Outro app aberto pode ser outra versao: volta a perguntar se /acao existe.
        if novo == .fechado || (novo == .outro) != (navegador == .outro) {
            acaoPelaRede = nil
        }
        if novo == .fechado { nivelLetra = 0 }
        navegador = novo
    }

    private func atualizarDaqui(a segundos: Double) {
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(segundos))
            guard let self, self.visivel else { return }
            await self.atualizar()
        }
    }

    private func canalMudou(_ novo: CanalRemoto.Estado) {
        canal = novo
        if novo == .recusado {
            mostrar("A TV recusou o controle. Aceite o Mac do JP na TV", .alerta)
        }
    }

    // MARK: - Teclas do controle

    func tecla(_ nome: String) {
        acender(nome)
        guard !offline else { return }
        if estadoTV == .foraDaRede {
            mostrar("TV fora da rede", .alerta)
        }
        remoto?.enviar(nome)
    }

    func ligarDesligar() {
        guard !offline else { return }
        if estadoTV == .foraDaRede {
            mostrar("A TV está fora da rede. Ligue pelo controle", .alerta)
        }
        remoto?.enviar("KEY_POWER")
        atualizarDaqui(a: 3)
        atualizarDaqui(a: 7)
    }

    /// Acende o botao do direcional por um instante (usado pelas setas do teclado).
    private func acender(_ nome: String) {
        teclaAcesa = nome
        tarefaAcesa?.cancel()
        tarefaAcesa = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(160))
            guard !Task.isCancelled else { return }
            self?.teclaAcesa = nil
        }
    }

    // MARK: - Navegador

    /// Manda o endereco para o Overscan BR. Devolve true se a TV aceitou.
    func abrirSite(_ digitado: String) async -> Bool {
        let endereco = digitado.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !endereco.isEmpty, !offline else { return false }
        guard let chave = Arquivos.ler(Arquivos.chave) else {
            mostrar("Chave do Overscan BR não encontrada", .alerta)
            return false
        }
        guard let url = URL(string: "http://\(ip):8081/abrir?chave=\(Rede.codificar(chave))&url=\(Rede.codificar(endereco))") else {
            return false
        }
        switch await Rede.pedir(url, tempo: 4) {
        case .texto(let bruto):
            let resposta = bruto.trimmingCharacters(in: .whitespacesAndNewlines)
            if resposta.hasPrefix("ok") {
                mostrar("Abrindo \(Rede.hostLegivel(endereco)) na TV", .ok)
                atualizarDaqui(a: 2)
                return true
            }
            if resposta.contains("Overscan diagnostics") {
                mostrar("Esse navegador não abre sites pelo Mac. Abra o Overscan BR na TV", .info)
            } else {
                mostrar(Self.primeiraLinha(resposta), .alerta)
            }
        case .recusada:
            definirNavegador(.fechado)
            mostrar("Abra o Overscan BR na TV", .info)
        case .semResposta:
            mostrar(estadoTV == .foraDaRede ? "TV fora da rede" : "A TV não respondeu", .alerta)
        }
        return false
    }

    func acao(_ id: String) {
        guard !offline else {
            aplicou(id)
            return
        }
        if acaoPelaRede == false, navegador != .fechado {
            usarReserva(id)
            return
        }
        guard let chave = Arquivos.ler(Arquivos.chave),
              let url = URL(string: "http://\(ip):8081/acao?chave=\(Rede.codificar(chave))&id=\(Rede.codificar(id))") else {
            usarReserva(id)
            return
        }
        Task {
            switch await Rede.pedir(url, tempo: 2.5) {
            case .texto(let bruto):
                let resposta = bruto.trimmingCharacters(in: .whitespacesAndNewlines)
                if resposta.hasPrefix("ok") {
                    acaoPelaRede = true
                    aplicou(id)
                    return
                }
                // O Overscan BR antigo devolve o relatorio; um sem /acao avisa que nao aceita.
                if resposta.contains("Overscan diagnostics") || resposta.contains("não aceita comandos") {
                    acaoPelaRede = false
                }
                usarReserva(id)
            case .recusada:
                definirNavegador(.fechado)
                mostrar("Abra o Overscan BR na TV", .info)
            case .semResposta:
                usarReserva(id)
            }
        }
    }

    private func usarReserva(_ id: String) {
        if let tecla = Self.teclaReserva[id] {
            remoto?.enviar(tecla)
            aplicou(id)
        } else if id == "menu" {
            mostrar("O Menu precisa do Overscan BR atualizado na TV", .info)
        } else {
            mostrar("Essa ação precisa do Overscan BR atualizado na TV", .info)
        }
    }

    private func aplicou(_ id: String) {
        switch id {
        case "zoomin": nivelLetra = min(nivelLetra + 1, 9)
        case "zoomout": nivelLetra = max(nivelLetra - 1, -9)
        case "zoomreset": nivelLetra = 0
        case "bookmark": mostrar("Site salvo nos favoritos", .ok)
        default: break
        }
    }

    func abrirNavegadorSamsung() {
        guard !offline, let url = URL(string: "http://\(ip):8001/api/v2/applications/org.tizen.browser") else { return }
        Task {
            if case .texto(let resposta) = await Rede.pedir(url, metodo: "POST", tempo: 5),
               resposta.contains("true") {
                mostrar("Abrindo o navegador da Samsung", .ok)
            } else {
                mostrar("A TV não abriu o navegador da Samsung", .alerta)
            }
        }
    }

    // MARK: - Ajustes

    func salvarIP(_ texto: String) -> Bool {
        let novo = texto.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !novo.isEmpty, !novo.contains(" "), !novo.contains("/"),
              URL(string: "http://\(novo):8001/") != nil else { return false }
        if novo != ip {
            ip = novo
            UserDefaults.standard.set(novo, forKey: Self.chaveIP)
            estadoTV = .verificando
            navegador = .verificando
            acaoPelaRede = nil
            nivelLetra = 0
            remoto?.trocar(ip: novo)
            if visivel { iniciarStatus() }
        }
        return true
    }

    // MARK: - Avisos

    func mostrar(_ texto: String, _ tipo: Aviso.Tipo) {
        aviso = Aviso(texto: texto, tipo: tipo)
        tarefaAviso?.cancel()
        tarefaAviso = Task { [weak self] in
            try? await Task.sleep(for: .seconds(tipo == .alerta ? 5 : 3.5))
            guard !Task.isCancelled else { return }
            self?.aviso = nil
        }
    }

    private static func primeiraLinha(_ texto: String) -> String {
        let linha = texto.split(whereSeparator: \.isNewline).first.map(String.init) ?? texto
        return linha.count > 110 ? String(linha.prefix(110)) + "…" : linha
    }
}

/// Setas, Return, Delete e Esc viram o controle enquanto o painel esta em foco
/// e o campo de endereco nao esta sendo editado.
@MainActor
final class TecladoDoPainel {
    private var monitor: Any?
    private var ultimaRepeticao = Date.distantPast
    private var janelas: [NSWindow] = []
    private weak var modelo: ModeloTV?

    func ligar(janelas: [NSWindow], modelo: ModeloTV) {
        self.janelas = janelas
        self.modelo = modelo
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] evento in
            self?.tratar(evento) ?? evento
        }
    }

    func desligar() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        janelas = []
    }

    private func tratar(_ evento: NSEvent) -> NSEvent? {
        guard let modelo, let janela = evento.window, janelas.contains(where: { $0 === janela }) else {
            return evento
        }
        if !evento.modifierFlags.intersection([.command, .control, .option]).isEmpty { return evento }
        let editando = janela.firstResponder is NSText

        if modelo.ajustesAbertos {
            if evento.keyCode == 53 {
                modelo.ajustesAbertos = false
                return nil
            }
            return evento
        }

        switch evento.keyCode {
        case 53: // Esc
            if editando {
                janela.makeFirstResponder(nil)
            } else {
                modelo.tecla("KEY_RETURN")
            }
            return nil
        case 51, 117: // Delete
            guard !editando else { return evento }
            modelo.tecla("KEY_RETURN")
            return nil
        case 36, 76: // Return
            guard !editando else { return evento }
            modelo.tecla("KEY_ENTER")
            return nil
        case 123, 124, 125, 126:
            guard !editando else { return evento }
            // Seta segurada: no maximo uma tecla a cada 120 ms, para nao encher a fila da TV.
            if evento.isARepeat {
                guard Date().timeIntervalSince(ultimaRepeticao) > 0.12 else { return nil }
                ultimaRepeticao = Date()
            }
            let teclas: [UInt16: String] = [123: "KEY_LEFT", 124: "KEY_RIGHT", 125: "KEY_DOWN", 126: "KEY_UP"]
            if let tecla = teclas[evento.keyCode] { modelo.tecla(tecla) }
            return nil
        default:
            return evento
        }
    }
}
