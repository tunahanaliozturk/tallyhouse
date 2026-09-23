import { useQuery } from "@tanstack/vue-query";
import { computed } from "vue";

import { api } from "@/api/client";

/**
 * The project's registered events. Every form offers these, so a funnel can only be built from events
 * that can exist, and the list is fetched once and shared by the query cache rather than per page.
 */
export function useSchemas() {
    const query = useQuery({
        queryKey: ["schemas"],
        queryFn: api.schemas,
        staleTime: 60_000,
    });

    const events = computed(() =>
        [...new Set((query.data.value ?? []).map((schema) => schema.event))].sort(),
    );

    const properties = computed(() => {
        const names = new Set<string>();

        for (const schema of query.data.value ?? []) {
            Object.keys(schema.spec.properties).forEach((name) => names.add(name));
        }

        return [...names].sort();
    });

    return { events, properties, isLoading: query.isLoading, error: query.error };
}
