import { Component, computed, input } from '@angular/core';
import { CommonModule } from '@angular/common';

export interface ResponsePoint {
  at: string;
  averageMs: number;
}
const Hour = 3_600_000;

// Use elapsed time on the x-axis and leave gaps where no checks were recorded.
export function responseChart(points: ResponsePoint[], asOf: string) {
  const end = Date.parse(asOf);
  const start = end - 24 * Hour;
  const max = Math.max(400, ...points.map((point) => point.averageMs)) * 1.15;
  const segments: { x: number; y: number; at: number }[][] = [];
  for (const point of [...points].sort((a, b) => Date.parse(a.at) - Date.parse(b.at))) {
    const at = Date.parse(point.at);
    const position = {
      at,
      x: 42 + Math.max(0, Math.min(1, (at - start) / (end - start))) * 600,
      y: 175 - (point.averageMs / max) * 150,
    };
    const segment = segments.at(-1);
    if (!segment || at - segment[segment.length - 1].at > Hour) segments.push([position]);
    else segment.push(position);
  }
  return {
    start,
    end,
    max,
    segments: segments.map((segment) => ({
      points: segment.map((point) => `${point.x},${point.y}`).join(' '),
      area: `${segment[0].x},175 ${segment.map((point) => `${point.x},${point.y}`).join(' ')} ${segment[segment.length - 1].x},175`,
      single: segment.length === 1 ? segment[0] : null,
    })),
  };
}

@Component({
  selector: 'uk-response-chart',
  imports: [CommonModule],
  host: { class: 'chart-panel' },
  templateUrl: './response-chart.html',
})
export class ResponseChart {
  readonly points = input.required<ResponsePoint[]>();
  readonly asOf = input.required<string>();
  readonly averageMs = input.required<number | null>();
  readonly chart = computed(() => responseChart(this.points(), this.asOf()));
}
