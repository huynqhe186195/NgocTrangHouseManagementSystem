import type { ReactNode } from 'react';
import { Modal } from 'antd';

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  size?: 'sm' | 'md' | 'lg';
  /** While true, Escape and backdrop clicks are ignored so in-flight work is not lost. */
  busy?: boolean;
}

const WIDTHS: Record<NonNullable<DialogProps['size']>, number> = {
  sm: 448,
  md: 512,
  lg: 640,
};

/**
 * The app's modal, drawn by antd's Modal. Its focus trap, scroll lock and
 * Escape handling are already tested upstream, and a hand-rolled copy of them
 * drifted from the rest of the app whenever a field was added.
 *
 * The API stays the app's own: callers pass `children` and a `footer` of
 * buttons rather than antd's `okButtonProps`/`onOk`, so every dialog in the app
 * keeps its existing call site and its own wording.
 */
export function Dialog({
  open,
  onClose,
  title,
  description,
  children,
  footer,
  size = 'md',
  busy = false,
}: DialogProps) {
  return (
    <Modal
      open={open}
      onCancel={() => {
        if (!busy) {
          onClose();
        }
      }}
      title={
        <div className="min-w-0">
          <h2 className="font-display text-lg font-bold text-on-surface">{title}</h2>
          {description && (
            <p className="mt-1 text-xs leading-relaxed font-normal text-on-surface-variant">
              {description}
            </p>
          )}
        </div>
      }
      footer={footer ?? null}
      width={WIDTHS[size]}
      closable={!busy}
      mask={{ closable: !busy }}
      keyboard={!busy}
      destroyOnHidden
      centered
      styles={{ body: { paddingTop: 12 } }}
    >
      {children}
    </Modal>
  );
}
