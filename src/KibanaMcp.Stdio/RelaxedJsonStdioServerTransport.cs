using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KibanaMcp;

/// <summary>
/// Newline-delimited JSON transport over stdin/stdout, used so external MCP clients receive messages
/// without Content-Length framing. Reused from the reference STDIO implementation.
/// </summary>
public sealed class RelaxedJsonStdioServerTransport : RelaxedJsonStreamServerTransport
{
    public RelaxedJsonStdioServerTransport(string serverName, ILoggerFactory? loggerFactory = null)
        : base(Console.OpenStandardInput(), new BufferedStream(Console.OpenStandardOutput()), serverName, loggerFactory)
    {
    }
}

public class RelaxedJsonStreamServerTransport : StreamServerTransport
{
    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();
    private readonly Stream outputStream;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public RelaxedJsonStreamServerTransport(Stream inputStream, Stream outputStream, string? serverName = null, ILoggerFactory? loggerFactory = null)
        : base(inputStream, outputStream, serverName, loggerFactory) => this.outputStream = outputStream;

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, typeof(JsonRpcMessage), JsonDefaults.McpWireOptions);
            await outputStream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await outputStream.WriteAsync(NewlineBytes, cancellationToken).ConfigureAwait(false);
            await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }
}
