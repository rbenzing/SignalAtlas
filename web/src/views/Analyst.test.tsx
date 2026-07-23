import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Analyst from "./Analyst";
import * as api from "../api";
import type { AnalystAnswer } from "../api";

afterEach(() => {
  vi.restoreAllMocks();
});

const ANSWER: AnalystAnswer = {
  text: "Device dev-1 last seen 2 minutes ago on LoRa.",
  citations: [{ feature: "device", value: "dev-1", weight: 1 }],
  mode: "offline",
  queryType: "WhatChanged",
};

describe("Analyst", () => {
  it("disables Send when the input is blank", () => {
    render(<Analyst />);
    expect(screen.getByRole("button", { name: /send/i })).toBeDisabled();
  });

  it("submits a question and renders the answer, mode, queryType and citations", async () => {
    vi.spyOn(api, "postAnalystQuery").mockResolvedValue(ANSWER);
    render(<Analyst />);

    const input = screen.getByPlaceholderText(/ask about signals/i);
    await userEvent.type(input, "What changed today?");
    const send = screen.getByRole("button", { name: /send/i });
    expect(send).toBeEnabled();
    await userEvent.click(send);

    expect(api.postAnalystQuery).toHaveBeenCalledWith("What changed today?");
    await screen.findByText(ANSWER.text);
    expect(screen.getByText(/offline/i)).toBeInTheDocument();
    expect(screen.getByText(/WhatChanged/i)).toBeInTheDocument();
    expect(screen.getByText(/device=dev-1/i)).toBeInTheDocument();
  });

  it("shows 'No records cited.' when citations is empty", async () => {
    vi.spyOn(api, "postAnalystQuery").mockResolvedValue({
      text: "None found.",
      citations: [],
      mode: "offline",
      queryType: "Unsupported",
    });
    render(<Analyst />);

    await userEvent.type(screen.getByPlaceholderText(/ask about signals/i), "huh?");
    await userEvent.click(screen.getByRole("button", { name: /send/i }));

    await screen.findByText("None found.");
    expect(screen.getByText(/no records cited/i)).toBeInTheDocument();
  });

  it("fills the input when an example prompt chip is clicked", async () => {
    render(<Analyst />);
    const chip = screen.getByText("What changed today?");
    await userEvent.click(chip);
    expect(screen.getByPlaceholderText(/ask about signals/i)).toHaveValue("What changed today?");
  });

  it("shows an inline error without losing the input on request failure", async () => {
    vi.spyOn(api, "postAnalystQuery").mockRejectedValue(new Error("POST /analyst/query failed: 500"));
    render(<Analyst />);

    await userEvent.type(screen.getByPlaceholderText(/ask about signals/i), "What changed today?");
    await userEvent.click(screen.getByRole("button", { name: /send/i }));

    await waitFor(() => expect(screen.getByText(/failed/i)).toBeInTheDocument());
  });

  it("submits on Enter key press", async () => {
    vi.spyOn(api, "postAnalystQuery").mockResolvedValue(ANSWER);
    render(<Analyst />);

    const input = screen.getByPlaceholderText(/ask about signals/i);
    await userEvent.type(input, "What changed today?{enter}");

    await screen.findByText(ANSWER.text);
    expect(api.postAnalystQuery).toHaveBeenCalledWith("What changed today?");
  });
});
