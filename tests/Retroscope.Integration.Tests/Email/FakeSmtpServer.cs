using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Retroscope.Integration.Tests.Email;

public enum FakeSmtpMode {
    StartTls,
    ImplicitTls,
    Plain,
}

/// <summary>A mail the fake accepted. <see cref="Tls"/> and <see cref="AuthUser"/> describe the session it came over.</summary>
public sealed record ReceivedMail(string MailFrom, IReadOnlyList<string> Recipients, string Data, bool Tls, string? AuthUser) {
    public string Header(string name) =>
        HeaderBlock.Split("\r\n").Single(x => x.StartsWith(name + ": ", StringComparison.OrdinalIgnoreCase))[(name.Length + 2)..];

    public string HeaderBlock => Data[..Data.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

    public string Body => Data[(Data.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
}

/// <summary>
/// An in-process SMTP server on loopback with a throwaway self-signed certificate: EHLO, STARTTLS or implicit TLS,
/// AUTH PLAIN/LOGIN, MAIL, RCPT, DATA, QUIT. It records every command line and refuses AUTH before TLS. BCL only.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable {
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _connections;

    public FakeSmtpServer(FakeSmtpMode mode = FakeSmtpMode.StartTls) {
        Mode = mode;
        Certificate = CreateCertificate();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public FakeSmtpMode Mode { get; }

    public X509Certificate2 Certificate { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool AdvertiseStartTls { get; set; } = true;

    public bool AdvertiseSmtpUtf8 { get; set; }

    /// <summary>"PLAIN", "LOGIN" or both; empty advertises no AUTH.</summary>
    public string AuthMechanisms { get; set; } = "PLAIN LOGIN";

    public (string User, string Password)? Credentials { get; set; } = ("mailer", "s3cret");

    /// <summary>A reply code for every RCPT (for example 451 or 550) instead of 250.</summary>
    public int? RcptReply { get; set; }

    /// <summary>Appends a forged reply to the STARTTLS answer in the same plaintext write.</summary>
    public bool InjectAfterStartTls { get; set; }

    public ConcurrentQueue<ReceivedMail> Received { get; } = new();

    /// <summary>Every command line, prefixed with "tls " when it arrived over TLS.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Accepts exactly this server's certificate, wherever the client connects.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString() == Certificate.GetCertHashString();

    public async Task<ReceivedMail> WaitForMailAsync(TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            if (Received.TryPeek(out var mail)) {
                return mail;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("No mail arrived.");
    }

    public async ValueTask DisposeAsync() {
        await _stop.CancelAsync();
        _listener.Stop();
        try {
            await _accepting;
        } catch (OperationCanceledException) {
        }
        _stop.Dispose();
        Certificate.Dispose();
    }

    private async Task AcceptAsync() {
        while (!_stop.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) {
                return;
            }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(async () => {
                using (client) {
                    try {
                        await ServeAsync(client.GetStream());
                    } catch (Exception e) when (e is IOException or AuthenticationException or FormatException or OperationCanceledException or ObjectDisposedException) {
                    }
                }
            });
        }
    }

    private async Task ServeAsync(Stream network) {
        var token = _stop.Token;
        var session = new Session(network);
        if (Mode == FakeSmtpMode.ImplicitTls) {
            session = new Session(await ServerTlsAsync(network, token)) { Tls = true };
        }
        await session.WriteAsync("220 fake.test ESMTP\r\n", token);
        while (await session.ReadLineAsync(token) is { } line) {
            Commands.Enqueue((session.Tls ? "tls " : "") + line);
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (verb) {
                case "EHLO":
                    await session.WriteAsync(Ehlo(session.Tls), token);
                    break;
                case "STARTTLS" when Mode == FakeSmtpMode.StartTls && !session.Tls && AdvertiseStartTls:
                    await session.WriteAsync(InjectAfterStartTls ? "220 go ahead\r\n250 injected\r\n" : "220 go ahead\r\n", token);
                    session = new Session(await ServerTlsAsync(network, token)) { Tls = true };
                    break;
                case "AUTH":
                    await AuthenticateAsync(session, line, token);
                    break;
                case "MAIL":
                    if (Credentials is not null && session.AuthUser is null) {
                        await session.WriteAsync("530 authentication required\r\n", token);
                        break;
                    }
                    session.MailFrom = line[(line.IndexOf(':') + 1)..];
                    session.Recipients.Clear();
                    await session.WriteAsync("250 ok\r\n", token);
                    break;
                case "RCPT":
                    if (RcptReply is { } reply) {
                        await session.WriteAsync($"{reply} refused\r\n", token);
                        break;
                    }
                    session.Recipients.Add(line[(line.IndexOf(':') + 1)..]);
                    await session.WriteAsync("250 ok\r\n", token);
                    break;
                case "DATA" when session.MailFrom is not null && session.Recipients.Count > 0:
                    await session.WriteAsync("354 go\r\n", token);
                    var data = new StringBuilder();
                    while (await session.ReadLineAsync(token) is { } dataLine && dataLine != ".") {
                        data.Append(dataLine.StartsWith('.') ? dataLine[1..] : dataLine).Append("\r\n");
                    }
                    Received.Enqueue(new ReceivedMail(session.MailFrom, [.. session.Recipients], data.ToString(), session.Tls, session.AuthUser));
                    await session.WriteAsync("250 queued\r\n", token);
                    break;
                case "QUIT":
                    await session.WriteAsync("221 bye\r\n", token);
                    return;
                default:
                    await session.WriteAsync("502 not here\r\n", token);
                    break;
            }
        }
    }

    private string Ehlo(bool tls) {
        var lines = new List<string> { "fake.test" };
        if (Mode == FakeSmtpMode.StartTls && !tls && AdvertiseStartTls) {
            lines.Add("STARTTLS");
        }
        if ((tls || Mode == FakeSmtpMode.Plain) && AuthMechanisms.Length > 0) {
            lines.Add("AUTH " + AuthMechanisms);
        }
        if (AdvertiseSmtpUtf8) {
            lines.Add("SMTPUTF8");
        }
        lines.Add("8BITMIME");
        return string.Concat(lines.Select((x, i) => $"250{(i == lines.Count - 1 ? ' ' : '-')}{x}\r\n"));
    }

    private async Task AuthenticateAsync(Session session, string line, CancellationToken token) {
        if (!session.Tls && Mode != FakeSmtpMode.Plain) {
            await session.WriteAsync("538 encryption required\r\n", token);
            return;
        }
        var parts = line.Split(' ');
        string user, password;
        if (parts is [_, var mechanism, var initial] && mechanism.Equals("PLAIN", StringComparison.OrdinalIgnoreCase)) {
            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(initial)).Split('\0');
            (user, password) = (fields[1], fields[2]);
        } else if (parts is [_, var login] && login.Equals("LOGIN", StringComparison.OrdinalIgnoreCase)) {
            await session.WriteAsync("334 VXNlcm5hbWU6\r\n", token);
            user = Encoding.UTF8.GetString(Convert.FromBase64String(await session.ReadLineAsync(token) ?? ""));
            await session.WriteAsync("334 UGFzc3dvcmQ6\r\n", token);
            password = Encoding.UTF8.GetString(Convert.FromBase64String(await session.ReadLineAsync(token) ?? ""));
        } else {
            await session.WriteAsync("504 unsupported\r\n", token);
            return;
        }
        if (Credentials is { } expected && (expected.User, expected.Password) == (user, password)) {
            session.AuthUser = user;
            await session.WriteAsync("235 ok\r\n", token);
        } else {
            await session.WriteAsync("535 bad credentials\r\n", token);
        }
    }

    private async Task<Stream> ServerTlsAsync(Stream network, CancellationToken token) {
        var tls = new SslStream(network, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, token);
        return tls;
    }

    private static X509Certificate2 CreateCertificate() {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Re-imported from PKCS#12: SslStream on macOS cannot serve with the ephemeral key CreateSelfSigned returns.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private sealed class Session(Stream stream) {
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public bool Tls { get; init; }

        public string? AuthUser { get; set; }

        public string? MailFrom { get; set; }

        public List<string> Recipients { get; } = [];

        public async Task WriteAsync(string text, CancellationToken token) {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token);
            await stream.FlushAsync(token);
        }

        public async Task<string?> ReadLineAsync(CancellationToken token) {
            while (true) {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0) {
                    var length = newline - _start - (newline > _start && _buffer[newline - 1] == '\r' ? 1 : 0);
                    var line = Encoding.UTF8.GetString(_buffer, _start, length);
                    _start = newline + 1;
                    return line;
                }
                if (_start > 0) {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                if (_end == _buffer.Length) {
                    throw new IOException("Line too long.");
                }
                var read = await stream.ReadAsync(_buffer.AsMemory(_end), token);
                if (read == 0) {
                    return null;
                }
                _end += read;
            }
        }
    }
}
