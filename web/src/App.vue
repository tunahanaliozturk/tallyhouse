<script setup lang="ts">
const links = [
    { to: "/funnels", label: "Funnels" },
    { to: "/retention", label: "Retention" },
    { to: "/trends", label: "Trends" },
    { to: "/sessions", label: "Sessions" },
    { to: "/quarantine", label: "Quarantine" },
];
</script>

<template>
    <a class="skip-link" href="#main">Skip to content</a>
    <header class="topbar">
        <span class="brand">
            <img src="/icon.svg" alt="" width="24" height="24" />
            Tallyhouse
        </span>
        <nav aria-label="Main">
            <RouterLink v-for="link in links" :key="link.to" :to="link.to" class="nav-link">{{
                link.label
            }}</RouterLink>
        </nav>
    </header>
    <main id="main" tabindex="-1">
        <RouterView />
    </main>
</template>
