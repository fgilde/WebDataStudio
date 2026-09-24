// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { GildeConnectModal } from "./GildeConnect";

// What the widget script registers: `inline` and friends read the attributes and cannot be set.
// React 19 writes a property instead of an attribute once an element is defined, and crashed here.
class Widget extends HTMLElement {
  get inline() { return this.hasAttribute("inline"); }
  get theme() { return this.getAttribute("theme"); }
}
customElements.define("gilde-contact", class extends Widget {});
customElements.define("gilde-support", class extends Widget {});

const draw = (widget: "contact" | "support") => render(
  <MantineProvider><GildeConnectModal widget={widget} onClose={() => {}} /></MantineProvider>);

describe("GildeConnectModal", () => {
  it("renders a widget whose element is already defined, as attributes", () => {
    draw("support");

    const element = document.querySelector("gilde-support")!;
    expect(element.hasAttribute("inline")).toBe(true);
    expect(element.getAttribute("project")).toBe("fgilde/WebDataStudio");
    expect(element.getAttribute("show-footer")).toBe("false");
    expect(element.getAttribute("language")).toBe("en");
    expect(element.textContent).toBe("Support WebDataStudio");
  });

  it("renders the other one after the first", () => {
    draw("contact");
    expect(document.querySelector("gilde-contact")?.getAttribute("title")).toBe("Contact WebDataStudio");
  });
});
