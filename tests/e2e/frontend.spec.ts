import { test, expect, Page } from "@playwright/test";

function attachErrorListeners(page: Page) {
  const consoleErrors = new Set<string>();
  const failedRequests = new Map<string, string>();
  const httpErrors: { url: string; status: number }[] = [];

  page.on("console", (msg) => {
    if (msg.type() === "error") {
      consoleErrors.add(msg.text().substring(0, 200));
    }
  });

  page.on("pageerror", (err) => {
    consoleErrors.add(`[JS error] ${err.message.substring(0, 200)}`);
  });

  page.on("requestfailed", (req) => {
    const key = new URL(req.url()).pathname;
    if (!failedRequests.has(key)) {
      failedRequests.set(
        key,
        `${req.method()} ${req.url()} — ${req.failure()?.errorText}`,
      );
    }
  });

  page.on("response", (res) => {
    if (res.status() >= 400) {
      httpErrors.push({ url: res.url(), status: res.status() });
    }
  });

  return { consoleErrors, failedRequests, httpErrors };
}

test.describe("NimbusIQ Frontend E2E", () => {
  test("homepage loads without errors", async ({ page }) => {
    const { consoleErrors, failedRequests, httpErrors } =
      attachErrorListeners(page);

    const response = await page.goto("/", {
      waitUntil: "networkidle",
      timeout: 30000,
    });
    expect(response?.status()).toBe(200);

    await expect(page.locator("text=NimbusIQ Dashboard")).toBeVisible({
      timeout: 10000,
    });
    await expect(
      page.locator("text=Azure infrastructure governance"),
    ).toBeVisible();

    expect(consoleErrors.size).toBe(0);
    expect(failedRequests.size).toBe(0);
    expect(httpErrors.length).toBe(0);
  });

  test("navigation sidebar links render", async ({ page }) => {
    await page.goto("/", { waitUntil: "networkidle", timeout: 30000 });

    for (const label of [
      "Dashboard",
      "Groups",
      "Recs",
      "Timeline",
      "Drift",
      "AI Chat",
      "Agents",
    ]) {
      await expect(page.locator(`text=${label}`).first()).toBeVisible();
    }
  });

  test("service groups page loads", async ({ page }) => {
    const { httpErrors } = attachErrorListeners(page);

    await page.goto("/", { waitUntil: "networkidle", timeout: 30000 });
    await page.locator("text=Groups").first().click();
    await page.waitForTimeout(3000);

    expect(httpErrors.filter((e) => e.status >= 500).length).toBe(0);
  });

  test("recommendations page loads", async ({ page }) => {
    const { httpErrors } = attachErrorListeners(page);

    await page.goto("/", { waitUntil: "networkidle", timeout: 30000 });
    await page.locator("text=Recs").first().click();
    await page.waitForTimeout(3000);

    expect(httpErrors.filter((e) => e.status >= 500).length).toBe(0);
  });

  test("API health endpoints return 200", async ({ page }) => {
    const apiBase = process.env.API_URL ?? "http://localhost:5145";

    for (const path of ["/health/ready", "/health/live"]) {
      const response = await page.request.get(`${apiBase}${path}`);
      expect(response.status()).toBe(200);
    }
  });

  test("no infinite API call loops", async ({ page }) => {
    const apiCalls: string[] = [];

    page.on("request", (req) => {
      if (req.url().includes("/api/v1/")) {
        apiCalls.push(new URL(req.url()).pathname);
      }
    });

    await page.goto("/", { waitUntil: "networkidle", timeout: 30000 });
    await page.waitForTimeout(5000);

    const callCounts = new Map<string, number>();
    for (const path of apiCalls) {
      callCounts.set(path, (callCounts.get(path) ?? 0) + 1);
    }

    for (const [path, count] of callCounts) {
      // Each endpoint should be called at most a few times (initial + retry), not dozens
      expect(
        count,
        `${path} called ${count} times — possible infinite loop`,
      ).toBeLessThan(10);
    }
  });
});
