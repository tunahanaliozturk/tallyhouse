<script setup lang="ts">
import { useInfiniteQuery } from "@tanstack/vue-query";
import { computed } from "vue";

import { api } from "@/api/client";
import QueryState from "@/components/QueryState.vue";

const pages = useInfiniteQuery({
    queryKey: ["quarantine"],
    queryFn: ({ pageParam }) => api.quarantine(pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
});

const items = computed(() => pages.data.value?.pages.flatMap((page) => page.items) ?? []);

const received = (value: string): string =>
    new Date(value).toLocaleString("en-GB", { timeZone: "UTC" }) + " UTC";
</script>

<template>
    <section aria-labelledby="quarantine-title">
        <header class="page-header">
            <h1 id="quarantine-title">Quarantine</h1>
            <p>
                Events that failed validation. They were acknowledged and kept, not dropped, and can
                be replayed by the operator once the schema or the client is fixed.
            </p>
        </header>

        <QueryState :loading="pages.isLoading.value" :error="pages.error.value" />

        <p v-if="pages.isSuccess.value && items.length === 0" class="note">
            Nothing is waiting in quarantine.
        </p>

        <ul class="quarantine">
            <li v-for="item in items" :key="item.id" class="quarantine__item">
                <p class="quarantine__reason">{{ item.reason }}</p>
                <p class="quarantine__meta">
                    <span>{{ item.event || "no event name" }}</span>
                    <span>message {{ item.messageId || "without id" }}</span>
                    <span>received {{ received(item.receivedAt) }}</span>
                </p>
                <details>
                    <summary>Payload as received</summary>
                    <pre>{{ item.payload }}</pre>
                </details>
            </li>
        </ul>

        <button
            v-if="pages.hasNextPage.value"
            type="button"
            class="button button--quiet"
            :disabled="pages.isFetchingNextPage.value"
            @click="pages.fetchNextPage()"
        >
            Load more
        </button>
    </section>
</template>
