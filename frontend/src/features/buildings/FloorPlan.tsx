interface RoomPlot {
  id: string;
  roomNumber: string;
  area: number;
  status: 1 | 2 | 3 | 4 | 5;
  capacity: number;
}

interface FloorPlanProps {
  rooms: RoomPlot[];
  selectedId: string | null;
  onSelect: (roomId: string) => void;
}

const statusStyles: Record<number, { fill: string; stroke: string; label: string }> = {
  1: { fill: '#E9F7E7', stroke: '#9BD69B', label: 'Trống' },
  2: { fill: '#FDE2E1', stroke: '#F2A9A6', label: 'Đang thuê' },
  3: { fill: '#FDF0CC', stroke: '#EBCB7A', label: 'Đã đặt' },
  4: { fill: '#E8EEF5', stroke: '#B3C4D8', label: 'Bảo trì' },
  5: { fill: '#F1F5F9', stroke: '#CBD5E1', label: 'Ngừng dùng' },
};

/**
 * Approximates the mockup's floor-plan drawing from real room data: each
 * room becomes a block sized by its area, laid out on two sides of a central
 * corridor. There is no geometry in the backend, so this is an honest
 * schematic rather than a to-scale plan.
 */
export function FloorPlan({ rooms, selectedId, onSelect }: FloorPlanProps) {
  if (rooms.length === 0) {
    return (
      <div className="flex h-64 items-center justify-center rounded-lg border border-dashed border-outline-variant bg-surface-lowest text-sm text-on-surface-variant">
        Tầng này chưa có phòng nào để hiển thị.
      </div>
    );
  }

  const half = Math.ceil(rooms.length / 2);
  const top = rooms.slice(0, half);
  const bottom = rooms.slice(half);
  const totalArea = rooms.reduce((sum, room) => sum + (room.area || 1), 0);

  return (
    <div className="rounded-lg border border-outline-variant bg-surface-lowest p-4">
      <div className="mb-2 text-xs text-on-surface-variant">
        Sơ đồ minh họa theo diện tích thật từng phòng (tổng {Math.round(totalArea)} m²)
      </div>

      <div className="overflow-x-auto">
        <div className="flex min-w-[560px] flex-col gap-0">
          <div className="flex gap-1">
            {top.map((room) => (
              <RoomBlock
                key={room.id}
                room={room}
                totalArea={totalArea}
                selected={room.id === selectedId}
                onSelect={onSelect}
              />
            ))}
          </div>

          <div className="my-1 flex h-8 items-center justify-center rounded bg-surface-low text-[11px] font-medium tracking-wide text-on-surface-variant">
            Hành lang chung
          </div>

          <div className="flex gap-1">
            {bottom.map((room) => (
              <RoomBlock
                key={room.id}
                room={room}
                totalArea={totalArea}
                selected={room.id === selectedId}
                onSelect={onSelect}
              />
            ))}
          </div>
        </div>
      </div>

      <div className="mt-3 flex flex-wrap gap-3 border-t border-outline-variant pt-3 text-[11px] text-on-surface-variant">
        {[1, 2, 3, 4, 5].map((status) => (
          <span key={status} className="flex items-center gap-1.5">
            <span
              className="h-3 w-3 rounded-[2px] border"
              style={{
                backgroundColor: statusStyles[status]!.fill,
                borderColor: statusStyles[status]!.stroke,
              }}
            />
            {statusStyles[status]!.label}
          </span>
        ))}
      </div>
    </div>
  );
}

function RoomBlock({
  room,
  totalArea,
  selected,
  onSelect,
}: {
  room: RoomPlot;
  totalArea: number;
  selected: boolean;
  onSelect: (roomId: string) => void;
}) {
  const style = statusStyles[room.status] ?? statusStyles[5]!;
  const flexGrow = Math.max(1, room.area || 1);
  const share = Math.round(((room.area || 1) / totalArea) * 100);

  return (
    <button
      type="button"
      onClick={() => onSelect(room.id)}
      title={`${room.roomNumber} — ${room.area} m² — ${style.label}`}
      style={{
        flexGrow,
        flexBasis: 0,
        backgroundColor: style.fill,
        borderColor: selected ? '#1d4ed8' : style.stroke,
        boxShadow: selected ? '0 0 0 2px rgba(29,78,216,0.35)' : undefined,
      }}
      className="flex min-w-[76px] flex-col items-start justify-center rounded-md border-2 px-2 py-3 text-left transition hover:brightness-[0.98]"
    >
      <span className="font-display text-sm font-bold text-on-surface">{room.roomNumber}</span>
      <span className="text-[11px] leading-tight text-on-surface-variant">{room.area} m²</span>
      <span className="mt-0.5 text-[10px] leading-tight text-on-surface-variant/80">
        {share}% tầng
      </span>
    </button>
  );
}
