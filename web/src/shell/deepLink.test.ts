import { describe, expect, it } from "vitest";
import { buildDeepLink, parseDeepLink } from "./deepLink";

describe("deep links", () => {
  it("round-trips an object link", () => {
    const link = buildDeepLink({ kind: "object", connectionId: "c1", objectRef: "Table:public/people" });
    expect(parseDeepLink(link)).toEqual({
      kind: "object", connectionId: "c1", objectRef: "Table:public/people",
    });
  });

  it("round-trips a saved query link", () => {
    const link = buildDeepLink({ kind: "query", connectionId: "c1", savedQueryId: "q7" });
    expect(parseDeepLink(link)).toEqual({ kind: "query", connectionId: "c1", savedQueryId: "q7" });
  });

  it("survives an object reference full of slashes", () => {
    const objectRef = "Table:some/deep/path/people";
    const link = buildDeepLink({ kind: "object", connectionId: "c1", objectRef });
    expect(parseDeepLink(link)).toMatchObject({ objectRef });
  });

  it("parses a full url, not only a bare hash", () => {
    expect(parseDeepLink("https://wds.example/app#/c/c1/o/Table%3Apeople")).toMatchObject({
      kind: "object", objectRef: "Table:people",
    });
  });

  it("returns null for an unknown path", () => {
    expect(parseDeepLink("#/nope/c1/o/x")).toBeNull();
    expect(parseDeepLink("#/c/c1/z/x")).toBeNull();
    expect(parseDeepLink("")).toBeNull();
  });

  it("returns null when the connection id is missing", () => {
    expect(parseDeepLink("#/c//o/Table%3Apeople")).toBeNull();
  });
});
