using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.Saga;
using DistributedLookup.Application.UseCases;
using DistributedLookup.Infrastructure.Persistence;
using MassTransit;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Health checks (MassTransit adds a "ready" + "masstransit" tagged check automatically)
builder.Services.AddHealthChecks();

// Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

// Repository
builder.Services.AddScoped<IJobRepository, RedisJobRepository>();

// Saga State Repository (for reading saga state directly)
builder.Services.AddScoped<ISagaStateRepository, RedisSagaStateRepository>();

// Worker Result Reader (read-only access to results stored by workers)
builder.Services.AddScoped<IWorkerResultReader, RedisWorkerResultReader>();

// Use Cases
builder.Services.AddScoped<SubmitLookupJob>();
builder.Services.AddScoped<GetJobStatus>();

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("api-limit", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;
    });

    options.AddSlidingWindowLimiter("expensive", opt =>
    {
        opt.PermitLimit = 20;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.SegmentsPerWindow = 6;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 5;
    });

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter("global", _ => new()
        {
            PermitLimit = 1000,
            Window = TimeSpan.FromMinutes(1)
        }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.Headers["Retry-After"] = "60";

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                error = "Rate limit exceeded",
                message = "Too many requests. Please try again later.",
                retryAfter = (int)retryAfter.TotalSeconds
            }, cancellationToken: token);
        }
        else
        {
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                error = "Rate limit exceeded",
                message = "Too many requests. Please try again later.",
                retryAfter = 60
            }, cancellationToken: token);
        }
    };
});

// MassTransit with RabbitMQ
builder.Services.AddMassTransit(x =>
{
    x.AddSagaStateMachine<LookupJobStateMachine, LookupJobState>()
        .RedisRepository(r =>
        {
            r.DatabaseConfiguration(redisConnection);
            r.KeyPrefix = "saga";
        });

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitHost = builder.Configuration.GetConnectionString("RabbitMQ") ?? "localhost";

        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        cfg.ConfigureEndpoints(context);
    });
});

// Add scoped payload
builder.Services.AddScoped<ConsumeContext>(_ =>
    throw new InvalidOperationException("ConsumeContext not available outside message consumers"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Health endpoints
// /health/ready -> includes MassTransit "ready" tagged check (bus + endpoints readiness)
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).DisableRateLimiting();

// /health/live -> basic liveness (process is running)
app.MapHealthChecks("/health/live").DisableRateLimiting();

// Enable rate limiting (must be before MapControllers)
app.UseRateLimiter();

app.UseAuthorization();
app.MapControllers();

app.Run();
