import { useEffect, useState } from 'react';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Field } from '../../components/ui/Field';
import { SelectField } from '../../components/ui/SelectField';
import { TextareaField } from '../../components/ui/TextareaField';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { useCreateRoomEquipment, useUpdateRoomEquipment } from '../../lib/api/buildingQueries';
import { equipmentConditionMeta } from '../../lib/api/types';
import type { RoomEquipmentCondition, RoomEquipmentDto } from '../../lib/api/types';

interface EquipmentFormDialogProps {
  open: boolean;
  roomId: string;
  mode: 'create' | 'edit';
  initial?: RoomEquipmentDto | null;
  onClose: () => void;
}

const CONDITION_OPTIONS = (Object.keys(equipmentConditionMeta) as unknown as string[]).map(
  (key) => ({
    value: key,
    label: equipmentConditionMeta[Number(key) as RoomEquipmentCondition].label,
  }),
);

export function EquipmentFormDialog({
  open,
  roomId,
  mode,
  initial = null,
  onClose,
}: EquipmentFormDialogProps) {
  const create = useCreateRoomEquipment(roomId);
  const update = useUpdateRoomEquipment(roomId);
  const pending = create.isPending || update.isPending;
  const errorView = describeMutationError(
    create.error ?? update.error,
    'equipment',
    mode === 'create' ? 'create' : 'edit',
  );

  const [name, setName] = useState('');
  const [category, setCategory] = useState('');
  const [quantity, setQuantity] = useState('1');
  const [condition, setCondition] = useState('1');
  const [notes, setNotes] = useState('');
  const [localError, setLocalError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }
    create.reset();
    update.reset();
    setLocalError(null);
    setName(initial?.name ?? '');
    setCategory(initial?.category ?? '');
    setQuantity(String(initial?.quantity ?? 1));
    setCondition(String(initial?.condition ?? 1));
    setNotes(initial?.notes ?? '');
    // The mutation objects are recreated each render; only the open flag and
    // the record being edited should reset the form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, initial]);

  function submit(event: React.FormEvent) {
    event.preventDefault();
    setLocalError(null);

    const trimmedName = name.trim();
    if (!trimmedName) {
      setLocalError('Tên thiết bị không được để trống.');
      return;
    }
    const parsedQuantity = Number(quantity);
    if (!Number.isInteger(parsedQuantity) || parsedQuantity < 1) {
      setLocalError('Số lượng phải là số nguyên từ 1 trở lên.');
      return;
    }

    const input = {
      name: trimmedName,
      category: category.trim() || null,
      quantity: parsedQuantity,
      condition: Number(condition) as RoomEquipmentCondition,
      installedDate: initial?.installedDate ?? null,
      notes: notes.trim() || null,
    };

    if (mode === 'create') {
      create.mutate(input, { onSuccess: onClose });
      return;
    }
    if (!initial) {
      return;
    }
    update.mutate({ equipmentId: initial.id, input }, { onSuccess: onClose });
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      busy={pending}
      title={mode === 'create' ? 'Thêm thiết bị' : 'Sửa thiết bị'}
      description="Thiết bị được lưu trên máy chủ và hiển thị cho cả quản trị viên lẫn khách xem phòng."
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={pending}>
            Hủy
          </Button>
          <Button type="submit" form="equipment-form" loading={pending}>
            {mode === 'create' ? 'Thêm thiết bị' : 'Lưu thay đổi'}
          </Button>
        </>
      }
    >
      <form id="equipment-form" onSubmit={submit} className="flex flex-col gap-4">
        {errorView && <Alert tone="error">{errorView.formError}</Alert>}
        {localError && <Alert tone="error">{localError}</Alert>}

        <Field
          label="Tên thiết bị"
          required
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Máy lạnh, Giường, Tủ quần áo…"
          error={errorView?.fieldErrors.name}
          maxLength={128}
          autoFocus
        />

        <Field
          label="Nhóm thiết bị"
          value={category}
          onChange={(event) => setCategory(event.target.value)}
          placeholder="Điện lạnh, Nội thất…"
          error={errorView?.fieldErrors.category}
          maxLength={64}
        />

        <div className="grid gap-3 sm:grid-cols-2">
          <Field
            label="Số lượng"
            required
            type="number"
            min={1}
            step={1}
            value={quantity}
            onChange={(event) => setQuantity(event.target.value)}
            error={errorView?.fieldErrors.quantity}
          />
          <SelectField
            label="Tình trạng"
            value={condition}
            onChange={(event) => setCondition(event.target.value)}
            options={CONDITION_OPTIONS}
            error={errorView?.fieldErrors.condition}
          />
        </div>

        <TextareaField
          label="Ghi chú"
          value={notes}
          onChange={(event) => setNotes(event.target.value)}
          rows={3}
          placeholder="Ví dụ: lắp tháng 9/2026, còn bảo hành…"
          error={errorView?.fieldErrors.notes}
        />
      </form>
    </Dialog>
  );
}
