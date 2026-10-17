import AppKit
import SwiftUI

// Argumentos escondidos, para conferir o app sem mexer na TV:
//   --snapshot arquivo.png    desenha o painel com dados de exemplo e sai
//                             (--variante fechado, espera, aviso ou ajustes mostra outros estados)
//   --icone pasta.iconset     desenha o icone do app em todos os tamanhos e sai
//   --testar-conexao          le os dois status e abre o canal do controle sem mandar nada
@main
enum Entrada {
    @MainActor
    static func main() {
        let argumentos = CommandLine.arguments
        if let i = argumentos.firstIndex(of: "--snapshot"), i + 1 < argumentos.count {
            let variante = argumentos.firstIndex(of: "--variante").flatMap {
                $0 + 1 < argumentos.count ? argumentos[$0 + 1] : nil
            }
            exit(Conferencia.gravarPainel(em: argumentos[i + 1], variante: variante) ? 0 : 1)
        }
        if let i = argumentos.firstIndex(of: "--icone"), i + 1 < argumentos.count {
            exit(Conferencia.gravarIcone(pasta: argumentos[i + 1]) ? 0 : 1)
        }
        if argumentos.contains("--testar-conexao") {
            Task { @MainActor in
                exit(await Conferencia.testarConexao())
            }
            RunLoop.main.run()
        }
        posicaoInicialNaBarra()
        ControleDaTVApp.main()
    }

    /// Na primeira abertura, pede ao macOS para colocar o item perto do relogio.
    /// Num MacBook com notch e barra cheia, os itens escondidos primeiro sao os
    /// mais perto do notch; os da ponta direita ficam a vista. O numero e a
    /// distancia da borda direita (a Central de Controle fica perto de 160 a 200,
    /// a bateria perto de 260). Item-0 e o nome que o MenuBarExtra da ao item.
    /// So vale uma vez: se a pessoa arrastar o item com Command, o lugar dela fica.
    private static func posicaoInicialNaBarra() {
        let padroes = UserDefaults.standard
        let marca = "posicaoInicialNaBarraDefinida"
        let chave = "NSStatusItem Preferred Position Item-0"
        guard !padroes.bool(forKey: marca) else { return }
        if padroes.object(forKey: chave) == nil {
            padroes.set(230, forKey: chave)
        }
        padroes.set(true, forKey: marca)
    }
}

struct ControleDaTVApp: App {
    @NSApplicationDelegateAdaptor(Delegado.self) private var delegado

    var body: some Scene {
        MenuBarExtra {
            Painel(modelo: delegado.modelo)
        } label: {
            // Icone e texto, para o item ser facil de achar na barra.
            HStack(spacing: 4) {
                Image(systemName: "tv")
                Text("TV")
            }
            .accessibilityLabel("Controle da TV")
        }
        .menuBarExtraStyle(.window)
    }
}

// MARK: - Janela normal

/// Dono do modelo (uma conexao so para os dois paineis) e da janela normal, que
/// abre junto com o app e volta pelo icone do Dock. Assim o controle fica ao
/// alcance mesmo quando a barra de menus esconde o icone (notch, barra cheia).
@MainActor
final class Delegado: NSObject, NSApplicationDelegate {
    let modelo = ModeloTV()
    private var janela: JanelaDoPainel?

    func applicationDidFinishLaunching(_ notification: Notification) {
        mostrarJanela()
    }

    func applicationShouldHandleReopen(_ app: NSApplication, hasVisibleWindows: Bool) -> Bool {
        mostrarJanela()
        return false
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ app: NSApplication) -> Bool {
        false
    }

    func mostrarJanela() {
        if janela == nil {
            let painel = Painel(modelo: modelo, aoFechar: { [weak self] in self?.janela?.close() })
            let hospedeiro = NSHostingView(rootView: painel)
            hospedeiro.frame.size = hospedeiro.fittingSize
            let nova = JanelaDoPainel(conteudo: hospedeiro)
            if !nova.setFrameUsingName("JanelaDoPainel") {
                nova.center()
            }
            nova.setFrameAutosaveName("JanelaDoPainel")
            janela = nova
        }
        guard let janela else { return }
        janela.makeKeyAndOrderFront(nil)
        NSApp.activate()
        // A sombra segue a forma arredondada do painel depois do primeiro desenho.
        DispatchQueue.main.async { [weak janela] in janela?.invalidateShadow() }
    }
}

/// Janela sem moldura: quem desenha a forma, os cantos e a borda e o proprio painel.
final class JanelaDoPainel: NSWindow {
    convenience init(conteudo: NSView) {
        self.init(
            contentRect: NSRect(origin: .zero, size: conteudo.frame.size),
            styleMask: [.borderless, .miniaturizable],
            backing: .buffered,
            defer: false
        )
        title = "Controle da TV"
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        isReleasedWhenClosed = false
        collectionBehavior.insert(.fullScreenNone)
        contentView = conteudo
    }

    // Sem moldura a janela nao aceitaria teclado (campo de endereco, setas).
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    // Command W e Command M do menu tambem valem sem os botoes da moldura.
    override func performClose(_ sender: Any?) { close() }
    override func performMiniaturize(_ sender: Any?) { miniaturize(sender) }
}

// MARK: - Conferencia sem tocar na TV

enum Conferencia {
    @MainActor
    static func gravarPainel(em caminho: String, variante: String? = nil) -> Bool {
        let modelo = ModeloTV.amostra()
        switch variante {
        case "fechado":
            modelo.navegador = .fechado
            modelo.canal = .conectando
        case "espera":
            modelo.estadoTV = .emEspera
            modelo.navegador = .outro
            modelo.nivelLetra = 2
        case "aviso":
            modelo.estadoTV = .foraDaRede
            modelo.navegador = .fechado
            modelo.aviso = Aviso(texto: "Abra o Overscan BR na TV", tipo: .info)
        case "ajustes":
            modelo.ajustesAbertos = true
        default:
            break
        }
        let painel = Painel(modelo: modelo, estatico: true)
            .padding(24)
        let render = ImageRenderer(content: painel)
        render.scale = 2
        render.isOpaque = false
        return gravarPNG(render.cgImage, em: caminho)
    }

    @MainActor
    static func gravarIcone(pasta: String) -> Bool {
        try? FileManager.default.createDirectory(atPath: pasta, withIntermediateDirectories: true)
        let tamanhos: [(String, CGFloat)] = [
            ("icon_16x16", 16), ("icon_16x16@2x", 32),
            ("icon_32x32", 32), ("icon_32x32@2x", 64),
            ("icon_128x128", 128), ("icon_128x128@2x", 256),
            ("icon_256x256", 256), ("icon_256x256@2x", 512),
            ("icon_512x512", 512), ("icon_512x512@2x", 1024),
        ]
        for (nome, pixels) in tamanhos {
            let render = ImageRenderer(content: IconeApp())
            render.scale = pixels / 1024
            render.isOpaque = false
            guard gravarPNG(render.cgImage, em: (pasta as NSString).appendingPathComponent(nome + ".png")) else {
                return false
            }
        }
        return true
    }

    private static func gravarPNG(_ imagem: CGImage?, em caminho: String) -> Bool {
        guard let imagem,
              let dados = NSBitmapImageRep(cgImage: imagem).representation(using: .png, properties: [:]) else {
            return false
        }
        return (try? dados.write(to: URL(fileURLWithPath: caminho))) != nil
    }

    /// So le: os dois status e o aperto de mao do canal do controle. Nenhuma tecla,
    /// nenhum site, nenhuma acao. Nao mostra chave nem token.
    @MainActor
    static func testarConexao() async -> Int32 {
        let salvo = UserDefaults.standard.string(forKey: "ipDaTV") ?? ""
        let ip = salvo.isEmpty ? ModeloTV.ipPadrao : salvo
        print("TV: \(ip)")
        print("Chave do Overscan BR: \(Arquivos.ler(Arquivos.chave) == nil ? "não encontrada" : "encontrada")")
        print("Token do controle: \(Arquivos.ler(Arquivos.token) == nil ? "não encontrado" : "encontrado")")

        if let info = await Rede.lerTV(ip: ip) {
            print("Status da TV: \(info.emEspera ? "em espera" : "ligada"), \(info.nome), \(info.modelo)")
        } else {
            print("Status da TV: fora da rede")
        }

        switch await Rede.lerNavegador(ip: ip) {
        case .none: print("Navegador: sem resposta")
        case .some(.fechado): print("Navegador: fechado")
        case .some(.telaInicial): print("Navegador: Overscan BR, tela inicial")
        case .some(.site(let host)): print("Navegador: Overscan BR, \(host)")
        case .some(.outro): print("Navegador: outro navegador aberto")
        case .some(.verificando): print("Navegador: verificando")
        }

        let canal = CanalRemoto(ip: ip)
        canal.ligar()
        let inicio = Date()
        while Date().timeIntervalSince(inicio) < 15 {
            if canal.estado == .conectado || canal.estado == .recusado { break }
            try? await Task.sleep(for: .milliseconds(100))
        }
        let estado = canal.estado
        canal.desligar()
        let tempo = String(format: "%.1f", Date().timeIntervalSince(inicio))
        switch estado {
        case .conectado:
            print("Canal do controle: conectado (ms.channel.connect em \(tempo) s), fechado sem mandar nada")
            return 0
        case .recusado:
            print("Canal do controle: a TV recusou (ms.channel.unauthorized)")
            return 2
        default:
            print("Canal do controle: não conectou em 15 s")
            return 3
        }
    }
}

/// Icone do app: o mesmo preto do painel, a TV e as quatro teclas coloridas.
struct IconeApp: View {
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 186, style: .continuous)
                .fill(LinearGradient(colors: [Color(hex: 0x24262D), Color(hex: 0x0E0F12)],
                                     startPoint: .top, endPoint: .bottom))
                .overlay(
                    RoundedRectangle(cornerRadius: 186, style: .continuous)
                        .strokeBorder(Color.white.opacity(0.10), lineWidth: 4)
                )
                .frame(width: 824, height: 824)
                .shadow(color: .black.opacity(0.35), radius: 20, y: 12)
            VStack(spacing: 70) {
                Image(systemName: "tv")
                    .font(.system(size: 380, weight: .medium))
                    .foregroundStyle(Color(hex: 0xF4F5F7))
                HStack(spacing: 34) {
                    ForEach([Paleta.vermelho, Paleta.verdeTecla, Paleta.amarelo, Paleta.azul], id: \.self) { cor in
                        Capsule().fill(cor).frame(width: 72, height: 30)
                    }
                }
            }
            .offset(y: 10)
        }
        .frame(width: 1024, height: 1024)
    }
}
