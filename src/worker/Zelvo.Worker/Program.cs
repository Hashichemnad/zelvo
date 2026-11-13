using Zelvo.Licensing;
using Zelvo.Worker;

// Background jobs host: SLA timers, notifications, integrations and AI jobs arrive with their
// modules. For now it only verifies the license at startup and reports that it is alive.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddZelvoLicensing();
builder.Services.AddHostedService<HeartbeatService>();

builder.Build().Run();
