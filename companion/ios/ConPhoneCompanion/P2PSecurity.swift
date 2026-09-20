import CryptoKit
import Foundation

enum P2PSecurity {
    private static let sessionLabel =
        Data("ConPhone-P2P-Session-v2".utf8)

    static func deriveSessionKey(
        secret: Data,
        deviceHash: Data,
        clientNonce: Data,
        serverNonce: Data
    ) throws -> Data {
        guard secret.count == 32,
              deviceHash.count == 32,
              clientNonce.count == 16,
              serverNonce.count == 16 else {
            throw SecurityError.invalidLength
        }

        let material =
            sessionLabel +
            deviceHash +
            clientNonce +
            serverNonce

        let code = HMAC<SHA256>.authenticationCode(
            for: material,
            using: SymmetricKey(data: secret)
        )
        return Data(code)
    }

    enum SecurityError: Error {
        case invalidLength
    }
}
