interface StepperProps {
  steps: string[];
  /** Zero-based index of the step in progress. */
  current: number;
}

export function Stepper({ steps, current }: StepperProps) {
  return (
    <ol className="flex items-start justify-between gap-2" aria-label="Tiến trình khôi phục mật khẩu">
      {steps.map((label, index) => {
        const done = index < current;
        const active = index === current;
        return (
          <li key={label} className="flex flex-1 flex-col items-center gap-2 text-center">
            <span
              aria-current={active ? 'step' : undefined}
              className={
                'flex h-10 w-10 items-center justify-center rounded-full text-sm font-semibold ' +
                (done
                  ? 'bg-primary text-on-primary'
                  : active
                    ? 'bg-primary text-on-primary ring-4 ring-primary/15'
                    : 'bg-surface-container text-on-surface-variant')
              }
            >
              {done ? (
                <svg viewBox="0 0 20 20" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="2.5">
                  <path d="M4 10.5l4 4 8-9" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              ) : (
                index + 1
              )}
            </span>
            <span
              className={
                'text-xs ' +
                (active ? 'font-semibold text-primary' : 'text-on-surface-variant')
              }
            >
              {label}
            </span>
          </li>
        );
      })}
    </ol>
  );
}
