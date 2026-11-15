import type { ReactNode } from 'react';

interface PageHeaderProps {
  title: string;
  description?: string;
  breadcrumb?: string[];
  actions?: ReactNode;
  /**
   * The building mockup puts the breadcrumb on the far right of the title row.
   * Other screens still read it above the title, so the default stays 'left'.
   */
  breadcrumbAlign?: 'left' | 'right';
}

function Breadcrumb({ crumbs, className = '' }: { crumbs: string[]; className?: string }) {
  return (
    <nav
      aria-label="Breadcrumb"
      className={`flex flex-wrap items-center gap-1.5 text-xs text-on-surface-variant ${className}`}
    >
      {crumbs.map((crumb, index) => (
        <span key={`${crumb}-${index}`} className="flex items-center gap-1.5">
          {index > 0 && <span aria-hidden className="text-outline">/</span>}
          <span className={index === crumbs.length - 1 ? 'font-semibold text-primary' : ''}>
            {crumb}
          </span>
        </span>
      ))}
    </nav>
  );
}

export function PageHeader({
  title,
  description,
  breadcrumb,
  actions,
  breadcrumbAlign = 'left',
}: PageHeaderProps) {
  const crumbs = breadcrumb ?? [];
  const alignRight = breadcrumbAlign === 'right' && crumbs.length > 0;

  return (
    <div className="mb-5 flex flex-col justify-between gap-4 md:flex-row md:items-start">
      <div className="min-w-0">
        {!alignRight && crumbs.length > 0 && <Breadcrumb crumbs={crumbs} className="mb-2" />}
        <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">{title}</h1>
        {description && <p className="mt-1 text-sm text-on-surface-variant">{description}</p>}
      </div>

      {(alignRight || actions) && (
        <div className="flex flex-col items-start gap-2 md:items-end">
          {alignRight && <Breadcrumb crumbs={crumbs} />}
          {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
        </div>
      )}
    </div>
  );
}
