import { Navigate, Route, Routes } from 'react-router-dom';
import { RequireAuth } from './lib/auth/requireAuth';
import { ForgotPasswordPage } from './features/auth/ForgotPasswordPage';
import { LoginPage } from './features/auth/LoginPage';
import { ResetPasswordPage } from './features/auth/ResetPasswordPage';
import { VerifyOtpPage } from './features/auth/VerifyOtpPage';
import { BuildingDetailPage, BuildingsPage } from './features/buildings/BuildingsPage';
import { FloorDesignerPage } from './features/buildings/FloorDesignerPage';
import { FloorPlanPage } from './features/buildings/FloorPlanPage';
import { RoomDetailPage } from './features/rooms/RoomDetailPage';
import { RoomSearchPage } from './features/rooms/RoomSearchPage';
import { RoomsPage } from './features/rooms/RoomsPage';
import { ContractDetailPage } from './features/contracts/ContractDetailPage';
import { ContractsPage } from './features/contracts/ContractsPage';
import { TenantDetailPage } from './features/tenants/TenantDetailPage';
import { TenantsPage } from './features/tenants/TenantsPage';
import { AppShell } from './features/shell/AppShell';
import { ForbiddenPage } from './features/shell/ForbiddenPage';
import { OverviewPage } from './features/shell/OverviewPage';

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/verify-otp" element={<VerifyOtpPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />

      <Route element={<RequireAuth />}>
        <Route element={<AppShell />}>
          <Route path="/" element={<OverviewPage />} />
          <Route path="/buildings" element={<BuildingsPage />} />
          <Route path="/buildings/:buildingId" element={<BuildingDetailPage />} />
          <Route path="/buildings/:buildingId/floors/:floorId" element={<FloorPlanPage />} />
          <Route
            path="/buildings/:buildingId/floors/:floorId/design"
            element={<FloorDesignerPage />}
          />
          <Route path="/rooms" element={<RoomsPage />} />
          <Route path="/rooms/:roomId" element={<RoomDetailPage />} />
          <Route path="/contracts" element={<ContractsPage />} />
          <Route path="/contracts/:contractId" element={<ContractDetailPage />} />
          <Route path="/tenants" element={<TenantsPage />} />
          <Route path="/tenants/:tenantId" element={<TenantDetailPage />} />
          <Route path="/search" element={<RoomSearchPage />} />
          <Route path="/forbidden" element={<ForbiddenPage />} />
        </Route>
      </Route>

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
