import { describe, expect, it } from "vitest";

import { addDays, formatDuration } from "./format";

describe("formatDuration", () => {
    it.each([
        [0, "0s"],
        [45.4, "45s"],
        [60, "1m"],
        [3_660, "1h 1m"],
        [90_000, "1d 1h"],
        // Only the two largest units: a funnel reader does not need the seconds on a three-day conversion.
        [266_461, "3d 2h"],
    ])("%d seconds reads as %s", (seconds, expected) => {
        expect(formatDuration(seconds)).toBe(expected);
    });
});

describe("addDays", () => {
    it("crosses month and year boundaries in UTC", () => {
        expect(addDays("2026-12-31", 1)).toBe("2027-01-01");
        expect(addDays("2026-03-01", -1)).toBe("2026-02-28");
    });
});
