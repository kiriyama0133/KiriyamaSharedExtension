import { defineConfig } from 'wxt';
import tailwindcss from '@tailwindcss/vite';

// See https://wxt.dev/api/config.html
export default defineConfig({
  modules: ['@wxt-dev/module-vue'],
  vite: () => ({
    plugins: [tailwindcss()],
  }),
  manifest: {
    name: 'KiriyamaShare',
    description: '监听页面 DOM 变化并实时共享给其它客户端',
    // wxt/utils/storage 读写 settings 需要它
    permissions: ['storage'],
  },
});
