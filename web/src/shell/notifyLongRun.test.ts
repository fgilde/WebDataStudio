import { describe, it, expect, vi, afterEach } from "vitest";
import { describeRun, shouldNotify, notifyLongRun } from "./notifyLongRun";

describe("when a finished run is worth an interruption", () => {
  it("only once it took long enough and nobody is watching", () => {
    expect(shouldNotify(45_000, 30, true)).toBe(true);

    // Watched: they saw it finish.
    expect(shouldNotify(45_000, 30, false)).toBe(false);
    // Quick: not worth a notification even if they looked away.
    expect(shouldNotify(2_000, 30, true)).toBe(false);
  });

  it("is off when the preference says zero", () => {
    expect(shouldNotify(10 * 60_000, 0, true)).toBe(false);
  });
});

describe("what the notification says", () => {
  it("names the rows and the time", () => {
    expect(describeRun(1_500, 42, null)).toBe("42 rows in 1.5 s");
    expect(describeRun(95_000, 0, null)).toBe("0 rows in 1 min 35 s");
  });

  it("says a statement that returned nothing finished", () => {
    expect(describeRun(2_000, null, null)).toBe("finished in 2.0 s");
  });

  it("leads with the failure, because that is the point of the message", () => {
    expect(describeRun(61_000, null, "deadlock detected")).toBe("failed after 1 min 1 s");
  });
});

describe("sending one", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("does nothing in a browser that has no notifications", async () => {
    vi.stubGlobal("Notification", undefined);

    expect(await notifyLongRun("Query finished", "1 row")).toBe(false);
  });

  it("asks for permission the first time one would be sent", async () => {
    const request = vi.fn().mockResolvedValue("granted");
    const made = vi.fn();

    vi.stubGlobal("Notification", Object.assign(made, {
      permission: "default", requestPermission: request,
    }));

    expect(await notifyLongRun("Query finished", "1 row")).toBe(true);
    expect(request).toHaveBeenCalled();
  });

  it("takes no for an answer", async () => {
    vi.stubGlobal("Notification", Object.assign(vi.fn(), {
      permission: "denied", requestPermission: vi.fn(),
    }));

    expect(await notifyLongRun("Query finished", "1 row")).toBe(false);
  });
});
