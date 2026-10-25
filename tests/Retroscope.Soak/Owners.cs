using System.Text.Json.Nodes;

namespace Retroscope.Soak;

/// <summary>
/// The verified accounts that own the soak's teams: the facilitator of every poker, retro and spotlight room and every
/// storm creator. Every room belongs to a team and only a verified user may create one (UX18), so they are inserted
/// through psql before the clients start, which spends no mail. The client (client/soak.mjs) signs them in, creates
/// one team per room or creator with a link invite, and signs every other client up through the route to join through
/// that link.
/// </summary>
internal static class Owners {
    /// <summary>bcrypt cost 4 of the clients' password (deploy/drills/client.mjs), the hash the e2e suite seeds.</summary>
    private const string SeedHash = "$2a$04$6x8JWnT3vccNDWa3GtzOROA19eccXMuJ/ZQs3MfUCjIfmf6S6BKRS";

    /// <summary>The owners' display names, by which the client finds each room's facilitator and each storm creator.</summary>
    public static IReadOnlyList<string> Names(Profile profile) => [
        .. Enumerable.Range(1, profile.PokerRooms).Select(i => $"Poker {i} 1"),
        .. Enumerable.Range(1, profile.RetroRooms).Select(i => $"Retro {i} 1"),
        .. Enumerable.Range(1, profile.SpotlightRooms).Select(i => $"Spot {i} 1"),
        .. Enumerable.Range(1, profile.StormCreators).Select(i => $"Storm {i}"),
    ];

    /// <summary>Inserts the owners as verified users; returns them as the client's <c>owners</c>: <c>[{email, name}]</c>.</summary>
    public static async Task<JsonArray> SeedAsync(Stack stack, Profile profile) {
        var owners = Names(profile)
            .Select(name => (Id: Guid.NewGuid(), Email: $"soak-{Guid.NewGuid():N}@example.test", Name: name))
            .ToList();
        var principals = string.Join(", ", owners.Select(o => $"('{o.Id}', 'user')"));
        var users = string.Join(", ", owners.Select(o => $"('{o.Id}', '{o.Email}', now(), '{SeedHash}', {Literal(o.Name)})"));
        await stack.PsqlAsync(
            $"insert into principals (id, kind) values {principals}; " +
            $"insert into users (id, email, email_verified_at, password_hash, full_name) values {users};");
        var config = new JsonArray();
        foreach (var owner in owners) {
            config.Add(new JsonObject { ["email"] = owner.Email, ["name"] = owner.Name });
        }
        return config;
    }

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
