// The parts of ECharts that src/Songler/wwwroot/js/charts.js uses. Adding a chart kind or option that needs another
// series, component or feature? Add it here and run `npm run build` (see CLAUDE.md). A missing part doesn't throw:
// the series or option just doesn't draw, so check the chart after.
import * as echarts from 'echarts/core';
import { BarChart, LineChart, HeatmapChart, CustomChart } from 'echarts/charts';
import { GridComponent, TooltipComponent, CalendarComponent, MarkLineComponent } from 'echarts/components';
import { LabelLayout } from 'echarts/features';
import { SVGRenderer } from 'echarts/renderers';

echarts.use([
    BarChart, LineChart, HeatmapChart, CustomChart,          // weekly bars, tempo line, practice calendar, tempo whiskers
    GridComponent, TooltipComponent, CalendarComponent, MarkLineComponent, // axes, tooltips, calendar, goal lines
    LabelLayout,                                             // axisLabel.hideOverlap
    SVGRenderer,                                             // charts.js inits with renderer: 'svg'
]);

// charts.js loads this as a plain script and reads window.echarts, like the full build
window.echarts = echarts;
