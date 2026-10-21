using Microsoft.Extensions.DependencyInjection.Extensions;
using Retroscope.Server.Jobs;

namespace Retroscope.Server.Auth.Email;

public static class EmailModule {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, EmailJsonContext.Default));
        services.AddSingleton(_ => SmtpOptions.FromConfiguration(configuration));
        services.AddSingleton<SmtpSender>();
        services.AddSingleton<Outbox>();
        services.AddSingleton<Tokens>();
        services.AddSingleton<AccountMail>();
        services.Replace(ServiceDescriptor.Singleton<IAccountMail>(sp => sp.GetRequiredService<AccountMail>()));
        services.AddSingleton<EmailWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailWorker>());
    }

    public static void Map(IEndpointRouteBuilder app) => EmailEndpoints.Map(app);
}
