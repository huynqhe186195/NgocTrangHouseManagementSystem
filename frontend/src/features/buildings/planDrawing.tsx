import { formatMetres, type Box } from '../../lib/floorGeometry';

/**
 * The architectural primitives both floor-plan screens draw with. The
 * read-only plan and the designer must produce the same drawing, so the wall
 * ring, the door opening and the dimension rules live here rather than in
 * either screen.
 */

export const WALL_COLOR = '#3d3d3d';
export const SELECTED_STROKE = '#1d4ed8';
const RULE_COLOR = '#94a3b8';
const RULE_TEXT = '#64748b';

/** Wall thickness in metres, capped so it never swallows a small room. */
export function wallThickness(box: Box): number {
  const unit = Math.min(box.width, box.height);
  return Math.max(0.06, Math.min(0.16, unit * 0.06));
}

/** Door opening on the bottom wall, wide enough to read but never the whole side. */
export function doorWidth(box: Box): number {
  return Math.min(box.width * 0.38, Math.max(0.7, Math.min(1.1, box.width * 0.3)));
}

/** Four walls with a gap in the middle of the bottom edge, ready for the door. */
export function wallPath(box: Box, wall: number, door: number): string {
  const inset = wall / 2;
  const left = box.x + inset;
  const right = box.x + box.width - inset;
  const top = box.y + inset;
  const bottom = box.y + box.height - inset;
  const doorStart = box.x + (box.width - door) / 2;
  const doorEnd = doorStart + door;

  return (
    `M ${left} ${top} H ${right} V ${bottom} H ${doorEnd} ` +
    `M ${doorStart} ${bottom} H ${left} Z`
  );
}

/** The quarter-circle leaf that shows which way the door swings. */
export function doorArcPath(box: Box, wall: number, door: number): string {
  const hingeX = box.x + (box.width - door) / 2;
  const bottom = box.y + box.height - wall / 2;
  return `M ${hingeX} ${bottom} a ${door} ${door} 0 0 1 ${door} 0`;
}

/**
 * Metre ticks along the top and left edges of the drawing, mirroring the
 * dimension lines in the mockup. The step widens with zoom so labels never
 * collide.
 */
export function PlanRulers({
  viewBox,
  step,
  showUnit = true,
}: {
  viewBox: { x: number; y: number; width: number; height: number };
  step: number;
  showUnit?: boolean;
}) {
  const fontSize = Math.max(0.26, viewBox.width * 0.012);
  const tick = fontSize * 0.5;

  const xTicks: number[] = [];
  for (
    let value = Math.ceil(viewBox.x / step) * step;
    value <= viewBox.x + viewBox.width;
    value += step
  ) {
    xTicks.push(value);
  }
  const yTicks: number[] = [];
  for (
    let value = Math.ceil(viewBox.y / step) * step;
    value <= viewBox.y + viewBox.height;
    value += step
  ) {
    yTicks.push(value);
  }

  return (
    <g pointerEvents="none" fontFamily="Inter, sans-serif">
      {xTicks.map((value) => (
        <g key={`x${value}`}>
          <line
            x1={value}
            y1={viewBox.y}
            x2={value}
            y2={viewBox.y + tick}
            stroke={RULE_COLOR}
            strokeWidth={fontSize * 0.06}
          />
          <text
            x={value}
            y={viewBox.y + tick * 2.1}
            textAnchor="middle"
            fontSize={fontSize}
            fill={RULE_TEXT}
          >
            {formatTick(value)}
          </text>
        </g>
      ))}
      {yTicks.map((value) => (
        <g key={`y${value}`}>
          <line
            x1={viewBox.x}
            y1={value}
            x2={viewBox.x + tick}
            y2={value}
            stroke={RULE_COLOR}
            strokeWidth={fontSize * 0.06}
          />
          <text
            x={viewBox.x + tick * 1.5}
            y={value + fontSize * 0.35}
            fontSize={fontSize}
            fill={RULE_TEXT}
          >
            {formatTick(value)}
          </text>
        </g>
      ))}
      {showUnit && (
        <text
          x={viewBox.x + viewBox.width - fontSize * 0.4}
          y={viewBox.y + tick * 2.1}
          textAnchor="end"
          fontSize={fontSize}
          fill={RULE_TEXT}
        >
          (m)
        </text>
      )}
    </g>
  );
}

export function formatTick(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

/** Keeps ruler labels readable: a tick every 1, 2, 5, 10, 20 or 50 m. */
export function tickStep(bounds: { width: number }, zoom: number): number {
  const perTick = bounds.width / zoom / 12;
  for (const candidate of [1, 2, 5, 10, 20, 50]) {
    if (perTick <= candidate) {
      return candidate;
    }
  }
  return 100;
}

/**
 * Overall width and height of the drawing, measured from the same bounds the
 * plan is drawn with — so the number on the rule is the real extent of the
 * saved geometry, not a decorative annotation.
 */
export function PlanDimensions({
  bounds,
  inset,
}: {
  bounds: { minX: number; minY: number; maxX: number; maxY: number; width: number; height: number };
  inset: number;
}) {
  const fontSize = Math.max(0.26, Math.min(bounds.width, bounds.height) * 0.03);
  const stroke = fontSize * 0.07;
  const arrow = fontSize * 0.42;

  const ruleY = bounds.maxY + inset;
  const ruleX = bounds.minX - inset;

  return (
    <g pointerEvents="none" fontFamily="Inter, sans-serif" stroke={RULE_COLOR}>
      {/* Bottom rule: extension lines, the rule itself, arrowheads both ends. */}
      <line
        x1={bounds.minX}
        y1={bounds.maxY}
        x2={bounds.minX}
        y2={ruleY + arrow}
        strokeWidth={stroke}
      />
      <line
        x1={bounds.maxX}
        y1={bounds.maxY}
        x2={bounds.maxX}
        y2={ruleY + arrow}
        strokeWidth={stroke}
      />
      <line
        x1={bounds.minX}
        y1={ruleY}
        x2={bounds.maxX}
        y2={ruleY}
        strokeWidth={stroke}
      />
      <path
        d={`M ${bounds.minX} ${ruleY} l ${arrow} ${-arrow * 0.45} v ${arrow * 0.9} Z`}
        fill={RULE_COLOR}
        stroke="none"
      />
      <path
        d={`M ${bounds.maxX} ${ruleY} l ${-arrow} ${-arrow * 0.45} v ${arrow * 0.9} Z`}
        fill={RULE_COLOR}
        stroke="none"
      />
      <text
        x={bounds.minX + bounds.width / 2}
        y={ruleY - arrow * 0.45}
        textAnchor="middle"
        fontSize={fontSize}
        fill={RULE_TEXT}
        stroke="none"
      >
        {formatMetres(bounds.width)}
      </text>

      {/* Left rule, rotated so the label reads bottom-up like a drawing sheet. */}
      <line
        x1={bounds.minX}
        y1={bounds.minY}
        x2={ruleX - arrow}
        y2={bounds.minY}
        strokeWidth={stroke}
      />
      <line
        x1={bounds.minX}
        y1={bounds.maxY}
        x2={ruleX - arrow}
        y2={bounds.maxY}
        strokeWidth={stroke}
      />
      <line
        x1={ruleX}
        y1={bounds.minY}
        x2={ruleX}
        y2={bounds.maxY}
        strokeWidth={stroke}
      />
      <path
        d={`M ${ruleX} ${bounds.minY} l ${-arrow * 0.45} ${arrow} h ${arrow * 0.9} Z`}
        fill={RULE_COLOR}
        stroke="none"
      />
      <path
        d={`M ${ruleX} ${bounds.maxY} l ${-arrow * 0.45} ${-arrow} h ${arrow * 0.9} Z`}
        fill={RULE_COLOR}
        stroke="none"
      />
      <text
        x={ruleX - arrow * 0.5}
        y={bounds.minY + bounds.height / 2}
        textAnchor="middle"
        fontSize={fontSize}
        fill={RULE_TEXT}
        stroke="none"
        transform={`rotate(-90 ${ruleX - arrow * 0.5} ${bounds.minY + bounds.height / 2})`}
      >
        {formatMetres(bounds.height)}
      </text>
    </g>
  );
}

/** North arrow, sitting in the corner of the viewable area like the mockup. */
export function PlanCompass({
  viewBox,
  size,
}: {
  viewBox: { x: number; y: number; width: number; height: number };
  size: number;
}) {
  // Kept clear of the top ruler, which draws its labels just inside the frame.
  const cx = viewBox.x + viewBox.width - size * 1.4;
  const cy = viewBox.y + size * 1.75;
  const r = size * 0.36;

  return (
    <g pointerEvents="none" fontFamily="Inter, sans-serif">
      <circle
        cx={cx}
        cy={cy}
        r={r}
        fill="#ffffff"
        fillOpacity={0.92}
        stroke="#cbd5e1"
        strokeWidth={size * 0.014}
      />
      <path
        d={`M ${cx} ${cy - r * 0.72} L ${cx + r * 0.42} ${cy + r * 0.52} L ${cx} ${cy + r * 0.2} L ${cx - r * 0.42} ${cy + r * 0.52} Z`}
        fill="#334155"
      />
      <text
        x={cx}
        y={cy - r - size * 0.06}
        textAnchor="middle"
        fontSize={size * 0.2}
        fontWeight={700}
        fill="#334155"
      >
        N
      </text>
    </g>
  );
}
