namespace KiriyamaShareBackend.Infrastructure.Common;

/// <summary>
/// 支持的数据库提供程序。目前只接 MSSQL —— 模板原本还带 PostgreSQL / MySQL / Oracle / SqLite，
/// 那几个 provider 与迁移工程都已移除；要加回来就在这里补 key，并在
/// <c>Persistence.Startup.UseDatabase</c> 里补分支。
/// </summary>
internal static class DbProviderKeys
{
    public const string SqlServer = "mssql";
}
