import { createApp } from 'vue';

// 和 popup 一样直接引这两份：观看页是扩展自己的普通页面（没有 Shadow DOM），
// 而 widget.css 里只有 .ksw-* 选择器，不会误伤别的元素。
import '@/assets/css/main.css';
import '@/assets/css/widget.css';
import ViewerApp from './ViewerApp.vue';

createApp(ViewerApp).mount('#app');
