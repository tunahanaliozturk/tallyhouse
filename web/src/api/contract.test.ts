import { describe, expect, it } from "vitest";

import { FunnelResult, SegmentResult } from "./contract";

const funnel = {
    steps: [
        {
            event: "signup",
            users: 1000,
            conversionFromPrevious: 1,
            conversionFromStart: 1,
            medianSecondsFromStart: null,
        },
        {
            event: "purchase",
            users: 250,
            conversionFromPrevious: 0.25,
            conversionFromStart: 0.25,
            medianSecondsFromStart: 3600,
        },
    ],
    sampling: { threshold: 10000, scale: 1, isEstimate: false },
};

describe("the response contract", () => {
    it("accepts what the server sends", () => {
        expect(FunnelResult.parse(funnel).steps[1]?.users).toBe(250);
    });

    it("refuses a response missing a field the dashboard renders, naming the field", () => {
        const broken = {
            ...funnel,
            steps: [
                {
                    event: "signup",
                    conversionFromPrevious: 1,
                    conversionFromStart: 1,
                    medianSecondsFromStart: null,
                },
            ],
        };

        const result = FunnelResult.safeParse(broken);

        expect(result.success).toBe(false);
        expect(result.error?.issues[0]?.path).toEqual(["steps", 0, "users"]);
    });

    it("refuses a count that is not a whole non-negative number", () => {
        const result = SegmentResult.safeParse({
            days: [{ day: "2026-09-01", events: -1, users: 3 }],
            lateEvents: 0,
            sampling: { threshold: 10000, scale: 1, isEstimate: false },
        });

        expect(result.success).toBe(false);
    });

    it("refuses a day that is not an ISO date", () => {
        const result = SegmentResult.safeParse({
            days: [{ day: "1 Sep 2026", events: 1, users: 1 }],
            lateEvents: 0,
            sampling: { threshold: 10000, scale: 1, isEstimate: false },
        });

        expect(result.success).toBe(false);
    });
});
