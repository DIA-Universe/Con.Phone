import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var bridge: ClipboardBridge

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
                    Text("iPhone → PC는 iOS의 클립보드 개인정보 보호 정책 때문에 이 앱의 전송 버튼을 사용하는 방식으로 시작합니다.")
                }

                Section("전송 경로") {
                    Text("BLE는 동일 Wi-Fi 없이 동작합니다. 큰 이미지는 P2P 고속 연결을 누르면 iOS 시스템 승인 후 Windows 전용 Wi-Fi 링크와 AES-GCM 채널로 전송합니다.")
                }
            }
            .navigationTitle("Con.Phone")
            .onAppear { bridge.start() }
        }
    }
}
