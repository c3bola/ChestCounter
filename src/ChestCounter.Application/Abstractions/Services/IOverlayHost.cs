namespace ChestCounter;

public interface IOverlayHost
{
    bool IsRunning { get; }
    bool IsVisible { get; }
    void Start(
        int? x,
        int? y,
        bool locked,
        int scale,
        Action<int, int> onMoved,
        Action<bool> onLockChanged,
        Action<int> onScaleChanged,
        bool visible,
        Action<bool>? onVisibleChanged);
    void Update(OverlayState state);
    void Toggle();
    void ToggleLock();
    void SetScale(int scale);
    void Stop();
}
