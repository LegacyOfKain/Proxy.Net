using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Proxy.Net;

public class TcpReverseProxy
{
    private readonly int localPort;
    private readonly string remoteServerHost;
    private readonly int remoteServerPort;
    private TcpListener? tcpListener;
    private CancellationTokenSource? cts;
    private Task? acceptLoopTask;
    private readonly ConcurrentBag<Task> clientTasks = new();

    public bool IsRunning => cts != null;

    public TcpReverseProxy(int localPort, string remoteServerHost, int remoteServerPort)
    {
        this.localPort = localPort;
        this.remoteServerHost = remoteServerHost;
        this.remoteServerPort = remoteServerPort;
    }

    public void Start()
    {
        if (cts != null)
            throw new InvalidOperationException("Already started.");

        try
        {
            tcpListener = new TcpListener(IPAddress.Any, localPort);
            tcpListener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start listener on port {localPort}: {ex.Message}", ex);
        }

        cts = new CancellationTokenSource();
        Console.WriteLine($"TCP Proxy listening on 0.0.0.0:{localPort} -> {remoteServerHost}:{remoteServerPort} {DateTime.Now}");

        acceptLoopTask = AcceptLoopAsync(cts.Token);
    }

    public async Task StopAsync()
    {
        if (cts == null)
            return;

        Console.WriteLine($"Stopping proxy...{DateTime.Now}");
        var localCts = cts;
        cts = null;

        try
        {
            localCts.Cancel();
            tcpListener?.Stop(); // Forces AcceptTcpClientAsync to exit
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message + "\n" + ex.StackTrace);
        }

        // Wait for accept loop to finish
        var loop = acceptLoopTask;
        if (loop != null)
            await Suppress(loop);

        // Wait for all active client proxy tasks to finish
        foreach (var t in clientTasks)
            await Suppress(t);

        acceptLoopTask = null;

        localCts.Dispose();
        await Task.Yield();
        Console.WriteLine($"Stopped.  {DateTime.Now}");
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client1;
                try
                {
                    client1 = await tcpListener!.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var remoteAddress = ((IPEndPoint)client1.Client.RemoteEndPoint!).Address.ToString();
                Console.WriteLine("Accepted session from " + remoteAddress);

                var handlerTask = HandleClientAsync(client1, remoteAddress, token);
                clientTasks.Add(handlerTask);
            }
        }
        finally
        {
            try { tcpListener?.Stop(); } catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client1, string remoteAddress, CancellationToken globalToken)
    {
        TcpClient? client2 = null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        var token = linkedCts.Token;

        try
        {
            client2 = new TcpClient();
            await client2.ConnectAsync(remoteServerHost, remoteServerPort, token);
            Console.WriteLine("Created connection to " + remoteServerHost + ":" + remoteServerPort);

            var stream1 = client1.GetStream();
            var stream2 = client2.GetStream();

            var task1 = ProxyStreamAsync(remoteAddress, stream1, remoteServerHost, stream2, token);
            var task2 = ProxyStreamAsync(remoteServerHost, stream2, remoteAddress, stream1, token);

            var finished = await Task.WhenAny(task1, task2);
            linkedCts.Cancel();
            await Task.WhenAll(Suppress(task1), Suppress(task2));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling client {remoteAddress}: {ex.Message}");
        }
        finally
        {
            try
            {
                try { client1.Client.Shutdown(SocketShutdown.Both); } catch { }
                client1.Close();
            }
            catch { }
            try
            {
                if (client2 != null)
                {
                    try { client2.Client.Shutdown(SocketShutdown.Both); } catch { }
                    client2.Close();
                }
            }
            catch { }
        }
    }

    private async Task ProxyStreamAsync(string stream1name, NetworkStream stream1, string stream2name, NetworkStream stream2, CancellationToken token)
    {
        var buffer = new byte[65536];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var len = await stream1.ReadAsync(buffer, 0, buffer.Length, token);
                if (len == 0)
                    break;

                await stream2.WriteAsync(buffer, 0, len, token);
                await stream2.FlushAsync(token);
                Console.WriteLine($"Proxied {len} bytes from {stream1name} to {stream2name}");
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception)
        {
            Console.WriteLine("Stream from " + stream1name + " to " + stream2name + " closed");
        }
    }

    private static async Task Suppress(Task t)
    {
        try { await t; } catch { }
    }

    // Optional: quick check you can bind after stop (returns true if port is free)
    public bool CanRebind()
    {
        try
        {
            using var l = new TcpListener(IPAddress.Loopback, localPort);
            l.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }
}