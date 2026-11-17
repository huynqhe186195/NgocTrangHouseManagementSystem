import { formatMetres, type Box, type PlanPoint } from '../../lib/floorGeometry';
import type { FloorLayoutItemKind } from '../../lib/api/types';
import { isFurniture } from '../../lib/api/types';

/**
 * The architectural primitives both floor-plan screens draw with. The
 * read-only plan and the designer must produce the same drawing, so the wall
 * ring, the door opening and the dimension rules live here rather than in
 * either screen.
 */

export const WALL_COLOR = '#2b2b2b';
/** The lighter core line inside a drawn wall band. */
const WALL_LIGHT = '#5a5a5a';
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
 * Wall thickness in metres for a partition between two rooms.
 *
 * Read off the mockup: its plan is drawn at roughly 26 px per metre, and a
 * partition there is a 5 px band — about 0.2 m, which is one brick wythe with
 * plaster on both faces. The previous 0.1-0.2 m ramp shrank the wall on small
 * rooms, so a 2 m bathroom drew a pencil line next to a 5 m bedroom's band and
 * the floor stopped reading as masonry. The band is now near-uniform, the way
 * a real plan is: partitions do not change thickness with the room they bound.
 */
export function wallThickness(box: Box): number {
  const unit = Math.min(box.width, box.height);
  return Math.max(0.22, Math.min(0.28, unit * 0.07));
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

/**
 * The solid kinds — corridor, stairwell, elevator, WC, service area, laundry,
 * storage — are rooms in their own right on the mockup's plan, walled at the
 * same weight as the rooms around them. Only the light kinds (a door leaf, a
 * balcony railing, a plant) stay thin.
 */
const WALLED_KINDS = new Set<number>([1, 2, 3, 4, 5, 8, 9]);

export interface LayoutObjectArtProps {
  box: Box;
  kind: FloorLayoutItemKind;
  /** Object walls are thinner than room walls; they are smaller boxes. */
  wall?: number;
  /** Bottom strip reserved for the caption, so the symbol sits above it. */
  caption?: number;
  /** A wall's centreline. Absent means a box wall from before polylines. */
  points?: PlanPoint[] | null;
  thickness?: number | null;
  cornerRadius?: number | null;
}

export function objectWall(box: Box): number {
  const unit = Math.min(box.width, box.height);
  return Math.max(0.07, Math.min(0.2, unit * OBJECT_WALL_RATIO));
}

/**
 * A corridor or a stairwell is drawn at partition weight, not at the thin
 * object weight: the mockup walls them exactly like the rooms they serve, so a
 * corridor between two rooms reads as one continuous masonry band rather than
 * as an unwalled grey strip.
 */
export function solidObjectWall(box: Box): number {
  return wallThickness(box);
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
 * Warm, paper-light fills and a soft outline: the mockup draws furniture as
 * pale blocks with a thin grey edge, clearly lighter than the masonry of the
 * walls, so a furnished room reads as a room rather than as more structure.
 */
const FURNITURE_FILL = '#FBF7F1';
const FURNITURE_SHADE = '#DFD6C9';
const FURNITURE_STROKE = '#9C9184';
const FURNITURE_SOFT = '#FFFFFF';

interface FurnitureArtProps {
  box: Box;
}

/** A bed: pillow against the headboard, a folded blanket across the foot. */
function BedArt({ box }: FurnitureArtProps) {
  const short = box.height;
  const strokeWidth = Math.max(0.012, short * 0.03);
  const headboard = short * 0.12;
  const pillowDepth = short * 0.26;
  const blanketStart = box.width * 0.52;

  return (
    <g>
      <rect
        x={box.x}
        y={box.y}
        width={box.width}
        height={box.height}
        fill={FURNITURE_FILL}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      {/* Headboard: a thicker band at the wall end. */}
      <rect x={box.x} y={box.y} width={headboard} height={box.height} fill={FURNITURE_SHADE} />
      {/* The pillow, laid against the headboard. */}
      <rect
        x={box.x + headboard + short * 0.05}
        y={box.y + short * 0.12}
        width={pillowDepth}
        height={box.height - short * 0.24}
        rx={short * 0.04}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.7}
      />
      {/* The blanket, with a turned-down edge like the mockup's. */}
      <rect
        x={box.x + headboard + blanketStart}
        y={box.y}
        width={Math.max(0, box.width - headboard - blanketStart - strokeWidth)}
        height={box.height}
        fill={FURNITURE_SHADE}
        fillOpacity={0.55}
      />
      <line
        x1={box.x + headboard + blanketStart}
        y1={box.y}
        x2={box.x + headboard + blanketStart}
        y2={box.y + box.height}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.7}
      />
    </g>
  );
}

/** A wardrobe: two doors split down the middle, with the hanging rail. */
function WardrobeArt({ box }: FurnitureArtProps) {
  const strokeWidth = Math.max(0.012, Math.min(box.width, box.height) * 0.032);

  return (
    <g>
      <rect
        x={box.x + strokeWidth}
        y={box.y + strokeWidth}
        width={box.width - strokeWidth * 2}
        height={box.height - strokeWidth * 2}
        fill={FURNITURE_FILL}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      <line
        x1={box.x + box.width * 0.18}
        y1={box.y + box.height * 0.5}
        x2={box.x + box.width * 0.82}
        y2={box.y + box.height * 0.5}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 1.2}
      />
      <line
        x1={box.x + box.width * 0.5}
        y1={box.y + strokeWidth}
        x2={box.x + box.width * 0.5}
        y2={box.y + box.height - strokeWidth}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
    </g>
  );
}

/** A desk with a chair tucked under it, the way the mockup seats a room. */
function DeskArt({ box }: FurnitureArtProps) {
  const strokeWidth = Math.max(0.012, box.height * 0.028);

  return (
    <g>
      <rect
        x={box.x + strokeWidth}
        y={box.y + strokeWidth}
        width={box.width - strokeWidth * 2}
        height={box.height * 0.62}
        fill={FURNITURE_FILL}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      {/* The chair: a back and a seat, drawn as two rounded blocks. */}
      <rect
        x={box.x + box.width * 0.34}
        y={box.y + box.height * 0.62}
        width={box.width * 0.32}
        height={box.height * 0.16}
        rx={strokeWidth}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      <rect
        x={box.x + box.width * 0.42}
        y={box.y + box.height * 0.78}
        width={box.width * 0.16}
        height={box.height * 0.16}
        rx={strokeWidth}
        fill={FURNITURE_FILL}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
    </g>
  );
}

/** A chair on its own: seat ring with a backrest. */
function ChairArt({ box }: FurnitureArtProps) {
  const unit = Math.min(box.width, box.height);
  const strokeWidth = Math.max(0.012, unit * 0.05);
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;

  return (
    <g>
      <circle
        cx={cx}
        cy={cy + unit * 0.08}
        r={unit * 0.3}
        fill={FURNITURE_FILL}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      <rect
        x={cx - unit * 0.3}
        y={cy - unit * 0.42}
        width={unit * 0.6}
        height={unit * 0.16}
        rx={strokeWidth}
        fill={FURNITURE_SHADE}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
    </g>
  );
}

/** A sofa: a back, two arms and a pair of cushions. */
function SofaArt({ box }: FurnitureArtProps) {
  const short = box.height;
  const strokeWidth = Math.max(0.012, short * 0.03);

  return (
    <g>
      <rect
        x={box.x + strokeWidth}
        y={box.y + strokeWidth}
        width={box.width - strokeWidth * 2}
        height={box.height - strokeWidth * 2}
        rx={short * 0.12}
        fill={FURNITURE_FILL}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      {/* Backrest along the wall side. */}
      <rect
        x={box.x + box.width * 0.06}
        y={box.y + box.height * 0.08}
        width={box.width * 0.88}
        height={box.height * 0.26}
        rx={short * 0.06}
        fill={FURNITURE_SHADE}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.8}
      />
      {/* Two seat cushions, split down the middle. */}
      <rect
        x={box.x + box.width * 0.08}
        y={box.y + box.height * 0.38}
        width={box.width * 0.4}
        height={box.height * 0.5}
        rx={short * 0.05}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.8}
      />
      <rect
        x={box.x + box.width * 0.52}
        y={box.y + box.height * 0.38}
        width={box.width * 0.4}
        height={box.height * 0.5}
        rx={short * 0.05}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.8}
      />
    </g>
  );
}

/** A wall-mounted air conditioner: a louvred box with the airflow arrow. */
function AirConditionerArt({ box }: FurnitureArtProps) {
  const short = Math.min(box.width, box.height);
  const strokeWidth = Math.max(0.01, short * 0.06);
  const centreX = box.x + box.width / 2;

  return (
    <g>
      <rect
        x={box.x + strokeWidth}
        y={box.y + strokeWidth}
        width={box.width - strokeWidth * 2}
        height={box.height - strokeWidth * 2}
        rx={short * 0.2}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      {/* The louvre, a line along the unit's length. */}
      <line
        x1={box.x + box.width * 0.18}
        y1={box.y + box.height * 0.62}
        x2={box.x + box.width * 0.82}
        y2={box.y + box.height * 0.62}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.8}
      />
      {/* Two zigzags of airflow, the usual air-conditioner symbol. */}
      <path
        d={`M ${centreX - short * 0.3} ${box.y + box.height * 0.28} q ${short * 0.16} ${-short * 0.18} ${short * 0.32} 0 q ${short * 0.16} ${short * 0.18} ${short * 0.32} 0`}
        fill="none"
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.7}
        strokeLinecap="round"
      />
    </g>
  );
}

/** A kitchen run: a sink and a hob on a worktop. */
function KitchenArt({ box }: FurnitureArtProps) {
  const strokeWidth = Math.max(0.012, box.height * 0.03);

  return (
    <g>
      <rect
        x={box.x + strokeWidth}
        y={box.y + strokeWidth}
        width={box.width - strokeWidth * 2}
        height={box.height - strokeWidth * 2}
        fill={FURNITURE_FILL}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      {/* Sink basin. */}
      <rect
        x={box.x + box.width * 0.1}
        y={box.y + box.height * 0.28}
        width={box.width * 0.28}
        height={box.height * 0.44}
        rx={strokeWidth}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 0.8}
      />
      {/* Hob: four burners. */}
      {(
        [
          [0.58, 0.34],
          [0.76, 0.34],
          [0.58, 0.66],
          [0.76, 0.66],
        ] as const
      ).map(([fx, fy]) => (
        <circle
          key={`${fx}-${fy}`}
          cx={box.x + box.width * fx}
          cy={box.y + box.height * fy}
          r={Math.min(box.width, box.height) * 0.07}
          fill="none"
          stroke={FURNITURE_STROKE}
          strokeWidth={strokeWidth * 0.7}
        />
      ))}
    </g>
  );
}

/** A refrigerator: a tall box with the door split and a handle. */
function RefrigeratorArt({ box }: FurnitureArtProps) {
  const strokeWidth = Math.max(0.012, box.height * 0.035);

  return (
    <g>
      <rect
        x={box.x + strokeWidth}
        y={box.y + strokeWidth}
        width={box.width - strokeWidth * 2}
        height={box.height - strokeWidth * 2}
        rx={strokeWidth}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      <line
        x1={box.x + box.width * 0.5}
        y1={box.y + strokeWidth}
        x2={box.x + box.width * 0.5}
        y2={box.y + box.height - strokeWidth}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      <line
        x1={box.x + box.width * 0.42}
        y1={box.y + box.height * 0.3}
        x2={box.x + box.width * 0.42}
        y2={box.y + box.height * 0.7}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth * 1.4}
        strokeLinecap="round"
      />
    </g>
  );
}

/** A television: a screen with the stand and the wall bracket. */
function TelevisionArt({ box }: FurnitureArtProps) {
  const strokeWidth = Math.max(0.01, box.height * 0.04);

  return (
    <g>
      <rect
        x={box.x + box.width * 0.06}
        y={box.y + box.height * 0.1}
        width={box.width * 0.88}
        height={box.height * 0.68}
        rx={strokeWidth}
        fill={FURNITURE_SOFT}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
      />
      <rect
        x={box.x + box.width * 0.42}
        y={box.y + box.height * 0.78}
        width={box.width * 0.16}
        height={box.height * 0.08}
        fill={FURNITURE_SHADE}
      />
      <line
        x1={box.x + box.width * 0.28}
        y1={box.y + box.height * 0.9}
        x2={box.x + box.width * 0.72}
        y2={box.y + box.height * 0.9}
        stroke={FURNITURE_STROKE}
        strokeWidth={strokeWidth}
        strokeLinecap="round"
      />
    </g>
  );
}

/**
 * Furniture is authored along the box's width — a bed's headboard at its left
 * edge, a desk's worktop across the top. When the owner stretches the box
 * taller than it is wide the drawing turns a quarter turn so the symbol still
 * lies the long way round, exactly as a bed drawn on a real plan would.
 */
const UPRIGHT_ROTATION = new Set<FloorLayoutItemKind>([13, 14, 15, 17, 19, 20, 21]);

function withUpright(
  kind: FloorLayoutItemKind,
  box: Box,
  art: React.ReactNode,
): React.ReactNode {
  if (!UPRIGHT_ROTATION.has(kind) || box.width >= box.height) {
    return art;
  }
  // Rotating about the box centre maps the authored w×h drawing onto the
  // stretched h×w box, so the symbol fills the footprint either way.
  return (
    <g transform={`rotate(90 ${box.x + box.width / 2} ${box.y + box.height / 2})`}>{art}</g>
  );
}

const FURNITURE_ART: Partial<
  Record<FloorLayoutItemKind, (props: FurnitureArtProps) => React.ReactElement>
> = {
  13: BedArt,
  14: WardrobeArt,
  15: DeskArt,
  16: ChairArt,
  17: SofaArt,
  18: AirConditionerArt,
  19: KitchenArt,
  20: RefrigeratorArt,
  21: TelevisionArt,
};

/**
 * A wall the owner places and stretches. Unlike the other palette objects it
 * has no floor or symbol: it is solid masonry along its own length, and its
 * thickness follows the short side of the box — so the same object can be a
 * storey's outer wall, a corridor partition or a stub beside a doorway.
 *
 * The mockup's outer wall is exactly this: a band the designer drags out,
 * which is why it can leave a gap at a stairwell or run past the last room.
 * Deriving it from the room boxes could never do that.
 */
function WallArt({ box }: { box: Box }) {
  const horizontal = box.width >= box.height;
  const thickness = horizontal ? box.height : box.width;
  const cap = Math.max(0.04, thickness * 0.18);
  const strokeWidth = Math.max(0.015, thickness * 0.07);

  return (
    <g>
      <rect x={box.x} y={box.y} width={box.width} height={box.height} fill={WALL_COLOR} />
      {/* A lighter core line, the way a cut wall is hatched on a drawing. */}
      {horizontal ? (
        <>
          <line
            x1={box.x + cap}
            y1={box.y + box.height / 2}
            x2={box.x + box.width - cap}
            y2={box.y + box.height / 2}
            stroke={WALL_LIGHT}
            strokeWidth={strokeWidth}
          />
          <line x1={box.x} y1={box.y} x2={box.x} y2={box.y + box.height} stroke={WALL_LIGHT} strokeWidth={strokeWidth} />
          <line
            x1={box.x + box.width}
            y1={box.y}
            x2={box.x + box.width}
            y2={box.y + box.height}
            stroke={WALL_LIGHT}
            strokeWidth={strokeWidth}
          />
        </>
      ) : (
        <>
          <line
            x1={box.x + box.width / 2}
            y1={box.y + cap}
            x2={box.x + box.width / 2}
            y2={box.y + box.height - cap}
            stroke={WALL_LIGHT}
            strokeWidth={strokeWidth}
          />
          <line x1={box.x} y1={box.y} x2={box.x + box.width} y2={box.y} stroke={WALL_LIGHT} strokeWidth={strokeWidth} />
          <line
            x1={box.x}
            y1={box.y + box.height}
            x2={box.x + box.width}
            y2={box.y + box.height}
            stroke={WALL_LIGHT}
            strokeWidth={strokeWidth}
          />
        </>
      )}
    </g>
  );
}

/**
 * A storey's outer wall is not one straight band. This turns the stored
 * centreline into an SVG path, and `radius` decides what happens at each bend:
 * zero keeps the sharp miter the mockup draws (its corners are chamfered, not
 * curved), anything above it swaps a corner for an arc, which is what stucco
 * and brickwork actually do.
 *
 * The cut is clamped to half of each adjoining leg, so a radius larger than the
 * wall itself shortens the fillet instead of swallowing the segment and folding
 * the line back on itself.
 */
export function roundedPolyline(points: PlanPoint[], radius: number): string {
  const first = points[0];
  if (!first) {
    return '';
  }
  const f = (value: number) => Number(value.toFixed(4));
  let d = `M ${f(first.x)} ${f(first.y)}`;

  if (points.length === 1) {
    return d;
  }

  const r = Number.isFinite(radius) && radius > 0 ? radius : 0;

  for (let i = 1; i < points.length; i += 1) {
    const current = points[i];
    if (!current) {
      continue;
    }
    const isLast = i === points.length - 1;

    if (r === 0 || isLast) {
      d += ` L ${f(current.x)} ${f(current.y)}`;
      continue;
    }

    const prev = points[i - 1];
    const next = points[i + 1];
    if (!prev || !next) {
      d += ` L ${f(current.x)} ${f(current.y)}`;
      continue;
    }
    const inLength = Math.hypot(current.x - prev.x, current.y - prev.y);
    const outLength = Math.hypot(next.x - current.x, next.y - current.y);
    if (inLength === 0 || outLength === 0) {
      d += ` L ${f(current.x)} ${f(current.y)}`;
      continue;
    }

    // The fillet has to fit inside both legs, or it would run past the next bend.
    const cut = Math.min(r, inLength / 2, outLength / 2);
    const entry = {
      x: current.x + ((prev.x - current.x) / inLength) * cut,
      y: current.y + ((prev.y - current.y) / inLength) * cut,
    };
    const exit = {
      x: current.x + ((next.x - current.x) / outLength) * cut,
      y: current.y + ((next.y - current.y) / outLength) * cut,
    };

    // A straight run has no corner to round; the arc would be drawn edge-on.
    const cross = (current.x - prev.x) * (next.y - current.y) -
      (current.y - prev.y) * (next.x - current.x);
    const straight = Math.abs(cross) < 1e-9;

    d += ` L ${f(entry.x)} ${f(entry.y)}`;
    if (!straight) {
      const sweep = cross > 0 ? 1 : 0;
      d += ` A ${f(cut)} ${f(cut)} 0 0 ${sweep} ${f(exit.x)} ${f(exit.y)}`;
    } else {
      d += ` L ${f(exit.x)} ${f(exit.y)}`;
    }
  }

  return d;
}

/**
 * A polyline wall: the body is one thick round-capped stroke, so the band closes
 * itself at every bend without building an offset polygon, and the light core
 * runs down the middle the way a cut wall is hatched on a drawing.
 */
export function WallPolylineArt({
  points,
  thickness,
  cornerRadius,
}: {
  points: PlanPoint[];
  thickness: number;
  cornerRadius: number;
}) {
  const d = roundedPolyline(points, cornerRadius);
  if (!d) {
    return null;
  }
  // Two points is a plain run, and an open end should read as a cut, not a
  // rounded stub: butt caps keep the wall's ends square to its own length.
  const strokeWidth = Math.max(0.02, thickness);
  return (
    <g>
      <path
        d={d}
        fill="none"
        stroke={WALL_COLOR}
        strokeWidth={strokeWidth}
        strokeLinejoin="miter"
        strokeLinecap="butt"
        strokeMiterlimit={2}
      />
      <path
        d={d}
        fill="none"
        stroke={WALL_LIGHT}
        strokeWidth={Math.max(0.015, strokeWidth * 0.14)}
        strokeLinejoin="miter"
        strokeLinecap="butt"
      />
    </g>
  );
}

/**
 * The drawing of one floor object. Everything that is not a bare rectangle
 * lives here so the designer preview and the finished plan never diverge.
 */
export function LayoutObjectArt({
  box,
  kind,
  wall,
  caption = 0,
  points = null,
  thickness = null,
  cornerRadius = null,
}: LayoutObjectArtProps) {
  const thickness2 =
    wall ?? (WALLED_KINDS.has(kind) ? solidObjectWall(box) : objectWall(box));
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

  if (kind === 12) {
    // A wall with a centreline bends; one stored only as a box (every wall
    // saved before polylines existed) still draws exactly as it did.
    if (points && points.length >= 2) {
      return (
        <WallPolylineArt
          points={points}
          thickness={thickness ?? Math.min(box.width, box.height)}
          cornerRadius={cornerRadius ?? 0}
        />
      );
    }
    return <WallArt box={box} />;
  }

  const furniture = FURNITURE_ART[kind];
  if (furniture) {
    // Furniture sits inside a room that is already walled, so it draws its own
    // outline and nothing else — a second masonry ring inside a room would
    // read as a partition wall the owner never placed.
    const Art = furniture;
    return <>{withUpright(kind, box, <Art box={art} />)}</>;
  }

  if (kind === 11) {
    return <TreeArt box={art} />;
  }

  if (kind === 6) {
    return <DoorArt box={art} wall={thickness2} />;
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
        d={closedWalls(box, thickness2)}
        fill="none"
        stroke={WALL_COLOR}
        strokeWidth={thickness2}
        strokeLinecap="square"
      />
      {kind === 2 && <StairArt box={art} wall={thickness2} />}
      {kind === 3 && <ElevatorArt box={art} wall={thickness2} />}
      {kind === 5 && <ToiletArt box={art} wall={thickness2} />}
      {kind === 10 && <BalconyArt box={art} wall={thickness2} />}
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
 * A bent wall's box is only the extent of its centreline, so a caption placed
 * at the box's centre would float in the middle of the floor rather than on
 * the wall. It is the one architectural object the finished plan leaves
 * unlabelled; a box-only wall keeps its caption.
 */
export function shouldCaption(
  points: PlanPoint[] | null,
  kind: FloorLayoutItemKind,
): boolean {
  if (points && points.length >= 2) {
    return false;
  }
  return !isFurniture(kind);
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
