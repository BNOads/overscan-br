import AppKit
import SwiftUI

// MARK: - Cores e letras

extension Color {
    init(hex: UInt32, opacidade: Double = 1) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            opacity: opacidade
        )
    }
}

enum Paleta {
    static let fundo = Color(hex: 0x0E0F12)
    static let cartao = Color(hex: 0x17181D)
    static let botao = Color(hex: 0x23252B)
    static let borda = Color.white.opacity(0.055)
    static let texto = Color(hex: 0xF4F5F7)
    static let texto2 = Color(hex: 0x8D919A)
    static let texto3 = Color(hex: 0x5B5F68)

    static let verde = Color(hex: 0x3DDC84)
    static let ambar = Color(hex: 0xF5B942)
    static let vermelho = Color(hex: 0xFF5C5C)
    static let azul = Color(hex: 0x5B9BFF)
    static let amarelo = Color(hex: 0xF6C945)
    static let verdeTecla = Color(hex: 0x4ADE80)
    static let lilas = Color(hex: 0xA996FF)
}

func fonte(_ tamanho: CGFloat, _ peso: Font.Weight = .regular) -> Font {
    .system(size: tamanho, weight: peso, design: .rounded)
}

struct EstiloToque: ButtonStyle {
    var escala: CGFloat = 0.96

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? escala : 1)
            .brightness(configuration.isPressed ? 0.07 : 0)
            .animation(.spring(response: 0.22, dampingFraction: 0.7), value: configuration.isPressed)
    }
}

// MARK: - Painel

struct Painel: View {
    let modelo: ModeloTV
    /// Sem controles do AppKit, para a imagem de conferencia.
    var estatico = false
    /// Presente quando o painel mora na janela normal: mostra o botao de fechar
    /// e deixa arrastar a janela pelo fundo.
    var aoFechar: (() -> Void)?

    @State private var endereco = ""
    @FocusState private var campoFocado: Bool

    static let largura: CGFloat = 372
    static let raio: CGFloat = 22

    var body: some View {
        VStack(spacing: 0) {
            cabecalho
                .padding(.bottom, 16)
            linhaNavegador
                .padding(.bottom, 11)
            campoEndereco
                .padding(.bottom, 8)
            atalhos
                .padding(.bottom, 16)
            controle
                .padding(.bottom, 10)
            blocos
                .padding(.bottom, 10)
            volume
                .padding(.bottom, 12)
            rodape
        }
        .padding(.horizontal, 20)
        .padding(.top, 18)
        .padding(.bottom, 12)
        .frame(width: Self.largura)
        .background {
            ZStack {
                Paleta.fundo
                if aoFechar != nil && !estatico {
                    ArrastaJanela()
                }
            }
        }
        .overlay {
            if modelo.ajustesAbertos {
                PainelAjustes(modelo: modelo, estatico: estatico)
                    .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: 0.18), value: modelo.ajustesAbertos)
        // A janela da barra de menus e transparente: a forma do painel e esta.
        .clipShape(RoundedRectangle(cornerRadius: Self.raio, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Self.raio, style: .continuous)
                .strokeBorder(Color.white.opacity(0.09), lineWidth: 1)
        )
        .background {
            if !estatico {
                AcessoJanela { origem, janela, visivel in
                    modelo.janela(janela, visivel: visivel, origem: origem)
                }
            }
        }
        .environment(\.colorScheme, .dark)
    }

    // MARK: Cabecalho

    private var corEstado: Color {
        switch modelo.estadoTV {
        case .ligada: Paleta.verde
        case .emEspera: Paleta.ambar
        case .foraDaRede: Paleta.vermelho
        case .verificando: Paleta.texto3
        }
    }

    private var textoEstado: String {
        switch modelo.estadoTV {
        case .ligada: "Ligada"
        case .emEspera: "Em espera"
        case .foraDaRede: "Fora da rede"
        case .verificando: "Verificando"
        }
    }

    private var cabecalho: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 6) {
                    Image(systemName: "tv")
                        .font(fonte(11, .semibold))
                    Text(modelo.nomeTV.isEmpty ? "Samsung" : "Samsung " + modelo.nomeTV)
                }
                .font(fonte(12, .semibold))
                .foregroundStyle(Paleta.texto2)
                .padding(.bottom, 6)

                Text("TV da Sala")
                    .font(fonte(28, .bold))
                    .foregroundStyle(Paleta.texto)
                    .padding(.bottom, 6)

                HStack(spacing: 7) {
                    Circle()
                        .fill(corEstado)
                        .frame(width: 7, height: 7)
                        .shadow(color: corEstado.opacity(0.7), radius: 4)
                    Text(textoEstado)
                        .foregroundStyle(Paleta.texto)
                    if !modelo.modeloTV.isEmpty {
                        Text(modelo.modeloTV)
                            .foregroundStyle(Paleta.texto3)
                            .padding(.leading, 2)
                    }
                }
                .font(fonte(13, .semibold))
            }
            Spacer(minLength: 0)
            Button {
                modelo.ligarDesligar()
            } label: {
                Image(systemName: "power")
                    .font(fonte(19, .semibold))
                    .foregroundStyle(Paleta.texto)
                    .frame(width: 50, height: 50)
                    .overlay(Circle().strokeBorder(Color.white.opacity(0.17), lineWidth: 1.5))
                    .contentShape(Circle())
            }
            .buttonStyle(EstiloToque(escala: 0.92))
            .help("Ligar ou desligar a TV")
            .accessibilityLabel("Ligar ou desligar a TV")
        }
    }

    // MARK: Navegador

    private var textoNavegador: String {
        switch modelo.navegador {
        case .verificando: "verificando"
        case .fechado: "fechado"
        case .telaInicial: "tela inicial"
        case .site(let host): host
        case .outro: "outro navegador aberto"
        }
    }

    private var navegadorAberto: Bool {
        switch modelo.navegador {
        case .site, .telaInicial: true
        default: false
        }
    }

    private var linhaNavegador: some View {
        HStack(spacing: 12) {
            Image(systemName: "globe")
                .font(fonte(16, .semibold))
                .foregroundStyle(navegadorAberto ? Paleta.azul : Paleta.texto2)
                .frame(width: 40, height: 40)
                .background(RoundedRectangle(cornerRadius: 13, style: .continuous).fill(Paleta.cartao))
                .overlay(RoundedRectangle(cornerRadius: 13, style: .continuous).strokeBorder(Paleta.borda))
            VStack(alignment: .leading, spacing: 1) {
                Text("Overscan BR")
                    .font(fonte(12, .medium))
                    .foregroundStyle(Paleta.texto2)
                Text(textoNavegador)
                    .font(fonte(16, .semibold))
                    .foregroundStyle(navegadorAberto ? Paleta.texto : Paleta.texto2)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 8)
            if case .site = modelo.navegador {
                Button {
                    modelo.acao("bookmark")
                } label: {
                    Image(systemName: "star")
                        .font(fonte(13, .semibold))
                        .foregroundStyle(Paleta.texto2)
                        .frame(width: 32, height: 32)
                        .background(Circle().fill(Paleta.cartao))
                        .overlay(Circle().strokeBorder(Paleta.borda))
                }
                .buttonStyle(EstiloToque(escala: 0.9))
                .help("Salvar este site nos favoritos")
                .accessibilityLabel("Salvar nos favoritos")
            } else if modelo.navegador == .fechado {
                Button {
                    modelo.abrirNavegadorSamsung()
                } label: {
                    Text("Navegador Samsung")
                        .font(fonte(12, .semibold))
                        .foregroundStyle(Paleta.texto2)
                        .padding(.horizontal, 12)
                        .frame(height: 30)
                        .background(Capsule().fill(Paleta.cartao))
                        .overlay(Capsule().strokeBorder(Paleta.borda))
                }
                .buttonStyle(EstiloToque())
                .help("Abrir o navegador da Samsung na TV")
            }
        }
    }

    private var campoEndereco: some View {
        let temTexto = !endereco.trimmingCharacters(in: .whitespaces).isEmpty
        return HStack(spacing: 10) {
            Image(systemName: "magnifyingglass")
                .font(fonte(14, .semibold))
                .foregroundStyle(Paleta.texto2)
            ZStack(alignment: .leading) {
                if endereco.isEmpty {
                    Text("Abrir site na TV")
                        .foregroundStyle(Paleta.texto3)
                        .allowsHitTesting(false)
                }
                if !estatico {
                    TextField("", text: $endereco)
                        .textFieldStyle(.plain)
                        .foregroundStyle(Paleta.texto)
                        .focused($campoFocado)
                        .onSubmit(enviar)
                        .accessibilityLabel("Abrir site na TV")
                }
            }
            .font(fonte(16, .medium))
            .frame(maxWidth: .infinity, alignment: .leading)
            Button(action: enviar) {
                Image(systemName: "arrow.up.right")
                    .font(fonte(13, .bold))
                    .foregroundStyle(temTexto ? Paleta.fundo : Paleta.texto3)
                    .frame(width: 34, height: 34)
                    .background(Circle().fill(temTexto ? Paleta.texto : Paleta.botao))
            }
            .buttonStyle(EstiloToque(escala: 0.9))
            .disabled(!temTexto)
            .help("Abrir na TV")
            .accessibilityLabel("Abrir na TV")
        }
        .padding(.leading, 16)
        .padding(.trailing, 7)
        .frame(height: 48)
        .background(RoundedRectangle(cornerRadius: 17, style: .continuous).fill(Paleta.cartao))
        .overlay(
            RoundedRectangle(cornerRadius: 17, style: .continuous)
                .strokeBorder(campoFocado ? Color.white.opacity(0.2) : Paleta.borda, lineWidth: 1)
        )
        .animation(.easeOut(duration: 0.15), value: campoFocado)
    }

    private var atalhos: some View {
        HStack(spacing: 8) {
            ForEach(["ge.globo.com", "google.com.br"], id: \.self) { site in
                Button {
                    abrir(site)
                } label: {
                    HStack(spacing: 6) {
                        Image(systemName: "arrow.up.forward")
                            .font(fonte(10, .bold))
                            .foregroundStyle(Paleta.texto3)
                        Text(site)
                            .font(fonte(13, .semibold))
                            .foregroundStyle(Paleta.texto2)
                    }
                    .padding(.horizontal, 13)
                    .frame(height: 29)
                    .background(Capsule().fill(Paleta.cartao))
                    .overlay(Capsule().strokeBorder(Paleta.borda))
                }
                .buttonStyle(EstiloToque())
                .help("Abrir \(site) na TV")
            }
            Spacer(minLength: 0)
        }
    }

    private func enviar() {
        abrir(endereco)
    }

    private func abrir(_ site: String) {
        let pedido = site
        Task {
            if await modelo.abrirSite(pedido), endereco == pedido {
                endereco = ""
                campoFocado = false
            }
        }
    }

    // MARK: Direcional

    private var controle: some View {
        HStack(spacing: 14) {
            Direcional(modelo: modelo, diametro: 176)
            VStack(spacing: 10) {
                BotaoLateral(titulo: "Voltar", simbolo: "arrow.uturn.backward") {
                    modelo.tecla("KEY_RETURN")
                }
                BotaoLateral(titulo: "Início", simbolo: "house") {
                    modelo.acao("home")
                }
                BotaoLateral(titulo: "Menu", simbolo: "line.3.horizontal") {
                    modelo.acao("menu")
                }
            }
        }
    }

    // MARK: Blocos

    private var textoLetra: String {
        switch modelo.nivelLetra {
        case 0: "Normal"
        case let n where n > 0: "+\(n)"
        case let n: "\u{2212}\(-n)"
        }
    }

    private var blocos: some View {
        VStack(spacing: 10) {
            HStack(spacing: 10) {
                Button {
                    modelo.acao("favbar")
                } label: {
                    Bloco(simbolo: "star.fill", cor: Paleta.vermelho, rotulo: "Barra", valor: "Favoritos",
                          marca: Paleta.vermelho) { EmptyView() }
                }
                .buttonStyle(EstiloToque(escala: 0.97))
                .help("Mostrar ou esconder a barra de favoritos (botão vermelho)")

                Button {
                    modelo.acao("videofull")
                } label: {
                    Bloco(simbolo: "arrow.up.left.and.arrow.down.right", cor: Paleta.azul, rotulo: "Vídeo",
                          valor: "Tela cheia", marca: Paleta.azul) { EmptyView() }
                }
                .buttonStyle(EstiloToque(escala: 0.97))
                .help("Vídeo em tela cheia (botão azul)")
            }
            HStack(spacing: 10) {
                Bloco(simbolo: "textformat.size", cor: Paleta.amarelo, rotulo: "Letra", valor: textoLetra,
                      aoTocarValor: { modelo.acao("zoomreset") }) {
                    HStack(spacing: 7) {
                        BotaoRedondo(simbolo: "minus", cor: Paleta.amarelo, dica: "Diminuir a letra (botão amarelo)") {
                            modelo.acao("zoomout")
                        }
                        BotaoRedondo(simbolo: "plus", cor: Paleta.verdeTecla, dica: "Aumentar a letra (botão verde)") {
                            modelo.acao("zoomin")
                        }
                    }
                }
                Bloco(simbolo: "arrow.up.arrow.down", cor: Paleta.lilas, rotulo: "Página", valor: "Rolar") {
                    HStack(spacing: 7) {
                        BotaoRedondo(simbolo: "chevron.up", dica: "Rolar para cima") {
                            modelo.tecla("KEY_CHUP")
                        }
                        BotaoRedondo(simbolo: "chevron.down", dica: "Rolar para baixo") {
                            modelo.tecla("KEY_CHDOWN")
                        }
                    }
                }
            }
        }
    }

    // MARK: Volume

    private var volume: some View {
        HStack(spacing: 12) {
            Image(systemName: "speaker.wave.2.fill")
                .font(fonte(13, .semibold))
                .foregroundStyle(Paleta.texto)
                .frame(width: 32, height: 32)
                .background(Circle().fill(Color.white.opacity(0.09)))
            VStack(alignment: .leading, spacing: 1) {
                Text("Som")
                    .font(fonte(12, .medium))
                    .foregroundStyle(Paleta.texto2)
                Text("Volume")
                    .font(fonte(17, .bold))
                    .foregroundStyle(Paleta.texto)
            }
            Spacer(minLength: 0)
            BotaoRedondo(simbolo: "minus", tamanho: 38, dica: "Baixar o volume") {
                modelo.tecla("KEY_VOLDOWN")
            }
            BotaoRedondo(simbolo: "speaker.slash.fill", tamanho: 38, dica: "Mudo") {
                modelo.tecla("KEY_MUTE")
            }
            BotaoRedondo(simbolo: "plus", tamanho: 38, dica: "Aumentar o volume") {
                modelo.tecla("KEY_VOLUP")
            }
        }
        .padding(.horizontal, 14)
        .frame(height: 60)
        .background(RoundedRectangle(cornerRadius: 22, style: .continuous).fill(Paleta.cartao))
        .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(Paleta.borda))
    }

    // MARK: Rodape

    private var rodape: some View {
        HStack(spacing: 8) {
            Group {
                if let aviso = modelo.aviso {
                    HStack(spacing: 7) {
                        Image(systemName: simbolo(de: aviso.tipo))
                            .font(fonte(12, .semibold))
                            .foregroundStyle(cor(de: aviso.tipo))
                        Text(aviso.texto)
                            .foregroundStyle(Paleta.texto)
                            .lineLimit(2)
                            .minimumScaleFactor(0.85)
                    }
                    .id(aviso.id)
                } else {
                    HStack(spacing: 7) {
                        indicadorConexao
                        Text(textoConexao)
                            .foregroundStyle(Paleta.texto2)
                    }
                }
            }
            .font(fonte(12, .medium))
            .transition(.opacity)
            Spacer(minLength: 8)
            if let aoFechar {
                Button(action: aoFechar) {
                    Image(systemName: "xmark")
                        .font(fonte(11, .bold))
                        .foregroundStyle(Paleta.texto2)
                        .frame(width: 30, height: 30)
                        .background(Circle().fill(Paleta.cartao))
                        .overlay(Circle().strokeBorder(Paleta.borda))
                }
                .buttonStyle(EstiloToque(escala: 0.9))
                .help("Fechar a janela (o app continua no Dock e na barra de menus)")
                .accessibilityLabel("Fechar a janela")
            }
            Button {
                modelo.ajustesAbertos = true
            } label: {
                Image(systemName: "gearshape.fill")
                    .font(fonte(14, .medium))
                    .foregroundStyle(Paleta.texto2)
                    .frame(width: 30, height: 30)
                    .background(Circle().fill(Paleta.cartao))
                    .overlay(Circle().strokeBorder(Paleta.borda))
            }
            .buttonStyle(EstiloToque(escala: 0.9))
            .help("Ajustes")
            .accessibilityLabel("Ajustes")
        }
        .frame(height: 32)
        .animation(.easeOut(duration: 0.2), value: modelo.aviso)
    }

    private var textoConexao: String {
        if modelo.estadoTV == .foraDaRede { return "TV fora da rede" }
        switch modelo.canal {
        case .conectado: return "Conectado à TV"
        case .recusado: return "A TV recusou o controle"
        case .conectando, .parado: return "Conectando…"
        }
    }

    @ViewBuilder
    private var indicadorConexao: some View {
        if modelo.estadoTV == .foraDaRede || modelo.canal == .recusado {
            Circle().fill(Paleta.vermelho).frame(width: 6, height: 6)
        } else if modelo.canal == .conectado {
            Circle()
                .fill(Paleta.verde)
                .frame(width: 6, height: 6)
                .shadow(color: Paleta.verde.opacity(0.7), radius: 3)
        } else {
            Giro()
        }
    }

    private func simbolo(de tipo: Aviso.Tipo) -> String {
        switch tipo {
        case .ok: "checkmark.circle.fill"
        case .info: "info.circle.fill"
        case .alerta: "exclamationmark.circle.fill"
        }
    }

    private func cor(de tipo: Aviso.Tipo) -> Color {
        switch tipo {
        case .ok: Paleta.verde
        case .info: Paleta.azul
        case .alerta: Paleta.ambar
        }
    }
}

// MARK: - Pecas

/// Circulo grande com quatro setas e o OK no meio.
struct Direcional: View {
    let modelo: ModeloTV
    let diametro: CGFloat

    var body: some View {
        let distancia = diametro * 0.345
        ZStack {
            Circle()
                .fill(LinearGradient(colors: [Color(hex: 0x1C1D23), Color(hex: 0x141519)],
                                     startPoint: .top, endPoint: .bottom))
            Circle()
                .strokeBorder(Color.white.opacity(0.065), lineWidth: 1)
            seta("chevron.up", "KEY_UP", nome: "Cima").offset(y: -distancia)
            seta("chevron.down", "KEY_DOWN", nome: "Baixo").offset(y: distancia)
            seta("chevron.left", "KEY_LEFT", nome: "Esquerda").offset(x: -distancia)
            seta("chevron.right", "KEY_RIGHT", nome: "Direita").offset(x: distancia)
            Button {
                modelo.tecla("KEY_ENTER")
            } label: {
                Text("OK")
                    .font(fonte(18, .bold))
                    .foregroundStyle(Paleta.texto)
                    .frame(width: diametro * 0.42, height: diametro * 0.42)
                    .background(
                        Circle().fill(LinearGradient(colors: [Color(hex: 0x2C2E35), Color(hex: 0x212329)],
                                                     startPoint: .top, endPoint: .bottom))
                    )
                    .overlay(Circle().strokeBorder(Color.white.opacity(modelo.teclaAcesa == "KEY_ENTER" ? 0.3 : 0.09)))
                    .shadow(color: .black.opacity(0.45), radius: 10, y: 5)
                    .scaleEffect(modelo.teclaAcesa == "KEY_ENTER" ? 0.95 : 1)
                    .contentShape(Circle())
            }
            .buttonStyle(EstiloToque(escala: 0.94))
            .help("OK")
            .accessibilityLabel("OK")
        }
        .frame(width: diametro, height: diametro)
        .animation(.easeOut(duration: 0.12), value: modelo.teclaAcesa)
    }

    private func seta(_ simbolo: String, _ tecla: String, nome: String) -> some View {
        Button {
            modelo.tecla(tecla)
        } label: {
            EmptyView()
        }
        .buttonStyle(EstiloSeta(simbolo: simbolo, acesa: modelo.teclaAcesa == tecla))
        .help(nome)
        .accessibilityLabel(nome)
    }
}

struct EstiloSeta: ButtonStyle {
    let simbolo: String
    let acesa: Bool

    func makeBody(configuration: Configuration) -> some View {
        let ativo = acesa || configuration.isPressed
        Image(systemName: simbolo)
            .font(fonte(16, .bold))
            .foregroundStyle(ativo ? Paleta.texto : Paleta.texto2)
            .frame(width: 46, height: 46)
            .background(Circle().fill(Color.white.opacity(ativo ? 0.09 : 0)))
            .contentShape(Circle())
            .scaleEffect(configuration.isPressed ? 0.92 : 1)
            .animation(.easeOut(duration: 0.12), value: ativo)
    }
}

struct BotaoLateral: View {
    let titulo: String
    let simbolo: String
    let acao: () -> Void

    var body: some View {
        Button(action: acao) {
            HStack(spacing: 10) {
                Image(systemName: simbolo)
                    .font(fonte(13, .semibold))
                    .foregroundStyle(Paleta.texto)
                    .frame(width: 32, height: 32)
                    .background(Circle().fill(Paleta.botao))
                Text(titulo)
                    .font(fonte(15, .semibold))
                    .foregroundStyle(Paleta.texto)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10)
            .frame(maxWidth: .infinity)
            .frame(height: 52)
            .background(RoundedRectangle(cornerRadius: 26, style: .continuous).fill(Paleta.cartao))
            .overlay(RoundedRectangle(cornerRadius: 26, style: .continuous).strokeBorder(Paleta.borda))
            .contentShape(RoundedRectangle(cornerRadius: 26, style: .continuous))
        }
        .buttonStyle(EstiloToque())
        .accessibilityLabel(titulo)
    }
}

struct BotaoRedondo: View {
    let simbolo: String
    var cor: Color = Paleta.texto
    var tamanho: CGFloat = 32
    var dica: String = ""
    let acao: () -> Void

    var body: some View {
        Button(action: acao) {
            Image(systemName: simbolo)
                .font(fonte(tamanho * 0.36, .bold))
                .foregroundStyle(cor)
                .frame(width: tamanho, height: tamanho)
                .background(Circle().fill(Paleta.botao))
                .overlay(Circle().strokeBorder(Color.white.opacity(0.05)))
                .contentShape(Circle())
        }
        .buttonStyle(EstiloToque(escala: 0.88))
        .help(dica)
        .accessibilityLabel(dica)
    }
}

/// Bloco no estilo do widget: icone em cima, rotulo cinza, valor em negrito.
struct Bloco<Controles: View>: View {
    let simbolo: String
    let cor: Color
    let rotulo: String
    let valor: String
    var marca: Color?
    var aoTocarValor: (() -> Void)?
    @ViewBuilder var controles: Controles

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top) {
                Image(systemName: simbolo)
                    .font(fonte(13, .semibold))
                    .foregroundStyle(cor)
                    .frame(width: 32, height: 32)
                    .background(Circle().fill(cor.opacity(0.15)))
                Spacer(minLength: 0)
                if let marca {
                    Circle()
                        .fill(marca)
                        .frame(width: 6, height: 6)
                        .padding(.top, 5)
                        .padding(.trailing, 3)
                }
            }
            Spacer(minLength: 0)
            HStack(alignment: .bottom, spacing: 4) {
                if let aoTocarValor {
                    Button(action: aoTocarValor) { textos }
                        .buttonStyle(EstiloToque())
                        .help("Clique para voltar ao tamanho normal")
                } else {
                    textos
                }
                Spacer(minLength: 0)
                controles
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity)
        .frame(height: 94)
        .background(RoundedRectangle(cornerRadius: 22, style: .continuous).fill(Paleta.cartao))
        .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(Paleta.borda))
        .contentShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
    }

    private var textos: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(rotulo)
                .font(fonte(12, .medium))
                .foregroundStyle(Paleta.texto2)
            Text(valor)
                .font(fonte(17, .bold))
                .foregroundStyle(Paleta.texto)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .contentShape(Rectangle())
    }
}

/// Indicador pequeno de "conectando".
struct Giro: View {
    @State private var girando = false

    var body: some View {
        Circle()
            .trim(from: 0.12, to: 0.85)
            .stroke(Paleta.texto2, style: StrokeStyle(lineWidth: 1.6, lineCap: .round))
            .frame(width: 11, height: 11)
            .rotationEffect(.degrees(girando ? 360 : 0))
            .animation(.linear(duration: 0.9).repeatForever(autoreverses: false), value: girando)
            .onAppear { girando = true }
    }
}

// MARK: - Ajustes

struct PainelAjustes: View {
    let modelo: ModeloTV
    var estatico = false

    @State private var ip = ""
    @State private var erro = false

    var body: some View {
        ZStack(alignment: .bottom) {
            Color.black.opacity(0.55)
                .contentShape(Rectangle())
                .onTapGesture { modelo.ajustesAbertos = false }

            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text("Ajustes")
                        .font(fonte(21, .bold))
                        .foregroundStyle(Paleta.texto)
                    Spacer()
                    Button {
                        modelo.ajustesAbertos = false
                    } label: {
                        Image(systemName: "xmark")
                            .font(fonte(11, .bold))
                            .foregroundStyle(Paleta.texto2)
                            .frame(width: 28, height: 28)
                            .background(Circle().fill(Paleta.botao))
                    }
                    .buttonStyle(EstiloToque(escala: 0.9))
                    .help("Fechar")
                    .accessibilityLabel("Fechar ajustes")
                }
                .padding(.bottom, 16)

                Text("Endereço IP da TV")
                    .font(fonte(12, .medium))
                    .foregroundStyle(Paleta.texto2)
                    .padding(.bottom, 7)

                HStack {
                    if estatico {
                        Text(modelo.ip).foregroundStyle(Paleta.texto)
                    } else {
                        TextField("", text: $ip, prompt: Text(ModeloTV.ipPadrao))
                            .textFieldStyle(.plain)
                            .foregroundStyle(Paleta.texto)
                            .onSubmit(salvar)
                    }
                    Spacer(minLength: 0)
                }
                .font(fonte(16, .medium))
                .padding(.horizontal, 14)
                .frame(height: 46)
                .background(RoundedRectangle(cornerRadius: 15, style: .continuous).fill(Paleta.cartao))
                .overlay(
                    RoundedRectangle(cornerRadius: 15, style: .continuous)
                        .strokeBorder(erro ? Paleta.vermelho.opacity(0.7) : Color.white.opacity(0.1))
                )
                .padding(.bottom, 14)

                linhaArquivo("Chave do Overscan BR", ok: modelo.chaveEncontrada, feminino: true)
                    .padding(.bottom, 6)
                linhaArquivo("Token do controle", ok: modelo.tokenEncontrado, feminino: false)
                    .padding(.bottom, 8)
                Text("Arquivos em Application Support, pasta Controle TV")
                    .font(fonte(11, .medium))
                    .foregroundStyle(Paleta.texto3)
                    .padding(.bottom, 12)

                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "menubar.rectangle")
                        .font(fonte(12, .semibold))
                        .foregroundStyle(Paleta.texto2)
                        .padding(.top, 1)
                    Text("Se o ícone não aparecer na barra de menus, ela pode estar cheia: segure Command e arraste ícones para liberar espaço, ou veja Ajustes do Sistema > Barra de Menus.")
                        .font(fonte(12, .medium))
                        .foregroundStyle(Paleta.texto2)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                }
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Paleta.cartao))
                .padding(.bottom, 18)

                HStack(spacing: 10) {
                    Button {
                        NSApplication.shared.terminate(nil)
                    } label: {
                        Text("Encerrar app")
                            .font(fonte(14, .semibold))
                            .foregroundStyle(Paleta.vermelho)
                            .padding(.horizontal, 16)
                            .frame(height: 40)
                            .background(Capsule().fill(Paleta.botao))
                    }
                    .buttonStyle(EstiloToque())
                    Spacer()
                    Button(action: salvar) {
                        Text("Salvar")
                            .font(fonte(14, .bold))
                            .foregroundStyle(Paleta.fundo)
                            .padding(.horizontal, 22)
                            .frame(height: 40)
                            .background(Capsule().fill(Paleta.texto))
                    }
                    .buttonStyle(EstiloToque())
                    .keyboardShortcut(.defaultAction)
                }
            }
            .padding(20)
            .background(RoundedRectangle(cornerRadius: 24, style: .continuous).fill(Color(hex: 0x15161A)))
            .overlay(RoundedRectangle(cornerRadius: 24, style: .continuous).strokeBorder(Color.white.opacity(0.08)))
            .shadow(color: .black.opacity(0.5), radius: 24, y: 10)
            .padding(10)
        }
        .onAppear { ip = modelo.ip }
    }

    private func linhaArquivo(_ nome: String, ok: Bool, feminino: Bool) -> some View {
        HStack(spacing: 8) {
            Image(systemName: ok ? "checkmark.circle.fill" : "xmark.circle.fill")
                .foregroundStyle(ok ? Paleta.verde : Paleta.vermelho)
            Text(nome)
                .foregroundStyle(Paleta.texto)
            Spacer()
            Text((ok ? "" : "não ") + (feminino ? "encontrada" : "encontrado"))
                .foregroundStyle(Paleta.texto2)
        }
        .font(fonte(13, .medium))
    }

    private func salvar() {
        if modelo.salvarIP(ip) {
            erro = false
            modelo.ajustesAbertos = false
            modelo.mostrar("Endereço da TV salvo", .ok)
        } else {
            erro = true
        }
    }
}

// MARK: - Janela do painel

/// Avisa quando o painel aparece ou some, e entrega a janela para o teclado.
struct AcessoJanela: NSViewRepresentable {
    let aoMudar: @MainActor (ObjectIdentifier, NSWindow?, Bool) -> Void

    func makeNSView(context: Context) -> VistaObservadora {
        let vista = VistaObservadora()
        vista.aoMudar = aoMudar
        return vista
    }

    func updateNSView(_ vista: VistaObservadora, context: Context) {
        vista.aoMudar = aoMudar
    }
}

final class VistaObservadora: NSView {
    var aoMudar: (@MainActor (ObjectIdentifier, NSWindow?, Bool) -> Void)?
    private var observadores: [NSObjectProtocol] = []
    private var estavaVisivel = false

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        for o in observadores { NotificationCenter.default.removeObserver(o) }
        observadores = []
        guard let janela = window else {
            avisar()
            return
        }
        let nomes: [Notification.Name] = [
            NSWindow.didChangeOcclusionStateNotification,
            NSWindow.didBecomeKeyNotification,
            NSWindow.didResignKeyNotification,
            NSWindow.willCloseNotification,
        ]
        for nome in nomes {
            let o = NotificationCenter.default.addObserver(forName: nome, object: janela, queue: .main) { [weak self] aviso in
                let fechando = aviso.name == NSWindow.willCloseNotification
                MainActor.assumeIsolated { self?.avisar(fechando: fechando) }
            }
            observadores.append(o)
        }
        avisar()
    }

    private func avisar(fechando: Bool = false) {
        guard let janela = window else {
            estavaVisivel = false
            aoMudar?(ObjectIdentifier(self), nil, false)
            return
        }
        let visivel = !fechando && janela.isVisible && janela.occlusionState.contains(.visible)
        if visivel && !estavaVisivel {
            // O painel acabou de abrir: ninguem com foco, para as setas irem para a TV.
            DispatchQueue.main.async { [weak janela] in
                janela?.makeFirstResponder(nil)
            }
            Self.tirarVidroDoSistema(de: janela)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak janela] in
                if let janela { Self.tirarVidroDoSistema(de: janela) }
            }
        }
        estavaVisivel = visivel
        aoMudar?(ObjectIdentifier(self), janela, visivel)
    }

    /// No macOS 26 a janela da barra de menus vem com um fundo de vidro de cantos
    /// menores que os do painel; ele apareceria nas quinas. O painel ja e opaco,
    /// entao o vidro sai e a forma (e a sombra) passam a ser as do painel.
    /// Se o sistema mudar essa estrutura, nada quebra: o vidro so continua la.
    static func tirarVidroDoSistema(de janela: NSWindow) {
        guard let raiz = janela.contentView?.superview?.layer ?? janela.contentView?.layer else { return }
        var mudou = false
        func varrer(_ camada: CALayer, nivel: Int) {
            guard nivel < 12 else { return }
            if NSStringFromClass(type(of: camada)) == "CABackdropLayer",
               (camada.filters ?? []).contains(where: { String(describing: $0).contains("glass") }),
               !camada.isHidden {
                camada.isHidden = true
                mudou = true
            }
            for filha in camada.sublayers ?? [] { varrer(filha, nivel: nivel + 1) }
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        varrer(raiz, nivel: 0)
        CATransaction.commit()
        if mudou { janela.invalidateShadow() }
    }
}

/// Fundo que arrasta a janela normal (nos espacos vazios do painel).
struct ArrastaJanela: NSViewRepresentable {
    func makeNSView(context: Context) -> VistaArrasto { VistaArrasto() }
    func updateNSView(_ vista: VistaArrasto, context: Context) {}
}

final class VistaArrasto: NSView {
    override var mouseDownCanMoveWindow: Bool { true }

    override func mouseDown(with evento: NSEvent) {
        window?.performDrag(with: evento)
    }
}
