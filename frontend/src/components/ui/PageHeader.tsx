import type { ReactNode } from 'react';

interface PageHeaderProps {
  title: string;
  description?: string;
  breadcrumb?: string[];
  actions?: ReactNode;
}

export function PageHeader({ title, description, breadcrumb, actions }: PageHeaderProps) {
  return (
    <div className="mb-5 flex flex-col justify-between gap-4 md:flex-row md:items-start">
      <div className="min-w-0">
        {breadcrumb && breadcrumb.length > 0 && (
          <nav aria-label="Breadcrumb" className="mb-2 flex flex-wrap items-center gap-1.5 text-xs text-on-surface-variant">
            {breadcrumb.map((crumb, index) => (
              <span key={`${crumb}-${index}`} className="flex items-center gap-1.5">
                {index > 0 && <span aria-hidden className="text-outline">/</span>}
                <span className={index === breadcrumb.length - 1 ? 'font-semibold text-primary' : ''}>
                  {crumb}
                </span>
              </span>
            ))}
          </nav>
        )}
        <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">{title}</h1>
        {description && <p className="mt-1 text-sm text-on-surface-variant">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}
