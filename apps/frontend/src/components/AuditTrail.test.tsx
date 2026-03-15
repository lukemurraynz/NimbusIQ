import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AuditTrail } from "./AuditTrail";
import { controlPlaneApi } from "../services/controlPlaneApi";

describe("AuditTrail", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("renders audit rows safely when eventName is missing", async () => {
    vi.spyOn(controlPlaneApi, "getAuditEvents").mockResolvedValue({
      value: [
        {
          id: "evt-1",
          eventName: undefined,
          actorType: undefined,
          actorId: undefined,
          timestamp: new Date().toISOString(),
        },
      ],
    } as unknown as Awaited<ReturnType<typeof controlPlaneApi.getAuditEvents>>);

    render(
      <AuditTrail
        entityType="recommendation"
        entityId="rec-1"
        accessToken="token"
      />,
    );

    await waitFor(() => {
      expect(screen.getByText("Unknown event")).toBeInTheDocument();
      expect(screen.getByText("system: unknown")).toBeInTheDocument();
    });
  });
});
