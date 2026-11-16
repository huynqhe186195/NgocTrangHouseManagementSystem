import { formatMetres, type Box } from '../../lib/floorGeometry';
import type { FloorLayoutItemKind } from '../../lib/api/types';

/**
 * The architectural primitives both floor-plan screens draw with. The
 * read-only plan and the designer must produce the same drawing, so the wall
 * ring, the door opening and the dimension rules live here rather than in
 * either screen.
 */

export const WALL_COLOR = '#2b2b2b';
export const SELECTED_STROKE = '#1d4ed8';
const RULE_COLOR = '#94a3b8';
const RULE_TEXT = '#64748b';
/** Hairlines inside a plan: stair treads, door leaves, railings. */
export const LINE_COLOR = '#8a939e';

/** Shared status palette, so every drawing of a floor reads the same colours. */
export const statusStyles: Record<number, { fill: string; stroke: string; label: string }> = {
  1: { fill: '#E9F7E7', stroke: '#9BD69B', label: 'Trống' },
  2: { fill: '#FDE2E1', stroke: '#F2A9A6', label: 'Đang thuê' },
  3: { fill: '#FDF0CC', stroke: '#EBCB7A', label: 'Đã đặt' },
  4: { fill: '#E8EEF5', stroke: '#B3C4D8', label: 'Bảo trì' },
  5: { fill: '#F1F5F9', stroke: '#CBD5E1', label: 'Ngừng dùng' },
};

/**
 * Wall thickness in metres for a partition between two rooms. The mockup sets
 * partitions noticeably lighter than the building envelope, so this stays at
 * roughly half the envelope's weight — otherwise the plan reads as a grid of
 * equally heavy boxes instead of a walled floor.
 */
export function wallThickness(box: Box): number {
  const unit = Math.min(box.width, box.height);
  return Math.max(0.1, Math.min(0.2, unit * 0.055));
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

/** The room's floor, inset so the wall band reads as masonry around it. */
export function innerBox(box: Box, wall: number): Box {
  const inset = wall / 2;
  return {
    x: box.x + inset,
    y: box.y + inset,
    width: Math.max(0.1, box.width - wall),
    height: Math.max(0.1, box.height - wall),
  };
}

/**
 * One straight run of the building envelope, with the side the band sits on.
 * `-1` is above/left of the footprint, `1` is below/right; the band always
 * lies outside the floor, so the drawn wall never eats into a room.
 */
export interface EnvelopeRun {
  orientation: 'h' | 'v';
  /** The footprint's own boundary line the band is fixed to. */
  at: number;
  from: number;
  to: number;
  side: 1 | -1;
}

const OUTLINE_EPSILON = 0.005;

function uniqueEdges(values: number[]): number[] {
  const sorted = [...values].sort((a, b) => a - b);
  const out: number[] = [];
  for (const value of sorted) {
    const last = out[out.length - 1];
    if (last === undefined || value - last > OUTLINE_EPSILON) {
      out.push(value);
    }
  }
  return out;
}

/**
 * The outline of the whole floor, not of one room.
 *
 * The mockup encloses the entire storey — rooms, corridor, stairwell — in one
 * continuous wall, the way a building is actually drawn. Stroking each room's
 * own edges cannot produce that: the corridor's ends and the gaps between
 * room blocks have no room to stroke them, so the envelope came out broken.
 *
 * The boxes are rasterised onto their own coordinate grid and every cell edge
 * that faces open space is kept, then collinear neighbours are merged back
 * into long runs. That handles the L-shapes and the notches between blocks
 * without needing a polygon clipper.
 *
 * `gap` closes slots narrower than itself before the outline is taken. Designed
 * plans leave a few tens of centimetres between blocks, which would otherwise
 * cut a sawtooth of 40 cm notches into a wall that a building simply does not
 * have; the mockup draws one clean footprint for the same reason.
 */
export function envelopeRuns(boxes: Box[], gap = 0): EnvelopeRun[] {
  if (boxes.length === 0) {
    return [];
  }
  const grow = Math.max(0, gap) / 2;
  const grown = boxes.map((box) => ({
    x: box.x - grow,
    y: box.y - grow,
    width: box.width + grow * 2,
    height: box.height + grow * 2,
  }));

  const xs = uniqueEdges(grown.flatMap((box) => [box.x, box.x + box.width]));
  const ys = uniqueEdges(grown.flatMap((box) => [box.y, box.y + box.height]));
  if (xs.length < 2 || ys.length < 2) {
    return [];
  }

  const cols = xs.length - 1;
  const rows = ys.length - 1;
  const covered: boolean[] = new Array<boolean>(cols * rows).fill(false);

  for (let row = 0; row < rows; row += 1) {
    for (let col = 0; col < cols; col += 1) {
      const cx = (xs[col]! + xs[col + 1]!) / 2;
      const cy = (ys[row]! + ys[row + 1]!) / 2;
      covered[row * cols + col] = grown.some(
        (box) =>
          cx > box.x + OUTLINE_EPSILON &&
          cx < box.x + box.width - OUTLINE_EPSILON &&
          cy > box.y + OUTLINE_EPSILON &&
          cy < box.y + box.height - OUTLINE_EPSILON,
      );
    }
  }

  const filled = (col: number, row: number): boolean =>
    col >= 0 && col < cols && row >= 0 && row < rows && covered[row * cols + col] === true;

  const horizontal: { at: number; from: number; to: number; side: 1 | -1 }[] = [];
  const vertical: { at: number; from: number; to: number; side: 1 | -1 }[] = [];

  for (let row = 0; row < rows; row += 1) {
    for (let col = 0; col < cols; col += 1) {
      if (!filled(col, row)) {
        continue;
      }
      if (!filled(col, row - 1)) {
        horizontal.push({ at: ys[row]!, from: xs[col]!, to: xs[col + 1]!, side: -1 });
      }
      if (!filled(col, row + 1)) {
        horizontal.push({ at: ys[row + 1]!, from: xs[col]!, to: xs[col + 1]!, side: 1 });
      }
      if (!filled(col - 1, row)) {
        vertical.push({ at: xs[col]!, from: ys[row]!, to: ys[row + 1]!, side: -1 });
      }
      if (!filled(col + 1, row)) {
        vertical.push({ at: xs[col + 1]!, from: ys[row]!, to: ys[row + 1]!, side: 1 });
      }
    }
  }

  return [
    ...mergeRuns(horizontal, 'h'),
    ...mergeRuns(vertical, 'v'),
  ];
}

function mergeRuns(
  runs: { at: number; from: number; to: number; side: 1 | -1 }[],
  orientation: 'h' | 'v',
): EnvelopeRun[] {
  const groups = new Map<string, { at: number; from: number; to: number; side: 1 | -1 }[]>();
  for (const run of runs) {
    const key = `${run.at}|${run.side}`;
    const bucket = groups.get(key);
    if (bucket) {
      bucket.push(run);
    } else {
      groups.set(key, [run]);
    }
  }

  const merged: EnvelopeRun[] = [];
  for (const bucket of groups.values()) {
    bucket.sort((a, b) => a.from - b.from);
    let current = { ...bucket[0]! };
    for (let index = 1; index < bucket.length; index += 1) {
      const next = bucket[index]!;
      if (next.from - current.to <= OUTLINE_EPSILON) {
        current.to = Math.max(current.to, next.to);
      } else {
        merged.push({ orientation, ...current });
        current = { ...next };
      }
    }
    merged.push({ orientation, ...current });
  }
  return merged;
}

/** The wall band a run draws, lying entirely outside the footprint. */
export function envelopeBand(run: EnvelopeRun, wall: number): Box {
  if (run.orientation === 'h') {
    return {
      x: run.from,
      y: run.side === -1 ? run.at - wall : run.at,
      width: run.to - run.from,
      height: wall,
    };
  }
  return {
    x: run.side === -1 ? run.at - wall : run.at,
    y: run.from,
    width: wall,
    height: run.to - run.from,
  };
}

const WINDOW_FILL = '#dce8f5';
const WINDOW_STROKE = '#aec3d9';

/**
 * Windows punched through an exterior run, spaced the way the mockup spaces
 * them. Only runs wide enough to hold a whole window get any, so a short
 * return beside a notch is left solid rather than split into slivers.
 */
function windowOpenings(
  run: EnvelopeRun,
  wall: number,
): { box: Box; frame: Box }[] {
  const span = run.to - run.from;
  const width = Math.min(2.2, Math.max(1.2, span * 0.32));
  const gap = 3.4;
  if (span < width + 0.8) {
    return [];
  }

  const count = Math.max(1, Math.floor((span - width) / gap) + 1);
  const used = width * count;
  const spare = span - used;
  const step = spare / (count + 1);
  const openings: { box: Box; frame: Box }[] = [];

  for (let index = 0; index < count; index += 1) {
    const start = run.from + step * (index + 1) + width * index;
    const inset = Math.min(wall * 0.22, 0.06);
    if (run.orientation === 'h') {
      const y = run.side === -1 ? run.at - wall : run.at;
      openings.push({
        box: { x: start + inset, y: y + inset, width: width - inset * 2, height: wall - inset * 2 },
        frame: { x: start, y, width, height: wall },
      });
    } else {
      const x = run.side === -1 ? run.at - wall : run.at;
      openings.push({
        box: { x: x + inset, y: start + inset, width: wall - inset * 2, height: width - inset * 2 },
        frame: { x, y: start, width: wall, height: width },
      });
    }
  }
  return openings;
}

/**
 * The building envelope: one continuous wall around everything the floor
 * actually occupies, with window openings on its exterior. This is the band
 * the mockup draws around the whole storey — it is the reason the plan reads
 * as a floor of a building rather than a scatter of separate boxes.
 */
export function PlanEnvelope({
  boxes,
  wall,
  selected,
  gap = 0.8,
}: {
  boxes: Box[];
  wall: number;
  selected?: boolean;
  /** Slots narrower than this are bridged, so the outline reads as one block. */
  gap?: number;
}) {
  const runs = envelopeRuns(boxes, gap);
  if (runs.length === 0) {
    return null;
  }
  const color = selected ? SELECTED_STROKE : WALL_COLOR;

  return (
    <g pointerEvents="none">
      {runs.map((run, index) => {
        const band = envelopeBand(run, wall);
        return (
          <rect
            key={`band-${index}`}
            x={band.x}
            y={band.y}
            width={band.width}
            height={band.height}
            fill={color}
          />
        );
      })}
      {runs.flatMap((run, index) =>
        windowOpenings(run, wall).map((opening, openingIndex) => (
          <g key={`win-${index}-${openingIndex}`}>
            <rect
              x={opening.frame.x}
              y={opening.frame.y}
              width={opening.frame.width}
              height={opening.frame.height}
              fill="none"
              stroke={selected ? SELECTED_STROKE : WINDOW_STROKE}
              strokeWidth={Math.max(0.02, wall * 0.12)}
            />
            <rect
              x={opening.box.x}
              y={opening.box.y}
              width={opening.box.width}
              height={opening.box.height}
              fill={WINDOW_FILL}
            />
          </g>
        )),
      )}
    </g>
  );
}

/* ------------------------------------------------------------------------ *
 * Floor objects.
 *
 * Each palette kind has its own architectural symbol rather than a plain
 * rectangle: a stairwell with treads, an elevator with its cross, a WC with
 * fixtures, a door with its swing. The drawing is shared by the read-only
 * plan, the designer and the thumbnail so all three agree.
 * ------------------------------------------------------------------------ */

const OBJECT_FILL = '#EFF3F8';
const OBJECT_WALL_RATIO = 0.07;

export interface LayoutObjectArtProps {
  box: Box;
  kind: FloorLayoutItemKind;
  /** Object walls are thinner than room walls; they are smaller boxes. */
  wall?: number;
  /** Bottom strip reserved for the caption, so the symbol sits above it. */
  caption?: number;
}

export function objectWall(box: Box): number {
  const unit = Math.min(box.width, box.height);
  return Math.max(0.07, Math.min(0.2, unit * OBJECT_WALL_RATIO));
}

/** Walls around an object, with no door opening. */
function closedWalls(box: Box, wall: number): string {
  const inset = wall / 2;
  const left = box.x + inset;
  const right = box.x + box.width - inset;
  const top = box.y + inset;
  const bottom = box.y + box.height - inset;
  return `M ${left} ${top} H ${right} V ${bottom} H ${left} Z`;
}

function StairArt({ box, wall }: { box: Box; wall: number }) {
  const inner = innerBox(box, wall);
  const horizontal = inner.width >= inner.height;
  const span = horizontal ? inner.width : inner.height;
  const treads = Math.max(4, Math.min(14, Math.round(span / 0.32)));
  const strokeWidth = Math.max(0.02, wall * 0.16);

  const steps = [];
  for (let index = 1; index < treads; index += 1) {
    const t = index / treads;
    steps.push(
      horizontal ? (
        <line
          key={index}
          x1={inner.x + inner.width * t}
          y1={inner.y}
          x2={inner.x + inner.width * t}
          y2={inner.y + inner.height}
          stroke={LINE_COLOR}
          strokeWidth={strokeWidth}
        />
      ) : (
        <line
          key={index}
          x1={inner.x}
          y1={inner.y + inner.height * t}
          x2={inner.x + inner.width}
          y2={inner.y + inner.height * t}
          stroke={LINE_COLOR}
          strokeWidth={strokeWidth}
        />
      ),
    );
  }

  // Direction of travel, as the mockup draws it down the middle of the flight.
  const midX = inner.x + inner.width / 2;
  const midY = inner.y + inner.height / 2;
  const head = Math.min(inner.width, inner.height) * 0.16;
  const from = horizontal ? inner.x + inner.width * 0.14 : inner.y + inner.height * 0.14;
  const to = horizontal ? inner.x + inner.width * 0.86 : inner.y + inner.height * 0.86;

  return (
    <>
      {steps}
      <line
        x1={horizontal ? from : midX}
        y1={horizontal ? midY : from}
        x2={horizontal ? to : midX}
        y2={horizontal ? midY : to}
        stroke={LINE_COLOR}
        strokeWidth={strokeWidth * 1.6}
      />
      <path
        d={
          horizontal
            ? `M ${to} ${midY} l ${-head} ${-head * 0.6} v ${head * 1.2} Z`
            : `M ${midX} ${to} l ${-head * 0.6} ${-head} h ${head * 1.2} Z`
        }
        fill={LINE_COLOR}
      />
    </>
  );
}

function ElevatorArt({ box, wall }: { box: Box; wall: number }) {
  const inner = innerBox(box, wall);
  const strokeWidth = Math.max(0.02, wall * 0.16);
  return (
    <>
      <line
        x1={inner.x}
        y1={inner.y}
        x2={inner.x + inner.width}
        y2={inner.y + inner.height}
        stroke={LINE_COLOR}
        strokeWidth={strokeWidth}
      />
      <line
        x1={inner.x + inner.width}
        y1={inner.y}
        x2={inner.x}
        y2={inner.y + inner.height}
        stroke={LINE_COLOR}
        strokeWidth={strokeWidth}
      />
    </>
  );
}

function ToiletArt({ box, wall }: { box: Box; wall: number }) {
  const inner = innerBox(box, wall);
  const strokeWidth = Math.max(0.015, wall * 0.12);
  const unit = Math.min(inner.width, inner.height);
  const bowl = unit * 0.26;
  const cx = inner.x + inner.width * 0.62;
  const cy = inner.y + inner.height * 0.55;

  return (
    <g fill="none" stroke={LINE_COLOR} strokeWidth={strokeWidth}>
      {/* Cistern against the wall, then the bowl in front of it. */}
      <rect
        x={inner.x + inner.width * 0.62 - bowl * 0.8}
        y={inner.y + inner.height * 0.12}
        width={bowl * 1.6}
        height={bowl * 0.5}
        rx={bowl * 0.1}
      />
      <ellipse cx={cx} cy={cy} rx={bowl * 0.78} ry={bowl} />
      {/* Basin tucked into the other corner. */}
      <rect
        x={inner.x + inner.width * 0.12}
        y={inner.y + inner.height * 0.16}
        width={inner.width * 0.2}
        height={inner.height * 0.18}
        rx={unit * 0.05}
      />
    </g>
  );
}

/** A doorway: the wall breaks and the leaf sweeps into the room. */
function DoorArt({ box, wall }: { box: Box; wall: number }) {
  const horizontal = box.width >= box.height;
  const strokeWidth = Math.max(0.05, wall * 0.7);
  const span = horizontal ? box.width : box.height;
  const gap = span * 0.62;

  if (horizontal) {
    const left = box.x + (box.width - gap) / 2;
    const right = left + gap;
    const y = box.y + box.height / 2;
    return (
      <g fill="none" stroke={WALL_COLOR} strokeWidth={strokeWidth} strokeLinecap="butt">
        <line x1={box.x} y1={y} x2={left} y2={y} />
        <line x1={right} y1={y} x2={box.x + box.width} y2={y} />
        <path
          d={`M ${left} ${y} a ${gap} ${gap} 0 0 1 ${gap * 0.15} ${-gap * 0.99}`}
          stroke={LINE_COLOR}
          strokeWidth={strokeWidth * 0.4}
        />
      </g>
    );
  }

  const top = box.y + (box.height - gap) / 2;
  const bottom = top + gap;
  const x = box.x + box.width / 2;
  return (
    <g fill="none" stroke={WALL_COLOR} strokeWidth={strokeWidth} strokeLinecap="butt">
      <line x1={x} y1={box.y} x2={x} y2={top} />
      <line x1={x} y1={bottom} x2={x} y2={box.y + box.height} />
      <path
        d={`M ${x} ${top} a ${gap} ${gap} 0 0 1 ${gap * 0.99} ${gap * 0.15}`}
        stroke={LINE_COLOR}
        strokeWidth={strokeWidth * 0.4}
      />
    </g>
  );
}

function BalconyArt({ box, wall }: { box: Box; wall: number }) {
  const inner = innerBox(box, wall);
  const horizontal = inner.width >= inner.height;
  const strokeWidth = Math.max(0.015, wall * 0.12);
  // The railing runs along the outer edge, the side away from the building.
  const offset = Math.max(0.1, Math.min(inner.width, inner.height) * 0.16);
  const railY = inner.y + inner.height - offset;
  const railX = inner.x + offset;
  const posts = Math.max(3, Math.round((horizontal ? inner.width : inner.height) / 0.5));

  return (
    <g stroke={LINE_COLOR} strokeWidth={strokeWidth} fill="none">
      <line
        x1={inner.x}
        y1={railY}
        x2={inner.x + inner.width}
        y2={railY}
      />
      <line x1={railX} y1={inner.y} x2={railX} y2={inner.y + inner.height} />
      {Array.from({ length: posts }, (_, index) => {
        const t = (index + 1) / (posts + 1);
        return (
          <line
            key={index}
            x1={inner.x + inner.width * t}
            y1={railY}
            x2={inner.x + inner.width * t}
            y2={inner.y + inner.height}
          />
        );
      })}
    </g>
  );
}

/** Leaf colours, taken from the plants the mockup scatters through the rooms. */
const LEAF_FILL = '#8CC63F';
const LEAF_SHADE = '#6FA82E';
const LEAF_STROKE = '#4C7A22';

/**
 * A potted plant: overlapping oval leaves fanning out from a small centre,
 * drawn the way the mockup does. The old three grey circles read as a diagram
 * symbol rather than as the greenery the reference puts beside each window.
 */
function TreeArt({ box }: { box: Box }) {
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  const r = Math.min(box.width, box.height) * 0.46;
  const leaves = 7;

  return (
    <g>
      {Array.from({ length: leaves }, (_, index) => {
        const angle = (360 / leaves) * index - 90;
        const distance = r * 0.5;
        const radians = (angle * Math.PI) / 180;
        return (
          <ellipse
            key={index}
            cx={cx + Math.cos(radians) * distance}
            cy={cy + Math.sin(radians) * distance}
            rx={r * 0.44}
            ry={r * 0.26}
            fill={index % 2 === 0 ? LEAF_FILL : LEAF_SHADE}
            stroke={LEAF_STROKE}
            strokeWidth={r * 0.055}
            transform={`rotate(${angle} ${cx + Math.cos(radians) * distance} ${cy + Math.sin(radians) * distance})`}
          />
        );
      })}
      <circle cx={cx} cy={cy} r={r * 0.13} fill={LEAF_STROKE} />
    </g>
  );
}

/**
 * The drawing of one floor object. Everything that is not a bare rectangle
 * lives here so the designer preview and the finished plan never diverge.
 */
export function LayoutObjectArt({ box, kind, wall, caption = 0 }: LayoutObjectArtProps) {
  const thickness = wall ?? objectWall(box);
  // The caption strip belongs to the symbol's frame, not to its drawing, so
  // the stair treads and WC fixtures stay inside the part left above it.
  const art =
    caption > 0 && box.height > caption
      ? { ...box, height: box.height - caption }
      : box;

  if (kind === 7) {
    // A note has no footprint; only its text is drawn by the caller.
    return null;
  }

  if (kind === 11) {
    return <TreeArt box={art} />;
  }

  if (kind === 6) {
    return <DoorArt box={art} wall={thickness} />;
  }

  const fill = kind === 10 ? '#F4F8FB' : OBJECT_FILL;

  return (
    <g>
      <rect
        x={box.x}
        y={box.y}
        width={box.width}
        height={box.height}
        fill={fill}
      />
      <path
        d={closedWalls(box, thickness)}
        fill="none"
        stroke={WALL_COLOR}
        strokeWidth={thickness}
        strokeLinecap="square"
      />
      {kind === 2 && <StairArt box={art} wall={thickness} />}
      {kind === 3 && <ElevatorArt box={art} wall={thickness} />}
      {kind === 5 && <ToiletArt box={art} wall={thickness} />}
      {kind === 10 && <BalconyArt box={art} wall={thickness} />}
    </g>
  );
}

export interface ObjectLabelFit {
  /** The symbol's own area, above the caption strip. */
  art: Box;
  /** Metres reserved at the bottom of the box for the caption. */
  caption: number;
  /** Baseline for the caption text. */
  baselineY: number;
  fontSize: number;
  textLength?: number;
}

/**
 * Splits an object's box into symbol and caption, following the mockup: the
 * name sits in a strip under the symbol instead of being printed through the
 * middle of it, where it used to cover the stair treads and the WC fixtures.
 * Returns null when the strip would be too thin to read — a door leaf or a
 * sliver of corridor is drawn unlabelled rather than captioned illegibly.
 */
export function objectLabel(box: Box, label: string): ObjectLabelFit | null {
  const text = label.trim();
  if (text.length === 0) {
    return null;
  }
  const unit = Math.min(box.width, box.height);
  const caption = Math.min(box.height * 0.28, Math.max(0.18, Math.min(0.5, unit * 0.24)));
  if (caption < 0.16 || box.height < caption * 2.2 || box.width < 0.9) {
    return null;
  }
  const fontSize = caption * 0.62;
  const fit = fitLabel(text, fontSize, box.width * 0.86);
  // Sits clear of the inner face of the bottom wall, so the caption reads on
  // the floor rather than across the masonry band.
  const baselineY = box.y + box.height - objectWall(box) - fontSize * 0.3;
  return {
    art: { ...box, height: box.height - caption },
    caption,
    baselineY,
    fontSize: fit.fontSize,
    textLength: fit.textLength,
  };
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

/**
 * Fits a label inside its box. The estimate is only used to shrink the type;
 * `textLength` is what actually guarantees the glyphs stay inside, since font
 * metrics differ between machines and a guessed advance width would overflow
 * on some of them.
 */
export function fitLabel(
  label: string,
  size: number,
  maxWidth: number,
): { fontSize: number; textLength?: number } {
  if (label.length === 0 || maxWidth <= 0) {
    return { fontSize: size };
  }
  const natural = label.length * size * 0.58;
  if (natural <= maxWidth) {
    return { fontSize: size };
  }
  const fontSize = Math.max(size * 0.4, size * (maxWidth / natural));
  // Re-estimating at the smaller size can still be off, so the box width is
  // imposed on the text rather than trusted to the estimate.
  return { fontSize, textLength: maxWidth };
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
