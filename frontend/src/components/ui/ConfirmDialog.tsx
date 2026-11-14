import type { ReactNode } from 'react';
import { Dialog } from './Dialog';
import { Button } from './Button';
import { Alert } from './Alert';

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  description: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  pending?: boolean;
  error?: string | null;
  traceId?: string | null;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = 'Xoá',
  cancelLabel = 'Huỷ',
  pending = false,
  error = null,
  traceId = null,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title={title}
      size="sm"
      busy={pending}
      footer={
        <>
          <Button variant="secondary" onClick={onCancel} disabled={pending}>
            {cancelLabel}
          </Button>
          <Button
            onClick={onConfirm}
            loading={pending}
            className="bg-[#F43F5E] text-white hover:bg-[#E11D48] focus-visible:ring-[#F43F5E]/25"
          >
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <div className="text-sm leading-relaxed text-on-surface-variant">{description}</div>
        {error && (
          <Alert tone="error">
            {error}
            {traceId && (
              <span className="mt-1 block font-mono text-[11px]">traceId: {traceId}</span>
            )}
          </Alert>
        )}
      </div>
    </Dialog>
  );
}
