using Npgsql;

namespace Retroscope.Server.Data;

public sealed class Db {
    public const string AppRole = "retroscope_app";

    private Db(string appConnectionString, string ownerConnectionString, string appPassword) {
        AppConnectionString = appConnectionString;
        OwnerConnectionString = ownerConnectionString;
        AppPassword = appPassword;
    }

    public string AppConnectionString { get; }

    public string OwnerConnectionString { get; }

    // The migrator gives the app role this password, so DATABASE_URL is the only place it is configured.
    public string AppPassword { get; }

    public static Db FromConfiguration(IConfiguration configuration) {
        var app = Require(configuration, "DATABASE_URL");
        var owner = Require(configuration, "DATABASE_OWNER_URL");
        var builder = new NpgsqlConnectionStringBuilder(app);
        if (builder.Username != AppRole) {
            throw new InvalidOperationException($"DATABASE_URL must connect as {AppRole}, not '{builder.Username}'.");
        }
        if (string.IsNullOrEmpty(builder.Password)) {
            throw new InvalidOperationException("DATABASE_URL must carry the app role's password.");
        }
        return new Db(app, owner, builder.Password);
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString) =>
        new NpgsqlSlimDataSourceBuilder(connectionString)
            .EnableArrays()
            .EnableJsonTypes()
            .Build();

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton(_ => FromConfiguration(configuration));
        services.AddSingleton(sp => CreateDataSource(sp.GetRequiredService<Db>().AppConnectionString));
    }

    public static void Map(IEndpointRouteBuilder app) {
    }

    private static string Require(IConfiguration configuration, string key) {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is not set.")
            : value;
    }
}
