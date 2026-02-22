using System.Text.Json;

public sealed class ConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();

    private BridgeConfig _current;

    public ConfigStore(string path)
    {
        _path = path;
        _current = LoadFromDisk() ?? new BridgeConfig();
        SaveToDisk(_current);
    }

    public BridgeConfig GetSnapshot()
    {
        lock (_lock)
        {
            // 얕은 복사 스냅샷(단순 값 타입/프로퍼티라 충분)
            return new BridgeConfig
            {
                Ds4BackAsTouchpadClick = _current.Ds4BackAsTouchpadClick,
                XInputUserIndex = _current.XInputUserIndex,
                PollHz = _current.PollHz,
                TreatTriggerAsButton = _current.TreatTriggerAsButton,
                TriggerButtonThreshold = _current.TriggerButtonThreshold,
                FeedbackToXInput = _current.FeedbackToXInput
            };
        }
    }

    public BridgeConfig Update(BridgeConfig next)
    {
        lock (_lock)
        {
            _current = next;
            SaveToDisk(_current);
            return GetSnapshot();
        }
    }

    private BridgeConfig? LoadFromDisk()
    {
        if (!File.Exists(_path)) return null;
        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<BridgeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private void SaveToDisk(BridgeConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_path, json);
    }
}