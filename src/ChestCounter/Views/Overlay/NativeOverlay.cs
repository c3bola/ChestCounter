using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>Transparent Win32 host for the shared Avalonia-rendered overlay.</summary>
[SupportedOSPlatform("windows")]
public static class NativeOverlay
{
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static volatile bool _visible;
    private static volatile bool _locked;
    private static byte[]? _pixels;
    private static byte[]? _png;
    private static Action<int, int>? _onMoved;
    private static Action<bool>? _onLockChanged;
    private static Action<bool>? _onVisibleChanged;
    private static Action<int>? _onScaleChanged;
    private static int _scale = OverlayScale.Default;
    private static WndProcDelegate? _wndProc;
    private static bool _rendering;

    public static bool IsRunning => _hwnd != IntPtr.Zero;
    public static bool IsVisible => IsRunning && _visible;
    public static bool IsLocked => _locked;
    public static bool HotkeyRegistered { get; private set; }
    public static bool LockHotkeyRegistered { get; private set; }

    public static void Start(int? x, int? y, bool locked, int scale, Action<int, int> onMoved,
        Action<bool> onLockChanged, Action<int> onScaleChanged, bool visible = true, Action<bool>? onVisibleChanged = null)
    {
        if (_thread != null) return;
        _visible = visible;
        _locked = locked;
        _scale = OverlayScale.Clamp(scale);
        _onMoved = onMoved;
        _onLockChanged = onLockChanged;
        _onScaleChanged = onScaleChanged;
        _onVisibleChanged = onVisibleChanged;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            var signaled = false;
            try { Run(x, y, () => { signaled = true; ready.Set(); }); }
            catch (Exception ex)
            {
                ErrorLog.Write("Start overlay", ex);
                Console.WriteLine(L.F("overlay.overlay_unavailable", ex.Message));
                if (!signaled) ready.Set();
            }
        }) { IsBackground = true, Name = "Overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (_hwnd == IntPtr.Zero)
        {
            _thread.Join();
            _thread = null;
        }
    }

    public static void Update(byte[] png, byte[] pixels)
    {
        _png = png;
        _pixels = pixels;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_APP_RENDER, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool Snapshot(string path)
    {
        var png = _png;
        if (png == null) return false;
        File.WriteAllBytes(path, png);
        return true;
    }

    public static void Toggle() { if (IsRunning) PostMessageW(_hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero); }
    public static void ToggleLock() { if (IsRunning) PostMessageW(_hwnd, WM_APP_LOCK, IntPtr.Zero, IntPtr.Zero); }
    public static void SetScale(int scale)
    {
        if (IsRunning) PostMessageW(_hwnd, WM_APP_SCALE, new IntPtr(OverlayScale.Clamp(scale)), IntPtr.Zero);
    }

    public static void Stop()
    {
        var thread = _thread;
        if (thread == null) return;
        if (IsRunning) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        thread.Join();
        _thread = null;
        _hwnd = IntPtr.Zero;
        _visible = false;
        HotkeyRegistered = false;
        LockHotkeyRegistered = false;
    }

    private static void Run(int? x, int? y, Action ready)
    {
        var instance = GetModuleHandleW(null);
        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32646)),
            lpszClassName = "ChestCounterOverlayShared",
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                wc.lpszClassName, "", WS_POPUP | WS_THICKFRAME, x ?? 24, y ?? 24,
                OverlayScale.Width(_scale), OverlayScale.Height(_scale),
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            KeepOnScreen(_hwnd);
            ApplyLock(_locked, false);
            HotkeyRegistered = RegisterHotKey(_hwnd, 1, 0x4006, 0x4F);
            LockHotkeyRegistered = RegisterHotKey(_hwnd, 2, 0x4006, 0x4C);
            if (!HotkeyRegistered || !LockHotkeyRegistered)
                ErrorLog.Write("Overlay shortcuts", new InvalidOperationException("Could not register one or more overlay shortcuts."));
            if (_visible) ShowWindow(_hwnd, 4);
            ready();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        finally
        {
            _hwnd = IntPtr.Zero;
            UnregisterClassW(wc.lpszClassName, instance);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE: return IntPtr.Zero;
            case WM_NCHITTEST:
            {
                if (_locked) return new IntPtr(-1);
                GetWindowRect(hwnd, out var rect);
                var px = (short)((long)lParam & 0xFFFF);
                var py = (short)(((long)lParam >> 16) & 0xFFFF);
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                var resize = px >= rect.Left + width * 0.88 && px <= rect.Left + width * 0.945
                    && py >= rect.Top + height * 0.83;
                return new IntPtr(resize ? 17 : 2);
            }
            case WM_SIZING:
            {
                var sizing = Marshal.PtrToStructure<RECT>(lParam);
                var width = Math.Clamp(sizing.Right - sizing.Left,
                    OverlayScale.Width(OverlayScale.Minimum), OverlayScale.Width(OverlayScale.Maximum));
                sizing.Right = sizing.Left + width;
                sizing.Bottom = sizing.Top + (int)Math.Round(width * 530.0 / 1120);
                Marshal.StructureToPtr(sizing, lParam, false);
                return new IntPtr(1);
            }
            case WM_SIZE:
            case WM_APP_RENDER:
                Render(hwnd);
                return IntPtr.Zero;
            case WM_APP_SCALE:
                _scale = OverlayScale.Clamp((int)(long)wParam);
                if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, OverlayScale.Width(_scale), OverlayScale.Height(_scale), 0x0016))
                {
                    ErrorLog.Write("Resize overlay", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                    return IntPtr.Zero;
                }
                KeepOnScreen(hwnd);
                return IntPtr.Zero;
            case WM_APP_LOCK:
                ApplyLock(!_locked, true);
                return IntPtr.Zero;
            case WM_HOTKEY:
                if ((long)wParam == 2) ApplyLock(!_locked, true);
                else SetVisible(!IsWindowVisible(hwnd));
                return IntPtr.Zero;
            case WM_NCRBUTTONUP:
                SetVisible(false);
                return IntPtr.Zero;
            case WM_APP_TOGGLE:
                SetVisible(!IsWindowVisible(hwnd));
                return IntPtr.Zero;
            case WM_EXITSIZEMOVE:
                if (GetWindowRect(hwnd, out var moved))
                {
                    _onMoved?.Invoke(moved.Left, moved.Top);
                    var percent = OverlayScale.Clamp((int)Math.Round(
                        (moved.Right - moved.Left) * 100.0 / OverlayScale.BaseWidth / 10) * 10);
                    if (percent != _scale) _onScaleChanged?.Invoke(percent);
                    else SetScale(_scale);
                }
                return IntPtr.Zero;
            case WM_DESTROY:
                UnregisterHotKey(hwnd, 1);
                UnregisterHotKey(hwnd, 2);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void ApplyLock(bool locked, bool notify)
    {
        _locked = locked;
        var style = GetWindowLongW(_hwnd, -20);
        SetWindowLongW(_hwnd, -20, locked ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
        if (notify) _onLockChanged?.Invoke(locked);
    }

    private static void SetVisible(bool visible)
    {
        _visible = visible;
        ShowWindow(_hwnd, visible ? 4 : 0);
        _onVisibleChanged?.Invoke(visible);
    }

    private static void KeepOnScreen(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetWindowRect(hwnd, out var rect) || !GetMonitorInfoW(monitor, ref info)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = Math.Max(info.Work.Left, Math.Min(rect.Left, info.Work.Right - width));
        var y = Math.Max(info.Work.Top, Math.Min(rect.Top, info.Work.Bottom - height));
        if (x != rect.Left || y != rect.Top)
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0015);
            _onMoved?.Invoke(x, y);
        }
    }

    private static void Render(IntPtr hwnd)
    {
        if (_rendering) return;
        var pixels = _pixels;
        if (pixels == null || !GetWindowRect(hwnd, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;
        _rendering = true;
        var screen = GetDC(IntPtr.Zero);
        var target = CreateCompatibleDC(screen);
        var targetBmp = CreateBitmapSection(screen, width, height, out var bits);
        if (screen == IntPtr.Zero || target == IntPtr.Zero || targetBmp == IntPtr.Zero)
        {
            if (targetBmp != IntPtr.Zero) DeleteObject(targetBmp);
            if (target != IntPtr.Zero) DeleteDC(target);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            ErrorLog.Write("Render overlay", new InvalidOperationException("Could not allocate a bitmap."));
            _rendering = false;
            return;
        }
        var oldTarget = SelectObject(target, targetBmp);
        try
        {
            var scaled = ScalePixels(pixels, width, height);
            Marshal.Copy(scaled, 0, bits, scaled.Length);
            var destination = new POINT { X = rect.Left, Y = rect.Top };
            var size = new SIZE { Width = width, Height = height };
            var origin = new POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(hwnd, screen, ref destination, ref size, target, ref origin, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { ErrorLog.Write("Render overlay", ex); }
        finally
        {
            SelectObject(target, oldTarget);
            DeleteObject(targetBmp);
            DeleteDC(target);
            ReleaseDC(IntPtr.Zero, screen);
            _rendering = false;
        }
    }

    private static byte[] ScalePixels(byte[] source, int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        var xs = new int[width];
        var xWeights = new int[width];
        for (var x = 0; x < width; x++)
        {
            var coordinate = Math.Clamp((x + 0.5) * 1120 / width - 0.5, 0, 1119);
            xs[x] = (int)coordinate;
            xWeights[x] = (int)((coordinate - xs[x]) * 256);
        }
        for (var y = 0; y < height; y++)
        {
            var coordinate = Math.Clamp((y + 0.5) * 530 / height - 0.5, 0, 529);
            var y0 = (int)coordinate;
            var y1 = Math.Min(y0 + 1, 529);
            var wy = (int)((coordinate - y0) * 256);
            for (var x = 0; x < width; x++)
            {
                var x0 = xs[x];
                var x1 = Math.Min(x0 + 1, 1119);
                var wx = xWeights[x];
                var a = (y0 * 1120 + x0) * 4;
                var b = (y0 * 1120 + x1) * 4;
                var c = (y1 * 1120 + x0) * 4;
                var d = (y1 * 1120 + x1) * 4;
                var target = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var top = source[a + channel] * (256 - wx) + source[b + channel] * wx;
                    var bottom = source[c + channel] * (256 - wx) + source[d + channel] * wx;
                    result[target + channel] = (byte)((top * (256 - wy) + bottom * wy + 32768) >> 16);
                }
            }
        }
        return result;
    }

    private static IntPtr CreateBitmapSection(IntPtr dc, int width, int height, out IntPtr pixels)
    {
        var info = new BITMAPINFO { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        return CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const int WS_EX_TRANSPARENT = 0x20;
    private const uint WS_EX_LAYERED = 0x80000, WS_EX_TOPMOST = 8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000, WS_THICKFRAME = 0x00040000;
    private const uint WM_CLOSE = 0x10, WM_DESTROY = 2, WM_SIZE = 5, WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84;
    private const uint WM_NCRBUTTONUP = 0xA5, WM_SIZING = 0x214, WM_EXITSIZEMOVE = 0x232, WM_HOTKEY = 0x312;
    private const uint WM_APP_TOGGLE = 0x8001, WM_APP_RENDER = 0x8002, WM_APP_LOCK = 0x8003, WM_APP_SCALE = 0x8004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint ClrUsed, ClrImportant, Colors; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint exStyle, string name, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dc, ref POINT destination, ref SIZE size, IntPtr source, ref POINT origin, uint color, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
