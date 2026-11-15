interface StatTileProps {
  icon: string;
  value: string;
  label: string;
  tone?: 'primary' | 'green' | 'orange';
}

/**
 * Background and icon-chip colours, measured from the building mockup: a pale
 * tinted card with the icon sitting in a slightly stronger chip of the same
 * hue.
 */
const tones = {
  primary: { card: 'bg-[#EFF4FF]', chip: 'bg-[#DBEAFE] text-[#1D4ED8]', value: 'text-[#1E3A8A]' },
  green: { card: 'bg-[#ECFDF5]', chip: 'bg-[#D1FAE5] text-[#047857]', value: 'text-[#065F46]' },
  orange: { card: 'bg-[#FFF7ED]', chip: 'bg-[#FFEDD5] text-[#EA580C]', value: 'text-[#9A3412]' },
} as const;

/** KPI tile from the building detail mockup (tầng / phòng / đã thuê / lấp đầy). */
export function StatTile({ icon, value, label, tone = 'primary' }: StatTileProps) {
  const look = tones[tone];
  return (
    <div className={`flex items-center gap-3 rounded-xl px-4 py-3.5 ${look.card}`}>
      <span
        className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-lg ${look.chip}`}
      >
        <span className="material-symbols-outlined text-[22px]">{icon}</span>
      </span>
      <div className="min-w-0 leading-tight">
        <p className={`font-display text-2xl font-bold tnum ${look.value}`}>{value}</p>
        <p className="text-xs leading-snug text-on-surface-variant">{label}</p>
      </div>
    </div>
  );
}
