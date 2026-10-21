using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

/// <param name="Return">Where to land after linking; a same-origin path, else <c>/account</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OidcLinkRequest(string? Return);

/// <summary>The provider URL the browser navigates to; the flow cookie is already set.</summary>
public sealed record OidcLinkResponse(string Url);

/// <summary>Google and Entra sign-in and explicit linking (§d, §e).</summary>
public static class OidcEndpoints {
    public const string DefaultReturn = "/dashboard";
    public const string DefaultLinkReturn = "/account";

    public static readonly RateLimit Start = RateLimit.Define("oidc.start", RateRule.PerIp(30, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Callback = RateLimit.Define("oidc.callback", RateRule.PerIp(30, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Link = RateLimit.Define(
        "oidc.link", RateRule.PerIp(30, TimeSpan.FromMinutes(1)), RateRule.PerPrincipal(10, TimeSpan.FromMinutes(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton(_ => OidcOptions.FromConfiguration(configuration));
        services.AddSingleton<Oidc>();
        services.AddSingleton<OidcAccounts>();
        services.AddSingleton<IBootContributor, OidcBootContributor>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/auth/oidc/{provider}/start", StartSignIn)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Start);
        app.MapGet("/auth/oidc/{provider}/callback", CallbackAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Callback);
        app.MapPost("/api/me/identities/{provider}/link", StartLinkAsync)
            .RequireRoutePolicy(RoutePolicy.User)
            .RequireRateLimit(Link);
    }

    // An unusable `return` is replaced, not refused: the sign-in still works and lands on the default page (S14).
    private static IResult StartSignIn(
        string provider,
        [FromQuery(Name = "return")] string? returnPath,
        HttpContext context,
        Oidc oidc,
        HostingOptions hosting) {
        if (!OidcProviders.TryParse(provider, out var parsed) || !oidc.IsEnabled(parsed)) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var path = ReturnPath.TryValidate(hosting.PublicUrl, returnPath, out var valid) ? valid : DefaultReturn;
        return Results.Redirect(oidc.Start(context, parsed, path, linkUser: null).AbsoluteUri);
    }

    private static async Task<IResult> StartLinkAsync(
        string provider,
        OidcLinkRequest? body,
        HttpContext context,
        Oidc oidc,
        HostingOptions hosting) {
        if (!OidcProviders.TryParse(provider, out var parsed) || !oidc.IsEnabled(parsed)) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var caller = (await context.GetCallerAsync())!;
        var path = ReturnPath.TryValidate(hosting.PublicUrl, body?.Return, out var valid) ? valid : DefaultLinkReturn;
        var url = oidc.Start(context, parsed, path, caller.Principal.Id);
        return Results.Json(new OidcLinkResponse(url.AbsoluteUri), OidcJsonContext.Default.OidcLinkResponse);
    }

    private static async Task<IResult> CallbackAsync(
        string provider,
        HttpContext context,
        Oidc oidc,
        OidcAccounts accounts,
        Sessions sessions,
        UsersRepo users,
        TimeProvider time,
        ILogger<Oidc> logger) {
        if (!OidcProviders.TryParse(provider, out var parsed) || !oidc.IsEnabled(parsed)) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var cancellationToken = context.RequestAborted;
        var query = context.Request.Query;
        var flow = oidc.TakeFlow(context, parsed);
        IResult Fail(string code) {
            OidcLog.Refused(logger, provider, code);
            return Results.Redirect($"{(flow?.LinkUser is null ? "/login" : "/account")}?error={code}");
        }

        if (flow is null || !oidc.TryConsume(flow, Single(query["state"]))) {
            return Fail("oidc_state");
        }
        if (query.ContainsKey("error") || Single(query["code"]) is not { Length: > 0 } code) {
            return Fail("oidc_denied");
        }
        if (await oidc.ExchangeAsync(parsed, code, flow.Verifier, cancellationToken) is not { } claims) {
            return Fail("oidc_failed");
        }
        if (!IdTokenValidator.TryValidate(parsed, oidc.Options, claims, flow.Nonce, time.GetUtcNow(), out var identity, out var error)) {
            return Fail(error);
        }

        if (flow.LinkUser is { } linkUser) {
            var caller = await context.GetCallerAsync();
            if (caller is not { IsUser: true } || caller.Principal.Id.Value != linkUser) {
                return Fail("link_session");
            }
            var outcome = await accounts.LinkExplicitAsync(caller.Principal.Id, identity, cancellationToken);
            if (outcome == OidcLinkOutcome.LinkedElsewhere) {
                return Fail("identity_linked_elsewhere");
            }
            if (outcome == OidcLinkOutcome.Linked) {
                await StorePictureAsync(caller.Principal.Id, identity, oidc, accounts, cancellationToken);
                sessions.EvictUser(caller.Principal.Id);
            }
            return Results.Redirect(flow.Return);
        }

        var resolution = await accounts.ResolveAsync(identity, cancellationToken);
        if (resolution is OidcResolution.Refused refused) {
            return Fail(refused.Code);
        }
        var signIn = (OidcResolution.SignIn)resolution;
        if (signIn.NewLink) {
            await StorePictureAsync(signIn.User, identity, oidc, accounts, cancellationToken);
        }
        var profile = await users.GetProfileAsync(signIn.User, cancellationToken);
        if (profile is null) {
            return Fail("oidc_conflict");
        }
        var principal = new Principal(profile.Id, PrincipalKind.User, profile.DisplayName, profile.AvatarBlobId, profile.IsAdmin);
        await sessions.SignInAsync(context, principal, profile.EmailVerified, OidcProviders.Method(parsed), cancellationToken);
        await users.WriteAsync(
            [UsersRepo.Audit("auth.login", principal.Id, principal.Id.ToString(), new Dictionary<string, string> {
                ["method"] = provider,
                ["ip"] = ClientIp.AuditPrefix(ClientIp.Of(context)),
            }, time.GetUtcNow())],
            cancellationToken);
        return Results.Redirect(flow.Return);
    }

    // §e: the Google picture is fetched once, when the identity is first linked, and only if there is no avatar yet.
    // A failed fetch never fails the sign-in.
    private static async Task StorePictureAsync(
        PrincipalId user,
        OidcIdentity identity,
        Oidc oidc,
        OidcAccounts accounts,
        CancellationToken cancellationToken) {
        if (identity.Picture is not { } picture || await accounts.HasAvatarAsync(user, cancellationToken)) {
            return;
        }
        if (await oidc.FetchPictureAsync(picture, cancellationToken) is { } bytes && SniffedImage.TryCreate(bytes, out var image)) {
            await accounts.SetAvatarIfNoneAsync(user, image, cancellationToken);
        }
    }

    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;
}

/// <summary>Adds <c>oidc</c>, the enabled providers, to the boot JSON so the sign-in screen shows only working buttons.</summary>
public sealed class OidcBootContributor(Oidc oidc) : IBootContributor {
    public ValueTask ContributeAsync(BootContext boot, CancellationToken cancellationToken) {
        string[] providers = [.. oidc.EnabledProviders.Select(OidcProviders.Name)];
        boot.Set("oidc", JsonSerializer.SerializeToElement(providers, OidcJsonContext.Default.StringArray));
        return ValueTask.CompletedTask;
    }
}
