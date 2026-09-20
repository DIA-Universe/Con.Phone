package universe.dia.conphone

object FriendlyStatus {
    fun summary(detail: String): String = when {
        detail.contains("실패") || detail.contains("거부") || detail.contains("시간 초과") || detail.contains("오류") ->
            "연결을 확인해 주세요. 설정에서 상세 정보를 볼 수 있어요."
        detail.contains("권한") -> "연결에 필요한 권한을 확인해 주세요."
        detail.contains("중지") -> "연결을 중지했어요."
        detail.contains("끊") || detail.contains("재연결") -> "PC에 다시 연결하고 있어요."
        detail.contains("ACK 대기") || detail.contains("적용 확인 대기") -> "PC에서 받을 때까지 기다리고 있어요."
        detail.contains("완료") || detail.contains("적용했습니다") -> "작업을 완료했어요."
        detail.contains("전송 중") || detail.contains("수신 중") -> "복사한 내용을 보내고 있어요."
        detail.contains("대기") -> "PC 연결을 기다리고 있어요."
        detail.contains("켜 주세요") -> "Bluetooth를 켜 주세요."
        detail.contains("페어링") -> "Bluetooth 설정에서 PC를 연결해 주세요."
        detail.contains("활성 대상이 됐") || detail.contains("Clipboard 연결됨") || detail.contains("채널 연결됨") -> "PC와 연결됐어요."
        detail.contains("검색") || detail.contains("준비") || detail.contains("연결 중") -> "PC에 연결하고 있어요."
        detail.contains("시작해") -> "PC 연결을 눌러 시작하세요."
        else -> "설정에서 연결 상태를 확인할 수 있어요."
    }
}
