using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Proxy.Net;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Proxy.Net.Tests;

public class TcpReverseProxyTests : IAsyncLifetime
{
    private readonly List<CancellationTokenSource> cts = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var c in cts)
        {
            try { c.Cancel(); } catch { }
            c.Dispose();
        }
        await Task.Delay(10);
    }

    [Fact]
    public async Task Forwards_Data_From_Client_To_Remote_Server()
    {
        // Arrange
        var payload = Encoding.UTF8.GetBytes("TestPayload123");
        var receivedTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (remotePort, serverCts) = StartTestServer(async stream =>
        {
            var ms = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                ms.Write(buffer, 0, read);
                if (ms.Length >= payload.Length)
                {
                    break;
                }
            }
            receivedTcs.TrySetResult(ms.ToArray());
        });
        cts.Add(serverCts);

        var localPort = GetFreePort();
        var proxy = new TcpReverseProxy(localPort, "127.0.0.1", remotePort);
        proxy.Start();
        await Task.Delay(50); // let listener start

        // Act
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, localPort);
            await client.GetStream().WriteAsync(payload, 0, payload.Length);
            client.Close();
        }

        var completed = await WaitOrTimeout(receivedTcs.Task, TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(completed, "Timed out waiting for server to receive data.");
        Assert.Equal(payload, await receivedTcs.Task);

        await proxy.StopAsync();
    }

    [Fact]
    public async Task Forwards_Data_From_Remote_Server_To_Client()
    {
        // Arrange
        var message = "HelloClient!";
        var (remotePort, serverCts) = StartTestServer(async stream =>
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
            await Task.Delay(50);
        });
        cts.Add(serverCts);

        var localPort = GetFreePort();
        var proxy = new TcpReverseProxy(localPort, "127.0.0.1", remotePort);
        proxy.Start();
        await Task.Delay(50);

        // Act
        string received;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, localPort);
            var buffer = new byte[1024];
            var n = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
            received = Encoding.UTF8.GetString(buffer, 0, n);
        }

        // Assert
        Assert.Equal(message, received);

        await proxy.StopAsync();
    }

    [Fact]
    public async Task Bidirectional_Communication_Works()
    {
        // Arrange
        var clientToServer = "Ping->Server";
        var serverToClient = "Pong->Client";
        var receivedFromClientTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (remotePort, serverCts) = StartTestServer(async stream =>
        {
            // Read client message
            var buf = new byte[4096];
            var read = await stream.ReadAsync(buf, 0, buf.Length);
            receivedFromClientTcs.TrySetResult(Encoding.UTF8.GetString(buf, 0, read));

            // Respond
            var resp = Encoding.UTF8.GetBytes(serverToClient);
            await stream.WriteAsync(resp, 0, resp.Length);
            await stream.FlushAsync();
        });
        cts.Add(serverCts);

        var localPort = GetFreePort();
        var proxy = new TcpReverseProxy(localPort, "127.0.0.1", remotePort);
        proxy.Start();
        await Task.Delay(50);

        // Act
        string receivedFromServer;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, localPort);
            var outBytes = Encoding.UTF8.GetBytes(clientToServer);
            await client.GetStream().WriteAsync(outBytes, 0, outBytes.Length);

            var buffer = new byte[4096];
            var read = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
            receivedFromServer = Encoding.UTF8.GetString(buffer, 0, read);
        }

        var ok = await WaitOrTimeout(receivedFromClientTcs.Task, TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(ok, "Server did not receive client payload in time.");
        Assert.Equal(clientToServer, await receivedFromClientTcs.Task);
        Assert.Equal(serverToClient, receivedFromServer);

        await proxy.StopAsync();
    }

    [Fact]
    public async Task Start_Twice_Throws()
    {
        var localPort = GetFreePort();
        var proxy = new TcpReverseProxy(localPort, "127.0.0.1", 65000);
        proxy.Start();
        await Task.Delay(20);

        Assert.Throws<InvalidOperationException>(() => proxy.Start());

        await proxy.StopAsync();
    }

    [Fact]
    public async Task StopAsync_Closes_Listener()
    {
        var (remotePort, serverCts) = StartTestServer(async _ => await Task.Delay(500));
        cts.Add(serverCts);

        var localPort = GetFreePort();
        var proxy = new TcpReverseProxy(localPort, "127.0.0.1", remotePort);
        proxy.Start();
        await Task.Delay(30);

        await proxy.StopAsync();
        await Task.Delay(30);

        bool connectSucceeded;
        try
        {
            using var c = new TcpClient();
            var connectTask = c.ConnectAsync(IPAddress.Loopback, localPort);
            var first = await Task.WhenAny(connectTask, Task.Delay(250));
            connectSucceeded = first == connectTask && connectTask.IsCompletedSuccessfully;
        }
        catch
        {
            connectSucceeded = false;
        }

        Assert.False(connectSucceeded, "Connection unexpectedly succeeded after StopAsync.");
    }

    // Helpers

    private static (int port, CancellationTokenSource cts) StartTestServer(Func<NetworkStream, Task> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                await handler(stream);
            }
            catch (OperationCanceledException) { }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }, cts.Token);

        return (port, cts);
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task<bool> WaitOrTimeout(Task task, TimeSpan timeout)
        => await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private static async Task<bool> WaitOrTimeout<TResult>(Task<TResult> task, TimeSpan timeout)
        => await Task.WhenAny(task, Task.Delay(timeout)) == task;
}