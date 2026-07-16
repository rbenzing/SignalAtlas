import { test, expect } from "@playwright/test";

// Smoke E2E — the app shell and dashboard render against a running API.
// Skipped by default (needs `npm run dev` on :5173 + the API on :5285 + Playwright browsers);
// runs in a CI lane with browsers installed.
test.skip("dashboard renders with the app shell", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Signal Atlas" })).toBeVisible();
  // The left nav routes to the six views.
  await expect(page.getByText("Live Spectrum")).toBeVisible();
  await expect(page.getByText("RF Map")).toBeVisible();
});
