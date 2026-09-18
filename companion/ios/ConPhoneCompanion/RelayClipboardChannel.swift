import Foundation
import CryptoKit

final class RelayClipboardChannel: NSObject {
    private let endpoint: String
    private let deviceHash: Data
    private let secret: Data
    private let aad: Data
    private let onFrame: (Data) -> Void
    private let onStatus: (String) -> Void
    private let onClosed: () -> Void

    private lazy var session = URLSession(
        configuration: .default,
        delegate: self,
        delegateQueue: nil
    )
    private var task: URLSessionWebSocketTask?
    private let queue = DispatchQueue(label: "ConPhone.RelayClipboard")
    private var receiveTask: Task<Void, Never>?

    @MainActor private(set) var ready = false

    init(
        endpoint: String,
        deviceHash: Data,
        secret: Data,
        onFrame: @escaping (Data) -> Void,
        onStatus: @escaping (String) -> Void,
        onClosed: @escaping () -> Void
    ) {
        self.endpoint = endpoint
        self.deviceHash = deviceHash
        self.secret = secret
        self.aad = Data("ConPhone-Clipboard-Relay-v1".utf8) + deviceHash
        self.onFrame = onFrame
        self.onStatus = onStatus
        self.onClosed = onClosed
        super.init()
    }

    func start() {
        guard task == nil else { return }

        do {
            let url = try buildUrl()
            let socket = session.webSocketTask(with: url)
            task = socket
            onStatus("Clipboard Relay 연결 중")
            socket.resume()
        } catch {
            onStatus("Clipboard Relay URL 오류: \(error.localizedDescription)")
            onClosed()
        }
    }

    func send(frames: [Data]) {
        guard let task else {
            onStatus("Clipboard Relay가 연결되어 있지 않습니다.")
            return
        }

        Task {
            do {
                for frame in frames {
                    try await task.send(.data(try encrypt(frame)))
                }
                onStatus("Clipboard 전송 완료 · Relay(E2E)")
            } catch {
                onStatus("Clipboard Relay 전송 실패: \(error.localizedDescription)")
            }
        }
    }

    func close() {
        receiveTask?.cancel()
        receiveTask = nil
        task?.cancel(with: .normalClosure, reason: Data("disabled".utf8))
        task = nil
        session.invalidateAndCancel()
        Task { @MainActor in self.ready = false }
    }

    private func beginReceiveLoop(_ socket: URLSessionWebSocketTask) {
        receiveTask?.cancel()
        receiveTask = Task { [weak self, weak socket] in
            guard let self, let socket else { return }
            while !Task.isCancelled {
                do {
                    let message = try await socket.receive()
                    switch message {
                    case .data(let data):
                        let plaintext = try self.decrypt(data)
                        self.onFrame(plaintext)
                    case .string:
                        self.onStatus("Relay가 비바이너리 프레임을 반환해 무시했습니다.")
                    @unknown default:
                        break
                    }
                } catch {
                    if !Task.isCancelled {
                        self.onStatus("Clipboard Relay 수신 종료: \(error.localizedDescription)")
                        self.onClosed()
                    }
                    break
                }
            }
        }
    }

    private func buildUrl() throws -> URL {
        var text = endpoint.trimmingCharacters(in: .whitespacesAndNewlines)
        while text.hasSuffix("/") { text.removeLast() }

        if text.lowercased().hasPrefix("https://") {
            text = "wss://" + String(text.dropFirst(8))
        } else if text.lowercased().hasPrefix("http://") {
            text = "ws://" + String(text.dropFirst(7))
        }

        guard text.lowercased().hasPrefix("wss://") ||
              text.lowercased().hasPrefix("ws://") else {
            throw RelayError.invalidEndpoint
        }

        let room = deriveToken(label: "ConPhone-Relay-Room-v1")
        let auth = deriveToken(label: "ConPhone-Relay-Auth-v1")

        guard var components = URLComponents(string: text) else {
            throw RelayError.invalidEndpoint
        }
        var path = components.path
        if !path.hasSuffix("/relay") {
            path = path.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/relay"
            if !path.hasPrefix("/") { path = "/" + path }
        }
        components.path = path
        components.queryItems = [
            URLQueryItem(name: "room", value: room),
            URLQueryItem(name: "auth", value: auth),
            URLQueryItem(name: "side", value: "phone")
        ]
        guard let url = components.url else { throw RelayError.invalidEndpoint }
        return url
    }

    private func deriveToken(label: String) -> String {
        let key = SymmetricKey(data: secret)
        let code = HMAC<SHA256>.authenticationCode(
            for: Data(label.utf8) + deviceHash,
            using: key
        )
        return Data(code)
            .base64EncodedString()
            .trimmingCharacters(in: CharacterSet(charactersIn: "="))
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    private func encrypt(_ plaintext: Data) throws -> Data {
        guard !plaintext.isEmpty, plaintext.count <= 2 * 1024 * 1024 else {
            throw RelayError.invalidFrame
        }

        let box = try AES.GCM.seal(
            plaintext,
            using: SymmetricKey(data: secret),
            authenticating: aad
        )
        return Data([1]) + Data(box.nonce) + box.ciphertext + box.tag
    }

    private func decrypt(_ envelope: Data) throws -> Data {
        guard envelope.count >= 30, envelope[0] == 1 else {
            throw RelayError.invalidFrame
        }

        let nonceData = envelope.subdata(in: 1..<13)
        let ciphertext = envelope.subdata(in: 13..<(envelope.count - 16))
        let tag = envelope.suffix(16)
        let nonce = try AES.GCM.Nonce(data: nonceData)
        let box = try AES.GCM.SealedBox(
            nonce: nonce,
            ciphertext: ciphertext,
            tag: tag
        )
        return try AES.GCM.open(
            box,
            using: SymmetricKey(data: secret),
            authenticating: aad
        )
    }

    private enum RelayError: Error {
        case invalidEndpoint
        case invalidFrame
    }
}

extension RelayClipboardChannel: URLSessionWebSocketDelegate {
    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didOpenWithProtocol protocol: String?
    ) {
        Task { @MainActor in self.ready = true }
        onStatus("Clipboard 인터넷 Relay 연결됨 · 서버는 암호문만 중계")
        beginReceiveLoop(webSocketTask)
    }

    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
        reason: Data?
    ) {
        Task { @MainActor in self.ready = false }
        task = nil
        onClosed()
    }
}
