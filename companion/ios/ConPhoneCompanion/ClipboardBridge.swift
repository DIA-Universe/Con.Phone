import Foundation
import Combine
import CoreBluetooth
import NetworkExtension
import ImageIO
import UIKit

final class ClipboardBridge: NSObject, ObservableObject {
    @Published var status = "대기 중"
    @Published var fastLinkAvailable = false

    private let serviceUUID = CBUUID(string: "67A7F001-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let txUUID = CBUUID(string: "67A7F002-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let rxUUID = CBUUID(string: "67A7F003-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let secretUUID = CBUUID(string: "67A7F004-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let capabilitiesUUID = CBUUID(string: "67A7F005-7E5B-4A2E-9B8B-1C9A5B6D1001")
    private let directLinkUUID = CBUUID(string: "67A7F006-7E5B-4A2E-9B8B-1C9A5B6D1001")

    private lazy var central = CBCentralManager(
        delegate: self,
        queue: nil,
        options: [CBCentralManagerOptionRestoreIdentifierKey: "ConPhoneClipboardCentral"]
    )

    private var peripheral: CBPeripheral?
    private var tx: CBCharacteristic?
    private var rx: CBCharacteristic?
    private var capabilitiesCharacteristic: CBCharacteristic?
    private var directLinkCharacteristic: CBCharacteristic?
    private var credential: Data?
    private var bleImageLimitBytes = 512 * 1024
    private var directConfig: DirectConfig?
    private var activeTarget = false
    private var p2p: P2PClipboardChannel?
    private var p2pGeneration: UUID?
    private var directHostRefreshAttempt = 0
    private var directConfigReadAttempt = 0
    private var capabilitiesPollScheduled = false
    private var pendingFastLinkConnect = false
    private var directJoinInFlight = false

    private struct DirectConfig {
        let ssid: String
        let passphrase: String
        let hostAddress: String?
    }

    private let bleReassembler = BleReassembler()
    private let bleWireReassembler = ClipboardWireReassembler()
    private let p2pWireReassembler = ClipboardWireReassembler()
    private var writeQueue: [Data] = []
    private var bleAckQueue: [Data] = []
    private var writeInFlight = false
    private var writeInFlightIsAck = false
    private var bleTransferCompletion: (() -> Void)?
    private var bleActiveContent: ClipboardContent?
    private var bleActiveSharedPayloadId: UUID?
    private var pendingContent: ClipboardContent?
    private var pendingSharedPayloadId: UUID?
    private var pendingGeneration = 0

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
                try ensureSafeImageDimensions(
                    width: Int(image.size.width * image.scale),
                    height: Int(image.size.height * image.scale))
                content = try ClipboardProtocol.image(.png, png)
            } else {
                status = "텍스트 또는 이미지만 지원합니다."
                return
            }

            try sendOrQueue(content)
        } catch {
            status = "클립보드 전송 준비 실패: \(error.localizedDescription)"
        }
    }

    func sendPendingShareIfPresent() {
        guard let (envelope, imageData) = SharedPayloadStore.peek() else {
            return
        }

        if pendingSharedPayloadId == envelope.id {
            return
        }

        do {
            let content: ClipboardContent

            switch envelope.kind {
            case .text:
                guard let text = envelope.text, !text.isEmpty else {
                    throw ClipboardProtocol.ProtocolError.invalidContent
                }
                content = try ClipboardProtocol.text(text)

            case .image:
                guard let imageData else {
                    throw ClipboardProtocol.ProtocolError.invalidContent
                }

                try ensureSafeImageDimensions(imageData)

                if let kind = detectEncodedImageKind(imageData) {
                    content = try ClipboardProtocol.image(kind, imageData)
                } else {
                    guard let image = UIImage(data: imageData),
                          let png = image.pngData() else {
                        throw ClipboardProtocol.ProtocolError.invalidContent
                    }

                    try ensureSafeImageDimensions(
                        width: Int(image.size.width * image.scale),
                        height: Int(image.size.height * image.scale))
                    content = try ClipboardProtocol.image(.png, png)
                }
            }

            try sendOrQueue(content, sharedPayloadId: envelope.id)
        } catch {
            SharedPayloadStore.clear(envelope.id)
            status = "공유 내용이 올바르지 않아 대기열에서 제거했습니다: \(error.localizedDescription)"
        }
    }

    private func sendOrQueue(
        _ content: ClipboardContent,
        sharedPayloadId: UUID? = nil
    ) throws {
        if let p2p, p2p.ready {
            let cursor = try ClipboardProtocol.makeFrameCursor(content)
            p2p.send(
                cursor: cursor,
                completion: { [weak self] in
                    DispatchQueue.main.async {
                        guard let self else { return }
                        if let sharedPayloadId {
                            SharedPayloadStore.clear(sharedPayloadId)
                            self.sendPendingShareIfPresent()
                        }
                    }
                },
                failure: { [weak self] error in
                    self?.recoverP2PTransfer(
                        content,
                        sharedPayloadId: sharedPayloadId,
                        error: error)
                })
            return
        }

        let bleAllowed =
            content.kind == .text ||
            content.data.count <= bleImageLimitBytes

        if bleAllowed,
           activeTarget,
           let peripheral,
           rx != nil,
           credential != nil {
            try enqueueBle(
                content,
                peripheral: peripheral,
                sharedPayloadId: sharedPayloadId,
                completion: { [weak self] in
                    guard let self else { return }
                    if let sharedPayloadId {
                        SharedPayloadStore.clear(sharedPayloadId)
                        self.sendPendingShareIfPresent()
                    }
                })
            return
        }

        setPendingContent(
            content,
            sharedPayloadId: sharedPayloadId)

        if !bleAllowed {
            status = "큰 이미지 전송을 위해 P2P 고속 연결이 필요합니다."

            if fastLinkAvailable {
                connectFastLink()
            } else if let peripheral,
                      let characteristic = directLinkCharacteristic {
                pendingFastLinkConnect = true
                peripheral.readValue(for: characteristic)
            } else {
                start()
            }
        } else {
            status = "PC 연결 후 Clipboard 전송 대기"
            start()
        }
    }

    private func flushPendingContent() {
        guard let content = pendingContent else { return }

        do {
            if let p2p, p2p.ready {
                let sharedId = pendingSharedPayloadId
                pendingContent = nil
                pendingSharedPayloadId = nil
                pendingGeneration += 1

                let cursor = try ClipboardProtocol.makeFrameCursor(content)
                p2p.send(
                    cursor: cursor,
                    completion: { [weak self] in
                        DispatchQueue.main.async {
                            guard let self else { return }
                            if let sharedId {
                                SharedPayloadStore.clear(sharedId)
                                self.sendPendingShareIfPresent()
                            }
                        }
                    },
                    failure: { [weak self] error in
                        self?.recoverP2PTransfer(
                            content,
                            sharedPayloadId: sharedId,
                            error: error)
                    })
                return
            }

            let bleAllowed =
                content.kind == .text ||
                content.data.count <= bleImageLimitBytes

            if bleAllowed,
               activeTarget,
               let peripheral,
               rx != nil,
               credential != nil {
                let sharedId = pendingSharedPayloadId
                pendingContent = nil
                pendingSharedPayloadId = nil
                pendingGeneration += 1

                try enqueueBle(
                    content,
                    peripheral: peripheral,
                    sharedPayloadId: sharedId,
                    completion: { [weak self] in
                        guard let self else { return }
                        if let sharedId {
                            SharedPayloadStore.clear(sharedId)
                            self.sendPendingShareIfPresent()
                        }
                    })
            }
        } catch {
            status = "대기 중 Clipboard 전송 실패: \(error.localizedDescription)"
        }
    }

    private func recoverP2PTransfer(
        _ content: ClipboardContent,
        sharedPayloadId: UUID?,
        error: Error
    ) {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }

            if self.pendingContent == nil {
                self.setPendingContent(
                    content,
                    sharedPayloadId: sharedPayloadId)
                self.status =
                    "P2P 전송 실패 · Clipboard를 재전송 대기 상태로 복원했습니다. (\(error.localizedDescription))"
            } else if sharedPayloadId != nil {
                // The durable App Group share queue still owns the payload.
                self.status =
                    "P2P 전송 실패 · 공유 항목은 영구 대기열에 보존했습니다."
            } else {
                self.status =
                    "P2P 전송 실패 · 기존 대기 항목이 있어 현재 클립보드는 다시 보내야 합니다."
            }

            self.start()
            self.flushPendingContent()
        }
    }

    private func setPendingContent(
        _ content: ClipboardContent,
        sharedPayloadId: UUID?
    ) {
        pendingContent = content
        pendingSharedPayloadId = sharedPayloadId
        pendingGeneration += 1
        let generation = pendingGeneration

        DispatchQueue.main.asyncAfter(deadline: .now() + 120) { [weak self] in
            guard let self,
                  self.pendingGeneration == generation,
                  self.pendingContent != nil else { return }

            self.clearPendingContent(clearSharedStore: false)
            self.status =
                "Clipboard 전송 대기 시간이 만료됐습니다. 공유한 내용은 다음 실행 때 다시 시도합니다."
        }
    }

    private func clearPendingContent(clearSharedStore: Bool) {
        let sharedId = pendingSharedPayloadId
        pendingContent = nil
        pendingSharedPayloadId = nil
        pendingGeneration += 1

        if clearSharedStore, let sharedId {
            SharedPayloadStore.clear(sharedId)
            DispatchQueue.main.async { [weak self] in
                self?.sendPendingShareIfPresent()
            }
        }
    }

    private func enqueueBle(
        _ content: ClipboardContent,
        peripheral: CBPeripheral,
        sharedPayloadId: UUID? = nil,
        completion: @escaping () -> Void = {}
    ) throws {
        guard !writeInFlight,
              writeQueue.isEmpty,
              bleTransferCompletion == nil,
              bleActiveContent == nil else {
            throw ClipboardProtocol.ProtocolError.busy
        }

        bleActiveContent = content
        bleActiveSharedPayloadId = sharedPayloadId

        do {
            let maxWrite = peripheral.maximumWriteValueLength(for: .withResponse)
            let cursor = try ClipboardProtocol.makeFrameCursor(
                content,
                chunkSize: 16 * 1024)

            while let frame = cursor.next() {
                let transferId = UInt32.random(in: UInt32.min...UInt32.max)
                writeQueue.append(contentsOf: try ClipboardProtocol.splitBle(
                    frame,
                    maxGattValue: maxWrite,
                    transferId: transferId
                ))
            }

            bleTransferCompletion = completion
            writeNext()
        } catch {
            writeQueue.removeAll()
            bleTransferCompletion = nil
            bleActiveContent = nil
            bleActiveSharedPayloadId = nil
            throw error
        }
    }

    private func recoverActiveBleTransfer(_ reason: String) {
        guard let content = bleActiveContent else {
            writeQueue.removeAll()
            writeInFlight = false
            bleTransferCompletion = nil
            return
        }

        let sharedId = bleActiveSharedPayloadId
        writeQueue.removeAll()
        writeInFlight = false
        bleTransferCompletion = nil
        bleActiveContent = nil
        bleActiveSharedPayloadId = nil

        setPendingContent(
            content,
            sharedPayloadId: sharedId)
        status = "\(reason) · Clipboard를 재전송 대기 상태로 복원했습니다."

        flushPendingContent()
        if pendingContent != nil {
            start()
        }
    }


    func connectFastLink() {
        if let peripheral, let characteristic = directLinkCharacteristic {
            pendingFastLinkConnect = true
            status = "P2P 최신 설정 확인 중"
            peripheral.readValue(for: characteristic)
            return
        }

        applyFastLinkConfiguration()
    }

    private func applyFastLinkConfiguration() {
        guard !directJoinInFlight else { return }

        guard let config = directConfig else {
            status = "Windows P2P 고속 링크 정보가 아직 없습니다."
            return
        }

        directJoinInFlight = true

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
                self.directJoinInFlight = false

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
            guard let self, self.p2p?.ready != true else { return }
            if self.directConfig?.hostAddress == nil {
                self.refreshDirectHost()
            } else {
                self.startP2PFastPath()
            }
        }
    }

    private func startP2PFastPath() {
        guard p2p?.ready != true,
              let config = directConfig,
              let host = config.hostAddress,
              let credential,
              credential.count == 64 else { return }

        let deviceHash = credential.subdata(in: 0..<32)
        let secret = credential.subdata(in: 32..<64)

        p2pGeneration = nil
        p2p?.close()

        let generation = UUID()
        p2pGeneration = generation

        let channel = P2PClipboardChannel(
            host: host,
            port: 47911,
            deviceHash: deviceHash,
            secret: secret,
            onFrame: { [weak self] frame in
                self?.receiveP2P(frame)
            },
            onStatus: { [weak self] text in
                DispatchQueue.main.async {
                    self?.status = text
                }
            },
            onReady: { [weak self] in
                DispatchQueue.main.async {
                    guard let self,
                          self.p2pGeneration == generation else { return }
                    self.flushPendingContent()
                }
            },
            onClosed: { [weak self] in
                DispatchQueue.main.async {
                    guard let self,
                          self.p2pGeneration == generation else { return }

                    self.p2p = nil
                    self.p2pGeneration = nil
                    self.status = "P2P 채널 종료 · BLE fallback 및 재전송 확인"
                    self.flushPendingContent()

                    DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
                        guard let self, self.activeTarget else { return }
                        self.sendPendingShareIfPresent()
                    }
                }
            }
        )
        p2p = channel
        channel.start()
    }

    private func receiveP2P(_ frame: Data) -> Data? {
        do {
            let chunk = try ClipboardProtocol.decode(frame)
            guard let content = try p2pWireReassembler.add(chunk) else {
                return nil
            }

            DispatchQueue.main.async { [weak self] in
                self?.apply(content)
            }

            return ClipboardProtocol.encodeAck(chunk.transferId)
        } catch {
            DispatchQueue.main.async { [weak self] in
                self?.status =
                    "P2P Clipboard 수신 거부: \(error.localizedDescription)"
            }
            return nil
        }
    }

    private func scheduleCapabilitiesRead() {
        guard !capabilitiesPollScheduled else { return }
        capabilitiesPollScheduled = true

        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self else { return }
            self.capabilitiesPollScheduled = false

            guard let peripheral = self.peripheral,
                  let characteristic = self.capabilitiesCharacteristic else { return }

            peripheral.readValue(for: characteristic)
        }
    }

    private func parseActiveTarget(_ value: Data) -> Bool? {
        guard value.count >= 9, value[0] == 2 else {
            return nil
        }
        return value[8] == 1
    }

    private func retryDirectConfigRead(_ peripheral: CBPeripheral) {
        guard let characteristic = directLinkCharacteristic else { return }

        directConfigReadAttempt += 1
        let delay = directConfigReadAttempt <= 8 ? 1.2 : 5.0
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self, weak peripheral] in
            guard let self,
                  let peripheral,
                  self.activeTarget,
                  self.p2p?.ready != true else { return }
            peripheral.readValue(for: characteristic)
        }
    }

    private func parseBleImageLimit(_ value: Data) -> Int {
        guard value.count >= 8, value[0] == 2 else {
            return 512 * 1024
        }

        let raw = Int(value.int32LE(at: 4))
        return min(max(raw, 64 * 1024), 4 * 1024 * 1024)
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
        guard !writeInFlight else { return }

        guard let peripheral, let rx else {
            if !bleAckQueue.isEmpty {
                bleAckQueue.removeAll()
                status =
                    "BLE Clipboard ACK 연결이 끊겨 응답을 취소했습니다."
            }

            if bleActiveContent != nil {
                recoverActiveBleTransfer(
                    "BLE 연결 준비 안 됨")
            }
            return
        }

        let next: Data

        if !bleAckQueue.isEmpty {
            writeInFlightIsAck = true
            next = bleAckQueue.removeFirst()
        } else if !writeQueue.isEmpty {
            writeInFlightIsAck = false
            next = writeQueue.removeFirst()
        } else {
            if bleActiveContent != nil {
                let completion = bleTransferCompletion
                bleTransferCompletion = nil
                bleActiveContent = nil
                bleActiveSharedPayloadId = nil
                status = "Clipboard 전송 완료 · BLE"
                completion?()
                flushPendingContent()
            }
            return
        }

        writeInFlight = true
        peripheral.writeValue(
            next,
            for: rx,
            type: .withResponse)
    }

    private func receiveBle(_ value: Data) {
        do {
            let fragment =
                try ClipboardProtocol.decodeBle(value)
            guard let frame =
                    try bleReassembler.add(fragment) else {
                return
            }

            let chunk =
                try ClipboardProtocol.decode(frame)
            guard let content =
                    try bleWireReassembler.add(chunk) else {
                return
            }

            apply(content)
            try enqueueBleAck(chunk.transferId)
        } catch {
            status =
                "Clipboard BLE 수신 거부: \(error.localizedDescription)"
        }
    }

    private func enqueueBleAck(_ transferId: Data) throws {
        guard activeTarget,
              let peripheral,
              rx != nil,
              credential != nil else {
            status =
                "BLE Clipboard ACK를 보낼 활성 연결이 없습니다."
            return
        }

        let maxWrite =
            peripheral.maximumWriteValueLength(
                for: .withResponse)

        let ack =
            ClipboardProtocol.encodeAck(transferId)
        let transportId =
            UInt32.random(
                in: UInt32.min...UInt32.max)

        let fragments =
            try ClipboardProtocol.splitBle(
                ack,
                maxGattValue: maxWrite,
                transferId: transportId)

        bleAckQueue.append(contentsOf: fragments)
        writeNext()
    }

    private func detectEncodedImageKind(_ data: Data) -> ClipboardKind? {
        if data.count >= 8,
           Array(data.prefix(8)) == [137, 80, 78, 71, 13, 10, 26, 10] {
            return .png
        }

        if data.count >= 4,
           data[data.startIndex] == 0xFF,
           data[data.index(after: data.startIndex)] == 0xD8,
           data[data.index(data.endIndex, offsetBy: -2)] == 0xFF,
           data[data.index(before: data.endIndex)] == 0xD9 {
            return .jpeg
        }

        if data.count >= 12,
           String(data: data.prefix(4), encoding: .ascii) == "RIFF",
           String(data: data.dropFirst(8).prefix(4), encoding: .ascii) == "WEBP" {
            return .webp
        }

        return nil
    }

    private func ensureSafeImageDimensions(width: Int, height: Int) throws {
        guard width > 0,
              height > 0,
              Int64(width) * Int64(height) <= 40_000_000 else {
            throw ClipboardProtocol.ProtocolError.tooLarge
        }
    }

    private func ensureSafeImageDimensions(_ data: Data) throws {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil)
                as? [CFString: Any],
              let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue,
              let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue else {
            throw ClipboardProtocol.ProtocolError.invalidContent
        }

        try ensureSafeImageDimensions(width: width, height: height)
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
                try ensureSafeImageDimensions(content.data)
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
        capabilitiesCharacteristic = nil
        capabilitiesPollScheduled = false
        directLinkCharacteristic = nil
        bleImageLimitBytes = 512 * 1024
        activeTarget = false
        directJoinInFlight = false

        let dataPlaneAlive = p2p?.ready == true
        bleAckQueue.removeAll()
        writeInFlightIsAck = false
        recoverActiveBleTransfer("Bluetooth 연결 끊김")
        status = dataPlaneAlive
            ? "Bluetooth 연결 끊김 · 기존 P2P Clipboard 채널 유지"
            : "PC Bluetooth 연결 끊김 · 재검색"
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
        peripheral.discoverCharacteristics(
            [txUUID, rxUUID, secretUUID, capabilitiesUUID, directLinkUUID],
            for: service)
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
                // Read after all characteristics are collected so the
                // Secret -> Capabilities -> DirectLink chain never depends
                // on CoreBluetooth's characteristic array order.
                break
            case capabilitiesUUID:
                capabilitiesCharacteristic = characteristic
            case directLinkUUID:
                directLinkCharacteristic = characteristic
            default:
                break
            }
        }

        if let secret = service.characteristics?.first(where: { $0.uuid == secretUUID }) {
            peripheral.readValue(for: secret)
        } else {
            status = "Clipboard 인증 characteristic 없음"
            return
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
                    guard self != nil, let peripheral else { return }
                    peripheral.readValue(for: characteristic)
                }
            } else if characteristic.uuid == capabilitiesUUID {
                bleImageLimitBytes = 512 * 1024
                scheduleCapabilitiesRead()
            } else if characteristic.uuid == directLinkUUID {
                activeTarget = false
                directJoinInFlight = false
                if let config = directConfig {
                    NEHotspotConfigurationManager.shared.removeConfiguration(
                        forSSID: config.ssid)
                }
                directConfig = nil
                fastLinkAvailable = false
                p2pGeneration = nil
                p2p?.close()
                p2p = nil
                status = "활성 대상이 변경되어 상태 확인 대기"
                scheduleCapabilitiesRead()
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
                flushPendingContent()
                if let capabilitiesCharacteristic {
                    peripheral.readValue(for: capabilitiesCharacteristic)
                } else if let directLinkCharacteristic {
                    peripheral.readValue(for: directLinkCharacteristic)
                }
            }
            return
        }

        if characteristic.uuid == capabilitiesUUID {
            bleImageLimitBytes = parseBleImageLimit(value)

            if let reportedActive = parseActiveTarget(value) {
                let changed = activeTarget != reportedActive
                activeTarget = reportedActive

                if !reportedActive {
                    directJoinInFlight = false
                    if let config = directConfig {
                        NEHotspotConfigurationManager.shared.removeConfiguration(
                            forSSID: config.ssid)
                    }
                    directConfig = nil
                    fastLinkAvailable = false
                    p2pGeneration = nil
                    p2p?.close()
                    p2p = nil

                    if changed {
                        status = "현재 iPhone은 대기 대상 · Clipboard 전송 비활성"
                    }
                } else {
                    if changed {
                        status = "현재 iPhone이 활성 대상이 됐습니다."
                    }
                    flushPendingContent()

                    if let directLinkCharacteristic {
                        peripheral.readValue(for: directLinkCharacteristic)
                    }
                }
            } else if let directLinkCharacteristic {
                // Backward compatibility with older v2 servers.
                peripheral.readValue(for: directLinkCharacteristic)
            }

            scheduleCapabilitiesRead()
            return
        }

        if characteristic.uuid == directLinkUUID {
            // A successful read is only possible for the active trusted target.
            // Keep BLE fallback usable even when the PC cannot publish P2P.
            activeTarget = true
            directConfigReadAttempt = 0
            directConfig = parseDirectConfig(value)
            fastLinkAvailable = directConfig != nil
            flushPendingContent()

            if let config = directConfig {
                if config.hostAddress != nil && directHostRefreshAttempt > 0 {
                    startP2PFastPath()
                } else if p2p?.ready != true {
                    // Windows creates the direct link lazily only when a fast path is
                    // actually needed. A valid config therefore acts as the request.
                    pendingFastLinkConnect = false
                    status = "Windows가 P2P 고속 전송을 요청했습니다 · iOS 승인 필요"
                    applyFastLinkConfiguration()
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

        let wasAck = writeInFlightIsAck
        writeInFlight = false
        writeInFlightIsAck = false

        if let error {
            if wasAck {
                bleAckQueue.removeAll()
                status =
                    "BLE Clipboard ACK 전송 실패: \(error.localizedDescription)"
                writeNext()
            } else {
                recoverActiveBleTransfer(
                    "BLE Clipboard 전송 실패: \(error.localizedDescription)")
            }
            return
        }

        writeNext()
    }
}
