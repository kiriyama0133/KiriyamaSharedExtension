namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 房间里的一个人。对应前端 <c>types/index.ts</c> 的 <c>Peer</c>，
/// 但**不含 <c>isSelf</c>** —— 那是客户端拿到列表后自己比对得出的，服务端没法替它判断。
/// </summary>
public class SharePeerDto
{
    /// <summary>连接级标识，同一个连接内稳定。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>光标配色，由服务端从调色板里分配，保证同一房间内不重样。</summary>
    public string Color { get; set; } = string.Empty;
}
