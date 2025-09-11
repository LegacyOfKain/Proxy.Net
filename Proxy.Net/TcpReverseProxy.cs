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

    public TcpReverseProxy(int localPort, string remoteServerHost, int remoteServerPort)
    {
        this.localPort = localPort;
        this.remoteServerHost = remoteServerHost;
        this.remoteServerPort = remoteServerPort;       
    }

    public void Start()
    {
        try 
        {
            tcpListener = new TcpListener(IPAddress.Any, localPort);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start listener on port {localPort}: {ex.Message}", ex);
        }

        if (cts != null)
        {
            throw new InvalidOperationException("Already started.");
        }
        cts = new CancellationTokenSource();
        tcpListener.Start();
        Console.WriteLine($"TCP Proxy listening on 0.0.0.0:{localPort} -> {remoteServerHost}:{remoteServerPort} {DateTime.Now}");

        _ = this.AcceptLoopAsync(cts.Token) ;
    }

    public async Task StopAsync()
    {
        if (cts == null)
        {
            return;
        }
        Console.WriteLine($"Stopping proxy...{DateTime.Now}");
        try
        {
            cts.Cancel(); 
            tcpListener?.Stop();
        }
        catch (Exception ex)  
        {
            Console.WriteLine(ex.Message+"\n"+ex.StackTrace);
        }

        finally
        {
            cts.Dispose();
            cts = null;
        }
        await Task.Yield();
        Console.WriteLine($"Stopped.  {DateTime.Now}");
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(10, token); // Prevent busy loop if no connections are incoming
                TcpClient client1;
                try
                {
                    Console.WriteLine("Waiting for incoming connection...");
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

                _ = this.HandleClientAsync(client1, remoteAddress, token);
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

            var task1 = this.ProxyStreamAsync(remoteAddress, stream1, remoteServerHost, stream2, token);
            var task2 = this.ProxyStreamAsync(remoteServerHost, stream2, remoteAddress, stream1, token);

            // When one direction finishes, cancel the other
            var finished = await Task.WhenAny(task1, task2);
            linkedCts.Cancel(); // Cancel the other direction immediately
            await Task.WhenAll(Suppress(task1), Suppress(task2));
        }
        catch (OperationCanceledException)
        {
            // Normal during stop
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling client {remoteAddress}: {ex.Message}");
        }
        finally
        {
            try { client1.Close(); } catch { }
            try { client2?.Close(); } catch { }
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
                {  
                    break;
                }
                await stream2.WriteAsync(buffer, 0, len, token);
                await stream2.FlushAsync(token);
                Console.WriteLine($"Proxied {len} bytes from {stream1name} to {stream2name}");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during cancellation
        }
        catch (IOException)
        {
            // Connection closed
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed
        }
        catch (Exception)
        {
            Console.WriteLine("Stream from " + stream1name + " to " + stream2name + " closed");
        }
    }

    private static async Task Suppress(Task t)
    {
        try { await t; } catch { /* swallow */ }
    }
}
