import { defineConfig } from 'vitest/config';

// The modules under test are the app's own files in src/Templing/wwwroot/js, imported as they are (no build step).
export default defineConfig({
    test: {
        environment: 'jsdom',
        setupFiles: ['./setup.js'],
        include: ['*.test.js'],
    },
});
