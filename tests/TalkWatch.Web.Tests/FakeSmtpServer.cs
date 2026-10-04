using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Just enough SMTP to receive a message from SmtpClient on the loopback: no TLS, no authentication. Keeps each
/// message's envelope recipients and raw data.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    public sealed record Message(IReadOnlyList<string> To, string Data);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<Message> Messages { get; } = [];

    /// <summary>Each EHLO or HELO line it was greeted with.</summary>
    public List<string> Greetings { get; } = [];

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using (client)
            {
                await ServeAsync(client.GetStream());
            }
        }
    }

    private async Task ServeAsync(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake ESMTP");
        var to = new List<string>();
        while (await reader.ReadLineAsync(_stop.Token) is { } line)
        {
            var verb = line.Split(' ', ':')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO" or "HELO":
                    Greetings.Add(line);
                    await writer.WriteLineAsync("250 fake");
                    break;
                case "RCPT":
                    to.Add(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Trim('<', '>'));
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } body && body != ".")
                    {
                        data.AppendLine(body);
                    }

                    Messages.Add(new Message([.. to], data.ToString()));
                    to.Clear();
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("250 OK");
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
