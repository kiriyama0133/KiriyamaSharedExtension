using Genocs.Logging;
using KiriyamaShareBackend.Application;
using KiriyamaShareBackend.Infrastructure;
using KiriyamaShareBackend.Infrastructure.Logging.Serilog;
using KiriyamaShareBackend.WebApi.Configurations;
using KiriyamaShareBackend.WebApi.Controllers;
using Serilog;
using Genocs.Core.Builders;

[assembly: ApiConventionType(typeof(GNXApiConventions))]

StaticLogger.EnsureInitialized();
try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddConfigurations();

    builder
            .AddGenocs()
            .Build();

    builder.Host
        .UseLogging(builder.Environment.EnvironmentName);

    builder.Services.AddControllers();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddApplication();

    var app = builder.Build();

    // 共享服务是纯内存的（房间寿命 = 房主的连接），不需要数据库也应该能启动，
    // 所以"建库 / 跑迁移"做成开关，默认关闭。
    // 要用认证（Identity 的 Users/Roles 表）时，把 Configurations/database.json 的
    // initializeOnStartup 打开即可。
    if (builder.Configuration.GetValue("DatabaseSettings:InitializeOnStartup", false))
    {
        await app.Services.InitializeDatabasesAsync();
    }

    app.UseInfrastructure(builder.Configuration);
    app.MapEndpoints();
    app.Run();
}
catch (Exception ex) when (!ex.GetType().Name.Equals("HostAbortedException", StringComparison.Ordinal))
{
    Log.Fatal(ex, "Unhandled exception");
}
finally
{
    Log.Information("Server Shutting down...");
    Log.CloseAndFlush();
}