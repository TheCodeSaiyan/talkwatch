using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TalkWatch.Capture;

/// <summary>
/// Reads one webhook request from a connection, so <c>listen</c> can seal what Talk sends without the plaintext
/// touching the disk. Deliberately minimal HTTP/1.1: a request line, headers, and a body by Content-Length or
/// chunked. Each request becomes two entries: "hook", the body, and "hook-headers", its headers as a JSON object,
/// which is where a signature would show.
/// </summary>
public static class WebhookRecorder
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    public static readonly byte[] Reply = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    /// <summary>The request's entries, or null when the connection closed before a whole request arrived.</summary>
    public static async Task<IReadOnlyList<CaptureEntry>?> ReadAsync(Stream stream, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var reader = new BufferedReader(stream);
        var head = await reader.ReadUntilBlankLineAsync(cancellationToken);
        if (head is null)
        {
            return null;
        }

        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            throw new InvalidDataException("Not an HTTP request.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var name = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                headers[name] = headers.TryGetValue(name, out var earlier) ? $"{earlier}, {value}" : value;
            }
        }

        byte[] body;
        if (headers.TryGetValue("transfer-encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await reader.ReadChunkedAsync(MaxBodyBytes, cancellationToken);
        }
        else if (headers.TryGetValue("content-length", out var length) && int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            body = count <= MaxBodyBytes
                ? await reader.ReadExactlyAsync(count, cancellationToken)
                : throw new InvalidDataException($"A body of {count} bytes is more than a webhook should send.");
        }
        else
        {
            body = [];
        }

        var method = requestLine[0];
        var path = requestLine[1];
        return
        [
            new CaptureEntry("hook", method, path, 200, headers.GetValueOrDefault("content-type"), at, body),
            new CaptureEntry("hook-headers", method, path, 200, "application/json", at, JsonSerializer.SerializeToUtf8Bytes(headers)),
        ];
    }

    private sealed class BufferedReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public async Task<string?> ReadUntilBlankLineAsync(CancellationToken cancellationToken)
        {
            var head = new MemoryStream();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken))
                {
                    return null;
                }

                head.WriteByte(_buffer[_start++]);
                if (head.Length > MaxHeaderBytes)
                {
                    throw new InvalidDataException("The request's headers are too long.");
                }

                if (head.Length >= 4 && EndsWithBlankLine(head))
                {
                    return Encoding.Latin1.GetString(head.GetBuffer(), 0, (int)head.Length - 4);
                }
            }
        }

        public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
        {
            var result = new byte[count];
            var read = 0;
            while (read < count)
            {
                if (_start == _end && !await FillAsync(cancellationToken))
                {
                    throw new InvalidDataException("The connection closed part way through the body.");
                }

                var take = Math.Min(count - read, _end - _start);
                Array.Copy(_buffer, _start, result, read, take);
                _start += take;
                read += take;
            }

            return result;
        }

        public async Task<byte[]> ReadChunkedAsync(int limit, CancellationToken cancellationToken)
        {
            var body = new MemoryStream();
            while (true)
            {
                var sizeLine = (await ReadLineAsync(cancellationToken)).Split(';')[0].Trim();
                if (!int.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                {
                    throw new InvalidDataException("A chunk's size could not be read.");
                }

                if (size == 0)
                {
                    // Trailers, if any, end at a blank line.
                    while ((await ReadLineAsync(cancellationToken)).Length > 0)
                    {
                    }

                    return body.ToArray();
                }

                if (body.Length + size > limit)
                {
                    throw new InvalidDataException("The body is more than a webhook should send.");
                }

                body.Write(await ReadExactlyAsync(size, cancellationToken));
                await ReadLineAsync(cancellationToken);
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken))
                {
                    throw new InvalidDataException("The connection closed part way through the body.");
                }

                var b = _buffer[_start++];
                if (b == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append((char)b);
            }
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, cancellationToken);
            return _end > 0;
        }

        private static bool EndsWithBlankLine(MemoryStream head)
        {
            var b = head.GetBuffer();
            var n = (int)head.Length;
            return b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n';
        }
    }
}
