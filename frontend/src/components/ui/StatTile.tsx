interface StatTileProps {
  icon: string;
  value: string;
  label: string;
  tone?: 'primary' | 'green' | 'orange';
}

const tones = {
  primary: 'bg-[#EFF6FF] text-[#1D4ED8]',
  green: 'bg-[#ECFDF5] text-[#047857]',
  orange: 'bg-[#FFF7ED] text-[#C2410C]',
} as const;

/** KPI tile from the building detail mockup (5 tầng / 80 phòng / 72 đã thuê / 90%). */
export function StatTile({ icon, value, label, tone = 'primary' }: StatTileProps) {
  return (
    <div className={`flex items-center gap-3 rounded-xl px-4 py-3.5 ${tones[tone]}`}>
      <span className="material-symbols-outlined text-2xl">{icon}</span>
      <div className="leading-tight">
        <p className="font-display text-xl font-bold tnum">{value}</p>
        <p className="text-xs opacity-80">{label}</p>
      </div>
    </div>
  );
}
