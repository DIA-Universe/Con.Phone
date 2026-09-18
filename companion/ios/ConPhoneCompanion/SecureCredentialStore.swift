import Foundation
import Security

final class SecureCredentialStore {
    private let service = "universe.dia.conphone.clipboard"
    private let credentialAccount = "device-credential-v1"
    private let relayKey = "conphone.relay.endpoint.v1"

    func saveCredential(_ data: Data) {
        guard data.count == 64 else { return }

        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: credentialAccount
        ]
        SecItemDelete(query as CFDictionary)

        var add = query
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(add as CFDictionary, nil)
    }

    func loadCredential() -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: credentialAccount,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess,
              let data = result as? Data,
              data.count == 64 else {
            return nil
        }
        return data
    }

    func saveRelayEndpoint(_ endpoint: String?) {
        let defaults = UserDefaults.standard
        let value = endpoint?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if value.isEmpty {
            defaults.removeObject(forKey: relayKey)
        } else {
            defaults.set(value, forKey: relayKey)
        }
    }

    func loadRelayEndpoint() -> String? {
        let value = UserDefaults.standard.string(forKey: relayKey)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return value?.isEmpty == false ? value : nil
    }

    func clear() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: credentialAccount
        ]
        SecItemDelete(query as CFDictionary)
        UserDefaults.standard.removeObject(forKey: relayKey)
    }
}
