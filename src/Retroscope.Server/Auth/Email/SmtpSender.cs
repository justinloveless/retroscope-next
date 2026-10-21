using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Retroscope.Server.Auth.Email;

public enum SmtpSecurity {
    /// <summary>Plain connection upgraded with STARTTLS (submission, port 587); refused when the server does not offer it.</summary>
    StartTls,

    /// <summary>TLS from the first byte (submissions, port 465).</summary>
    ImplicitTls,

    /// <summary>No TLS at all: only for a local test relay such as mailpit.</summary>
    None,
}

/// <summary>Mail submission settings from <c>SMTP_*</c>. Without <c>SMTP_HOST</c> mail stays queued and nothing is sent.</summary>
public sealed record SmtpOptions {
    public const int DefaultPort = 587;
    public const int ImplicitTlsPort = 465;

    public string? Host { get; init; }

    public int Port { get; init; } = DefaultPort;

    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    public string? User { get; init; }

    public string? Password { get; init; }

    /// <summary>The envelope sender, a bare address.</summary>
    public string FromAddress { get; init; } = "";

    /// <summary>The <c>From</c> header's display name, if any.</summary>
    public string? FromName { get; init; }

    /// <summary>The name this server gives in <c>EHLO</c>; the public host.</summary>
    public string HeloName { get; init; } = "localhost";

    /// <summary>Null validates the server certificate against the system trust store and <see cref="Host"/>.</summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsConfigured => !string.IsNullOrEmpty(Host);

    public static SmtpOptions FromConfiguration(IConfiguration configuration) {
        var host = configuration["SMTP_HOST"];
        var helo = Uri.TryCreate(configuration["PUBLIC_URL"], UriKind.Absolute, out var publicUrl) ? publicUrl.IdnHost : "localhost";
        if (string.IsNullOrWhiteSpace(host)) {
            return new SmtpOptions { HeloName = helo };
        }
        var port = DefaultPort;
        if (!string.IsNullOrWhiteSpace(configuration["SMTP_PORT"])
            && (!int.TryParse(configuration["SMTP_PORT"], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) {
            throw new InvalidOperationException("SMTP_PORT must be a port number.");
        }
        var security = configuration["SMTP_SECURITY"]?.Trim().ToLowerInvariant() switch {
            null or "" => port == ImplicitTlsPort ? SmtpSecurity.ImplicitTls : SmtpSecurity.StartTls,
            "starttls" => SmtpSecurity.StartTls,
            "tls" => SmtpSecurity.ImplicitTls,
            "none" => SmtpSecurity.None,
            _ => throw new InvalidOperationException("SMTP_SECURITY must be starttls, tls or none."),
        };
        var (fromName, fromAddress) = ParseFrom(configuration["SMTP_FROM"]);
        var user = configuration["SMTP_USER"];
        var password = configuration["SMTP_PASSWORD"];
        if (string.IsNullOrEmpty(user) != string.IsNullOrEmpty(password)) {
            throw new InvalidOperationException("SMTP_USER and SMTP_PASSWORD must be set together.");
        }
        return new SmtpOptions {
            Host = host.Trim(),
            Port = port,
            Security = security,
            User = string.IsNullOrEmpty(user) ? null : user,
            Password = string.IsNullOrEmpty(password) ? null : password,
            FromAddress = fromAddress,
            FromName = fromName,
            HeloName = helo,
        };
    }

    /// <summary><c>no-reply@example.com</c> or <c>Retroscope &lt;no-reply@example.com&gt;</c>.</summary>
    public static (string? Name, string Address) ParseFrom(string? value) {
        var from = value?.Trim();
        if (string.IsNullOrEmpty(from) || !MailHeaders.IsSafe(from)) {
            throw new InvalidOperationException("SMTP_FROM must be an address, optionally as \"Name <address>\".");
        }
        string? name = null;
        var address = from;
        if (from.EndsWith('>') && from.LastIndexOf('<') is var open and >= 0) {
            name = from[..open].Trim().Trim('"').Trim();
            address = from[(open + 1)..^1];
        }
        if (!MailHeaders.IsAddress(address) || (name is not null && name.Any(c => c is '"' or '<' or '>' or '\\'))) {
            throw new InvalidOperationException("SMTP_FROM must be an address, optionally as \"Name <address>\".");
        }
        return (string.IsNullOrEmpty(name) ? null : name, address);
    }
}

/// <summary>A refused or failed submission. The message names the step and the reply code only, never the mail.</summary>
public sealed class SmtpFailureException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// A small SMTP submission client (RFC 5321, RFC 3207 STARTTLS, RFC 4954 AUTH PLAIN/LOGIN, RFC 6531 SMTPUTF8), used
/// instead of <c>System.Net.Mail.SmtpClient</c>, which cannot do implicit TLS on 465 and takes certificate validation
/// from process-wide state. One connection per mail; the queue is small.
/// </summary>
public sealed class SmtpSender(SmtpOptions options, TimeProvider time) {
    public async Task SendAsync(OutboxMail mail, CancellationToken cancellationToken) {
        if (!options.IsConfigured) {
            throw new SmtpFailureException("SMTP is not configured.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var token = timeout.Token;
        try {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(options.Host!, options.Port, token);
            Stream stream = tcp.GetStream();
            try {
                if (options.Security == SmtpSecurity.ImplicitTls) {
                    stream = await TlsAsync(stream, token);
                }
                await SubmitAsync(new SmtpConnection(stream), mail, token);
            } finally {
                await stream.DisposeAsync();
            }
        } catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested) {
            throw new SmtpFailureException("The SMTP server did not answer in time.", e);
        } catch (Exception e) when (e is IOException or SocketException or AuthenticationException) {
            throw new SmtpFailureException($"SMTP connection failed: {e.GetType().Name}.", e);
        }
    }

    private async Task SubmitAsync(SmtpConnection connection, OutboxMail mail, CancellationToken token) {
        await connection.ExpectAsync("greeting", 220, token);
        var extensions = await connection.CommandAsync($"EHLO {options.HeloName}", "EHLO", 250, token);
        if (options.Security == SmtpSecurity.StartTls) {
            if (!Offers(extensions, "STARTTLS")) {
                throw new SmtpFailureException("The SMTP server does not offer STARTTLS.");
            }
            await connection.CommandAsync("STARTTLS", "STARTTLS", 220, token);
            connection = new SmtpConnection(await TlsAsync(connection.TakeStream(), token));
            extensions = await connection.CommandAsync($"EHLO {options.HeloName}", "EHLO", 250, token);
        }
        if (options.User is not null) {
            await AuthenticateAsync(connection, extensions, token);
        }

        var utf8 = !Ascii.IsValid(mail.To) || !Ascii.IsValid(options.FromAddress);
        if (utf8 && !Offers(extensions, "SMTPUTF8")) {
            throw new SmtpFailureException("The address needs SMTPUTF8, which the SMTP server does not offer.");
        }
        await connection.CommandAsync($"MAIL FROM:<{options.FromAddress}>{(utf8 ? " SMTPUTF8" : "")}", "MAIL", 250, token);
        await connection.CommandAsync($"RCPT TO:<{mail.To}>", "RCPT", 250, token, alsoAccept: 251);
        await connection.CommandAsync("DATA", "DATA", 354, token);
        await connection.WriteAsync(MimeMessage.Build(options, mail, time.GetUtcNow()), token);
        await connection.ExpectAsync("message", 250, token);
        try {
            await connection.CommandAsync("QUIT", "QUIT", 221, token);
        } catch (Exception e) when (e is SmtpFailureException or IOException) {
            // The message was accepted; how the server says goodbye does not matter.
        }
    }

    private async Task AuthenticateAsync(SmtpConnection connection, IReadOnlyList<string> extensions, CancellationToken token) {
        if (options.Security == SmtpSecurity.None) {
            throw new SmtpFailureException("Refusing to send SMTP credentials without TLS.");
        }
        var mechanisms = extensions
            .Where(x => x.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mechanisms.Contains("PLAIN")) {
            var plain = Base64($"\0{options.User}\0{options.Password}");
            await connection.CommandAsync($"AUTH PLAIN {plain}", "AUTH", 235, token);
        } else if (mechanisms.Contains("LOGIN")) {
            await connection.CommandAsync("AUTH LOGIN", "AUTH", 334, token);
            await connection.CommandAsync(Base64(options.User!), "AUTH", 334, token);
            await connection.CommandAsync(Base64(options.Password!), "AUTH", 235, token);
        } else {
            throw new SmtpFailureException("The SMTP server offers neither AUTH PLAIN nor AUTH LOGIN.");
        }
    }

    private async Task<Stream> TlsAsync(Stream inner, CancellationToken token) {
        var tls = new SslStream(inner, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions {
                TargetHost = options.Host,
                RemoteCertificateValidationCallback = options.CertificateValidation,
            },
            token);
        return tls;
    }

    private static bool Offers(IReadOnlyList<string> extensions, string keyword) =>
        extensions.Any(x => x.Equals(keyword, StringComparison.OrdinalIgnoreCase)
            || x.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase));

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}

/// <summary>Reply-reading and command-writing over one stream; a new one is made after the STARTTLS handshake.</summary>
internal sealed class SmtpConnection(Stream stream) {
    private const int MaxLineBytes = 2048;
    private const int MaxReplyLines = 100;

    private readonly byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    /// <summary>
    /// Hands the raw stream to the TLS layer. Bytes the server sent after its STARTTLS reply would be read as if
    /// they had come through TLS (command injection, CVE-2011-0411 and kin), so their presence fails the send.
    /// </summary>
    public Stream TakeStream() {
        if (_start != _end) {
            throw new SmtpFailureException("The SMTP server sent data after its STARTTLS reply.");
        }
        return stream;
    }

    public async Task<IReadOnlyList<string>> CommandAsync(
        string command,
        string step,
        int expected,
        CancellationToken token,
        int? alsoAccept = null) {
        await WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), token);
        return await ExpectAsync(step, expected, token, alsoAccept);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) {
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    /// <returns>The text of every reply line after the code (for EHLO, the greeting and then the extension keywords).</returns>
    public async Task<IReadOnlyList<string>> ExpectAsync(string step, int expected, CancellationToken token, int? alsoAccept = null) {
        var lines = new List<string>();
        while (true) {
            var line = await ReadLineAsync(token);
            if (line.Length < 3
                || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                || (line.Length > 3 && line[3] is not ('-' or ' '))) {
                throw new SmtpFailureException($"Malformed SMTP reply at {step}.");
            }
            lines.Add(line.Length > 4 ? line[4..] : "");
            if (line.Length > 3 && line[3] == '-') {
                if (lines.Count >= MaxReplyLines) {
                    throw new SmtpFailureException($"SMTP reply at {step} is too long.");
                }
                continue;
            }
            if (code != expected && code != alsoAccept) {
                throw new SmtpFailureException($"SMTP {step} answered {code}.");
            }
            return lines;
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken token) {
        while (true) {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0) {
                var length = newline - _start;
                if (length > 0 && _buffer[newline - 1] == '\r') {
                    length--;
                }
                var line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start = newline + 1;
                return line;
            }
            if (_end - _start >= MaxLineBytes) {
                throw new SmtpFailureException("SMTP reply line is too long.");
            }
            if (_start > 0) {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var read = await stream.ReadAsync(_buffer.AsMemory(_end), token);
            if (read == 0) {
                throw new SmtpFailureException("The SMTP server closed the connection.");
            }
            _end += read;
        }
    }
}

/// <summary>Builds the DATA payload: RFC 5322 headers, a text/plain body, dot-stuffing and the terminating dot.</summary>
internal static class MimeMessage {
    private const int MaxLineLength = 998;

    public static byte[] Build(SmtpOptions options, OutboxMail mail, DateTimeOffset now) {
        var lines = mail.Body.ReplaceLineEndings("\n").Split('\n');
        var sevenBit = Ascii.IsValid(mail.Body) && lines.All(x => x.Length <= MaxLineLength);
        var domain = options.FromAddress[(options.FromAddress.LastIndexOf('@') + 1)..];
        var message = new StringBuilder();
        Header(message, "From", options.FromName is null ? options.FromAddress : $"{Phrase(options.FromName)} <{options.FromAddress}>");
        Header(message, "To", mail.To);
        Header(message, "Subject", Ascii.IsValid(mail.Subject) ? mail.Subject : EncodedWord(mail.Subject));
        Header(message, "Date", now.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture));
        Header(message, "Message-ID", $"<{Guid.NewGuid():N}@{domain}>");
        Header(message, "MIME-Version", "1.0");
        Header(message, "Content-Type", "text/plain; charset=utf-8");
        Header(message, "Content-Transfer-Encoding", sevenBit ? "7bit" : "base64");
        message.Append("\r\n");
        if (sevenBit) {
            foreach (var line in lines) {
                message.Append(line.StartsWith('.') ? "." : "").Append(line).Append("\r\n");
            }
        } else {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));
            for (var i = 0; i < encoded.Length; i += 76) {
                message.Append(encoded.AsSpan(i, Math.Min(76, encoded.Length - i))).Append("\r\n");
            }
        }
        message.Append(".\r\n");
        return Encoding.UTF8.GetBytes(message.ToString());
    }

    private static void Header(StringBuilder message, string name, string value) {
        if (!MailHeaders.IsSafe(value)) {
            throw new ArgumentException($"The {name} header may not contain line breaks or control characters.", nameof(value));
        }
        message.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static string Phrase(string name) =>
        !Ascii.IsValid(name) ? EncodedWord(name)
        : name.All(c => char.IsAsciiLetterOrDigit(c) || c == ' ') ? name
        : $"\"{name}\"";

    private static string EncodedWord(string value) => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
}
