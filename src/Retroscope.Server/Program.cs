using Retroscope.Server.Auth.Email;
using Retroscope.Server.Data;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jira;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Transport;

// Composition root. Frozen after U0: modules change what their hooks do, never this file.
if (args is ["healthcheck", ..]) {
    return await Health.ProbeAsync(args);
}

var builder = WebApplication.CreateSlimBuilder(args);
var services = builder.Services;
var configuration = builder.Configuration;

services.ConfigureHttpJsonOptions(options => {
    var chain = options.SerializerOptions.TypeInfoResolverChain;
    chain.Insert(0, HostingJsonContext.Default);
    chain.Insert(1, AuthJsonContext.Default);
    chain.Insert(2, OidcJsonContext.Default);
    chain.Insert(3, BlobJsonContext.Default);
    chain.Insert(4, TeamHttpJsonContext.Default);
    chain.Insert(5, RoomHttpJsonContext.Default);
    chain.Insert(6, JiraHttpJsonContext.Default);
    chain.Insert(7, JiraClientJsonContext.Default);
    chain.Insert(8, AdminJsonContext.Default);
    chain.Insert(9, PokerRoomJsonContext.Default);
    chain.Insert(10, RetroRoomJsonContext.Default);
    chain.Insert(11, TeamRoomJsonContext.Default);
});

HostingModule.AddServices(services, configuration);
Db.AddServices(services, configuration);
Leader.AddServices(services, configuration);
ActorsModule.AddServices(services, configuration);
TransportModule.AddServices(services, configuration);
PokerRoom.AddServices(services, configuration);
RetroRoom.AddServices(services, configuration);
TeamRoom.AddServices(services, configuration);
JiraClient.AddServices(services, configuration);
AuthEndpoints.AddServices(services, configuration);
MeEndpoints.AddServices(services, configuration);
GuestEndpoints.AddServices(services, configuration);
OidcEndpoints.AddServices(services, configuration);
EmailModule.AddServices(services, configuration);
BlobEndpoints.AddServices(services, configuration);
DashboardEndpoints.AddServices(services, configuration);
TeamsEndpoints.AddServices(services, configuration);
InvitesEndpoints.AddServices(services, configuration);
TemplatesEndpoints.AddServices(services, configuration);
EndorsementsEndpoints.AddServices(services, configuration);
PokerEndpoints.AddServices(services, configuration);
RetroEndpoints.AddServices(services, configuration);
MentionsEndpoints.AddServices(services, configuration);
JiraEndpoints.AddServices(services, configuration);
AdminEndpoints.AddServices(services, configuration);

var app = builder.Build();

if (args is ["spot-load", ..]) {
    return await SpotLoadCommand.RunAsync(app, args);
}

HostingModule.UsePipeline(app);

HostingModule.Map(app);
Db.Map(app);
Leader.Map(app);
ActorsModule.Map(app);
TransportModule.Map(app);
PokerRoom.Map(app);
RetroRoom.Map(app);
TeamRoom.Map(app);
JiraClient.Map(app);
AuthEndpoints.Map(app);
MeEndpoints.Map(app);
GuestEndpoints.Map(app);
OidcEndpoints.Map(app);
EmailModule.Map(app);
BlobEndpoints.Map(app);
DashboardEndpoints.Map(app);
TeamsEndpoints.Map(app);
InvitesEndpoints.Map(app);
TemplatesEndpoints.Map(app);
EndorsementsEndpoints.Map(app);
PokerEndpoints.Map(app);
RetroEndpoints.Map(app);
MentionsEndpoints.Map(app);
JiraEndpoints.Map(app);
AdminEndpoints.Map(app);

await app.RunAsync();
return 0;

public partial class Program;
