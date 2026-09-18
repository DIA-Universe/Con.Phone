import Foundation
import Combine
import CoreBluetooth
import NetworkExtension
import UIKit

final class ClipboardBridge: NSObject, ObservableObject {
    @Published var status = "대기 중"
    @Published var fastLinkAvailable = false

    private let serviceUUID = CBUUID(string: "67A7F001-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let txUUID = CBUUID(string: "67A7F002-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let rxUUID = CBUUID(string: "67A7F003-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let secretUUID = CBUUID(string: "67A7F004-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let directLinkUUID = CBUUID(string: "67A7F006-7E5B-4A2E-9B8B-1C9A5B6D1001")

    private lazy var central = CBCentralManager(
        delegate: self,
        queue: nil,
        options: [CBCentralManagerOptionRestoreIdentifierKey: "ConPhoneClipboardCentral"]
    )

    private var peripheral: CBPeripheral?
    private var tx: CBCharacteristic?
    private var rx: CBCharacteristic?
    private var directLinkCharacteristic: CBCharacteristic?
    private var credential: Data?
    private var directConfig: DirectConfig?
    private var lan: LanClipboardChannel?
    private var directHostRefreshAttempt = 0

    private struct DirectConfig {
        let ssid: String
        let passphrase: String
        let hostAddress: String?
    }

    private let bleReassembler = BleReassembler()
    private let wireReassembler = ClipboardWireReassembler()
    private var writeQueue: [Data] = []
    private var writeInFlight = false

    func start() {
        _ = central
        if central.state == .poweredOn {
            scan()
        } else {
            status = "Bluetooth 준비 대기"
        }
    }

    func sendPasteboard() {
        do {
            let pasteboard = UIPasteboard.general
            let content: ClipboardContent
            if let text = pasteboard.string, !text.isEmpty {
                content = try ClipboardProtocol.text(text)
            } else if let image = pasteboard.image, let png = image.pngData() {
                content = try ClipboardProtocol.image(.png, png)
            } else {
                status = "텍스트 또는 이미지만 지원합니다."
                return
            }

            if let lan, lan.ready {
                lan.send(frames: try ClipboardProtocol.encode(content))
                return
            }

            guard let peripheral, let rx else {
                status = fastLinkAvailable
                    ? "BLE 연결이 없습니다. P2P 고속 연결을 다시 확인해 주세요."
                    : "PC Clipboard BLE 연결이 없습니다."
                return
            }

            if content.kind != .text && content.data.count > 512 * 1024 {
                status = fastLinkAvailable
                    ? "이미지가 커서 BLE 대신 P2P 고속 연결이 필요합니다."
                    : "512 KiB 초과 이미지는 P2P 고속 연결 후 전송해 주세요."
                return
            }

            let maxWrite = peripheral.maximumWriteValueLength(for: .withResponse)
            let frames = try ClipboardProtocol.encode(content, chunkSize: 16 * 1024)
            for frame in frames {
                let transferId = UInt32.random(in: UInt32.min...UInt32.max)
                writeQueue.append(contentsOf: try ClipboardProtocol.splitBle(
                    frame,
                    maxGattValue: maxWrite,
                    transferId: transferId
                ))
            }
            writeNext()
        } catch {
            status = "클립보드 전송 준비 실패: \(error.localizedDescription)"
        }
    }

    func connectFastLink() {
        guard let config = directConfig else {
            status = "Windows P2P 고속 링크 정보가 아직 없습니다."
            return
        }

        let hotspot = NEHotspotConfiguration(
            ssid: config.ssid,
            passphrase: config.passphrase,
            isWEP: false
        )
        hotspot.joinOnce = true
        status = "P2P Wi-Fi 연결 승인 대기"

        NEHotspotConfigurationManager.shared.apply(hotspot) { [weak self] error in
            DispatchQueue.main.async {
                guard let self else { return }

                if let error {
                    let nsError = error as NSError
                    if nsError.code != NEHotspotConfigurationError.alreadyAssociated.rawValue {
                        self.status = "P2P Wi-Fi 연결 실패/거부: \(error.localizedDescription)"
                        return
                    }
                }

                self.status = "P2P Wi-Fi 연결됨 · Windows 주소 확인 중"
                self.directHostRefreshAttempt = 0
                self.refreshDirectHost()
            }
        }
    }

    private func refreshDirectHost() {
        guard directHostRefreshAttempt < 8 else {
            status = "P2P Windows 주소 확인 실패 · BLE fallback 유지"
            return
        }
        directHostRefreshAttempt += 1

        guard let peripheral, let characteristic = directLinkCharacteristic else {
            status = "P2P 설정 BLE 연결이 없습니다."
            return
        }

        peripheral.readValue(for: characteristic)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in
            guard let self, self.lan?.ready != true else { return }
            if self.directConfig?.hostAddress == nil {
                self.refreshDirectHost()
            } else {
                self.startLanFastPath()
            }
        }
    }

    private func startLanFastPath() {
        guard lan?.ready != true,
              let config = directConfig,
              let host = config.hostAddress,
              let credential,
              credential.count == 64 else { return }

        let deviceHash = credential.subdata(in: 0..<32)
        let secret = credential.subdata(in: 32..<64)

        lan?.close()
        let channel = LanClipboardChannel(
            host: host,
            port: 47911,
            deviceHash: deviceHash,
            secret: secret,
            onFrame: { [weak self] frame in
                DispatchQueue.main.async {
                    self?.receiveLan(frame)
                }
            },
            onStatus: { [weak self] text in
                DispatchQueue.main.async {
                    self?.status = text
                }
            }
        )
        lan = channel
        channel.start()
    }

    private func receiveLan(_ frame: Data) {
        do {
            let chunk = try ClipboardProtocol.decode(frame)
            guard let content = try wireReassembler.add(chunk) else { return }
            apply(content)
        } catch {
            status = "P2P Clipboard 수신 거부: \(error.localizedDescription)"
        }
    }

    private func parseDirectConfig(_ value: Data) -> DirectConfig? {
        guard value.count >= 4, value[0] == 2 else { return nil }
        let ssidLength = Int(value[1])
        let passLength = Int(value[2])
        let hostLength = Int(value[3])
        guard ssidLength >= 1, ssidLength <= 32,
              passLength >= 8, passLength <= 63,
              hostLength <= 45,
              value.count == 4 + ssidLength + passLength + hostLength else {
            return nil
        }

        let ssidStart = 4
        let passStart = ssidStart + ssidLength
        let hostStart = passStart + passLength
        guard let ssid = String(
                data: value.subdata(in: ssidStart..<passStart),
                encoding: .utf8),
              let passphrase = String(
                data: value.subdata(in: passStart..<hostStart),
                encoding: .utf8) else {
            return nil
        }

        let host = hostLength == 0
            ? nil
            : String(data: value.subdata(in: hostStart..<value.count), encoding: .ascii)

        return DirectConfig(
            ssid: ssid,
            passphrase: passphrase,
            hostAddress: host
        )
    }

    private func scan() {
        guard central.state == .poweredOn else { return }
        status = "Con.Phone PC 검색 중"
        central.stopScan()
        central.scanForPeripherals(
            withServices: [serviceUUID],
            options: [CBCentralManagerScanOptionAllowDuplicatesKey: false]
        )
    }

    private func writeNext() {
        guard !writeInFlight,
              !writeQueue.isEmpty,
              let peripheral,
              let rx else { return }

        writeInFlight = true
        let next = writeQueue.removeFirst()
        peripheral.writeValue(next, for: rx, type: .withResponse)
    }

    private func receiveBle(_ value: Data) {
        do {
            let fragment = try ClipboardProtocol.decodeBle(value)
            guard let frame = try bleReassembler.add(fragment) else { return }
            let chunk = try ClipboardProtocol.decode(frame)
            guard let content = try wireReassembler.add(chunk) else { return }
            apply(content)
        } catch {
            status = "Clipboard BLE 수신 거부: \(error.localizedDescription)"
        }
    }

    private func apply(_ content: ClipboardContent) {
        do {
            switch content.kind {
            case .text:
                guard let text = String(data: content.data, encoding: .utf8) else {
                    throw ClipboardProtocol.ProtocolError.invalidContent
                }
                UIPasteboard.general.string = text
                status = "PC 텍스트를 iPhone 클립보드에 받았습니다."
            case .png, .jpeg, .webp:
                guard let image = UIImage(data: content.data) else {
                    throw ClipboardProtocol.ProtocolError.invalidContent
                }
                UIPasteboard.general.image = image
                status = "PC 이미지를 iPhone 클립보드에 받았습니다."
            }
        } catch {
            status = "iOS 클립보드 적용 실패: \(error.localizedDescription)"
        }
    }
}

extension ClipboardBridge: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        switch central.state {
        case .poweredOn:
            scan()
        case .poweredOff:
            status = "Bluetooth를 켜 주세요."
        case .unauthorized:
            status = "Bluetooth 권한이 필요합니다."
        default:
            status = "Bluetooth 준비 중"
        }
    }

    func centralManager(
        _ central: CBCentralManager,
        didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any],
        rssi RSSI: NSNumber
    ) {
        central.stopScan()
        self.peripheral = peripheral
        peripheral.delegate = self
        status = "PC BLE 연결 중"
        central.connect(peripheral)
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        status = "BLE 연결됨 · Clipboard 서비스 확인"
        peripheral.discoverServices([serviceUUID])
    }

    func centralManager(
        _ central: CBCentralManager,
        didFailToConnect peripheral: CBPeripheral,
        error: Error?
    ) {
        status = "PC 연결 실패 · 재검색"
        self.peripheral = nil
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.scan() }
    }

    func centralManager(
        _ central: CBCentralManager,
        didDisconnectPeripheral peripheral: CBPeripheral,
        timestamp: CFAbsoluteTime,
        isReconnecting: Bool,
        error: Error?
    ) {
        tx = nil
        rx = nil
        directLinkCharacteristic = nil
        if lan?.ready != true {
            credential = nil
            directConfig = nil
            fastLinkAvailable = false
        }
        writeQueue.removeAll()
        writeInFlight = false
        status = "PC 연결 끊김 · 재검색"
        if !isReconnecting {
            self.peripheral = nil
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.scan() }
        }
    }

    func centralManager(_ central: CBCentralManager, willRestoreState dict: [String: Any]) {
        if let restored = (dict[CBCentralManagerRestoredStatePeripheralsKey] as? [CBPeripheral])?.first {
            peripheral = restored
            restored.delegate = self
            status = "백그라운드 BLE 연결 복원 중"
        }
    }
}

extension ClipboardBridge: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard error == nil,
              let service = peripheral.services?.first(where: { $0.uuid == serviceUUID }) else {
            status = "Clipboard BLE 서비스 없음"
            return
        }
        peripheral.discoverCharacteristics([txUUID, rxUUID, secretUUID, directLinkUUID], for: service)
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didDiscoverCharacteristicsFor service: CBService,
        error: Error?
    ) {
        guard error == nil else {
            status = "Clipboard characteristic 확인 실패"
            return
        }

        for characteristic in service.characteristics ?? [] {
            switch characteristic.uuid {
            case txUUID:
                tx = characteristic
                peripheral.setNotifyValue(true, for: characteristic)
            case rxUUID:
                rx = characteristic
            case secretUUID:
                peripheral.readValue(for: characteristic)
            case directLinkUUID:
                directLinkCharacteristic = characteristic
            default:
                break
            }
        }

        status = "BLE Clipboard 준비 중"
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didUpdateValueFor characteristic: CBCharacteristic,
        error: Error?
    ) {
        if let error {
            if characteristic.uuid == secretUUID {
                status = "Windows에서 기기 승인 대기"
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self, weak peripheral] in
                    guard let self, let peripheral else { return }
                    peripheral.readValue(for: characteristic)
                }
            } else if characteristic.uuid == directLinkUUID {
                status = "P2P 설정 읽기 실패 · BLE fallback 유지"
            } else {
                status = "BLE 읽기 실패: \(error.localizedDescription)"
            }
            return
        }

        guard let value = characteristic.value else { return }

        if characteristic.uuid == secretUUID {
            if value.count == 64 {
                credential = value
                status = "BLE Clipboard 연결됨 · 동일 Wi-Fi 불필요"
                if let directLinkCharacteristic {
                    peripheral.readValue(for: directLinkCharacteristic)
                }
            }
            return
        }

        if characteristic.uuid == directLinkUUID {
            if let config = parseDirectConfig(value) {
                directConfig = config
                fastLinkAvailable = true
                if config.hostAddress != nil && directHostRefreshAttempt > 0 {
                    startLanFastPath()
                } else {
                    status = "P2P 고속 연결 사용 가능 · 필요하면 버튼으로 연결"
                }
            }
            return
        }

        if characteristic.uuid == txUUID {
            receiveBle(value)
        }
    }

    func peripheral(
        _ peripheral: CBPeripheral,
        didWriteValueFor characteristic: CBCharacteristic,
        error: Error?
    ) {
        guard characteristic.uuid == rxUUID else { return }
        writeInFlight = false

        if let error {
            writeQueue.removeAll()
            status = "BLE Clipboard 전송 실패: \(error.localizedDescription)"
            return
        }

        if writeQueue.isEmpty {
            status = "Clipboard 전송 완료 · BLE"
        } else {
            writeNext()
        }
    }
}
