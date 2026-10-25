using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Integration.Tests.AuthCore;

// Signup and every other account creation share one insert, and the claim removes the squatter's identities in the
// same transaction that verifies the account.
[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class UserCreationTests(PostgresFixture postgres) {
    private const string RowSql = """
        select row(u.email_verified_at, u.password_hash, u.full_name, u.nickname, u.avatar_blob_id, u.theme, u.is_admin,
                   u.disabled_at, u.mentions_seen_at, u.created_at, u.updated_at, p.kind, p.created_at)::text
        from users u join principals p on p.id = u.id where u.id = $1
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signup_and_create_write_the_same_row(bool verified) {
        await using var host = await AuthHost.StartAsync(postgres);
        var users = host.Service<UsersRepo>();
        var created = new PrincipalId(Guid.CreateVersion7());
        var signedUp = new PrincipalId(Guid.CreateVersion7());
        var verifiedAt = verified ? Now.AddDays(-1) : (DateTimeOffset?)null;

        Assert.True(await users.CreateAsync(new NewUser(created, "a@example.com", AuthHost.ImportedHash, "Ada", verifiedAt, Now), AuthHost.Token));
        var (isNew, existing) = await users.SignupAsync(
            new NewUser(signedUp, "b@example.com", AuthHost.ImportedHash, "Ada", verifiedAt, Now), "hmac", "203.0.113.0/24", AuthHost.Token);

        Assert.True(isNew);
        Assert.Null(existing);
        Assert.Equal(await host.ScalarAsync<string>(RowSql, created.Value), await host.ScalarAsync<string>(RowSql, signedUp.Value));
    }

    [Fact]
    public async Task A_taken_address_creates_nothing() {
        await using var host = await AuthHost.StartAsync(postgres);
        var users = host.Service<UsersRepo>();
        var owner = await host.CreateUserAsync("ada@example.com");

        var created = await users.CreateAsync(
            new NewUser(new PrincipalId(Guid.CreateVersion7()), "ada@example.com", null, "", Now, Now), AuthHost.Token);
        var (isNew, existing) = await users.SignupAsync(
            new NewUser(new PrincipalId(Guid.CreateVersion7()), "ada@example.com", AuthHost.ImportedHash, "Mallory", null, Now),
            "hmac",
            "203.0.113.0/24",
            AuthHost.Token);

        Assert.False(created);
        Assert.False(isNew);
        Assert.Equal(owner, existing);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from principals"));
    }

    [Fact]
    public async Task A_claim_unlinks_every_identity_with_the_verification() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com");
        await host.ExecuteAsync("insert into external_identities (provider, subject, user_id) values ('google', 'g-1', $1)", user.Value);

        var claimed = await host.Service<AccountClaims>().ClaimAsync(user, OwnershipProof.MagicLink, AuthHost.Token);

        Assert.True(claimed);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from external_identities"));
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "select count(*) from audit_log where action = 'auth.unlink' and actor_id is null and meta = '{\"provider\": \"google\", \"reason\": \"claim\"}'"));
        Assert.True(await host.ScalarAsync<bool>("select email_verified_at is not null and password_hash is null from users"));
    }

    [Fact]
    public async Task A_verified_account_is_neither_claimed_nor_unlinked() {
        await using var host = await AuthHost.StartAsync(postgres);
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        await host.ExecuteAsync("insert into external_identities (provider, subject, user_id) values ('google', 'g-1', $1)", user.Value);

        var claimed = await host.Service<AccountClaims>().ClaimAsync(user, OwnershipProof.MagicLink, AuthHost.Token);

        Assert.False(claimed);
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from external_identities"));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log"));
    }
}
