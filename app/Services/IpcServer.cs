using System.IO.Pipes;
using System.Text.Json;

namespace ControllerManager.Services;

/// <summary>
/// Runs in the main instance. Listens for requests from shortcut/Steam wrapper
/// invocations and dispatches them to the orchestrator.
/// </summary>
public sealed class IpcServer : IDisposable
{
    public const string PipeName = "ControllerManager";

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;

    public event EventHandler<IpcRequest>? RequestReceived;

    public IpcServer()
    {
        _listenTask = Task.Run(ListenLoop);
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token);

                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) continue;

                IpcRequest? req = null;
                try { req = JsonSerializer.Deserialize<IpcRequest>(line); } catch { }
                if (req is null) continue;

                RequestReceived?.Invoke(this, req);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse { Status = "ok" }));
            }
            catch (OperationCanceledException) { break; }
            catch { /* client disconnected or pipe error — restart loop */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listenTask.Wait(2000); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// Sends a single request to the running instance and returns the response.
/// </summary>
public static class IpcClient
{
    public static async Task<IpcResponse?> SendAsync(IpcRequest request, int timeoutMs = 5000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", IpcServer.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutMs);

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
            var line = await reader.ReadLineAsync();
            if (line is null) return null;
            return JsonSerializer.Deserialize<IpcResponse>(line);
        }
        catch { return null; }
    }
}

public sealed class IpcRequest
{
    public string Op        { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string[] Args    { get; set; } = [];
}

public sealed class IpcResponse
{
    public string Status  { get; set; } = "";
    public string Message { get; set; } = "";
}
