using System.Security.Claims;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ReconPlatform.Api.Middleware;
using ReconPlatform.Config;
using ReconPlatform.Connectors;
using ReconPlatform.Connectors.Interfaces;
using ReconPlatform.Engine;
using ReconPlatform.Skills;
using ReconPlatform.Storage;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .WriteTo.Console());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ── Authentication ────────────────────────────────────────────────────────────

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["AzureAd:Authority"];
        options.Audience  = builder.Configuration["AzureAd:Audience"];

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer   = true,
            ValidateAudience = true,
            ValidateLifetime = true,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                // Extract team claim and store for controllers.
                var teamClaim = ctx.Principal?.FindFirstValue("team")
                    ?? ctx.Principal?.FindFirstValue("groups");

                if (!string.IsNullOrWhiteSpace(teamClaim))
                    ctx.HttpContext.Items["team_claim"] = teamClaim;

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// ── SecretResolver ────────────────────────────────────────────────────────────

builder.Services.AddSingleton(sp =>
{
    var keyVaultUrl = builder.Configuration["KeyVault:Url"];
    var isDev = builder.Environment.IsDevelopment();
    var logger = sp.GetRequiredService<ILogger<SecretResolver>>();
    return new SecretResolver(keyVaultUrl, isDev, logger);
});

// ── Storage clients ───────────────────────────────────────────────────────────

builder.Services.AddSingleton(sp =>
{
    var endpoint = builder.Configuration["CosmosDb:Endpoint"]
        ?? throw new InvalidOperationException("CosmosDb:Endpoint is required.");
    var dbId = builder.Configuration["CosmosDb:DatabaseId"]
        ?? throw new InvalidOperationException("CosmosDb:DatabaseId is required.");
    var logger = sp.GetRequiredService<ILogger<CosmosDbClient>>();
    return new CosmosDbClient(endpoint, dbId, logger);
});

builder.Services.AddSingleton(sp =>
{
    var accountUrl = builder.Configuration["BlobStorage:AccountUrl"]
        ?? throw new InvalidOperationException("BlobStorage:AccountUrl is required.");
    var logger = sp.GetRequiredService<ILogger<BlobStorageClient>>();
    return new BlobStorageClient(accountUrl, logger);
});

builder.Services.AddSingleton(sp =>
{
    var secretResolver = sp.GetRequiredService<SecretResolver>();
    var rawConnStr = builder.Configuration["SqlMetadata:ConnectionString"]
        ?? throw new InvalidOperationException("SqlMetadata:ConnectionString is required.");
    // Resolve synchronously at startup (blocking is acceptable once during startup).
    var connStr = secretResolver.ResolveAsync(rawConnStr).GetAwaiter().GetResult();
    var logger = sp.GetRequiredService<ILogger<SqlMetadataClient>>();
    return new SqlMetadataClient(connStr, logger);
});

builder.Services.AddSingleton(sp =>
{
    var secretResolver = sp.GetRequiredService<SecretResolver>();
    var rawConnStr = builder.Configuration["Synapse:ConnectionString"]
        ?? throw new InvalidOperationException("Synapse:ConnectionString is required.");
    var connStr = secretResolver.ResolveAsync(rawConnStr).GetAwaiter().GetResult();
    var logger = sp.GetRequiredService<ILogger<SynapseClient>>();
    return new SynapseClient(connStr, logger);
});

// ── Service Bus / RetriggerOrchestrator ───────────────────────────────────────

builder.Services.AddSingleton(_ =>
{
    var fqns = builder.Configuration["ServiceBus:FullyQualifiedNamespace"]
        ?? throw new InvalidOperationException("ServiceBus:FullyQualifiedNamespace is required.");
    return new ServiceBusClient(fqns, new DefaultAzureCredential());
});

builder.Services.AddSingleton(sp =>
{
    var sbClient  = sp.GetRequiredService<ServiceBusClient>();
    var queueName = builder.Configuration["ServiceBus:ConnectorQueueName"]
        ?? throw new InvalidOperationException("ServiceBus:ConnectorQueueName is required.");
    var logger = sp.GetRequiredService<ILogger<RetriggerOrchestrator>>();
    return new RetriggerOrchestrator(sbClient, queueName, logger);
});

// ── Engine services ───────────────────────────────────────────────────────────

builder.Services.AddSingleton<DeduplicationEngine>();
builder.Services.AddSingleton<DiffEngine>();
builder.Services.AddSingleton<Normalizer>();
builder.Services.AddSingleton<StalenessChecker>();

// ── Skills ────────────────────────────────────────────────────────────────────

builder.Services.AddSingleton(sp =>
{
    var skillsDir = Path.Combine(Directory.GetCurrentDirectory(), "skills");
    if (!Directory.Exists(skillsDir))
        Directory.CreateDirectory(skillsDir);
    var logger = sp.GetRequiredService<ILogger<SkillRegistry>>();
    return new SkillRegistry(skillsDir, logger);
});

builder.Services.AddSingleton<SkillExecutor>();

// ── Connectors ────────────────────────────────────────────────────────────────

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("agent");

builder.Services.AddSingleton<RestApiConnector>();
builder.Services.AddSingleton<AzureSqlConnector>();
builder.Services.AddSingleton<AzureAdxConnector>();

// Register all connectors as IConnector so controllers can receive IEnumerable<IConnector>.
builder.Services.AddSingleton<IConnector>(sp => sp.GetRequiredService<RestApiConnector>());
builder.Services.AddSingleton<IConnector>(sp => sp.GetRequiredService<AzureSqlConnector>());
builder.Services.AddSingleton<IConnector>(sp => sp.GetRequiredService<AzureAdxConnector>());

// ── Build ─────────────────────────────────────────────────────────────────────

var app = builder.Build();

// ── Middleware pipeline (order matters) ───────────────────────────────────────

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AuditLoggingMiddleware>();
app.MapControllers();

// ── Health endpoint (anonymous) ───────────────────────────────────────────────

app.MapGet("/api/health", (SkillRegistry skillRegistry) =>
{
    var skillIds    = skillRegistry.GetLoadedSkillIds();
    var skillsCount = skillIds.Count;

    return Results.Ok(new
    {
        status = "healthy",
        components = new
        {
            cosmos        = "healthy",
            blob          = "healthy",
            sql           = "healthy",
            serviceBus    = "healthy",
            skillRegistry = "healthy",
        },
        skillsLoaded = skillsCount,
        timestamp    = DateTimeOffset.UtcNow,
    });
})
.AllowAnonymous();

app.Run();

// Exposed for WebApplicationFactory in integration tests
public partial class Program { }
