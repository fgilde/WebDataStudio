import type { DiagramEdgeDto, DiagramNodeDto } from "../api";
import { heightOf, type PlacedNode } from "./layout";
import { MAX_ROWS } from "./TableNode";

const escapeText = (value: string) =>
  value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

export interface DrawnTable { node: DiagramNodeDto; place: PlacedNode }

/// A standalone SVG of the same boxes and lines: it has to survive outside the app, so it carries
/// its own background and explicit colours instead of the theme's CSS variables.
export function buildSvg(tables: DrawnTable[], edges: DiagramEdgeDto[], dark: boolean): string {
  const ink = dark ? "#e6e6e6" : "#1a1a1a";
  const line = dark ? "#7a7a7a" : "#999999";
  const paper = dark ? "#1a1b1e" : "#ffffff";
  const head = dark ? "#2c2e33" : "#f1f3f5";

  const width = Math.max(...tables.map(t => t.place.x + t.place.width), 200) + 40;
  const height = Math.max(...tables.map(t => t.place.y + t.place.height), 200) + 40;
  const byId = new Map(tables.map(t => [t.node.id, t]));

  const connections = edges.filter(e => byId.has(e.source) && byId.has(e.target)).map(edge => {
    const from = byId.get(edge.source)!.place;
    const to = byId.get(edge.target)!.place;
    return `<path d="M ${from.x + from.width} ${from.y + 14} C ${from.x + from.width + 40} ${from.y + 14}, ` +
      `${to.x - 40} ${to.y + 14}, ${to.x} ${to.y + 14}" fill="none" stroke="${line}"/>`;
  });

  const boxes = tables.map(({ node, place }) => {
    const rows = node.columns.slice(0, MAX_ROWS).map((column, index) =>
      `<text x="${place.x + 8}" y="${place.y + 42 + index * 18}" font-size="11" fill="${ink}">` +
      `${escapeText(column.primaryKey ? `${column.name} (pk)` : column.name)}` +
      `</text>`);

    return [
      `<rect x="${place.x}" y="${place.y}" width="${place.width}" height="${place.height}" `,
      `fill="${paper}" stroke="${line}" rx="6"/>`,
      `<rect x="${place.x}" y="${place.y}" width="${place.width}" height="26" fill="${head}" rx="6"/>`,
      `<text x="${place.x + 8}" y="${place.y + 18}" font-size="12" font-weight="600" fill="${ink}">`,
      `${escapeText(node.name)}</text>`,
      rows.join(""),
    ].join("");
  });

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" ` +
    `viewBox="0 0 ${width} ${height}" font-family="sans-serif">` +
    `<rect width="${width}" height="${height}" fill="${paper}"/>` +
    `${connections.join("")}${boxes.join("")}</svg>`;
}

export const download = (blob: Blob, name: string) => {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
};

export const downloadSvg = (svg: string, name = "diagram.svg") =>
  download(new Blob([svg], { type: "image/svg+xml" }), name);

/// PNG goes through a canvas: the SVG is drawn into it at 2x for a readable raster.
export function downloadPng(svg: string, name = "diagram.png"): Promise<void> {
  return new Promise((resolve, reject) => {
    const size = /width="(\d+)" height="(\d+)"/.exec(svg);
    const width = Number(size?.[1] ?? 800);
    const height = Number(size?.[2] ?? 600);

    const image = new Image();
    // A data URL keeps the canvas untainted, which a blob URL from another origin would not.
    image.src = "data:image/svg+xml;base64," + btoa(unescape(encodeURIComponent(svg)));

    image.onload = () => {
      const canvas = document.createElement("canvas");
      canvas.width = width * 2;
      canvas.height = height * 2;

      const context = canvas.getContext("2d");
      if (!context) { reject(new Error("this browser has no 2d canvas")); return; }

      context.scale(2, 2);
      context.drawImage(image, 0, 0);
      canvas.toBlob(blob => {
        if (blob) download(blob, name);
        resolve();
      }, "image/png");
    };

    image.onerror = () => reject(new Error("the diagram could not be rasterised"));
  });
}

export const placementOf = (node: DiagramNodeDto, x: number, y: number): PlacedNode =>
  ({ id: node.id, x, y, width: 220, height: heightOf(node) });
