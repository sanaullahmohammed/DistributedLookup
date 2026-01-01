using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts;
using DistributedLookup.Infrastructure.Configuration;
using DistributedLookup.Infrastructure.Persistence;
using DistributedLookup.Workers.RdapWorker;
using MassTransit;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Add Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

// Configure RedisWorkerResultStore options
builder.Services.Configure<RedisWorkerResultStoreOptions>(
    builder.Configuration.GetSection(RedisWorkerResultStoreOptions.SectionName));

// Register IWorkerResultStore (workers use this instead of IJobRepository)
builder.Services.AddSingleton<RedisWorkerResultStore>();
builder.Services.AddSingleton<IWorkerResultStore>(sp => sp.GetRequiredService<RedisWorkerResultStore>());

// Configure WorkerResultStoreOptions for resolver
builder.Services.Configure<WorkerResultStoreOptions>(options =>
{
    options.Register<RedisWorkerResultStore>(StorageType.Redis);
    options.DefaultStorageType = StorageType.Redis;
});

// Add HttpClient
builder.Services.AddHttpClient<RDAPConsumer>();

// Add MassTransit
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<RDAPConsumer>();

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
