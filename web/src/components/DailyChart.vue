<script setup lang="ts">
import { computed } from "vue";

import { formatCompact, formatCount } from "@/format";

export interface DailyPoint {
    day: string;
    value: number;
}

const props = defineProps<{ points: readonly DailyPoint[]; label: string }>();

const width = 720;
const height = 220;
const padding = { top: 12, right: 12, bottom: 28, left: 56 };

const max = computed(() => Math.max(1, ...props.points.map((point) => point.value)));

const bars = computed(() => {
    const inner = width - padding.left - padding.right;
    const slot = props.points.length === 0 ? inner : inner / props.points.length;
    const plot = height - padding.top - padding.bottom;

    return props.points.map((point, index) => {
        const barHeight = (point.value / max.value) * plot;
        return {
            ...point,
            x: padding.left + index * slot + slot * 0.15,
            y: padding.top + plot - barHeight,
            width: Math.max(1, slot * 0.7),
            height: barHeight,
        };
    });
});

const ticks = computed(() =>
    [0, 0.5, 1].map((fraction) => ({
        value: max.value * fraction,
        y: padding.top + (height - padding.top - padding.bottom) * (1 - fraction),
    })),
);

// Label roughly six days along the axis whatever the range, so a 90-day chart is not a wall of dates.
const labelled = computed(() => {
    const every = Math.max(1, Math.ceil(bars.value.length / 6));
    return bars.value.filter((_, index) => index % every === 0);
});
</script>

<template>
    <figure class="chart">
        <svg :viewBox="`0 0 ${width} ${height}`" role="img" :aria-label="`${label} per day`">
            <g v-for="tick in ticks" :key="tick.y">
                <line
                    :x1="padding.left"
                    :x2="width - padding.right"
                    :y1="tick.y"
                    :y2="tick.y"
                    class="chart__grid"
                />
                <text :x="padding.left - 8" :y="tick.y + 4" class="chart__axis" text-anchor="end">
                    {{ formatCompact(tick.value) }}
                </text>
            </g>
            <rect
                v-for="bar in bars"
                :key="bar.day"
                :x="bar.x"
                :y="bar.y"
                :width="bar.width"
                :height="bar.height"
                class="chart__bar"
            >
                <title>{{ bar.day }}: {{ formatCount(bar.value) }}</title>
            </rect>
            <text
                v-for="bar in labelled"
                :key="`label-${bar.day}`"
                :x="bar.x + bar.width / 2"
                :y="height - 8"
                class="chart__axis"
                text-anchor="middle"
            >
                {{ bar.day.slice(5) }}
            </text>
        </svg>
        <figcaption class="visually-hidden">
            {{ label }} per day, from {{ points[0]?.day }} to {{ points.at(-1)?.day }}
        </figcaption>
    </figure>
</template>
