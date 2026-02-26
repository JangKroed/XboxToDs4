using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using System.Text.Json;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// 설정 파일 경로(실행 폴더에 저장)
var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
builder.Services.AddSingleton(new ConfigStore(configPath));
builder.Services.AddSingleton<BridgeState>();
builder.Services.AddHostedService<Ds4BridgeWorker>();

var app = builder.Build();

// 정적 파일(wwwroot) + index.html 서빙
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (BridgeState state) =>
{
    return Results.Json(new
    {
        state.Running,
        state.LastError,
        state.PhysicalConnected,
        state.VirtualConnected,
        state.LastPacketNumber,
        state.LastFeedbackLarge,
        state.LastFeedbackSmall
    });
});

app.MapGet("/api/xinput/slots", () =>
{
    var slots = new List<object>();
    for (uint i = 0; i < 4; i++)
    {
        var res = XInputNative.XInputGetState(i, out var st);
        slots.Add(new
        {
            index = i,
            connected = res == XInputNative.ERROR_SUCCESS,
            packet = st.dwPacketNumber
        });
    }
    return Results.Json(slots);
});

app.MapGet("/api/config", (ConfigStore store) => Results.Json(store.GetSnapshot()));

app.MapPut("/api/config", async (HttpContext ctx, ConfigStore store) =>
{
    var next = await JsonSerializer.DeserializeAsync<BridgeConfig>(ctx.Request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (next is null) return Results.BadRequest(new { error = "invalid json" });
    if (next.PollHz < 60) next.PollHz = 60;
    if (next.PollHz > 1000) next.PollHz = 1000;
    if (next.XInputUserIndex < 0) next.XInputUserIndex = 0;
    if (next.XInputUserIndex > 3) next.XInputUserIndex = 3;

    return Results.Json(store.Update(next));
});

// 로컬에서만 열리게
app.Urls.Add("http://127.0.0.1:47777");
app.Run();


// -------------------- 내부 구현 --------------------

sealed class BridgeState
{
    public volatile bool Running;
    public volatile bool PhysicalConnected;
    public volatile bool VirtualConnected;
    public volatile uint LastPacketNumber;
    public volatile string? LastError;

    public volatile byte LastFeedbackLarge;
    public volatile byte LastFeedbackSmall;
}

sealed class Ds4BridgeWorker : BackgroundService
{
    private readonly ConfigStore _configStore;
    private readonly BridgeState _state;

    public Ds4BridgeWorker(ConfigStore configStore, BridgeState state)
    {
        _configStore = configStore;
        _state = state;
    }

    private static void RawOutputLoop(
        IDualShock4Controller ds4,
        ConfigStore configStore,
        BridgeState state,
        CancellationToken token)
    {
        long lastRumbleTick = Environment.TickCount64;
        byte lastLarge = 0, lastSmall = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // ViGEm.NET README에 나오는 방식: AwaitRawOutputReport(timeout, out timedOut)  [oai_citation:3‡GitHub](https://github.com/nefarius/ViGEm.NET)
                var enumerable = ds4.AwaitRawOutputReport(250, out var timedOut);

                if (timedOut)
                {
                    // 안전장치: 한동안 rumble이 안 오면 0으로 한번 끊어주기(스틱 럼블 고착 방지)
                    if (Environment.TickCount64 - lastRumbleTick > 300 && (lastLarge != 0 || lastSmall != 0))
                    {
                        var cfg = configStore.GetSnapshot();
                        if (cfg.FeedbackToXInput)
                            SendXInputRumble(cfg.XInputUserIndex, 0, 0);

                        lastLarge = 0;
                        lastSmall = 0;
                    }
                    continue;
                }

                var buf = enumerable as byte[] ?? enumerable.ToArray();
                if (buf.Length < 6) continue;

                // DS4 output report 특징:
                // - features byte가 buf[1]에 있고 (rumble/lightbar 구분)  [oai_citation:4‡GitHub](https://github.com/nefarius/ViGEmBus/issues/80)
                // - rumble 값은 보통 buf[4], buf[5]에 있음(Trace 예시)  [oai_citation:5‡GitHub](https://github.com/nefarius/ViGEmBus/issues/80)
                byte features = buf[1];
                byte large = buf.Length > 4 ? buf[4] : (byte)0;
                byte small = buf.Length > 5 ? buf[5] : (byte)0;

                bool hasRumble = (features & 0x01) != 0; // 0x01 rumble 플래그(게임마다 0x03,0x07 등 다양)  [oai_citation:6‡GitHub](https://github.com/nefarius/ViGEmBus/issues/80)
                if (!hasRumble) continue;

                lastRumbleTick = Environment.TickCount64;

                if (large != lastLarge || small != lastSmall)
                {
                    state.LastFeedbackLarge = large;
                    state.LastFeedbackSmall = small;

                    var cfg = configStore.GetSnapshot();
                    if (cfg.FeedbackToXInput)
                        SendXInputRumble(cfg.XInputUserIndex, large, small);

                    lastLarge = large;
                    lastSmall = small;
                }
            }
            catch (Exception ex)
            {
                state.LastError = $"DS4 raw output: {ex.Message}";
                Thread.Sleep(500);
            }
        }
    }

    private static void SendXInputRumble(int userIndex, byte largeMotor, byte smallMotor)
    {
        var vib = new XInputNative.XINPUT_VIBRATION
        {
            wLeftMotorSpeed = (ushort)(largeMotor * 257),  // 0..255 -> 0..65535
            wRightMotorSpeed = (ushort)(smallMotor * 257),
        };
        _ = XInputNative.XInputSetState((uint)userIndex, ref vib);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state.Running = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            IDualShock4Controller? ds4 = null;
            ViGEmClient? client = null;

            try
            {
                client = new ViGEmClient();
                ds4 = client.CreateDualShock4Controller();
                ds4.AutoSubmitReport = false;
                ds4.Connect();
                // ds4.Connect(); _state.VirtualConnected = true; 바로 다음에 추가
                var feedbackCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _ = Task.Run(() => RawOutputLoop(ds4, _configStore, _state, feedbackCts.Token), feedbackCts.Token);

                // DS4 피드백(진동/라이트바 등) → 물리 XInput 진동 전달
                ds4.FeedbackReceived += (sender, e) =>
                {
                    var cfg = _configStore.GetSnapshot();
                    if (!cfg.FeedbackToXInput) return;

                    // 라이브러리 버전에 따라 프로퍼티명이 다를 가능성까지 고려해서 reflection으로 읽기
                    byte large = ReadByteProp(e, "LargeMotor");
                    byte small = ReadByteProp(e, "SmallMotor");

                    _state.LastFeedbackLarge = large;
                    _state.LastFeedbackSmall = small;

                    var vib = new XInputNative.XINPUT_VIBRATION
                    {
                        // 0..255 -> 0..65535 스케일
                        wLeftMotorSpeed = (ushort)(large * 257),
                        wRightMotorSpeed = (ushort)(small * 257)
                    };

                    _ = XInputNative.XInputSetState((uint)cfg.XInputUserIndex, ref vib);
                };

                // 메인 루프
                uint lastPacket = 0;
                byte lastLarge = 0;
                byte lastSmall = 0;

                while (!stoppingToken.IsCancellationRequested)
                {
                    var cfg = _configStore.GetSnapshot();
                    int delayMs = Math.Max(1, (int)Math.Round(1000.0 / cfg.PollHz));

                    var res = XInputNative.XInputGetState((uint)cfg.XInputUserIndex, out var st);
                    if (res == XInputNative.ERROR_DEVICE_NOT_CONNECTED)
                    {
                        _state.PhysicalConnected = false;
                        _state.LastError = null;

                        // 물리 패드가 없으면 중립값 유지
                        SubmitNeutral(ds4);
                        await Task.Delay(250, stoppingToken);
                        continue;
                    }

                    if (res != XInputNative.ERROR_SUCCESS)
                    {
                        _state.PhysicalConnected = false;
                        _state.LastError = $"XInputGetState error=0x{res:X}";
                        await Task.Delay(250, stoppingToken);
                        continue;
                    }

                    _state.PhysicalConnected = true;
                    _state.LastError = null;
                    long lastSubmitTick = Environment.TickCount64;

                    // 변화 없으면 Submit 생략(가볍게)
                    var now = Environment.TickCount64;
                    bool heartbeat = (now - lastSubmitTick) >= 20;

                    if (st.dwPacketNumber != lastPacket || heartbeat)
                    {
                        lastPacket = st.dwPacketNumber;
                        ApplyMapping(ds4, st.Gamepad, cfg);
                        ds4.SubmitReport();
                        lastSubmitTick = now;
                    }

                    // 피드백 끊김 방지(가끔 0,0이 안 오는 케이스가 있어 주기적으로 0 전송이 필요할 때도 있음)
                    // MVP에서는 생략. 필요하면 여기서 타임아웃 기반으로 0,0 한번 쏴주는 방식 추가.

                    await Task.Delay(delayMs, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _state.LastError = ex.Message;
            }
            finally
            {
                _state.VirtualConnected = false;
                try { ds4?.Disconnect(); } catch { }
                ds4 = null;
                client = null;
            }

            // 재시도 텀
            await Task.Delay(1000, stoppingToken);
        }

        _state.Running = false;
    }

    private static void ApplyMapping(IDualShock4Controller ds4, XInputNative.XINPUT_GAMEPAD g, BridgeConfig cfg)
    {
        // 버튼
        bool a = (g.wButtons & XInputNative.XINPUT_GAMEPAD_A) != 0;
        bool b = (g.wButtons & XInputNative.XINPUT_GAMEPAD_B) != 0;
        bool x = (g.wButtons & XInputNative.XINPUT_GAMEPAD_X) != 0;
        bool y = (g.wButtons & XInputNative.XINPUT_GAMEPAD_Y) != 0;

        bool lb = (g.wButtons & XInputNative.XINPUT_GAMEPAD_LEFT_SHOULDER) != 0;
        bool rb = (g.wButtons & XInputNative.XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;

        bool back = (g.wButtons & XInputNative.XINPUT_GAMEPAD_BACK) != 0;
        bool start = (g.wButtons & XInputNative.XINPUT_GAMEPAD_START) != 0;

        bool l3 = (g.wButtons & XInputNative.XINPUT_GAMEPAD_LEFT_THUMB) != 0;
        bool r3 = (g.wButtons & XInputNative.XINPUT_GAMEPAD_RIGHT_THUMB) != 0;

        // XInput -> DS4 (Xbox A/B/X/Y 기준)
        ds4.SetButtonState(DualShock4Button.Cross, a);
        ds4.SetButtonState(DualShock4Button.Circle, b);
        ds4.SetButtonState(DualShock4Button.Square, x);
        ds4.SetButtonState(DualShock4Button.Triangle, y);

        ds4.SetButtonState(DualShock4Button.ShoulderLeft, lb);
        ds4.SetButtonState(DualShock4Button.ShoulderRight, rb);

        ds4.SetButtonState(DualShock4Button.Share, back);
        ds4.SetButtonState(DualShock4Button.Options, start);

        ds4.SetButtonState(DualShock4Button.ThumbLeft, l3);
        ds4.SetButtonState(DualShock4Button.ThumbRight, r3);

        // 트리거(아날로그)
        ds4.SetSliderValue(DualShock4Slider.LeftTrigger, g.bLeftTrigger);
        ds4.SetSliderValue(DualShock4Slider.RightTrigger, g.bRightTrigger);

        if (cfg.TreatTriggerAsButton)
        {
            ds4.SetButtonState(DualShock4Button.TriggerLeft, g.bLeftTrigger >= cfg.TriggerButtonThreshold);
            ds4.SetButtonState(DualShock4Button.TriggerRight, g.bRightTrigger >= cfg.TriggerButtonThreshold);
        }
        else
        {
            ds4.SetButtonState(DualShock4Button.TriggerLeft, false);
            ds4.SetButtonState(DualShock4Button.TriggerRight, false);
        }

        // Sunshine 옵션: Back=TouchpadClick도 트리거  [oai_citation:11‡LizardByte Documentation](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2configuration.html?utm_source=chatgpt.com)
        ds4.SetButtonState(DualShock4SpecialButton.Touchpad, cfg.Ds4BackAsTouchpadClick && back);

        // DPad 방향
        bool up = (g.wButtons & XInputNative.XINPUT_GAMEPAD_DPAD_UP) != 0;
        bool down = (g.wButtons & XInputNative.XINPUT_GAMEPAD_DPAD_DOWN) != 0;
        bool left = (g.wButtons & XInputNative.XINPUT_GAMEPAD_DPAD_LEFT) != 0;
        bool right = (g.wButtons & XInputNative.XINPUT_GAMEPAD_DPAD_RIGHT) != 0;
        ds4.SetDPadDirection(ToDs4Dpad(up, down, left, right));

        // 스틱 (-32768..32767) -> (0..255)
        ds4.SetAxisValue(DualShock4Axis.LeftThumbX, ToByteAxis(g.sThumbLX));
        ds4.SetAxisValue(DualShock4Axis.LeftThumbY, ToByteAxisInvertY(g.sThumbLY)); // DS4 Y축 반대감 고려
        ds4.SetAxisValue(DualShock4Axis.RightThumbX, ToByteAxis(g.sThumbRX));
        ds4.SetAxisValue(DualShock4Axis.RightThumbY, ToByteAxisInvertY(g.sThumbRY));
    }

    private static void SubmitNeutral(IDualShock4Controller ds4)
    {
        ds4.SetButtonState(DualShock4Button.Cross, false);
        ds4.SetButtonState(DualShock4Button.Circle, false);
        ds4.SetButtonState(DualShock4Button.Square, false);
        ds4.SetButtonState(DualShock4Button.Triangle, false);

        ds4.SetButtonState(DualShock4Button.ShoulderLeft, false);
        ds4.SetButtonState(DualShock4Button.ShoulderRight, false);
        ds4.SetButtonState(DualShock4Button.TriggerLeft, false);
        ds4.SetButtonState(DualShock4Button.TriggerRight, false);

        ds4.SetButtonState(DualShock4Button.Share, false);
        ds4.SetButtonState(DualShock4Button.Options, false);
        ds4.SetButtonState(DualShock4SpecialButton.Touchpad, false);

        ds4.SetButtonState(DualShock4Button.ThumbLeft, false);
        ds4.SetButtonState(DualShock4Button.ThumbRight, false);

        ds4.SetDPadDirection(DualShock4DPadDirection.None);

        ds4.SetAxisValue(DualShock4Axis.LeftThumbX, 128);
        ds4.SetAxisValue(DualShock4Axis.LeftThumbY, 128);
        ds4.SetAxisValue(DualShock4Axis.RightThumbX, 128);
        ds4.SetAxisValue(DualShock4Axis.RightThumbY, 128);

        ds4.SetSliderValue(DualShock4Slider.LeftTrigger, 0);
        ds4.SetSliderValue(DualShock4Slider.RightTrigger, 0);

        ds4.SubmitReport();
    }

    private static DualShock4DPadDirection ToDs4Dpad(bool up, bool down, bool left, bool right)
    {
        if (up && right) return DualShock4DPadDirection.Northeast;
        if (up && left) return DualShock4DPadDirection.Northwest;
        if (down && right) return DualShock4DPadDirection.Southeast;
        if (down && left) return DualShock4DPadDirection.Southwest;
        if (up) return DualShock4DPadDirection.North;
        if (down) return DualShock4DPadDirection.South;
        if (left) return DualShock4DPadDirection.West;
        if (right) return DualShock4DPadDirection.East;
        return DualShock4DPadDirection.None;
    }

    private static byte ToByteAxis(int v)
    {
        // XInput: -32768..32767   [oai_citation:2‡Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_gamepad?utm_source=chatgpt.com)
        v = Math.Clamp(v, -32768, 32767);

        // [-32768..32767] -> [0..255]
        int value = v + 32768; // 0..65535

        // 반올림 포함 변환
        int b = (value * 255 + 32767) / 65535;
        return (byte)Math.Clamp(b, 0, 255);
    }

    private static byte ToByteAxisInvertY(short v)
    {
        // short.MinValue(-32768) 반전 오버플로우 방지:
        // -(int)v 로 올려서 처리하면 -(-32768)=32768이 되고 Clamp로 32767에 맞춰짐
        return ToByteAxis(-(int)v);
    }

    private static byte ReadByteProp(object obj, string propName)
    {
        var p = obj.GetType().GetProperty(propName);
        if (p is null) return 0;
        var val = p.GetValue(obj);
        return val switch
        {
            byte b => b,
            int i => (byte)i,
            ushort us => (byte)us,
            _ => 0
        };
    }
}