import Foundation

// Arquivos compartilhados com os scripts: a chave do Overscan BR e o token do
// controle remoto da TV. O conteudo nunca vai para log nem para a tela.
enum Arquivos {
    static let pasta: URL = FileManager.default.homeDirectoryForCurrentUser
        .appending(path: "Library/Application Support/Controle TV", directoryHint: .isDirectory)
    static let chave: URL = pasta.appending(path: "tv_chave.txt")
    static let token: URL = pasta.appending(path: "tv_token.txt")

    /// Primeira linha do arquivo, sem espacos. Nil se faltar ou estiver vazio.
    static func ler(_ arquivo: URL) -> String? {
        guard let texto = try? String(contentsOf: arquivo, encoding: .utf8) else { return nil }
        let linha = texto.split(whereSeparator: \.isNewline).first.map(String.init) ?? ""
        let limpa = linha.trimmingCharacters(in: .whitespaces)
        return limpa.isEmpty ? nil : limpa
    }

    static func gravarToken(_ token: String) {
        try? FileManager.default.createDirectory(at: pasta, withIntermediateDirectories: true)
        try? token.write(to: Arquivos.token, atomically: true, encoding: .utf8)
    }
}

/// O que um pedido HTTP para a TV devolveu.
enum Resposta: Sendable {
    case texto(String)
    /// A porta respondeu fechada (o app nao esta aberto).
    case recusada
    /// Nada respondeu a tempo: TV fora da rede ou ocupada.
    case semResposta
}

struct InfoTV: Sendable {
    var emEspera: Bool
    var nome: String
    var modelo: String
}

enum EstadoNavegador: Equatable, Sendable {
    case verificando
    case fechado
    case telaInicial
    case site(String)
    case outro
}

enum Rede {
    static let sessao: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 3
        config.timeoutIntervalForResource = 8
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.urlCache = nil
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }()

    static func pedir(_ url: URL, metodo: String = "GET", tempo: TimeInterval = 3) async -> Resposta {
        var pedido = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: tempo)
        pedido.httpMethod = metodo
        do {
            let (dados, _) = try await sessao.data(for: pedido)
            return .texto(String(decoding: dados, as: UTF8.self))
        } catch let erro as URLError where erro.code == .cannotConnectToHost {
            return .recusada
        } catch {
            return .semResposta
        }
    }

    /// Codifica tudo que nao for letra ASCII, numero ou - . _ ~
    static func codificar(_ valor: String) -> String {
        let permitidos = CharacterSet(charactersIn:
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        return valor.addingPercentEncoding(withAllowedCharacters: permitidos) ?? ""
    }

    /// Estado da TV pela porta 8001. Nil quando ninguem responde.
    static func lerTV(ip: String) async -> InfoTV? {
        guard let url = URL(string: "http://\(ip):8001/api/v2/") else { return nil }
        guard case .texto(let texto) = await pedir(url, tempo: 2.5) else { return nil }
        let json = (try? JSONSerialization.jsonObject(with: Data(texto.utf8))) as? [String: Any] ?? [:]
        let aparelho = json["device"] as? [String: Any] ?? [:]
        let energia = (aparelho["PowerState"] as? String)?.lowercased() ?? "on"
        let nome = desescapar((aparelho["name"] as? String) ?? (json["name"] as? String) ?? "")
        let modelo = (aparelho["modelName"] as? String) ?? ""
        return InfoTV(emEspera: energia == "standby", nome: nome, modelo: modelo)
    }

    /// Estado do navegador pela porta 8081. Nil quando a TV nao responde.
    static func lerNavegador(ip: String) async -> EstadoNavegador? {
        guard let url = URL(string: "http://\(ip):8081/") else { return nil }
        switch await pedir(url, tempo: 3) {
        case .recusada: return .fechado
        case .semResposta: return nil
        case .texto(let texto): return interpretar(texto)
        }
    }

    static func interpretar(_ texto: String) -> EstadoNavegador {
        guard texto.contains("build     : local build") else { return .outro }
        var endereco = ""
        for linha in texto.split(separator: "\n", omittingEmptySubsequences: false)
        where linha.hasPrefix("url       : ") {
            endereco = String(linha.dropFirst("url       : ".count)).trimmingCharacters(in: .whitespaces)
            break
        }
        if endereco.isEmpty || endereco.hasPrefix("data:") || endereco.hasPrefix("about:") {
            return .telaInicial
        }
        guard let host = URL(string: endereco)?.host(), !host.isEmpty else { return .telaInicial }
        return .site(semWWW(host))
    }

    static func semWWW(_ host: String) -> String {
        host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    /// Host legivel do que a pessoa digitou ("https://www.ge.globo.com/x" vira "ge.globo.com").
    static func hostLegivel(_ digitado: String) -> String {
        let comEsquema = digitado.contains("://") ? digitado : "https://" + digitado
        if let host = URL(string: comEsquema)?.host(), !host.isEmpty { return semWWW(host) }
        return digitado
    }

    static func desescapar(_ texto: String) -> String {
        texto
            .replacingOccurrences(of: "&quot;", with: "\"")
            .replacingOccurrences(of: "&#34;", with: "\"")
            .replacingOccurrences(of: "&#39;", with: "'")
            .replacingOccurrences(of: "&apos;", with: "'")
            .replacingOccurrences(of: "&lt;", with: "<")
            .replacingOccurrences(of: "&gt;", with: ">")
            .replacingOccurrences(of: "&amp;", with: "&")
    }
}
