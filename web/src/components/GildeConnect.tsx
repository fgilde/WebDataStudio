import { useEffect, useRef } from "react";
import {
  Modal, parseThemeColor, useComputedColorScheme, useMantineTheme,
} from "@mantine/core";

const WIDGETS_URL = "https://connect.gilde.org/widgets/v1.js";

export type GildeWidget = "contact" | "support";

/// The widget element, made by hand with attributes only. React 19 writes a property instead of an
/// attribute once a custom element is defined, and the widget's `inline` is a getter: the second
/// form opened threw, and took the whole page with it.
function WidgetElement({ tag, attributes, label }: {
  tag: string; attributes: Record<string, string>; label: string;
}) {
  const host = useRef<HTMLDivElement>(null);
  const key = JSON.stringify(attributes);

  useEffect(() => {
    const element = document.createElement(tag);
    for (const [name, value] of Object.entries(JSON.parse(key) as Record<string, string>))
      element.setAttribute(name, value);
    element.textContent = label;
    host.current?.append(element);
    return () => element.remove();
  }, [tag, key, label]);

  return <div ref={host} />;
}

/// gilde.org's contact and support forms, inline in a modal: the widget's own button would open a
/// second dialog on top of the drawer. The script is only fetched once somebody asks for a form, so
/// a studio without internet never waits on it.
export function GildeConnectModal({ widget, onClose }: {
  widget: GildeWidget | null;
  onClose: () => void;
}) {
  const theme = useMantineTheme();
  const scheme = useComputedColorScheme("dark");
  // The accent the studio is wearing right now, so the form looks like part of it.
  const accent = parseThemeColor({ color: theme.primaryColor, theme, colorScheme: scheme }).value;

  useEffect(() => {
    if (widget) import(/* @vite-ignore */ WIDGETS_URL).catch(() => {});
  }, [widget]);

  const common = {
    project: "fgilde/WebDataStudio",
    widget: widget ?? "contact",
    inline: "",
    theme: scheme,
    accent,
    language: "en",
    width: "560",
    radius: "18",
    padding: "28",
    "show-logo": "true",
    "show-description": "false",
    "show-homepage": "true",
    "show-preview-notice": "false",
    "show-footer": "false",
  };

  return (
    // No frame of its own: the widget is the card, and Escape or a click beside it closes it.
    <Modal opened={widget !== null} onClose={onClose} size={600} centered withCloseButton={false}
      aria-label={widget === "support" ? "Support WebDataStudio" : "Contact WebDataStudio"}
      styles={{
        content: { background: "transparent", boxShadow: "none" },
        body: { padding: 0, display: "flex", justifyContent: "center" },
      }}>
      {widget === "contact" && (
        <WidgetElement tag="gilde-contact" label="Contact WebDataStudio"
          attributes={{ ...common, title: "Contact WebDataStudio" }} />
      )}
      {widget === "support" && (
        <WidgetElement tag="gilde-support" label="Support WebDataStudio"
          attributes={{
            ...common, "show-support-hint": "false", "support-layout": "rows",
            "show-support-icons": "true", "show-support-qr": "true", title: "Support WebDataStudio",
          }} />
      )}
    </Modal>
  );
}
