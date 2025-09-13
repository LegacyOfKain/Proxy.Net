# Proxy.Net

Simple .NET 8 TCP reverse proxy library.

## Features

- Asynchronous, high-performance TCP proxying
- Bidirectional communication
- Easy to use API
- Graceful shutdown and resource cleanup

## Installation

Install via NuGet:

```
dotnet add package Proxy.Net
```

## Usage Examples

### Basic Example

```
using Proxy.Net; 
using System; 
using System.Threading.Tasks;

class Program 
{ 
    static async Task Main(string[] args) 
    { 
        int localPort = 8888; 
        string remoteHost = "example.com"; 
        int remotePort = 80;

        // Parse command-line arguments if provided
        if (args.Length >= 3)
        {
            localPort = int.Parse(args[0]);
            remoteHost = args[1];
            remotePort = int.Parse(args[2]);
        }
    
        Console.WriteLine($"Starting proxy: localhost:{localPort} → {remoteHost}:{remotePort}");
    
        var proxy = new TcpReverseProxy(localPort, remoteHost, remotePort);
    
        // Handle Ctrl+C to gracefully shut down
        Console.CancelKeyPress += async (s, e) => 
        {
            e.Cancel = true;
            Console.WriteLine("Shutting down proxy...");
            await proxy.StopAsync();
            Environment.Exit(0);
        };
    
        try
        {
            proxy.Start();
            Console.WriteLine("Proxy started successfully. Press Ctrl+C to exit.");
        
            // Keep the application running
            await Task.Delay(-1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
```

## Common Use Cases

- Forward local development traffic to production or staging environments
- Create TCP tunnels through firewalls
- Load balance TCP connections
- Monitor network traffic between services
- Testing network applications
- Avoiding cross-origin resource sharing (CORS) issues in web development

## License

MIT

## Contributing

Contributions are welcome! Feel free to submit issues and pull requests.