using Avalonia.Threading;

namespace ChestCounter;

internal sealed class NativeOverlayHost(string overlayImagePath) : IOverlayHost
{
    private OverlayVisual? _visual;
    private string? _lastTotal;
    private string? _lastTreasure;
    private volatile bool _stopped;

    public bool IsRunning => OperatingSystem.IsWindows() && NativeOverlay.IsRunning;
    public bool IsVisible => OperatingSystem.IsWindows() && NativeOverlay.IsVisible;

    public void Start(
        int? x,
        int? y,
        bool locked,
        int scale,
        Action<int, int> onMoved,
        Action<bool> onLockChanged,
        Action<int> onScaleChanged,
        bool visible,
        Action<bool>? onVisibleChanged)
    {
        _stopped = false;
        if (OperatingSystem.IsWindows())
        {
            NativeOverlay.Start(x, y, locked, scale, onMoved, onLockChanged, onScaleChanged, visible, onVisibleChanged);
        }
    }

    public void Update(OverlayState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_stopped) return;
            var treasure = state.Rows.FirstOrDefault(row => row.Treasure)?.Value;
            if (_lastTotal == state.Total && _lastTreasure == treasure) return;
            try
            {
                _visual ??= new OverlayVisual();
                var (png, pixels) = _visual.RenderState(state);
                ObsServer.UpdateImage(png);
                if (OperatingSystem.IsWindows()) NativeOverlay.Update(png, pixels);
                try
                {
                    var temp = overlayImagePath + ".tmp";
                    File.WriteAllBytes(temp, png);
                    File.Move(temp, overlayImagePath, true);
                }
                catch (IOException ex) { ErrorLog.Write("Write overlay image", ex); }
                catch (UnauthorizedAccessException ex) { ErrorLog.Write("Write overlay image", ex); }
                _lastTotal = state.Total;
                _lastTreasure = treasure;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Render shared overlay", ex);
                Console.WriteLine(L.F("overlay.overlay_unavailable", ex.Message));
            }
        });
    }

    public void Toggle()
    {
        if (OperatingSystem.IsWindows())
        {
            NativeOverlay.Toggle();
        }
    }

    public void ToggleLock()
    {
        if (OperatingSystem.IsWindows()) NativeOverlay.ToggleLock();
    }

    public void SetScale(int scale)
    {
        if (OperatingSystem.IsWindows()) NativeOverlay.SetScale(scale);
    }

    public void Stop()
    {
        _stopped = true;
        if (OperatingSystem.IsWindows())
        {
            NativeOverlay.Stop();
        }
        _visual?.Dispose();
        _visual = null;
        _lastTotal = null;
        _lastTreasure = null;
    }
}
