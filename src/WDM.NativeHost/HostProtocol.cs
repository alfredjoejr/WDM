using System.Text;
using System.Text.Json;

namespace WDM.NativeHost;

/// <summary>
/// Implements the Chrome/Edge/Firefox Native Messaging protocol framing:
/// 32-bit unsigned little-endian length prefix followed by UTF-8 JSON payload over standard IO.
/// </summary>
public static class HostProtocol
{
    private static readonly Stream InputStream = Console.OpenStandardInput();
    private static readonly Stream OutputStream = Console.OpenStandardOutput();
    private static readonly object WriteLock = new();

    public static async Task<JsonElement?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        byte[] lengthBuffer = new byte[4];
        int totalRead = 0;

        while (totalRead < 4)
        {
            int read = await InputStream.ReadAsync(lengthBuffer.AsMemory(totalRead, 4 - totalRead), cancellationToken);
            if (read == 0) return null; // Pipe closed by browser
            totalRead += read;
        }

        int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
        if (messageLength <= 0 || messageLength > 10 * 1024 * 1024) // Sanity check max 10MB
        {
            return null;
        }

        byte[] messageBuffer = new byte[messageLength];
        totalRead = 0;

        while (totalRead < messageLength)
        {
            int read = await InputStream.ReadAsync(messageBuffer.AsMemory(totalRead, messageLength - totalRead), cancellationToken);
            if (read == 0) return null;
            totalRead += read;
        }

        var json = Encoding.UTF8.GetString(messageBuffer);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static async Task SendMessageAsync<T>(T payload, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        byte[] messageBytes = Encoding.UTF8.GetBytes(json);
        byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

        // Lock to ensure atomic framing writes
        lock (WriteLock)
        {
            OutputStream.Write(lengthBytes, 0, 4);
            OutputStream.Write(messageBytes, 0, messageBytes.Length);
            OutputStream.Flush();
        }

        await Task.CompletedTask;
    }
}
