import { useEffect } from "react";
import {
  Modal, parseThemeColor, useComputedColorScheme, useMantineTheme,
} from "@mantine/core";

const WIDGETS_URL = "https://connect.gilde.org/widgets/v1.js";

declare module "react" {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace JSX {
    interface IntrinsicElements {
      "gilde-contact": React.HTMLAttributes<HTMLElement> & Record<string, string>;
      "gilde-support": React.HTMLAttributes<HTMLElement> & Record<string, string>;
    }
  }
}

export type GildeWidget = "contact" | "support";

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
        <gilde-contact {...common} title="Contact WebDataStudio">Contact WebDataStudio</gilde-contact>
      )}
      {widget === "support" && (
        <gilde-support {...common} show-support-hint="false" support-layout="rows"
          show-support-icons="true" show-support-qr="true"
          title="Support WebDataStudio">Support WebDataStudio</gilde-support>
      )}
    </Modal>
  );
}
