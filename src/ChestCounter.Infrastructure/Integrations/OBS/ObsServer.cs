using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ChestCounter;

/// <summary>
/// Tiny local web server (127.0.0.1 only) while the tool is open: OBS adds <see cref="Url"/> as a browser source.
/// "/" is the overlay page, "/view" its revision, and "/image" its transparent PNG.
/// Only requests addressed to localhost / 127.0.0.1 are answered (protection against DNS rebinding).
/// </summary>
public static class ObsServer
{
    public const int DefaultPort = Config.DefaultObsPort;

    private static TcpListener? _listener;
    private static byte[]? _image;
    private static long _revision;
    private static int _scale = OverlayScale.Default;

    /// <summary>Address to paste in OBS, or null when the server could not start.</summary>
    public static string? Url { get; private set; }

    /// <summary>Starts on the first free port from <paramref name="port"/> (a few tries). Safe to call twice.</summary>
    public static void Start(int port)
    {
        if (_listener != null)
        {
            return;
        }
        for (var p = port; p < port + 10; p++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, p);
                listener.Start();
                _listener = listener;
                Url = $"http://localhost:{p}/";
                new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "OBS" }.Start();
                return;
            }
            catch (SocketException)
            {
                // port in use: try the next one
            }
        }
        ErrorLog.Write($"OBS: no free port from {port}", new InvalidOperationException("no free port"));
    }

    public static void Stop()
    {
        try { _listener?.Stop(); } catch { /* closing */ }
        _listener = null;
        Url = null;
        Volatile.Write(ref _image, null);
        Interlocked.Exchange(ref _revision, 0);
    }

    /// <summary>Publishes the same rendered image used by the native overlay.</summary>
    public static void UpdateImage(byte[] image)
    {
        Volatile.Write(ref _image, image);
        Interlocked.Increment(ref _revision);
    }

    public static void UpdateScale(int scale) => Volatile.Write(ref _scale, OverlayScale.Clamp(scale));

    /// <summary>True when the Host header names this machine: "localhost:port" or "127.0.0.1:port".</summary>
    private static bool IsLocalHost(string host, int port) =>
        host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase) ||
        host.Equals($"127.0.0.1:{port}", StringComparison.Ordinal);

    private static void AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch
            {
                return; // stopped
            }
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private static void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }
                var lines = Encoding.ASCII.GetString(buffer, 0, read).Split('\n');
                var parts = lines[0].Split(' ');
                var path = parts.Length > 1 ? parts[1].Split('?')[0] : "/";
                var host = lines.Skip(1)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim() ?? "";

                byte[] bytes;
                var contentType = "text/html; charset=utf-8";
                var status = "200 OK";
                var extraHeaders = "";
                if (!IsLocalHost(host, ((IPEndPoint)client.Client.LocalEndPoint!).Port))
                {
                    // Another site pointing its own name at 127.0.0.1 (DNS rebinding): never answer it.
                    status = "403 Forbidden";
                    bytes = Encoding.UTF8.GetBytes(L.T("http.forbidden"));
                }
                else
                {
                    switch (path)
                    {
                        case "/":
                        case "/overlay":
                            bytes = Encoding.UTF8.GetBytes(ReportWriter.OverlayLivePage());
                            break;
                        case "/view":
                            contentType = "text/plain; charset=utf-8";
                            bytes = Encoding.UTF8.GetBytes(Interlocked.Read(ref _revision).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            extraHeaders = $"X-Overlay-Scale: {Volatile.Read(ref _scale).ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n";
                            break;
                        case "/image":
                            contentType = "image/png";
                            var image = Volatile.Read(ref _image);
                            if (image == null)
                            {
                                status = "503 Service Unavailable";
                                contentType = "text/plain; charset=utf-8";
                                bytes = Encoding.UTF8.GetBytes(L.T("error.overlay_unavailable"));
                            }
                            else
                            {
                                bytes = image;
                            }
                            break;
                        default:
                            status = "404 Not Found";
                            bytes = Encoding.UTF8.GetBytes(L.T("http.not_found"));
                            break;
                    }
                }

                var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n" +
                             extraHeaders + "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(header));
                stream.Write(bytes);
            }
            catch (IOException)
            {
                // OBS closed the connection: nothing to do
            }
            catch (SocketException)
            {
                // OBS closed the connection.
            }
            catch (ObjectDisposedException)
            {
                // The connection was closed.
            }
            catch (Exception ex)
            {
                ErrorLog.Write("OBS request", ex);
            }
        }
    }
}
