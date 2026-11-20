import { BuildingPolicies } from '../../lib/auth/policies';
import type { CurrentUser } from '../../lib/auth/AuthProvider';

/**
 * The mockup has two different sidebars: the administration shell and the
 * tenant shell. The role is read from the server-issued claims, never chosen
 * in the browser — but it only decides which links are *shown*. The backend
 * still authorizes every call.
 */

export type ShellRole = 'admin' | 'tenant';

export interface NavItem {
  label: string;
  icon: string;
  /**
   * Absent when the module has no route yet. Such an entry is rendered as a
   * disabled row rather than a link, so nothing leads to a 404 or a fake page.
   */
  to?: string;
  /** Reasons the entry is not available, shown as the row's tooltip. */
  unavailableHint?: string;
}

export interface NavSection {
  title?: string;
  items: NavItem[];
}

const NOT_BUILT = 'Chức năng này chưa được xây dựng trên hệ thống.';

export const NAV_ADMIN: NavSection[] = [
  {
    items: [
      { label: 'Tổng quan', icon: 'home', to: '/' },
      { label: 'Tòa nhà', icon: 'apartment', to: '/buildings' },
      { label: 'Phòng', icon: 'meeting_room', to: '/rooms' },
      { label: 'Khách thuê', icon: 'group', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Hợp đồng', icon: 'description', to: '/contracts' },
      { label: 'Hóa đơn', icon: 'receipt_long', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Thu chi', icon: 'account_balance_wallet', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Báo cáo', icon: 'bar_chart', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Người dùng', icon: 'person', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Cài đặt', icon: 'settings', to: undefined, unavailableHint: NOT_BUILT },
    ],
  },
];

export const NAV_TENANT: NavSection[] = [
  {
    items: [
      { label: 'Trang chủ', icon: 'home', to: '/' },
      { label: 'Tìm phòng', icon: 'search', to: '/search' },
      { label: 'Tòa nhà', icon: 'apartment', to: '/buildings' },
      { label: 'Tiện ích', icon: 'widgets', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Hợp đồng', icon: 'description', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Thanh toán', icon: 'payments', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Hỗ trợ', icon: 'support_agent', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Thông báo', icon: 'notifications', to: undefined, unavailableHint: NOT_BUILT },
      { label: 'Cài đặt', icon: 'settings', to: undefined, unavailableHint: NOT_BUILT },
    ],
  },
];

/**
 * Anyone allowed to manage a building operates the admin shell; everyone else
 * gets the tenant shell. A granted policy is what the server already told us,
 * so this cannot show a shell the account could not use.
 */
export function resolveShellRole(
  user: CurrentUser | null,
  hasPermission: (policy: string) => boolean,
): ShellRole {
  if (!user) {
    return 'tenant';
  }
  if (user.roles.some((role) => role.toLowerCase() === 'admin')) {
    return 'admin';
  }
  const managesSomething =
    hasPermission(BuildingPolicies.buildingsManage) ||
    hasPermission(BuildingPolicies.floorsManage) ||
    hasPermission(BuildingPolicies.roomsManage) ||
    hasPermission(BuildingPolicies.contractsRead) ||
    hasPermission(BuildingPolicies.buildingsBootstrapOwner);
  return managesSomething ? 'admin' : 'tenant';
}

export function navigationFor(role: ShellRole): NavSection[] {
  return role === 'admin' ? NAV_ADMIN : NAV_TENANT;
}
