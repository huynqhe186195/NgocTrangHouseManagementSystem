import { useEffect, useState } from 'react';
import { Dialog } from '../../components/ui/Dialog';
import { Button } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { TextareaField } from '../../components/ui/TextareaField';
import { Alert } from '../../components/ui/Alert';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { useCreateBuilding, useUpdateBuilding } from '../../lib/api/buildingQueries';
import type { BuildingDto } from '../../lib/api/types';

interface BuildingFormDialogProps {
  open: boolean;
  mode: 'create' | 'edit';
  initial?: BuildingDto | null;
  onClose: () => void;
}

export function BuildingFormDialog({ open, mode, initial, onClose }: BuildingFormDialogProps) {
  const create = useCreateBuilding();
  const update = useUpdateBuilding();
  const mutation = mode === 'create' ? create : update;

  const [name, setName] = useState('');
  const [address, setAddress] = useState('');
  const [description, setDescription] = useState('');
  const [touched, setTouched] = useState(false);

  useEffect(() => {
    if (!open) {
      return;
    }
    setName(initial?.name ?? '');
    setAddress(initial?.address ?? '');
    setDescription(initial?.description ?? '');
    setTouched(false);
    create.reset();
    update.reset();
    // Re-seeding on open only; the mutation objects are stable per render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, initial]);

  const view = describeMutationError(mutation.error, 'building', mode);

  const nameError = touched && name.trim().length === 0 ? 'Vui lòng nhập tên tòa nhà.' : undefined;
  const addressError =
    touched && address.trim().length === 0 ? 'Vui lòng nhập địa chỉ tòa nhà.' : undefined;

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (mutation.isPending) {
      return;
    }
    setTouched(true);
    if (name.trim().length === 0 || address.trim().length === 0) {
      return;
    }

    const input = {
      name: name.trim(),
      address: address.trim(),
      description: description.trim().length === 0 ? null : description.trim(),
    };

    if (mode === 'create') {
      create.mutate(input, { onSuccess: onClose });
    } else if (initial) {
      update.mutate({ id: initial.id, input }, { onSuccess: onClose });
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      busy={mutation.isPending}
      size="md"
      title={mode === 'create' ? 'Thêm tòa nhà' : 'Chỉnh sửa tòa nhà'}
      description={
        mode === 'create'
          ? 'Tòa nhà đầu tiên bạn tạo sẽ cấp quyền chủ sở hữu cho chính tài khoản này.'
          : undefined
      }
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Huỷ
          </Button>
          <Button type="submit" form="building-form" loading={mutation.isPending}>
            {mode === 'create' ? 'Tạo tòa nhà' : 'Lưu thay đổi'}
          </Button>
        </>
      }
    >
      <form id="building-form" onSubmit={handleSubmit} className="flex flex-col gap-4" noValidate>
        {view && (
          <Alert tone="error">
            {view.formError}
            {view.traceId && (
              <span className="mt-1 block font-mono text-[11px]">traceId: {view.traceId}</span>
            )}
          </Alert>
        )}

        <Field
          label="Tên tòa nhà"
          required
          autoFocus
          maxLength={128}
          value={name}
          onChange={(event) => setName(event.target.value)}
          error={view?.fieldErrors.name ?? nameError}
          placeholder="Ví dụ: Ngọc Trang Building"
        />
        <Field
          label="Địa chỉ"
          required
          maxLength={256}
          value={address}
          onChange={(event) => setAddress(event.target.value)}
          error={view?.fieldErrors.address ?? addressError}
          placeholder="Số nhà, đường, phường, quận, tỉnh/thành"
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
      </form>
    </Dialog>
  );
}
