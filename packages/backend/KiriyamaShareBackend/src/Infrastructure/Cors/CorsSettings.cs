namespace KiriyamaShareBackend.Infrastructure.Cors;

public class CorsSettings
{
    public string? Angular { get; set; }

    public string? Blazor { get; set; }

    public string? React { get; set; }

    /// <summary>
    /// 浏览器扩展的 origin 前缀，分号分隔（例如 <c>chrome-extension://;moz-extension://</c>）。
    ///
    /// 这里存的是**前缀**而不是完整 origin：扩展在开发期（未打包加载）的 id 会随加载路径变化，
    /// 写死一个 <c>chrome-extension://&lt;id&gt;</c>，每次重装扩展都要回来改配置。
    /// 上线打包、id 固定之后把它换成完整 origin 即可，判断逻辑不用动。
    /// </summary>
    public string? ExtensionOriginPrefixes { get; set; }
}
