/**
 * 监听"地址变了"。
 *
 * SPA 路由跳转不会触发任何原生事件 —— `history.pushState` / `replaceState` 是静默的，
 * 而 content script 跑在隔离世界里，去改写宿主的 `history` 属于越界（也不可靠）。
 * 所以除了 `popstate` / `hashchange` 这两个原生信号，必须再轮询 `location.href` 兜底，
 * 这是唯一在所有站点都成立的做法。轮询只是比一个字符串，代价可以忽略。
 *
 * 注意：这里只负责"发现了地址变化"这个事实。新页面有没有渲染完是另一回事 ——
 * 调用方要在发现变化之后留一点时间再去抓 DOM（见 background 里重抓快照那一步）。
 */

/** 默认轮询间隔。短到用户感觉不出延迟，长到完全不值得优化 */
const DEFAULT_INTERVAL = 700;

/**
 * 地址变化时回调一次，返回取消监听的函数。
 *
 * @param onChange 收到新的 href
 * @param intervalMs 轮询间隔（毫秒）
 */
export function watchLocation(onChange: (url: string) => void, intervalMs = DEFAULT_INTERVAL): () => void {
  let current = location.href;

  const check = () => {
    if (location.href === current) return;
    current = location.href;
    onChange(current);
  };

  window.addEventListener('popstate', check);
  window.addEventListener('hashchange', check);
  const timer = window.setInterval(check, intervalMs);

  return () => {
    window.removeEventListener('popstate', check);
    window.removeEventListener('hashchange', check);
    window.clearInterval(timer);
  };
}
