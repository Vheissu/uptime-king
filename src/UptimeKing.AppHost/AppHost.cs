var builder = DistributedApplication.CreateBuilder(args);
var password = builder.AddParameter("workspacePassword", secret: true);
var postgres = builder.AddPostgres("postgres").WithImageTag("17-alpine").WithDataVolume();
var database = postgres.AddDatabase("uptime");
var mail = builder
    .AddContainer("mail", "axllent/mailpit", "v1.27")
    .WithHttpEndpoint(targetPort: 8025, name: "inbox")
    .WithEndpoint(targetPort: 1025, name: "smtp");
var api = builder
    .AddProject<Projects.UptimeKing_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("Auth__Password", password)
    .WithEnvironment("Smtp__Host", mail.GetEndpoint("smtp").Property(EndpointProperty.Host))
    .WithEnvironment("Smtp__Port", mail.GetEndpoint("smtp").Property(EndpointProperty.Port))
    .WithEnvironment("Smtp__UseTls", "false")
    .WithEnvironment("Smtp__From", "uptime@localhost")
    .WithHttpHealthCheck("/health/ready");
builder
    .AddJavaScriptApp("web", "../UptimeKing.Web", runScriptName: "start")
    .WithNpm()
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(port: 4200, targetPort: 4200, isProxied: false)
    .WithExternalHttpEndpoints();
builder.Build().Run();
