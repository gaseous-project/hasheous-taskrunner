using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace hasheous_taskrunner.Tests;

internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;
    private readonly Func<HttpListenerRequest, (int statusCode, string body)> _handler;
    private readonly ConcurrentBag<string> _paths = new();
    private int _requestCount;

    public string BaseUrl { get; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public IReadOnlyCollection<string> Paths => _paths.ToArray();

    public TestHttpServer(Func<HttpListenerRequest, (int statusCode, string body)> handler)
    {
        _handler = handler;
        int port = GetOpenPort();
        BaseUrl = $"http://127.0.0.1:{port}";

        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();

        _serverTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context == null)
            {
                continue;
            }

            Interlocked.Increment(ref _requestCount);
            _paths.Add(context.Request.RawUrl ?? "");

            var responseData = _handler(context.Request);
            context.Response.StatusCode = responseData.statusCode;
            context.Response.ContentType = "application/json";

            byte[] payload = Encoding.UTF8.GetBytes(responseData.body);
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length, _cts.Token);
            context.Response.Close();
        }
    }

    public async Task<bool> WaitForRequestsAsync(int count, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (RequestCount >= count)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return RequestCount >= count;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Best effort cleanup.
        }

        try
        {
            _serverTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown races.
        }

        _cts.Dispose();
    }

    private static int GetOpenPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
