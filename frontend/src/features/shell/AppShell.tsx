import { useEffect, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../../lib/auth/AuthProvider';
import { HeaderSearch } from './HeaderSearch';
import { navigationFor, resolveShellRole, type NavItem } from './navigation';
import { NotificationBell } from './NotificationBell';

/**
 * The mockup's brand mark is a green hexagonal badge with a house struck out
 * of it, drawn at 37x42. The logo file is a 1376x768 lockup, so squeezing it
 * into a 36 px square left it unrecognisable; this redraws the badge at the
 * mockup's own proportions and colour (#13BE7B).
 */
function BrandMark({ className = '' }: { className?: string }) {
  return (
    <svg viewBox="0 0 37 42" aria-hidden className={`shrink-0 ${className}`}>
      <path d="M18.5 0 L36.5 10.7 V31.3 L18.5 42 L0.5 31.3 V10.7 Z" fill="#13BE7B" />
      <path
        d="M11.5 27.5 V18 L18.5 10.5 L25.5 18 V27.5"
        fill="none"
        stroke="#0F1D33"
        strokeWidth="5"
        strokeLinejoin="miter"
        strokeLinecap="butt"
      />
      <rect x="18" y="23.5" width="3" height="4.8" fill="#0F1D33" />
    </svg>
  );
}

/**
 * Two shells, not one: the mockup draws a different sidebar for the
 * administration and the tenant. Which one is shown comes from the claims the
 * server issued; the backend remains the final authority on every call.
 */
export function AppShell() {
  const { user, signOut, hasPermission } = useAuth();
  const navigate = useNavigate();

  const role = resolveShellRole(user, hasPermission);
  const sections = navigationFor(role, hasPermission);

  // Desktop: collapse the sidebar to an icon rail. Mobile: slide it over the
  // content. One flag drives both because only one mode is reachable at a time.
  const [navOpen, setNavOpen] = useState(true);
  const [mobileOpen, setMobileOpen] = useState(false);

  // A route change on a phone should not leave the drawer covering the page.
  const location = useLocation();
  useEffect(() => {
    setMobileOpen(false);
  }, [location.pathname]);

  function handleSignOut() {
    signOut();
    navigate('/login', { replace: true });
  }

  const displayName = user?.name ?? user?.userName ?? '';
  const initials = displayName
    .split(' ')
    .filter(Boolean)
    .slice(-2)
    .map((part) => part[0])
    .join('')
    .toUpperCase();

  return (
    <div className="flex min-h-screen bg-surface">
      {mobileOpen && (
        <div
          className="fixed inset-0 z-30 bg-black/40 lg:hidden"
          aria-hidden
          onClick={() => setMobileOpen(false)}
        />
      )}

      <aside
        className={
          'flex shrink-0 flex-col bg-navy text-white ' +
          (mobileOpen
            ? 'fixed inset-y-0 left-0 z-40 w-[238px] '
            : 'hidden w-[238px] lg:flex ') +
          (navOpen ? 'lg:w-[238px]' : 'lg:w-[76px]')
        }
      >
        <div
          className={
            'flex items-center gap-[19px] py-[17px] ' +
            (navOpen ? 'pl-[26px]' : 'lg:justify-center lg:px-2')
          }
        >
          <BrandMark className="h-[42px] w-[37px]" />
          {navOpen && (
            <div className="leading-tight">
              <p className="font-display text-xl font-bold">Ngọc Trang</p>
              <p className="mt-1.5 whitespace-nowrap text-xs text-white/60">
                Hệ thống quản lý nhà trọ
              </p>
            </div>
          )}
        </div>

        <nav className="flex-1 overflow-y-auto px-3 py-2">
          {sections.map((section, index) => (
            <div key={section.title ?? `s${index}`} className="mb-4">
              {section.title && navOpen && (
                <p className="px-3 py-2 text-[10px] font-semibold tracking-widest text-white/40 uppercase">
                  {section.title}
                </p>
              )}
              <ul className="flex flex-col gap-0.5">
                {section.items.map((item) => (
                  <li key={item.label}>
                    <NavRow
                      item={item}
                      collapsed={!navOpen}
                      onNavigate={() => setMobileOpen(false)}
                    />
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-[70px] items-center gap-4 border-b border-outline-variant bg-surface-lowest px-4 sm:px-5">
          <button
            type="button"
            aria-label={mobileOpen || navOpen ? 'Thu gọn menu' : 'Mở menu'}
            aria-expanded={mobileOpen || navOpen}
            onClick={() => {
              // Only one of the two can be on screen at a time, so the same
              // button is the drawer toggle below `lg` and the rail toggle above.
              if (window.matchMedia('(min-width: 1024px)').matches) {
                setNavOpen((open) => !open);
              } else {
                setMobileOpen((open) => !open);
              }
            }}
            className="rounded-lg p-2 text-on-surface-variant transition hover:bg-surface-low"
          >
            <span className="material-symbols-outlined text-xl">menu</span>
          </button>

          <HeaderSearch />

          <div className="ml-auto flex items-center gap-3">
            <NotificationBell />

            <div className="hidden items-center gap-2.5 border-l border-outline-variant pl-3 sm:flex">
              <span className="flex h-9 w-9 items-center justify-center rounded-full bg-surface-container text-xs font-bold text-on-surface-variant">
                {initials || 'NT'}
              </span>
              <div className="leading-tight">
                <p className="text-sm font-semibold text-on-surface">{displayName}</p>
                <p className="text-[11px] text-on-surface-variant">
                  {role === 'admin' ? 'Quản trị viên' : 'Người thuê'}
                </p>
              </div>
            </div>

            <button
              type="button"
              onClick={handleSignOut}
              className="rounded-lg border border-outline-variant px-3 py-1.5 text-xs font-semibold text-on-surface-variant transition hover:bg-surface-low"
            >
              Đăng xuất
            </button>
          </div>
        </header>

        <main className="flex-1 overflow-y-auto p-4 sm:p-5">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function NavRow({
  item,
  collapsed,
  onNavigate,
}: {
  item: NavItem;
  collapsed: boolean;
  onNavigate: () => void;
}) {
  if (!item.to) {
    return (
      <span
        title={item.unavailableHint}
        aria-disabled
        className={
          'flex cursor-not-allowed items-center gap-3 rounded-[0.5rem] px-3 py-2.5 text-sm text-white/35 ' +
          (collapsed ? 'lg:justify-center lg:px-2' : '')
        }
      >
        <span className="material-symbols-outlined text-[20px]">{item.icon}</span>
        {!collapsed && <span>{item.label}</span>}
      </span>
    );
  }

  return (
    <NavLink
      to={item.to}
      end={item.to === '/'}
      onClick={onNavigate}
      title={collapsed ? item.label : undefined}
      className={({ isActive }) =>
        'flex items-center gap-3 rounded-[0.5rem] px-3 py-2.5 text-sm transition ' +
        (collapsed ? 'lg:justify-center lg:px-2 ' : '') +
        (isActive
          ? 'bg-[#1d4ed8] font-semibold text-white'
          : 'text-white/75 hover:bg-navy-hover hover:text-white')
      }
    >
      <span className="material-symbols-outlined text-[20px]">{item.icon}</span>
      {!collapsed && <span>{item.label}</span>}
    </NavLink>
  );
}
