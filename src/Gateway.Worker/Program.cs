using Gateway.Core;
using Gateway.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddGatewayCore(builder.Configuration);
builder.Services.AddHostedService<ReconciliationWorker>();
builder.Build().Run();
