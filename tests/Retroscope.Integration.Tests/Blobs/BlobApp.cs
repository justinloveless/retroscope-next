using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Retroscope.Integration.Tests.Hosting;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Blobs;

// The blob endpoints on the hosting pipeline, against a migrated database. Rows the endpoints only read (boards,
// teams, grants) are written as the owner, the way the actors and the importer would have written them.
internal sealed class BlobApp : IAsyncDisposable {
    private static readonly string PasswordHash = "$2a$10$" + new string('a', 53);

    private readonly TestApp _app;

    private BlobApp(TestApp app, TestDatabase database, CapturingLoggerProvider logs) {
        _app = app;
        Database = database;
        Logs = logs;
    }

    public TestDatabase Database { get; }

    public CapturingLoggerProvider Logs { get; }

    public BlobsRepo Repo => _app.Services.GetRequiredService<BlobsRepo>();

    /// <param name="time">A frozen clock keeps the rate limiters from refilling while a test runs.</param>
    public static async Task<BlobApp> StartAsync(PostgresFixture postgres, long? bytesMax = null, TimeProvider? time = null) {
        var database = await postgres.CreateDatabaseAsync();
        var settings = new Dictionary<string, string?> { ["BLOB_BYTES_MAX"] = bytesMax?.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logs = new CapturingLoggerProvider();
        var app = await TestApp.StartAsync(
            settings,
            services => {
                services.AddSingleton(_ => Db.CreateDataSource(database.AppConnectionString));
                services.AddSingleton<ILoggerProvider>(logs);
                if (time is not null) {
                    services.AddSingleton(time);
                }
                BlobEndpoints.AddServices(services, configuration);
            },
            BlobEndpoints.Map);
        return new BlobApp(app, database, logs);
    }

    public async Task<HttpResponseMessage> UploadAsync(
        Guid board,
        byte[] body,
        string? caller,
        string peer = "198.51.100.10",
        string contentType = "application/octet-stream") {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/retro/{board}/images") {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Add(CsrfFilter.HeaderName, "1");
        request.Headers.Add("Origin", TestApp.PublicUrl);
        request.Headers.Add(TestApp.PeerHeader, peer);
        if (caller is not null) {
            request.Headers.Add(TestApp.CallerHeader, caller);
        }
        return await _app.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<HttpResponseMessage> GetAsync(Guid blob, string? caller, string? peer = null) {
        using var request = TestApp.Get($"/blob/{blob}", peer);
        if (caller is not null) {
            request.Headers.Add(TestApp.CallerHeader, caller);
        }
        return await _app.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<Guid> UploadedIdAsync(Guid board, byte[] body, string caller) {
        using var response = await UploadAsync(board, body, caller);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("blobId").GetGuid();
    }

    public async Task<Guid> InsertGuestAsync() {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("insert into principals (id, kind, display_name) values ($1, 'guest', 'Gus')", id);
        return id;
    }

    public async Task<Guid> InsertUserAsync() {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id);
        await ExecuteAsync("insert into users (id, email) values ($1, $2)", id, $"{id:N}@example.com");
        return id;
    }

    public async Task<Guid> InsertTeamAsync(Guid? member = null, string role = "member") {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("insert into teams (id, name) values ($1, 'Team')", id);
        if (member is not null) {
            await ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, $3)", id, member, role);
        }
        return id;
    }

    public async Task<Guid> InsertBoardAsync(
        string visibility = "link",
        Guid? team = null,
        Guid? createdBy = null,
        bool archived = false,
        bool deleted = false,
        string stage = "thinking",
        bool stagesEnabled = false) {
        var id = Guid.CreateVersion7();
        var password = visibility == "password";
        await ExecuteAsync(
            """
            insert into retro_boards (id, code, team_id, title, created_by, visibility, password_hash, password_set_at,
                                      stage, stages_enabled, archived_at, deleted_at)
            values ($1, $2, $3, 'Board', $4, $5, $6, $7, $8, $9, $10, $11)
            """,
            id,
            Guid.NewGuid().ToString("N")[..10],
            team,
            createdBy,
            visibility,
            password ? PasswordHash : null,
            password ? DateTimeOffset.UtcNow.AddMinutes(-5) : null,
            stage,
            stagesEnabled,
            archived ? DateTimeOffset.UtcNow : null,
            deleted ? DateTimeOffset.UtcNow : null);
        return id;
    }

    public Task InsertActionItemsColumnAsync(Guid board) => ExecuteAsync(
        "insert into retro_columns (id, board_id, title, color, sort_key, is_action_items) values ($1, $2, 'Actions', 'blue', 0, true)",
        Guid.CreateVersion7(),
        board);

    /// <param name="current">A grant for the board's current password, or for an earlier one.</param>
    public Task InsertGrantAsync(Guid board, Guid principal, bool current) => ExecuteAsync(
        """
        insert into board_access_grants (board_id, principal_id, password_epoch)
        select id, $2, case when $3 then password_set_at else password_set_at - interval '1 day' end
        from retro_boards where id = $1
        """,
        board,
        principal,
        current);

    /// <summary>A board image row whose recorded size is <paramref name="byteSize"/>; quotas only add up sizes.</summary>
    public async Task<Guid> InsertBoardImageRowAsync(
        Guid board,
        int byteSize,
        TimeSpan age = default,
        bool attached = false) {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes, attached_at, created_at)
            values ($1, 'board_image', $2, 'image/png', $3, sha256('x'), 'x', $4, now() - $5)
            """,
            id,
            board,
            byteSize,
            attached ? DateTimeOffset.UtcNow : null,
            age);
        return id;
    }

    public async Task<T> ScalarAsync<T>(string sql, params object?[] values) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = Command(connection, sql, values);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async Task ExecuteAsync(string sql, params object?[] values) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = Command(connection, sql, values);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object?[] values) {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
        return command;
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider {
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() {
    }

    private sealed class Logger(CapturingLoggerProvider provider) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
