using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Auth.Email;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jobs;
using Retroscope.Server.Json;

namespace Retroscope.Integration.Tests.Email;

// Hosting, the database, the auth modules and the email module on a TestServer, with a fake clock and a fake SMTP
// server. The worker's background loop is off unless asked for, so a test sends exactly when it calls DeliverAsync.
internal sealed class EmailHost : IAsyncDisposable {
    public const string PublicUrl = "https://retro.test";
    public const string PeerHeader = "X-Test-Peer";
    public const string DefaultPeer = "203.0.113.9";

    private EmailHost(WebApplication app, TestDatabase database, FakeTimeProvider time, FakeSmtpServer smtp) {
        App = app;
        Database = database;
        Time = time;
        Smtp = smtp;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public TestDatabase Database { get; }

    public FakeTimeProvider Time { get; }

    public FakeSmtpServer Smtp { get; }

    public HttpClient Client { get; }

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<EmailHost> StartAsync(
        PostgresFixture postgres,
        FakeSmtpMode mode = FakeSmtpMode.StartTls,
        bool runWorker = false,
        Func<SmtpOptions, SmtpOptions>? smtpOptions = null) {
        var database = await postgres.CreateDatabaseAsync();
        var smtp = new FakeSmtpServer(mode);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["PUBLIC_URL"] = PublicUrl,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
            ["DATABASE_URL"] = database.AppConnectionString,
            ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            ["APP_KEYS"] = "k1:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["SMTP_HOST"] = "127.0.0.1",
            ["SMTP_PORT"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SMTP_SECURITY"] = mode switch {
                FakeSmtpMode.ImplicitTls => "tls",
                FakeSmtpMode.Plain => "none",
                _ => "starttls",
            },
            ["SMTP_USER"] = mode == FakeSmtpMode.Plain ? null : "mailer",
            ["SMTP_PASSWORD"] = mode == FakeSmtpMode.Plain ? null : "s3cret",
            ["SMTP_FROM"] = "Retroscope <no-reply@retro.test>",
        });
        if (mode == FakeSmtpMode.Plain) {
            smtp.Credentials = null;
        }
        var services = builder.Services;
        services.ConfigureHttpJsonOptions(options => {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HostingJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, AuthJsonContext.Default);
        });
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IStartupFilter, PeerFilter>();
        HostingModule.AddServices(services, builder.Configuration);
        Db.AddServices(services, builder.Configuration);
        AuthEndpoints.AddServices(services, builder.Configuration);
        MeEndpoints.AddServices(services, builder.Configuration);
        var before = services.Count;
        EmailModule.AddServices(services, builder.Configuration);
        if (!runWorker) {
            foreach (var hosted in services.Skip(before).Where(x => x.ServiceType == typeof(IHostedService)).ToList()) {
                services.Remove(hosted);
            }
        }
        var configured = SmtpOptions.FromConfiguration(builder.Configuration) with { CertificateValidation = smtp.Validate };
        services.Replace(ServiceDescriptor.Singleton(smtpOptions is null ? configured : smtpOptions(configured)));

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        AuthEndpoints.Map(app);
        MeEndpoints.Map(app);
        EmailModule.Map(app);
        await app.StartAsync(Token);
        return new EmailHost(app, database, time, smtp);
    }

    public EmailBrowser Browser(string peer = DefaultPeer) => new(this, peer);

    /// <summary>Runs the worker once, as its loop would.</summary>
    public Task<int> DeliverAsync() => Service<EmailWorker>().RunOnceAsync(Token);

    /// <summary>Delivers and returns the token from the one link in the one new mail.</summary>
    public async Task<(ReceivedMail Mail, string Token)> DeliverLinkAsync(string page) {
        var sent = Smtp.Received.Count;
        await DeliverAsync();
        var mail = Smtp.Received.Skip(sent).Single();
        var match = Regex.Match(mail.Body, $@"{Regex.Escape(PublicUrl)}/auth/{page}#t=([A-Za-z0-9_-]{{43}})\r\n");
        Assert.True(match.Success, mail.Body);
        return (mail, match.Groups[1].Value);
    }

    public async Task<PrincipalId> CreateUserAsync(string email, string? passwordHash = AuthHost.ImportedHash, bool verified = false) {
        var id = new PrincipalId(Guid.CreateVersion7());
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id.Value);
        await ExecuteAsync(
            "insert into users (id, email, password_hash, email_verified_at, full_name) values ($1, $2, $3, $4, 'Ada')",
            id.Value,
            email,
            (object?)passwordHash ?? DBNull.Value,
            verified ? DateTimeOffset.UtcNow : DBNull.Value);
        return id;
    }

    public async Task<string> CreateSessionAsync(PrincipalId user, AuthMethod method = AuthMethod.Password) {
        var token = OpaqueToken.New();
        var now = Time.GetUtcNow();
        await Service<SessionsRepo>().InsertAsync(
            SessionsRepo.Insert(TokenHash.Of(token), user, method, now, now + Sessions.IdleLifetime, now + Sessions.AbsoluteLifetime, "test"),
            Token);
        return token;
    }

    public async Task ExecuteAsync(string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.DisposeAsync();
        await Smtp.DisposeAsync();
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        NpgsqlConnection.ClearPool(owner);
    }

    private sealed class PeerFilter : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextMiddleware) => {
                context.Connection.RemoteIpAddress = IPAddress.Parse(
                    context.Request.Headers.TryGetValue(PeerHeader, out var peer) ? peer.ToString() : DefaultPeer);
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>One browser: its own cookie jar and address, and same-origin CSRF headers on unsafe requests.</summary>
internal sealed class EmailBrowser(EmailHost host, string peer) {
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public string? Cookie(string name) => _cookies.GetValueOrDefault(name);

    public void SetCookie(string name, string value) => _cookies[name] = value;

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body) {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(EmailHost.PeerHeader, peer);
        if (method != HttpMethod.Get) {
            request.Headers.Add("X-RS", "1");
            request.Headers.Add("Origin", EmailHost.PublicUrl);
        }
        if (_cookies.Count > 0) {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(x => $"{x.Key}={x.Value}")));
        }
        if (body is not null) {
            request.Content = JsonContent.Create(body);
        }
        var response = await host.Client.SendAsync(request, EmailHost.Token);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies)) {
            foreach (var header in setCookies) {
                var cookie = SetCookieHeader.Parse(header);
                if (cookie.MaxAge == TimeSpan.Zero) {
                    _cookies.Remove(cookie.Name);
                } else {
                    _cookies[cookie.Name] = cookie.Value;
                }
            }
        }
        return response;
    }
}
