import { QueryClient, VueQueryPlugin } from "@tanstack/vue-query";
import { createApp } from "vue";
import { createRouter, createWebHistory } from "vue-router";

import App from "./App.vue";
import "./styles.css";

// Each screen is its own chunk: opening the funnel page does not download the quarantine browser.
const router = createRouter({
    history: createWebHistory(),
    routes: [
        { path: "/", redirect: "/funnels" },
        {
            path: "/funnels",
            component: () => import("./views/FunnelView.vue"),
            meta: { title: "Funnels" },
        },
        {
            path: "/retention",
            component: () => import("./views/RetentionView.vue"),
            meta: { title: "Retention" },
        },
        {
            path: "/trends",
            component: () => import("./views/TrendsView.vue"),
            meta: { title: "Trends" },
        },
        {
            path: "/sessions",
            component: () => import("./views/SessionsView.vue"),
            meta: { title: "Sessions" },
        },
        {
            path: "/quarantine",
            component: () => import("./views/QuarantineView.vue"),
            meta: { title: "Quarantine" },
        },
        { path: "/:rest(.*)*", redirect: "/funnels" },
    ],
});

router.afterEach((to) => {
    document.title = `${String(to.meta.title ?? "Dashboard")} · Tallyhouse`;
});

// Analytics answers change slowly and cost real work: keep them for a minute, and do not refetch just
// because the window regained focus.
const queries = new QueryClient({
    defaultOptions: {
        queries: { staleTime: 60_000, refetchOnWindowFocus: false, retry: 1 },
    },
});

createApp(App).use(router).use(VueQueryPlugin, { queryClient: queries }).mount("#app");
