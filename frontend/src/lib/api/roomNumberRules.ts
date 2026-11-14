/**
 * Mirrors RoomAppService.ValidateRoomNumber (server-side, authoritative):
 * digits only, at least 3 characters, and everything but the last two digits
 * must equal the floor number. Floor 1 -> 101, 102; floor 2 -> 201, 202;
 * ground floor 0 -> 001, 002.
 *
 * This only exists to tell the user before the round trip; the server still
 * decides.
 */
export function validateRoomNumber(roomNumber: string, floorNumber: number): string | null {
  const trimmed = roomNumber.trim();

  if (trimmed.length === 0) {
    return 'Vui lòng nhập mã phòng.';
  }
  if (!/^\d+$/.test(trimmed)) {
    return 'Mã phòng chỉ được gồm chữ số.';
  }
  if (trimmed.length < 3) {
    return 'Mã phòng phải có ít nhất 3 chữ số, ví dụ 101.';
  }

  const floorPart = trimmed.slice(0, -2);
  if (!/^\d+$/.test(floorPart) || Number(floorPart) !== floorNumber) {
    return `Mã phòng phải bắt đầu bằng số tầng. Tầng ${floorNumber} cần mã như ${suggestRoomNumber(floorNumber)}.`;
  }

  return null;
}

/** First room number that satisfies the rule for this floor, e.g. floor 2 -> 201. */
export function suggestRoomNumber(floorNumber: number): string {
  return `${floorNumber}01`;
}
