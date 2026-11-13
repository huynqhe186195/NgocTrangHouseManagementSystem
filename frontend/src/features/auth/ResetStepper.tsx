interface ResetStepperProps {
  /** Zero-based index of the active step. */
  current: 0 | 1 | 2;
}

const steps = [
  { label: 'Bước 1: Tài khoản' },
  { label: 'Bước 2: Mã xác thực' },
  { label: 'Bước 3: Mật khẩu' },
];

/**
 * Horizontal 3-step connector stepper from the forgot-password mockups
 * (Docs/front-end/authentication & authorization/forgot_password_FE_1..3).
 */
export function ResetStepper({ current }: ResetStepperProps) {
  return (
    <div
      aria-label="Tiến trình khôi phục mật khẩu"
      className="mx-auto mb-8 grid w-full max-w-lg grid-cols-[auto_1fr_auto_1fr_auto] items-start justify-center gap-x-2"
    >
      {steps.map((step, index) => {
        const state = index < current ? 'done' : index === current ? 'active' : 'todo';
        const connectorDone = index < current;
        return (
          <div key={step.label} className="col-span-1 contents">
            <div className="flex flex-col items-center gap-1.5">
              <div
                aria-hidden
                className={
                  'flex h-9 w-9 items-center justify-center rounded-full transition-all ' +
                  (state === 'done'
                    ? 'bg-primary-dark text-white shadow-sm'
                    : state === 'active'
                      ? 'bg-primary font-display text-sm font-bold text-white shadow-md shadow-primary/30 ring-4 ring-primary/15'
                      : 'bg-surface-container text-sm font-semibold text-on-surface-variant')
                }
              >
                {state === 'done' ? (
                  <span className="material-symbols-outlined text-lg">check</span>
                ) : (
                  index + 1
                )}
              </div>
              <span
                className={
                  'text-center text-[11px] tracking-tight ' +
                  (state === 'active'
                    ? 'font-bold text-primary'
                    : 'font-medium text-on-surface-variant')
                }
              >
                {step.label}
              </span>
            </div>
            {index < steps.length - 1 && (
              <div
                aria-hidden
                className={
                  'mt-[17px] h-0.5 w-full rounded-full transition-colors ' +
                  (connectorDone ? 'bg-primary' : 'bg-surface-container')
                }
              />
            )}
          </div>
        );
      })}
    </div>
  );
}
