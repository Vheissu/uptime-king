import { describe, expect, it } from 'vitest';
import { responseChart } from './response-chart';

describe('Response chart', () => {
  it('positions a single recent sample near the right edge instead of 24 hours ago', () => {
    const chart = responseChart(
      [{ at: '2026-10-12T11:00:00Z', averageMs: 100 }],
      '2026-10-12T12:00:00Z',
    );
    expect(chart.segments[0].single?.x).toBe(617);
  });
  it('shows gaps and preserves elapsed time between non-adjacent samples', () => {
    const chart = responseChart(
      [
        { at: '2026-10-12T00:00:00Z', averageMs: 100 },
        { at: '2026-10-12T01:00:00Z', averageMs: 100 },
        { at: '2026-10-12T10:00:00Z', averageMs: 100 },
      ],
      '2026-10-12T12:00:00Z',
    );
    expect(chart.segments).toHaveLength(2);
    expect(chart.segments[0].points.split(' ').map((point) => Number(point.split(',')[0]))).toEqual(
      [342, 367],
    );
    expect(chart.segments[1].single?.x).toBe(592);
  });
  it('does not draw an invented line for an empty history', () => {
    expect(responseChart([], '2026-10-12T12:00:00Z').segments).toEqual([]);
  });
});
