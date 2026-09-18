import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var bridge: ClipboardBridge
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        NavigationStack {
            Form {
                Section("연결") {
                    Text(bridge.status)
                    Button("PC 검색 / 연결") {
                        bridge.start()
                    }
                    Button("현재 텍스트/이미지를 PC로 보내기") {
                        bridge.sendPasteboard()
                    }
                    Button("P2P 고속 연결") {
                        bridge.connectFastLink()
                    }
                    .disabled(!bridge.fastLinkAvailable)
                }

                Section("동작") {
                    Text("PC에서 iPhone으로 입력을 전환할 때 최신 텍스트/이미지를 Companion이 받아 iOS 클립보드에 넣습니다.")
                    Text("iPhone → PC는 이 앱의 전송 버튼을 쓰거나, 다른 앱의 공유 메뉴에서 Con.Phone을 선택하면 됩니다. 공유 확장에서 저장된 내용은 Con.Phone이 열릴 때 자동 전송합니다.")
                }

                Section("전송 경로") {
                    Text("BLE는 동일 Wi-Fi 없이 동작합니다. 큰 이미지는 P2P 고속 연결을 누르면 iOS 시스템 승인 후 Windows 전용 Wi-Fi 링크와 AES-GCM 채널로 전송합니다.")
                }
            }
            .navigationTitle("Con.Phone")
            .onAppear {
                bridge.start()
                bridge.sendPendingShareIfPresent()
            }
            .onChange(of: scenePhase) { _, phase in
                if phase == .active {
                    bridge.start()
                    bridge.sendPendingShareIfPresent()
                }
            }
        }
    }
}
