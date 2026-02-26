# Ds4Bridge

Windows에서 **물리 컨트롤러(XInput)** 입력을 **가상 DualShock 4(DS4)** 로 포워딩하는 경량 브리지입니다.  
ViGEmBus(가상 게임패드 드라이버) 위에 **가상 "Wireless Controller(DS4)"** 를 생성하여, 게임이 DS4로 인식하도록 돕습니다.

- Web UI: `http://127.0.0.1:47777` (로컬 전용)
- 설정 저장: 실행 폴더의 `config.json`

> 이 프로젝트는 Sunshine/Moonlight 코드를 포함하지 않습니다. 일부 옵션 이름은 참고용으로 차용했습니다.

---

## 주요 기능

- XInput(0~3) 컨트롤러 입력 → 가상 DS4로 포워딩
- (옵션) 가상 DS4로 들어오는 진동 → 물리 XInput 패드로 전달
- HidHide와 함께 사용 시 **더블 입력 방지**(게임에는 가상 DS4만 보이게)
- 로컬 Web UI에서 옵션 토글/값 변경
- `poll_hz` 기본값 1000Hz

---

## 지원(현재)

- 입력: XInput 기반 컨트롤러
  - 예: Xbox Controller, Flydigi Vader 5 Pro (XInput 모드)
- 출력: 가상 DualShock 4(DS4)

> DualSense(HID 입력) 등은 추후 확장 포인트입니다.

---

## 준비물

1. ViGEmBus 설치(필수)

- https://vigembus.com/

2. HidHide 설치(권장, 더블 입력 방지)

- https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/

---

## 실행

### 개발 실행

```powershell
dotnet restore
dotnet run
```
