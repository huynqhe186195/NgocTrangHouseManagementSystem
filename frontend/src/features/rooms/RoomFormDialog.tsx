import { useEffect, useMemo, useState } from 'react';
import { Dialog } from '../../components/ui/Dialog';
import { Button } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { SelectField } from '../../components/ui/SelectField';
import { TextareaField } from '../../components/ui/TextareaField';
import { Alert } from '../../components/ui/Alert';
import { describeMutationError } from '../../lib/api/mutationErrors';
import {
  useBuildings,
  useCreateRoom,
  useFloors,
  useUpdateRoom,
} from '../../lib/api/buildingQueries';
import { roomDirectionLabels, roomStatusMeta } from '../../lib/api/types';
import type { RoomDirection, RoomDto, RoomStatus } from '../../lib/api/types';
import { suggestRoomNumber, validateRoomNumber } from '../../lib/api/roomNumberRules';

interface RoomFormDialogProps {
  open: boolean;
  mode: 'create' | 'edit';
  /** Create: pre-bind the parent floor. Omit to show the building -> floor picker. */
  floorId?: string;
  initial?: RoomDto | null;
  onClose: () => void;
}

const STATUS_OPTIONS = (Object.keys(roomStatusMeta) as unknown as string[]).map((value) => ({
  value,
  label: roomStatusMeta[Number(value) as RoomStatus].label,
}));

/** Empty value = not published, which the server stores as null. */
const DIRECTION_OPTIONS = [
  { value: '', label: 'Chưa xác định' },
  ...(Object.keys(roomDirectionLabels) as unknown as string[]).map((value) => ({
    value,
    label: roomDirectionLabels[Number(value) as RoomDirection],
  })),
];

export function RoomFormDialog({ open, mode, floorId, initial, onClose }: RoomFormDialogProps) {
  const create = useCreateRoom();
  const update = useUpdateRoom();
  const mutation = mode === 'create' ? create : update;

  const buildings = useBuildings();
  const floors = useFloors();

  const [selectedBuildingId, setSelectedBuildingId] = useState('');
  const [selectedFloorId, setSelectedFloorId] = useState('');
  const [roomNumber, setRoomNumber] = useState('');
  const [name, setName] = useState('');
  const [area, setArea] = useState('');
  const [capacity, setCapacity] = useState('1');
  const [status, setStatus] = useState('1');
  const [description, setDescription] = useState('');
  const [roomType, setRoomType] = useState('');
  const [direction, setDirection] = useState('');
  const [touched, setTouched] = useState(false);

  const allFloors = useMemo(() => floors.data ?? [], [floors.data]);

  useEffect(() => {
    if (!open) {
      return;
    }
    setRoomNumber(initial?.roomNumber ?? '');
    setName(initial?.name ?? '');
    setArea(initial?.area === null || initial?.area === undefined ? '' : String(initial.area));
    setCapacity(String(initial?.capacity ?? 1));
    setStatus(String(initial?.status ?? 1));
    setDescription(initial?.description ?? '');
    setRoomType(initial?.roomType ?? '');
    setDirection(initial?.direction === null || initial?.direction === undefined
      ? ''
      : String(initial.direction));
    setTouched(false);
    create.reset();
    update.reset();
    setSelectedBuildingId('');
    setSelectedFloorId('');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, initial, floorId]);

  const needsPicker = mode === 'create' && floorId === undefined;

  const buildingOptions = (buildings.data ?? []).map((building) => ({
    value: building.id,
    label: building.name,
  }));

  const floorOptions = useMemo(
    () =>
      allFloors
        .filter((floor) => floor.buildingId === selectedBuildingId)
        .sort((a, b) => a.floorNumber - b.floorNumber)
        .map((floor) => ({
          value: floor.id,
          label: `Tầng ${floor.floorNumber}${floor.name ? ` — ${floor.name}` : ''}`,
        })),
    [allFloors, selectedBuildingId],
  );

  // UpdateRoomDto has no floorId: an edit always stays on the room's own floor.
  const effectiveFloorId = floorId ?? (mode === 'edit' ? (initial?.floorId ?? '') : selectedFloorId);
  const effectiveFloor = allFloors.find((floor) => floor.id === effectiveFloorId);

  const view = describeMutationError(mutation.error, 'room', mode);

  const roomNumberError = (() => {
    if (!touched) {
      return undefined;
    }
    if (!effectiveFloor) {
      return 'Vui lòng chọn tầng trước.';
    }
    return validateRoomNumber(roomNumber, effectiveFloor.floorNumber) ?? undefined;
  })();

  const nameError = touched && name.trim().length === 0 ? 'Vui lòng nhập tên phòng.' : undefined;

  const capacityError = (() => {
    if (!touched) {
      return undefined;
    }
    const parsed = Number(capacity);
    if (capacity.trim().length === 0 || !Number.isInteger(parsed) || parsed < 1 || parsed > 100) {
      return 'Sức chứa phải là số nguyên từ 1 đến 100.';
    }
    return undefined;
  })();

  const areaError = (() => {
    if (area.trim().length === 0) {
      return undefined;
    }
    const parsed = Number(area);
    if (!Number.isFinite(parsed) || parsed < 0.1 || parsed > 10000) {
      return 'Diện tích phải từ 0.1 đến 10000 m².';
    }
    return undefined;
  })();

  const statusValue = Number(status);
  const statusError =
    touched && !(statusValue >= 1 && statusValue <= 5)
      ? 'Vui lòng chọn trạng thái cho phòng.'
      : undefined;

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (mutation.isPending) {
      return;
    }
    setTouched(true);

    if (
      !effectiveFloor ||
      validateRoomNumber(roomNumber, effectiveFloor.floorNumber) !== null ||
      name.trim().length === 0 ||
      !Number.isInteger(Number(capacity)) ||
      Number(capacity) < 1 ||
      Number(capacity) > 100 ||
      !(statusValue >= 1 && statusValue <= 5) ||
      (area.trim().length > 0 && areaError !== undefined)
    ) {
      return;
    }

    const parsedArea = area.trim().length === 0 ? null : Number(area);
    const payload = {
      roomNumber: roomNumber.trim(),
      name: name.trim(),
      area: parsedArea,
      capacity: Number(capacity),
      status: statusValue as RoomStatus,
      description: description.trim().length === 0 ? null : description.trim(),
      roomType: roomType.trim().length === 0 ? null : roomType.trim(),
      direction: direction === '' ? null : (Number(direction) as RoomDirection),
    };

    if (mode === 'create') {
      create.mutate({ ...payload, floorId: effectiveFloorId }, { onSuccess: onClose });
    } else if (initial) {
      update.mutate({ id: initial.id, input: payload }, { onSuccess: onClose });
    }
  }

  const contextLabel = effectiveFloor
    ? `${(buildings.data ?? []).find((b) => b.id === effectiveFloor.buildingId)?.name ?? '—'} / Tầng ${effectiveFloor.floorNumber}`
    : null;

  return (
    <Dialog
      open={open}
      onClose={onClose}
      busy={mutation.isPending}
      size="lg"
      title={mode === 'create' ? 'Thêm phòng' : `Chỉnh sửa phòng ${initial?.roomNumber ?? ''}`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Huỷ
          </Button>
          <Button type="submit" form="room-form" loading={mutation.isPending}>
            {mode === 'create' ? 'Tạo phòng' : 'Lưu thay đổi'}
          </Button>
        </>
      }
    >
      <form id="room-form" onSubmit={handleSubmit} className="flex flex-col gap-4" noValidate>
        {view && (
          <Alert tone="error">
            {view.formError}
            {view.traceId && (
              <span className="mt-1 block font-mono text-[11px]">traceId: {view.traceId}</span>
            )}
          </Alert>
        )}

        {needsPicker ? (
          <div className="grid gap-4 sm:grid-cols-2">
            <SelectField
              label="Tòa nhà"
              required
              placeholder="Chọn tòa nhà"
              options={buildingOptions}
              value={selectedBuildingId}
              onChange={(event) => {
                setSelectedBuildingId(event.target.value);
                setSelectedFloorId('');
              }}
              hint={buildings.error ? 'Chưa tải được danh sách tòa nhà.' : undefined}
            />
            <SelectField
              label="Tầng"
              required
              placeholder="Chọn tầng"
              options={floorOptions}
              value={selectedFloorId}
              disabled={!selectedBuildingId}
              onChange={(event) => setSelectedFloorId(event.target.value)}
              hint="Tầng quyết định quy tắc mã phòng."
            />
          </div>
        ) : (
          contextLabel && (
            <div className="rounded-lg bg-surface-low px-3 py-2 text-xs text-on-surface-variant">
              Thuộc: <span className="font-semibold text-on-surface">{contextLabel}</span>
            </div>
          )
        )}

        <div className="grid gap-4 sm:grid-cols-2">
          <Field
            label="Mã phòng"
            required
            autoFocus
            maxLength={32}
            inputMode="numeric"
            value={roomNumber}
            onChange={(event) => setRoomNumber(event.target.value)}
            error={view?.fieldErrors.roomNumber ?? roomNumberError}
            hint={
              effectiveFloor
                ? `Tầng ${effectiveFloor.floorNumber}: dùng ít nhất 3 chữ số, bắt đầu bằng ${effectiveFloor.floorNumber} (ví dụ ${suggestRoomNumber(effectiveFloor.floorNumber)}).`
                : 'Chọn tầng để biết quy tắc mã phòng.'
            }
          />
          <Field
            label="Tên phòng"
            required
            maxLength={128}
            value={name}
            onChange={(event) => setName(event.target.value)}
            error={view?.fieldErrors.name ?? nameError}
            placeholder="Ví dụ: Phòng đơn có ban công"
          />
        </div>

        <div className="grid gap-4 sm:grid-cols-3">
          <Field
            label="Diện tích (m²)"
            type="number"
            step="0.1"
            min={0.1}
            max={10000}
            value={area}
            onChange={(event) => setArea(event.target.value)}
            error={view?.fieldErrors.area ?? areaError}
            hint="Không bắt buộc."
          />
          <Field
            label="Sức chứa"
            required
            type="number"
            step={1}
            min={1}
            max={100}
            value={capacity}
            onChange={(event) => setCapacity(event.target.value)}
            error={view?.fieldErrors.capacity ?? capacityError}
          />
          <SelectField
            label="Trạng thái"
            required
            options={STATUS_OPTIONS}
            value={status}
            onChange={(event) => setStatus(event.target.value)}
            error={view?.fieldErrors.status ?? statusError}
          />
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field
            label="Loại phòng"
            maxLength={64}
            value={roomType}
            onChange={(event) => setRoomType(event.target.value)}
            error={view?.fieldErrors.roomType}
            placeholder="Ví dụ: Phòng tiêu chuẩn"
            hint="Không bắt buộc, tối đa 64 ký tự."
          />
          <SelectField
            label="Hướng"
            options={DIRECTION_OPTIONS}
            value={direction}
            onChange={(event) => setDirection(event.target.value)}
            error={view?.fieldErrors.direction}
          />
        </div>

        <TextareaField
          label="Mô tả"
          rows={3}
          maxLength={500}
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          error={view?.fieldErrors.description}
          hint="Không bắt buộc, tối đa 500 ký tự."
        />
      </form>
    </Dialog>
  );
}
