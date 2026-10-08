using KiriyamaShareBackend.Application.Sharing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;

namespace KiriyamaShareBackend.Infrastructure.Sharing;

/// <summary>
/// 共享服务的注册与挂载。
/// </summary>
internal static class Startup
{
    internal static IServiceCollection AddSharing(this IServiceCollection services, IConfiguration config)
    {
        var logger = Log.ForContext(typeof(Startup));

        services.Configure<ShareHubSettings>(config.GetSection(ShareHubSettings.Position));

        var settings = config.GetSection(ShareHubSettings.Position).Get<ShareHubSettings>() ?? new ShareHubSettings();

        if (!settings.Enabled)
        {
            logger.Warning(
                "Sharing hub is disabled by configuration ('{Section}:enabled = false').",
                ShareHubSettings.Position);

            return services;
        }

        // 房间表必须是单例：每个实例一份成员表的话，同一个房间的两个人会互相看不见。
        services.AddSingleton<IShareRegistry, InMemoryShareRegistry>();

        // SignalR 的服务注册由共享模块自己负责 —— 模板原本是在通知模块里 AddSignalR 的，
        // 通知模块删除后不能把这句一起带走，否则 MapHub<ShareHub> 会因为缺少必需服务而在启动时炸掉。
        services.AddSignalR();

        // 把官方 JsonHubProtocol 换成 `DeepJsonHubProtocol`：**线格式逐字节相同**，只是读的深度上限可配。
        //
        // 非换不可的原因：官方那个协议把 JSON 嵌套上限写死成 64 且没有开关（sealed + private + 默认 reader 选项），
        // 而基线是节点树 —— 每层 DOM 花 2 层 JSON，64 层只够约 30 层 DOM。真实页面一超，
        // ParseMessage 就抛异常，SignalR 的反应是**把连接 abort 掉**（不是拒收这条消息），
        // 房间跟着连接一起消失，表现成"刚分享就没了"。
        //
        // 协议实例是走 DI（IEnumerable<IHubProtocol> → IHubProtocolResolver）解析的，两个都叫 "json" 会有歧义，
        // 所以先把内置的摘掉再挂自己的。本项目没有引用 MessagePack，不存在误伤别的协议。
        services.RemoveAll<IHubProtocol>();
        services.AddSingleton<IHubProtocol, DeepJsonHubProtocol>();

        // patch / 基线都比普通通知大得多，SignalR 默认的 32KB 很容易顶到
        services.Configure<HubOptions>(options =>
        {
            options.MaximumReceiveMessageSize = settings.MaximumReceiveMessageSize;
        });

        // 把这两个传输层上限也打出来：它们都不是默认值，而"跑的是不是带修复的那个二进制"
        // 只能靠日志分辨 —— 上一轮就是拿着旧二进制复现了一遍，白折腾。
        logger.Information(
            "Sharing hub enabled: maxRooms={MaxRooms} codeLength={CodeLength} maxOps={MaxOps} maxReceiveBytes={MaxReceiveBytes} maxJsonDepth={MaxJsonDepth}",
            settings.MaxRooms,
            settings.RoomCodeLength,
            settings.MaxOpsPerPatch,
            settings.MaximumReceiveMessageSize,
            settings.MaxJsonDepth);

        return services;
    }

    internal static IEndpointRouteBuilder MapSharing(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider.GetService<IOptions<ShareHubSettings>>()?.Value
                       ?? new ShareHubSettings();

        if (!settings.Enabled)
        {
            return endpoints;
        }

        // 不挂 RequireAuthorization：扩展端没有账号，靠共享码进房间（见 ShareHub 的注释）
        endpoints.MapHub<ShareHub>("/share");

        return endpoints;
    }
}
