namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 发起共享的入参：描述"要被共享的那个页面"。
/// 与前端 <c>PageInfo</c> 对应。
/// </summary>
public class ShareStartRequest
{
    /// <summary>页面标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>页面完整地址，必须是 http/https。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>页面主机名，订阅端列表直接显示它。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>发起方希望显示的名字；留空由服务端分配一个。</summary>
    public string? Name { get; set; }
}
