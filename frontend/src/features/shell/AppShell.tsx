import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../../lib/auth/AuthProvider';

interface NavItem {
  to: string;
  label: string;
  icon: string;
  /** BuildingManagement permission that reveals the entry; absent = always. */
  policy?: string;
}

const sections: { title?: string; items: NavItem[] }[] = [
  {
    items: [{ to: '/', label: 'Tổng quan', icon: 'home' }],
  },
  {
    title: 'Quản lý',
    // Only routes that exist are listed; every omitted module would 404.
    // Buildings and Rooms are shown without a policy gate because the server
    // denies these calls today and the screen must be able to say so.
    items: [
      { to: '/buildings', label: 'Tòa nhà', icon: 'apartment' },
      { to: '/rooms', label: 'Phòng', icon: 'meeting_room' },
    ],
  },
  {
    title: 'Hệ thống',
    items: [{ to: '/', label: 'Tài khoản', icon: 'settings' }],
  },
];

export function AppShell() {
  const { user, signOut, hasPermission } = useAuth();
  const navigate = useNavigate();

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
      <aside className="hidden w-[238px] shrink-0 flex-col bg-navy text-white lg:flex">
        <div className="flex items-center gap-3 px-5 py-5">
          <img
            src="/ngoc-trang-logo.png"
            alt=""
            className="h-9 w-9 shrink-0 rounded-lg bg-white object-contain p-0.5"
          />
          <div className="leading-tight">
            <p className="font-display text-sm font-bold">Ngọc Trang</p>
            <p className="text-[10px] text-white/60">Hệ thống quản lý nhà trọ</p>
          </div>
        </div>

        <nav className="flex-1 overflow-y-auto px-3 py-2">
          {sections.map((section, index) => (
            <div key={section.title ?? `s${index}`} className="mb-4">
              {section.title && (
                <p className="px-3 py-2 text-[10px] font-semibold tracking-widest text-white/40 uppercase">
                  {section.title}
                </p>
              )}
              <ul className="flex flex-col gap-0.5">
                {section.items
                  .filter((item) => !item.policy || hasPermission(item.policy))
                  .map((item) => (
                    <li key={item.to}>
                      <NavLink
                        to={item.to}
                        end={item.to === '/'}
                        className={({ isActive }) =>
                          'flex items-center gap-3 rounded-[0.5rem] px-3 py-2.5 text-sm transition ' +
                          (isActive
                            ? 'bg-[#1d4ed8] font-semibold text-white'
                            : 'text-white/75 hover:bg-navy-hover hover:text-white')
                        }
                      >
                        <span className="material-symbols-outlined text-[20px]">{item.icon}</span>
                        <span>{item.label}</span>
                      </NavLink>
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
            aria-label="Mở menu"
            className="rounded-lg p-2 text-on-surface-variant transition hover:bg-surface-low lg:hidden"
          >
            <span className="material-symbols-outlined text-xl">menu</span>
          </button>

          <div className="relative hidden max-w-xl flex-1 md:block">
            <span className="material-symbols-outlined pointer-events-none absolute top-1/2 left-3.5 -translate-y-1/2 text-xl text-outline">
              search
            </span>
            <input
              type="search"
              placeholder="Tìm kiếm tòa nhà, tầng, phòng..."
              className="h-10 w-full rounded-lg border border-outline-variant bg-surface-low pl-11 pr-4 text-sm text-on-surface transition-colors placeholder:text-outline focus:border-primary focus:bg-surface-lowest focus:outline-none"
            />
          </div>

          <div className="ml-auto flex items-center gap-3">
            <button
              type="button"
              aria-label="Thông báo"
              className="relative rounded-lg p-2 text-on-surface-variant transition hover:bg-surface-low"
            >
              <span className="material-symbols-outlined text-xl">notifications</span>
            </button>

            <div className="hidden items-center gap-2.5 border-l border-outline-variant pl-3 sm:flex">
              <span className="flex h-9 w-9 items-center justify-center rounded-full bg-surface-container text-xs font-bold text-on-surface-variant">
                {initials || 'NT'}
              </span>
              <div className="leading-tight">
                <p className="text-sm font-semibold text-on-surface">{displayName}</p>
                <p className="text-[11px] text-on-surface-variant">
                  {user?.roles?.[0] ?? user?.email ?? 'Quản trị viên'}
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
