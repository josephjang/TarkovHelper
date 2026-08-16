using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TarkovHelper.Tests;

/// <summary>
/// A minimal loopback HTTP/1.1 file server for tests that need a real endpoint rather
/// than a mocked HttpClient: it lets DatabaseUpdateService run its actual fetch,
/// compare, download, and swap path against a directory tree laid out like the
/// repository.
///
/// Deliberately a raw TcpListener and not HttpListener: HttpListener goes through
/// HTTP.sys, which needs elevation or a netsh URL reservation, and these tests must run
/// in the ordinary non-elevated suite. Only what the client under test uses is
/// implemented: GET, Content-Length, 404, and no keep-alive.
/// </summary>
internal sealed class LocalFileServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _rootPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requestedPaths = new();
    private readonly object _requestLock = new();

    public LocalFileServer(string rootPath)
    {
        _rootPath = rootPath;
        _listener = new TcpListener(IPAddress.Loopback, 0); // port 0: the OS picks a free one
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// Paths requested so far, in order. Lets a test prove a negative: that a check
    /// which found nothing new never reached for the database.
    /// </summary>
    public IReadOnlyList<string> RequestedPaths
    {
        get { lock (_requestLock) { return _requestedPaths.ToArray(); } }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return; // disposed mid-accept: the expected way this loop ends
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();

                var requestLine = await ReadLineAsync(stream);
                if (string.IsNullOrEmpty(requestLine)) return;

                // Drain the headers; the body is irrelevant because only GET is served.
                while (!string.IsNullOrEmpty(await ReadLineAsync(stream)))
                {
                }

                var parts = requestLine.Split(' ');
                var target = parts.Length > 1 ? parts[1] : "/";
                lock (_requestLock) { _requestedPaths.Add(target); }

                var filePath = ResolveFilePath(target);
                if (filePath == null || !File.Exists(filePath))
                {
                    await WriteResponseAsync(stream, "404 Not Found", Array.Empty<byte>());
                    return;
                }

                await WriteResponseAsync(stream, "200 OK", await File.ReadAllBytesAsync(filePath));
            }
        }
        catch (IOException)
        {
            // A client that hangs up mid-response is not a test failure.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Maps a request target onto the served tree, refusing anything that would escape
    /// it. Traversal cannot matter for a loopback test server, but a file server that
    /// silently serves outside its root is the kind of thing that gets copied.
    /// </summary>
    private string? ResolveFilePath(string target)
    {
        var path = target.Split('?')[0].TrimStart('/');
        if (path.Length == 0) return null;

        var decoded = Uri.UnescapeDataString(path).Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_rootPath, decoded));
        var rootFull = Path.GetFullPath(_rootPath) + Path.DirectorySeparatorChar;

        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1));
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            if (buffer[0] == (byte)'\n') return builder.ToString().TrimEnd('\r');
            builder.Append((char)buffer[0]);
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(header);
        if (body.Length > 0) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
