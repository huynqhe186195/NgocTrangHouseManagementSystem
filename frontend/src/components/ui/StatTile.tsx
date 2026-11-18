interface StatTileProps {
  icon: string;
  value: string;
  label: string;
  tone?: 'primary' | 'green' | 'orange';
  /**
   * The mockup draws a pale disc behind the first three icons and paints the
   * "Tỷ lệ lấp đầy" pie straight on the card, so the chip is per-tile rather
   * than per-tone.
   */
  chip?: boolean;
}

/**
 * Card, disc and glyph colours, sampled from the building mockup: a very pale
 * tinted card, a slightly stronger disc, and the glyph in a saturated shade of
 * the same hue.
 */
const tones = {
  primary: {
    card: 'bg-[#F1F6FC]',
    chip: 'bg-[#E4EFFD]',
    glyph: 'text-[#1D4ED8]',
    value: 'text-[#1E3A8A]',
  },
  green: {
    card: 'bg-[#EFF8F6]',
    chip: 'bg-[#DDF9EA]',
    glyph: 'text-[#047857]',
    value: 'text-[#065F46]',
  },
  orange: {
    card: 'bg-[#FFF7ED]',
    chip: 'bg-[#FFEDD5]',
    glyph: 'text-[#EA580C]',
    value: 'text-[#9A3412]',
  },
} as const;

/** KPI tile from the building detail mockup (tầng / phòng / đã thuê / lấp đầy). */
export function StatTile({ icon, value, label, tone = 'primary', chip = true }: StatTileProps) {
  const look = tones[tone];
  // The mockup's KPI glyphs are solid, while the icon font is loaded outlined.
  const filled = { fontVariationSettings: "'FILL' 1, 'wght' 400, 'GRAD' 0, 'opsz' 24" };
  return (
    <div className={`flex items-center gap-3.5 rounded-xl px-4 py-3.5 ${look.card}`}>
      {chip ? (
        <span
          className={`flex h-12 w-12 shrink-0 items-center justify-center rounded-full ${look.chip}`}
        >
          <span
            className={`material-symbols-outlined text-[28px] ${look.glyph}`}
            style={filled}
          >
            {icon}
          </span>
        </span>
      ) : (
        <span
          className={`material-symbols-outlined w-12 shrink-0 text-[34px] ${look.glyph}`}
          style={filled}
        >
          {icon}
        </span>
      )}
      <div className="min-w-0 leading-tight">
        <p className={`font-display text-2xl font-bold tnum ${look.value}`}>{value}</p>
        <p className="text-xs leading-snug text-on-surface-variant">{label}</p>
      </div>
    </div>
  );
}
