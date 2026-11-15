import { useEffect, useState } from 'react';
import { Dialog } from '../../components/ui/Dialog';
import { Button } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { SelectField } from '../../components/ui/SelectField';
import { TextareaField } from '../../components/ui/TextareaField';
import { Alert } from '../../components/ui/Alert';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { useCreateFloor, useRooms, useUpdateFloor } from '../../lib/api/buildingQueries';
import { floorStatusMeta } from '../../lib/api/types';
import type { FloorDto, FloorStatus } from '../../lib/api/types';

const STATUS_OPTIONS = ([1, 2, 3] as FloorStatus[]).map((value) => ({
  value: String(value),
  label: floorStatusMeta[value].label,
}));

interface FloorFormDialogProps {
  open: boolean;
  mode: 'create' | 'edit';
  buildingId: string;
  initial?: FloorDto | null;
  onClose: () => void;
}

export function FloorFormDialog({ open, mode, buildingId, initial, onClose }: FloorFormDialogProps) {
  const create = useCreateFloor();
  const update = useUpdateFloor();
  const mutation = mode === 'create' ? create : update;
  const rooms = useRooms();

  const [floorNumber, setFloorNumber] = useState('0');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [status, setStatus] = useState<FloorStatus>(1);
  const [touched, setTouched] = useState(false);

  useEffect(() => {
    if (!open) {
      return;
    }
    setFloorNumber(String(initial?.floorNumber ?? 0));
    setName(initial?.name ?? '');
    setDescription(initial?.description ?? '');
    setStatus(initial?.status ?? 1);
    setTouched(false);
    create.reset();
    update.reset();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, initial]);

  const view = describeMutationError(mutation.error, 'floor', mode);

  const parsedFloor = Number(floorNumber);
  const floorNumberError = (() => {
    if (!touched) {
      return undefined;
    }
    if (floorNumber.trim().length === 0) {
      return 'Vui lòng nhập số tầng.';
    }
    if (!Number.isInteger(parsedFloor) || parsedFloor < 0) {
      return 'Số tầng phải là số nguyên không âm.';
    }
    return undefined;
  })();
  const nameError = touched && name.trim().length === 0 ? 'Vui lòng nhập tên tầng.' : undefined;

  // Renumbering leaves the child rooms' numbers violating the floor rule, and
  // the server does not re-validate them. Warn rather than silently corrupt.
  const hasRooms = (rooms.data ?? []).some((room) => room.floorId === initial?.id);
  const renumbering =
    mode === 'edit' &&
    initial !== undefined &&
    initial !== null &&
    Number.isInteger(parsedFloor) &&
    parsedFloor !== initial.floorNumber;

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (mutation.isPending) {
      return;
    }
    setTouched(true);
    if (
      floorNumber.trim().length === 0 ||
      !Number.isInteger(parsedFloor) ||
      parsedFloor < 0 ||
      name.trim().length === 0
    ) {
      return;
    }

    const payload = {
      name: name.trim(),
      description: description.trim().length === 0 ? null : description.trim(),
      floorNumber: parsedFloor,
      status,
    };

    if (mode === 'create') {
      create.mutate({ ...payload, buildingId }, { onSuccess: onClose });
    } else if (initial) {
      update.mutate({ id: initial.id, input: payload }, { onSuccess: onClose });
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      busy={mutation.isPending}
      size="md"
      title={mode === 'create' ? 'Thêm tầng' : `Chỉnh sửa tầng ${initial?.floorNumber ?? ''}`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Huỷ
          </Button>
          <Button type="submit" form="floor-form" loading={mutation.isPending}>
            {mode === 'create' ? 'Tạo tầng' : 'Lưu thay đổi'}
          </Button>
        </>
      }
    >
      <form id="floor-form" onSubmit={handleSubmit} className="flex flex-col gap-4" noValidate>
        {view && (
          <Alert tone="error">
            {view.formError}
            {view.traceId && (
              <span className="mt-1 block font-mono text-[11px]">traceId: {view.traceId}</span>
            )}
          </Alert>
        )}

        {renumbering && hasRooms && (
          <Alert tone="warning">
            Tầng này đang có phòng. Đổi số tầng sẽ làm mã các phòng hiện có không còn khớp quy tắc
            (mã phòng phải bắt đầu bằng số tầng), và thao tác sửa phòng sau đó sẽ bị máy chủ từ
            chối. Hãy đổi mã phòng trước khi lưu.
          </Alert>
        )}

        <Field
          label="Số tầng"
          required
          autoFocus
          type="number"
          min={0}
          step={1}
          inputMode="numeric"
          value={floorNumber}
          onChange={(event) => setFloorNumber(event.target.value)}
          error={view?.fieldErrors.floorNumber ?? floorNumberError}
          hint="Số 0 là tầng trệt."
        />
        <Field
          label="Tên tầng"
          required
          maxLength={128}
          value={name}
          onChange={(event) => setName(event.target.value)}
          error={view?.fieldErrors.name ?? nameError}
          placeholder="Ví dụ: Tầng 1"
        />
        <TextareaField
          label="Mô tả"
          rows={3}
          maxLength={500}
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          error={view?.fieldErrors.description}
          hint="Không bắt buộc, tối đa 500 ký tự."
        />
        <SelectField
          label="Trạng thái"
          required
          options={STATUS_OPTIONS}
          value={String(status)}
          onChange={(event) => setStatus(Number(event.target.value) as FloorStatus)}
          error={view?.fieldErrors.status}
          hint="Ngừng dùng chỉ là nhãn quản lý — máy chủ vẫn cho phép thao tác trên tầng này."
        />
      </form>
    </Dialog>
  );
}
