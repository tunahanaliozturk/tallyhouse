import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

// Runs against the compose stack, after CI has sent a batch with one full conversion. The dashboard reads
// the demo project through nginx, which adds the read key; nothing here holds a credential.

test("the funnel page answers from the real pipeline and has no accessibility violations", async ({
    page,
}) => {
    await page.goto("/funnels");

    const steps = page.getByRole("list", { name: "Funnel steps" });
    await expect(steps.getByRole("listitem")).toHaveCount(3);
    await expect(steps.getByRole("listitem").first()).toContainText("signup");

    const results = await new AxeBuilder({ page }).analyze();
    expect(results.violations).toEqual([]);
});

test("a funnel built from different steps reruns when submitted", async ({ page }) => {
    await page.goto("/funnels");
    await expect(
        page.getByRole("list", { name: "Funnel steps" }).getByRole("listitem"),
    ).toHaveCount(3);

    await page.getByRole("button", { name: "Remove step 2" }).click();
    await page.getByRole("button", { name: "Run" }).click();

    await expect(
        page.getByRole("list", { name: "Funnel steps" }).getByRole("listitem"),
    ).toHaveCount(2);
});

test("every screen renders without accessibility violations", async ({ page }) => {
    for (const [path, heading] of [
        ["/retention", "Retention"],
        ["/trends", "Trends"],
        ["/sessions", "Sessions"],
        ["/quarantine", "Quarantine"],
    ] as const) {
        await page.goto(path);
        await expect(page.getByRole("heading", { level: 1, name: heading })).toBeVisible();
        await expect(page.getByRole("status")).toHaveCount(0);

        const results = await new AxeBuilder({ page }).analyze();
        expect(results.violations, path).toEqual([]);
    }
});
