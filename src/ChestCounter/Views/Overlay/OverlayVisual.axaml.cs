using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace ChestCounter;

public partial class OverlayVisual : Canvas, IDisposable
{
    public const int PixelWidth = 1120;
    public const int PixelHeight = 530;
    private readonly TextBlock _chestCaption;
    private readonly TextBlock _chestValue;
    private readonly TextBlock _treasureCaption;
    private readonly TextBlock _treasureValue;
    private readonly List<Bitmap> _images = new();

    public OverlayVisual()
    {
        InitializeComponent();
        _chestCaption = this.FindControl<TextBlock>("ChestCaption")
            ?? throw new InvalidOperationException("Overlay chest caption is missing.");
        _chestValue = this.FindControl<TextBlock>("ChestValue")
            ?? throw new InvalidOperationException("Overlay chest value is missing.");
        _treasureCaption = this.FindControl<TextBlock>("TreasureCaption")
            ?? throw new InvalidOperationException("Overlay treasure caption is missing.");
        _treasureValue = this.FindControl<TextBlock>("TreasureValue")
            ?? throw new InvalidOperationException("Overlay treasure value is missing.");
        LoadImage("ChestImage", "chest-dashboard.png");
        LoadImage("LogoImage", "logo.png");
        LoadImage("FireworksImage", "fireworks.png");
        LoadImage("ExtractorImage", "extractor.png");
        LoadImage("LocatorImage", "lucky_Locator.png");
    }

    private void LoadImage(string controlName, string fileName)
    {
        var image = this.FindControl<Image>(controlName)
            ?? throw new InvalidOperationException($"Overlay image control '{controlName}' is missing.");
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://ContadorDeBau/Assets/{fileName}"));
            var bitmap = new Bitmap(stream);
            _images.Add(bitmap);
            image.Source = bitmap;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Load overlay image {fileName}", ex);
        }
    }

    public void Dispose()
    {
        foreach (var image in _images) image.Dispose();
        _images.Clear();
    }

    public void SetState(OverlayState state)
    {
        _chestCaption.Text = L.T("overlay.chests_opened").ToUpper(L.Culture);
        _chestValue.Text = state.Total;
        _treasureCaption.Text = L.T("main.treasures").ToUpper(L.Culture);
        _treasureValue.Text = state.Rows.FirstOrDefault(row => row.Treasure)?.Value ?? "—";
    }

    public (byte[] Png, byte[] Pixels) RenderState(OverlayState state)
    {
        SetState(state);

        // The Canvas is rendered off-screen, without an attached ContentPresenter.
        Measure(new Size(PixelWidth, PixelHeight));
        Arrange(new Rect(0, 0, PixelWidth, PixelHeight));
        using var rendered = new RenderTargetBitmap(new PixelSize(PixelWidth, PixelHeight));
        rendered.Render(this);
        using var stream = new MemoryStream();
        rendered.Save(stream);
        var png = stream.ToArray();
        stream.Position = 0;
        using var decoded = WriteableBitmap.DecodeToWidth(stream, PixelWidth, BitmapInterpolationMode.HighQuality);
        using var framebuffer = decoded.Lock();
        var bgra = framebuffer.Format == PixelFormats.Bgra8888;
        if (!bgra && framebuffer.Format != PixelFormats.Rgba8888)
            throw new NotSupportedException($"Unsupported overlay pixel format: {framebuffer.Format}");
        var pixels = new byte[PixelWidth * PixelHeight * 4];
        for (var y = 0; y < PixelHeight; y++)
        {
            Marshal.Copy(IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                pixels, y * PixelWidth * 4, PixelWidth * 4);
        }
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (!bgra) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            if (framebuffer.AlphaFormat == AlphaFormat.Opaque) pixels[i + 3] = 255;
            if (framebuffer.AlphaFormat == AlphaFormat.Unpremul)
            {
                var alpha = pixels[i + 3];
                pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
            }
        }
        return (png, pixels);
    }
}
