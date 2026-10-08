import { createApp } from 'vue';
// 和 content script 用的是同一份主题令牌与组件样式，popup 里没有 Shadow DOM，
// 直接按普通样式表引入即可（widget.css 里只有 .ksw-* 选择器，不会误伤别的元素）。
import '@/assets/css/main.css';
import '@/assets/css/widget.css';
import './style.css';
import App from './App.vue';

createApp(App).mount('#app');
