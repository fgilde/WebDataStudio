import { useEffect, useRef } from "react";
import * as echarts from "echarts/core";
import {
  BarChart, GaugeChart, HeatmapChart, LineChart, PieChart, SankeyChart, TreemapChart,
} from "echarts/charts";
import {
  GridComponent, LegendComponent, TooltipComponent, VisualMapComponent,
} from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";

// Only what the widgets draw. The full bundle is a megabyte, and a dashboard does not need a
// candlestick to be a dashboard.
echarts.use([
  BarChart, LineChart, PieChart, TreemapChart, HeatmapChart, SankeyChart, GaugeChart,
  GridComponent, TooltipComponent, LegendComponent, VisualMapComponent,
  CanvasRenderer,
]);

/// One chart, mounted, resized and disposed.
///
/// The option is rebuilt outside this: what changes here is only "draw that", so a widget whose
/// rows arrived does not remount the canvas — it sets the option, which is what makes a refreshing
/// dashboard cheap.
export function EChart({ option }: { option: Record<string, unknown> }) {
  const host = useRef<HTMLDivElement>(null);
  const chart = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    if (!host.current) return;

    chart.current = echarts.init(host.current, undefined, { renderer: "canvas" });

    // A widget is a box somebody drags the corner of, so the chart follows the box rather than the
    // window.
    const observer = new ResizeObserver(() => chart.current?.resize());
    observer.observe(host.current);

    return () => {
      observer.disconnect();
      chart.current?.dispose();
      chart.current = null;
    };
  }, []);

  useEffect(() => {
    // `true` replaces the option rather than merging it: a widget whose type changed must not keep
    // the axes of the one it was.
    chart.current?.setOption(option, true);
  }, [option]);

  return <div ref={host} style={{ width: "100%", height: "100%" }} />;
}
