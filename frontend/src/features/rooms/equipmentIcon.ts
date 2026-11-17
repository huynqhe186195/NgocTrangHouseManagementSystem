import type { RoomEquipmentDto } from '../../lib/api/types';

/**
 * Picks a Material Symbols glyph from the equipment's own name and category.
 * Both the admin equipment table and the tenant amenity chips use it, so a
 * "Máy lạnh" reads the same on either screen.
 */
export function equipmentIcon(item: Pick<RoomEquipmentDto, 'name' | 'category'>): string {
  const haystack = `${item.name} ${item.category ?? ''}`.toLowerCase();
  // Most specific first: "tủ lạnh" and "bình nóng lạnh" both contain "lạnh",
  // so they must be tested before the air-conditioner rule.
  const rules: [RegExp, string][] = [
    [/tủ lạnh|tu lanh|fridge/, 'kitchen'],
    [/nóng lạnh|nong lanh|bình nước nóng|water_heater/, 'water_heater'],
    [/máy giặt|may giat|wash/, 'local_laundry_service'],
    [/wifi|wi-fi|internet|mạng/, 'wifi'],
    [/ban công|ban cong|balcony|logia/, 'balcony'],
    [/vệ sinh|ve sinh|nhà tắm|nha tam|toilet|bathroom/, 'bathtub'],
    [/lạnh|lanh|điều hoà|dieu hoa|air/, 'ac_unit'],
    [/giường|giuong|bed|nệm|nem/, 'bed'],
    [/tủ|tu |wardrobe|kệ|ke /, 'shelves'],
    [/bàn|ban |desk|ghế|ghe/, 'table_restaurant'],
    [/tivi|tv|truyền hình/, 'tv'],
    [/quạt|quat|fan/, 'mode_fan'],
    [/bếp|bep|kitchen/, 'countertops'],
  ];
  for (const [pattern, icon] of rules) {
    if (pattern.test(haystack)) {
      return icon;
    }
  }
  return 'handyman';
}