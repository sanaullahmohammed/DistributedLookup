using DistributedLookup.Application.Interfaces;
using DistributedLookup.Infrastructure.Persistence;
using DistributedLookup.Workers.PingWorker;
using MassTransit;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Add Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

// Add Repository
builder.Services.AddScoped<IJobRepository, RedisJobRepository>();

// Add MassTransit
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PingConsumer>();

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

var host = builder.Build();
host.Run();
