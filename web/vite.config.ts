import { fileURLToPath, URL } from "node:url";

import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vitest/config";

// The browser never holds a key. In development this proxy adds the project's read key on the way to the
// API, and in the container nginx does the same, so the dashboard is a read-only window onto one project
// and nothing in the bundle, the page or the browser's storage is a credential.
const api = process.env.TALLYHOUSE_API ?? "http://localhost:5180";
const readKey = process.env.TALLYHOUSE_READ_KEY ?? "thr_demo_read_key_do_not_use_outside_localhost";

export default defineConfig({
    plugins: [vue()],
    resolve: {
        alias: {
            "@": fileURLToPath(new URL("./src", import.meta.url)),
        },
    },
    server: {
        proxy: {
            "/v1": {
                target: api,
                changeOrigin: true,
                headers: { Authorization: `Bearer ${readKey}` },
            },
        },
    },
    test: {
        environment: "jsdom",
        globals: true,
        include: ["src/**/*.test.ts"],
        coverage: {
            provider: "v8",
            reporter: ["text", "lcov"],
            include: ["src/**/*.{ts,vue}"],
            exclude: ["src/api/schema.d.ts", "src/main.ts", "src/**/*.test.ts"],
        },
    },
    build: {
        // The budget script reads the manifest to work out what a first visit actually costs.
        manifest: true,
        // The gate is scripts/bundle-budget.mjs, which measures the first load gzipped. This only keeps Vite
        // from warning about the Vue runtime chunk, which is the size it is.
        chunkSizeWarningLimit: 160,
    },
});
