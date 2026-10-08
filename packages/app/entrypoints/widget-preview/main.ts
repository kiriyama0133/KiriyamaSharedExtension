import '@/assets/css/main.css';
import '@/assets/css/widget.css';

import { createApp } from 'vue';
import PreviewApp from './PreviewApp.vue';

/**
 * 组件预览页（扩展页面，构建后是 widget-preview.html）。
 *
 * 存在的意义：改组件样式时不必每次都「重新加载扩展 → 开一个真实网站」，
 * 直接在扩展页面里就能看。样式入口和 content script 完全一致，
 * 所以这里看到的就是注入到宿主页面后的真实效果。
 */
createApp(PreviewApp).mount('#app');
