import Foundation
import Network
import CryptoKit
import Security

final class P2PClipboardChannel {
    private let connection: NWConnection
    private let secret: Data
    private let deviceHash: Data
    private let aad: Data
    private let queue = DispatchQueue(label: "ConPhone.P2PClipboard")
    private let onFrame: (Data) -> Void
    private let onStatus: (String) -> Void

    private var pendingWrites: [Data] = []
    private var writing = false
    private(set) var ready = false

    init(
        host: String,
        port: UInt16,
        deviceHash: Data,
        secret: Data,
        onFrame: @escaping (Data) -> Void,
        onStatus: @escaping (String) -> Void
    ) {
        self.deviceHash = deviceHash
        self.secret = secret
        self.aad = Data("ConPhone-Clipboard-P2P-v1".utf8) + deviceHash
        self.onFrame = onFrame
        self.onStatus = onStatus

        let endpointPort = NWEndpoint.Port(rawValue: port)!
        self.connection = NWConnection(
            host: NWEndpoint.Host(host),
            port: endpointPort,
            using: .tcp
        )
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.handshake()
            case .failed(let error):
                self.ready = false
                self.onStatus("P2P TCP 연결 실패: \(error.localizedDescription)")
            case .cancelled:
                self.ready = false
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    func send(frames: [Data]) {
        queue.async { [weak self] in
            guard let self, self.ready else { return }
            self.pendingWrites.append(contentsOf: frames)
            self.pumpWrites()
        }
    }

    func close() {
        queue.async { [weak self] in
            self?.ready = false
            self?.pendingWrites.removeAll()
            self?.connection.cancel()
        }
    }

    private func handshake() {
        var clientNonce = Data(count: 16)
        clientNonce.withUnsafeMutableBytes { bytes in
            _ = SecRandomCopyBytes(kSecRandomDefault, 16, bytes.baseAddress!)
        }

        let hello = Data("CPHELLO1".utf8)
        let signed = hello + deviceHash + clientNonce
        let mac = hmac(signed)
        let message = signed + mac

        connection.send(content: message, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            if let error {
                self.onStatus("P2P 인증 요청 실패: \(error.localizedDescription)")
                self.connection.cancel()
                return
            }

            self.receiveExact(56) { result in
                switch result {
                case .failure(let error):
                    self.onStatus("P2P 인증 응답 실패: \(error.localizedDescription)")
                    self.connection.cancel()
                case .success(let response):
                    guard response.count == 56,
                          response.prefix(8) == Data("CPREADY1".utf8) else {
                        self.onStatus("P2P 인증 응답 형식 오류")
                        self.connection.cancel()
                        return
                    }

                    let serverNonce = response.subdata(in: 8..<24)
                    let serverMac = response.subdata(in: 24..<56)
                    let expected = self.hmac(
                        Data("CPREADY1".utf8) + clientNonce + serverNonce
                    )
                    guard serverMac == expected else {
                        self.onStatus("P2P 인증 실패")
                        self.connection.cancel()
                        return
                    }

                    self.ready = true
                    self.onStatus("Clipboard P2P 고속 채널 연결됨")
                    self.readEncryptedLoop()
                    self.pumpWrites()
                }
            }
        })
    }

    private func pumpWrites() {
        guard ready, !writing, !pendingWrites.isEmpty else { return }
        writing = true
        let plaintext = pendingWrites.removeFirst()

        do {
            let sealed = try AES.GCM.seal(
                plaintext,
                using: SymmetricKey(data: secret),
                authenticating: aad
            )
            let nonce = Data(sealed.nonce)
            let body = nonce + sealed.ciphertext + sealed.tag
            var prefix = Data()
            prefix.appendInt32LE(Int32(body.count))
            let packet = prefix + body

            connection.send(content: packet, completion: .contentProcessed { [weak self] error in
                guard let self else { return }
                self.queue.async {
                    self.writing = false
                    if let error {
                        self.ready = false
                        self.pendingWrites.removeAll()
                        self.onStatus("P2P Clipboard 전송 실패: \(error.localizedDescription)")
                        self.connection.cancel()
                        return
                    }

                    if self.pendingWrites.isEmpty {
                        self.onStatus("Clipboard 전송 완료 · P2P")
                    }
                    self.pumpWrites()
                }
            })
        } catch {
            writing = false
            pendingWrites.removeAll()
            onStatus("P2P Clipboard 암호화 실패: \(error.localizedDescription)")
        }
    }

    private func readEncryptedLoop() {
        receiveExact(4) { [weak self] result in
            guard let self, self.ready else { return }

            switch result {
            case .failure(let error):
                self.ready = false
                self.onStatus("P2P 수신 종료: \(error.localizedDescription)")
                self.connection.cancel()
            case .success(let prefix):
                let length = Int(prefix.int32LE(at: 0))
                guard length >= 28,
                      length <= 1024 * 1024 + 70 + 28 else {
                    self.ready = false
                    self.onStatus("P2P 수신 길이 오류")
                    self.connection.cancel()
                    return
                }

                self.receiveExact(length) { bodyResult in
                    switch bodyResult {
                    case .failure(let error):
                        self.ready = false
                        self.onStatus("P2P 수신 실패: \(error.localizedDescription)")
                        self.connection.cancel()
                    case .success(let body):
                        do {
                            let nonceData = body.subdata(in: 0..<12)
                            let ciphertext = body.subdata(in: 12..<(body.count - 16))
                            let tag = body.suffix(16)
                            let nonce = try AES.GCM.Nonce(data: nonceData)
                            let box = try AES.GCM.SealedBox(
                                nonce: nonce,
                                ciphertext: ciphertext,
                                tag: tag
                            )
                            let plaintext = try AES.GCM.open(
                                box,
                                using: SymmetricKey(data: self.secret),
                                authenticating: self.aad
                            )
                            self.onFrame(plaintext)
                            self.readEncryptedLoop()
                        } catch {
                            self.ready = false
                            self.onStatus("P2P 수신 무결성 오류")
                            self.connection.cancel()
                        }
                    }
                }
            }
        }
    }

    private func receiveExact(
        _ count: Int,
        accumulated: Data = Data(),
        completion: @escaping (Result<Data, Error>) -> Void
    ) {
        if accumulated.count == count {
            completion(.success(accumulated))
            return
        }

        connection.receive(
            minimumIncompleteLength: 1,
            maximumLength: count - accumulated.count
        ) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let error {
                completion(.failure(error))
                return
            }

            var result = accumulated
            if let data { result.append(data) }

            if result.count == count {
                completion(.success(result))
            } else if isComplete {
                completion(.failure(ChannelError.closed))
            } else {
                self.receiveExact(count, accumulated: result, completion: completion)
            }
        }
    }

    private func hmac(_ data: Data) -> Data {
        let code = HMAC<SHA256>.authenticationCode(
            for: data,
            using: SymmetricKey(data: secret)
        )
        return Data(code)
    }

    private enum ChannelError: Error {
        case closed
    }
}
