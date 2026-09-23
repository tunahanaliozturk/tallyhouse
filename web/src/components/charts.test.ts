import { render, screen, within } from "@testing-library/vue";
import { describe, expect, it } from "vitest";

import FunnelChart from "./FunnelChart.vue";
import RetentionGrid from "./RetentionGrid.vue";

describe("FunnelChart", () => {
    const steps = [
        {
            event: "signup",
            users: 2000,
            conversionFromPrevious: 1,
            conversionFromStart: 1,
            medianSecondsFromStart: null,
        },
        {
            event: "activate",
            users: 900,
            conversionFromPrevious: 0.45,
            conversionFromStart: 0.45,
            medianSecondsFromStart: 5400,
        },
        {
            event: "purchase",
            users: 270,
            conversionFromPrevious: 0.3,
            conversionFromStart: 0.135,
            medianSecondsFromStart: 266_461,
        },
    ];

    it("shows each step with its users, conversions and time to convert", () => {
        render(FunnelChart, { props: { steps, estimate: false } });

        const items = within(screen.getByRole("list", { name: "Funnel steps" })).getAllByRole(
            "listitem",
        );
        expect(items).toHaveLength(3);

        expect(within(items[0]!).getByText("2,000")).toBeTruthy();
        expect(within(items[0]!).queryByText("From previous")).toBeNull();

        expect(within(items[2]!).getByText("30%")).toBeTruthy();
        expect(within(items[2]!).getByText("13.5%")).toBeTruthy();
        expect(within(items[2]!).getByText("3d 2h")).toBeTruthy();
    });

    it("marks sampled counts as estimates", () => {
        render(FunnelChart, { props: { steps, estimate: true } });

        expect(screen.getByText("≈ 2,000")).toBeTruthy();
    });
});

describe("RetentionGrid", () => {
    it("shows each day's returns as a share of the cohort", () => {
        render(RetentionGrid, {
            props: {
                days: 3,
                cohorts: [{ day: "2026-09-01", users: 4, retained: [4, 2, 1, 0] }],
            },
        });

        const row = screen.getByRole("row", { name: /2026-09-01/ });
        const cells = within(row).getAllByRole("cell");

        expect(cells.map((cell) => cell.textContent?.trim())).toEqual([
            "4",
            "100%",
            "50%",
            "25%",
            "0%",
        ]);
    });

    it("renders an empty day as zero rather than missing", () => {
        render(RetentionGrid, {
            props: { days: 2, cohorts: [{ day: "2026-09-02", users: 0, retained: [] }] },
        });

        const cells = within(screen.getByRole("row", { name: /2026-09-02/ })).getAllByRole("cell");
        expect(cells.map((cell) => cell.textContent?.trim())).toEqual(["0", "0%", "0%", "0%"]);
    });
});
