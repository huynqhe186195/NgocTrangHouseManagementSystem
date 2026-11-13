import { useAuth } from '../../lib/auth/AuthProvider';

export function OverviewPage() {
  const { user, grantedPolicies } = useAuth();
  const granted = Object.entries(grantedPolicies).filter(([, value]) => value);
  const buildingPolicies = granted.filter(([key]) => key.startsWith('BuildingManagement.'));

  return (
    <div className="mx-auto flex max-w-5xl flex-col gap-6">
      <header>
        <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">
          Xin chào, {user?.name ?? user?.userName}
        </h1>
        <p className="mt-1 text-sm text-on-surface-variant">
          Đây là khung portal. Các màn nghiệp vụ sẽ được bổ sung theo từng phase.
        </p>
      </header>

      <section className="grid gap-4 sm:grid-cols-2">
        <article className="rounded-[0.75rem] border border-outline-variant bg-surface-lowest p-5">
          <p className="text-[11px] font-semibold tracking-wide text-on-surface-variant uppercase">
            Phiên đăng nhập
          </p>
          <p className="mt-2 text-sm text-on-surface">
            Đã xác thực qua OpenIddict. Token chỉ giữ trong bộ nhớ và sessionStorage.
          </p>
        </article>

        <article className="rounded-[0.75rem] border border-outline-variant bg-surface-lowest p-5">
          <p className="text-[11px] font-semibold tracking-wide text-on-surface-variant uppercase">
            Quyền nghiệp vụ
          </p>
          <p className="mt-2 text-sm text-on-surface">
            {buildingPolicies.length > 0
              ? `Được cấp ${buildingPolicies.length} quyền quản lý tòa nhà.`
              : 'Chưa có quyền quản lý tòa nhà. Máy chủ đang từ chối mọi thao tác tòa nhà cho tới khi được cấp.'}
          </p>
        </article>
      </section>

      {buildingPolicies.length > 0 && (
        <section className="rounded-[0.75rem] border border-outline-variant bg-surface-lowest p-5">
          <p className="mb-3 text-[11px] font-semibold tracking-wide text-on-surface-variant uppercase">
            Quyền được cấp
          </p>
          <ul className="flex flex-wrap gap-2">
            {buildingPolicies.map(([policy]) => (
              <li key={policy} className="rounded-full bg-surface-low px-3 py-1 text-xs text-on-surface-variant">
                {policy}
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
