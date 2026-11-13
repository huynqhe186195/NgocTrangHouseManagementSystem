import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { Brand } from '../../components/ui/Brand';
import { useAuth } from '../../lib/auth/AuthProvider';

interface NavItem {
  to: string;
  label: string;
  /** BuildingManagement permission that reveals the entry; absent = always. */
  policy?: string;
}

const sections: { title?: string; items: NavItem[] }[] = [
  {
    items: [{ to: '/', label: 'Tổng quan' }],
  },
  {
    title: 'Quản lý',
    items: [
      { to: '/buildings', label: 'Tòa nhà', policy: 'BuildingManagement.Buildings.Read' },
      { to: '/rooms', label: 'Phòng', policy: 'BuildingManagement.Rooms.Read' },
      { to: '/tenants', label: 'Khách thuê', policy: 'BuildingManagement.Rooms.Read' },
      { to: '/contracts', label: 'Hợp đồng', policy: 'BuildingManagement.Contracts.Read' },
      { to: '/invoices', label: 'Hóa đơn', policy: 'BuildingManagement.Invoices.ReadRevisionHistory' },
      { to: '/utilities', label: 'Điện nước', policy: 'BuildingManagement.Utilities.Read' },
    ],
  },
];

export function AppShell() {
  const { user, signOut, hasPermission } = useAuth();
  const navigate = useNavigate();

  function handleSignOut() {
    signOut();
    navigate('/login', { replace: true });
  }

  return (
    <div className="flex min-h-screen bg-surface">
      <aside className="hidden w-[238px] shrink-0 flex-col bg-navy text-white lg:flex">
        <div className="flex items-center gap-3 px-5 py-5">
          <div className="rounded-lg bg-white p-1.5">
            <img src="/ngoc-trang-logo.png" alt="" className="h-7 w-7 object-contain" />
          </div>
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
                          'block rounded-[0.5rem] px-3 py-2 text-sm transition ' +
                          (isActive
                            ? 'bg-[#1d4ed8] font-semibold text-white'
                            : 'text-white/75 hover:bg-navy-hover hover:text-white')
                        }
                      >
                        {item.label}
                      </NavLink>
                    </li>
                  ))}
              </ul>
            </div>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-[70px] items-center justify-between gap-4 border-b border-outline-variant bg-surface-lowest px-5">
          <div className="hidden sm:block">
            <Brand compact />
          </div>
          <div className="flex items-center gap-3">
            <div className="text-right leading-tight">
              <p className="text-sm font-semibold text-on-surface">{user?.name ?? user?.userName}</p>
              <p className="text-[11px] text-on-surface-variant">{user?.email ?? 'Quản trị viên'}</p>
            </div>
            <button
              type="button"
              onClick={handleSignOut}
              className="rounded-[0.5rem] border border-outline-variant px-3 py-1.5 text-xs font-semibold text-on-surface-variant transition hover:bg-surface-low"
            >
              Đăng xuất
            </button>
          </div>
        </header>

        <main className="flex-1 overflow-y-auto p-5">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
