using System.Text.Json.Serialization;

public sealed class BridgeConfig
{
    // Sunshine 옵션명 그대로 채용
    // Back/Select 입력이 DS4 Touchpad Click도 트리거
    [JsonPropertyName("ds4_back_as_touchpad_click")]
    public bool Ds4BackAsTouchpadClick { get; set; } = true;

    // XInput 몇 번 패드를 읽을지 (0~3)
    [JsonPropertyName("xinput_user_index")]
    public int XInputUserIndex { get; set; } = 0;

    // 폴링 주기 (Hz)
    [JsonPropertyName("poll_hz")]
    public int PollHz { get; set; } = 1000;

    // 트리거를 버튼으로도 취급할지(DS4 TriggerLeft/Right 버튼)
    [JsonPropertyName("treat_trigger_as_button")]
    public bool TreatTriggerAsButton { get; set; } = true;

    // 트리거 버튼 판정 임계값(0~255)
    [JsonPropertyName("trigger_button_threshold")]
    public byte TriggerButtonThreshold { get; set; } = 20;

    // 가상 DS4 -> 물리 XInput 진동 전달
    [JsonPropertyName("feedback_to_xinput")]
    public bool FeedbackToXInput { get; set; } = true;
}