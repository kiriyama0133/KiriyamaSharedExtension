import '@/assets/css/main.css';
import '@/assets/css/widget.css';

import { createApp } from 'vue';
import { createShadowRootUi } from 'wxt/utils/content-script-ui/shadow-root';
import ShareWidget from '@/components/ShareWidget.vue';
import { createRecorder } from '@/utils/recorder';
import { watchLocation } from '@/utils/navigation';
import { useShareSession } from '@/composables/useShareSession';
import { isRuntimeMessage } from '@/types';
import type { RuntimeMessage } from '@/types';

/**
 * 注入到宿主页面的入口。
 *
 * 做四件事：
 *   1. 把控件挂进 Shadow DOM（样式与宿主页面互不干扰）；
 *   2. 启动 DOM 采集器，把页面变更经 background 往外送；
 *   3. 上报"本标签页是哪个页面"，并在地址变化时再报一次（跳转事件靠它）；
 *   4. 上报"本标签页什么时候被用户看着了"（共享目标跟着激活的标签页走，靠它）。
 *
 * 还有两个必须由**页面里**的脚本才能完成的差事，都是被动应答：
 *   - `ksw:get-page-info`：工具栏弹窗看不到宿主 DOM，只能来问这里；
 *   - `ksw:capture-snapshot`：整页快照（观众端的复现基线）只有这里抓得到。
 *
 * 复现曾经也在这个脚本里（观众只收不发，靠角色互斥避免绕环），现在搬到了
 * 扩展自己的观看页 `entrypoints/viewer` —— 那边是独立的 JS 上下文，
 * 天然不存在"把复现出来的变更又采集回去"的环，也就不再需要角色互斥了。
 *
 * 控件挂上来是**隐藏**的（ShareWidget 的 defaultVisible 默认 false）：页面上不会
 * 自动冒出任何东西，采集器照常静默跑；用户按 Alt+Shift+K 才唤出面板。
 *
 * 关于 runAt：这里用 document_idle。共享是用户手动点「开始共享」触发的，
 * 真正需要采集的是那之后的变化，所以不值得为了多抓几帧首屏而提前到
 * document_start（那时候 <body> 可能还不存在，挂载会麻烦不少）。
 */
export default defineContentScript({
  // 共享的场景是任意网页，所以是全站匹配
  matches: ['<all_urls>'],
  // 让本脚本 import 的 CSS 被注入到 Shadow Root 里，而不是页面上
  cssInjectionMode: 'ui',
  runAt: 'document_idle',

  async main(ctx) {
    const session = useShareSession();
    await session.initSession();

    const ui = await createShadowRootUi(ctx, {
      name: 'kiriyama-share',
      position: 'inline',
      anchor: 'body',
      append: 'last',
      onMount(container) {
        const app = createApp(ShareWidget);
        app.mount(container);
        return app;
      },
      onRemove(app) {
        app?.unmount();
      },
    });

    ui.mount();

    // 把我们自己的宿主节点标成"不要采集"。rrweb 的 blockClass 会连同它的子树一起跳过，
    // 这是比手工在 observer 里判断更彻底的过滤 —— 它从根上断掉
    // 「采集 → 渲染 → 再采集」的环（浮层每次挂载/改属性都会被自己看到）。
    ui.shadowHost.classList.add('rr-block');

    const recorder = createRecorder({
      onStats: (stats) => session.setObserverStats(stats),

      // 基线到了。这里只做展示，真正的上行由 background 在需要时开口要
      // （它才是知道"房间现在到哪个版本"的那一方）。
      onBaseline: () => {
        session.noteBaseline();
      },

      onOps: (ops) => {
        session.noteOps(ops);
        // 没在共享就只统计不上报，别白占带宽
        if (!session.state.sharing) return;
        void browser.runtime
          .sendMessage({
            type: 'ksw:dom-patch',
            // 房间号与 baseVersion 都由 background 贴上去：只有它看得见服务端的回执
            patch: { id: `p${Date.now().toString(36)}`, url: location.href, ops, at: Date.now() },
          } satisfies RuntimeMessage)
          .catch(() => {
            /* background 还没起来 / 正在重载，丢一批无所谓 */
          });
      },
    });

    /**
     * 取一份**当前**的基线给 background。
     *
     * 每次都要重新起一遍采集：`record()` 启动时同步发出的 FullSnapshot 才是"现在这棵树"，
     * 而 SPA 跳转之后旧的基线描述的是跳转之前那一页 —— 交上去等于让观众看到过期页面。
     * 重启同时会把镜像重置、id 重新编号，接收方那边换基线也是整个重置，两边语义一致。
     */
    function freshBaseline() {
      if (recorder.getStats().running) recorder.restart();
      else recorder.start();
      return recorder.capture();
    }

    /**
     * 上报"本标签页现在是哪个页面"。
     *
     * 两个时机都要报：
     *   - 脚本刚跑起来时（整页刷新后 content script 是全新的上下文，
     *     而 background 那边"正在共享"的状态还活着，它得知道房主回到了哪一页）；
     *   - 地址变化时（SPA 路由跳转不发任何原生事件，只能靠 watchLocation 发现）。
     *
     * 这不是"我请求跳转"，只是陈述事实 —— 是不是真的发生了跳转、要不要推给别人，
     * 由 background 比对它记录的目标地址后决定（只有正在共享的那个标签页说了算）。
     */
    function reportPage() {
      const page = session.capturePage();
      if (!page) return;

      void browser.runtime
        .sendMessage({ type: 'ksw:page-changed', page } satisfies RuntimeMessage)
        .catch(() => {
          /* background 不在，等下一次 */
        });
    }

    reportPage();
    const stopWatching = watchLocation(() => reportPage());

    /**
     * 上报"现在被看着的是我这个标签页" —— 共享目标跟着激活的标签页走。
     *
     * 判断用 `visibilityState === 'visible' && document.hasFocus()` 的**组合**，缺一不可：
     *   - 只看 `visibilityState`：换窗口时两个窗口的激活标签页都是 visible，分不出谁在前台；
     *   - 只看 `hasFocus()`：同一窗口里被切到后台的标签页不会收到 blur（它本来就没焦点），
     *     切回来时也就没有 focus 事件，漏掉同窗口内的标签切换。
     * 组合起来正好：同窗口切换由 visibilitychange 报，换窗口由 window 的 focus/blur 报。
     *
     * 只在**状态真的翻转**时报，不是每次 focus 都报：页面里随便一句 `element.focus()`
     * 也会冒泡到 window，照着报就会让 background 每次白抓一份整页快照。
     */
    let foreground = false;

    function reportForeground() {
      const next = document.visibilityState === 'visible' && document.hasFocus();
      const was = foreground;
      foreground = next;
      if (!next || was) return;

      const page = session.capturePage();
      if (!page) return;

      void browser.runtime
        .sendMessage({ type: 'ksw:tab-foreground', page } satisfies RuntimeMessage)
        .catch(() => {
          /* background 不在，等下一次 */
        });
    }

    document.addEventListener('visibilitychange', reportForeground);
    window.addEventListener('focus', reportForeground);
    window.addEventListener('blur', reportForeground);

    // 初值故意留 false：这样本标签页如果**此刻就是**用户在看的那一个，
    // 下面这一下会把它当成一次"进入前台"补报上去 —— 用户切到一个当时还在加载的
    // 标签页时（onActivated 那一刻它上面还没有 content script 可问），
    // 正是靠这一次补报把共享目标换过来的。
    reportForeground();

    /**
     * 回答 background 的两个提问，都用同步 sendResponse。
     *
     * 同步（3 参）而不是返回 Promise：抓页面信息和抓快照都是纯同步操作，
     * 而同步调用在原生 Chrome 和 webextension-polyfill 两种实现下行为一致。
     */
    browser.runtime.onMessage.addListener((message, _sender, sendResponse) => {
      if (!isRuntimeMessage(message)) return;

      if (message.type === 'ksw:get-page-info') {
        sendResponse(session.capturePage());
        return;
      }

      if (message.type === 'ksw:capture-snapshot') {
        sendResponse(freshBaseline());
      }
    });

    // 页面被卸载时收尾：停掉轮询、摘掉前台监听、停掉采集
    ctx.onInvalidated(() => {
      stopWatching();
      recorder.stop();
      document.removeEventListener('visibilitychange', reportForeground);
      window.removeEventListener('focus', reportForeground);
      window.removeEventListener('blur', reportForeground);
    });
  },
});
