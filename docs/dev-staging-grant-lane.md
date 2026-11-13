# Hướng dẫn tự bật lane staging Slice A trên instance dev của Owner (P4.3)

Tài liệu này dành cho **chính Owner** tự thực hiện trên instance dev
(`https://localhost:44311` + Vite `5173`). Claude **không** tự dừng/khởi
động lại instance này. Mọi bước dưới đây Owner chạy bằng tay trong Windows
PowerShell.

## Bản chất của cổng

Building-scope gate hiện **deny-all** ở lane dev vì grant reader mặc định
(`DenyAllBuildingOperatorGrantReader`) trả về danh sách rỗng với mọi user.
Backend **đã có sẵn cơ chế opt-in** mở cổng này — Slice A staging trong
`NgocTrangHouseManagementSystemHttpApiHostModule` (dòng ~136). KHÔNG cần sửa
backend. Khi Host khởi động với đủ điều kiện, các adapter staging thay thế
deny-all:

- `SliceAStagingGrantReader` đọc grant thật từ bảng
  `BuildingOperatorAccessGrant` (đã EF map + migration)
- `SliceAStagingFirstOwnerWriter`: `POST /api/building-management/building`
  tạo Building **và** issue grant đầu tiên cho root trong cùng transaction
- `SliceAStagingWriteBoundary` mở write CRUD cho Floor/Room

## Điều kiện activation (phải đủ TẤT CẢ)

1. Environment = **Staging** (env `ASPNETCORE_ENVIRONMENT=Staging`)
2. `BuildingManagement:SliceAStaging:Enabled` = `true`
3. `BuildingManagement:SliceAStaging:Activation` =
   `I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES`
4. **Hai DB PostgreSQL riêng biệt**, cả hai tên bắt đầu bằng
   `bm_slicea_staging_` (một cho Identity/`Default`, một cho
   `BuildingManagement`) — Host kiểm tra tên qua connection string
5. **Đúng 1 root user id** ràng buộc từ key CÓ CHỈ MỤC
   `BuildingManagement:Bootstrap:RootUserIds:0` (một value scalar sẽ bind
   thành string và `Get<Guid[]>()` trả về null → Host từ chối khởi động)

## Các bước trên instance dev

### 0. Chuẩn bị (một lần)

Chuẩn bị 2 database dùng-một-lần trên container disposable hiện có
(`bm-p21-pg-15432`, superuser `postgres`, port 15432 — KHÔNG đụng 15433/5432):

```powershell
$container = 'bm-p21-pg-15432'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$identityDb  = "bm_slicea_staging_identity_$stamp"
$buildingDb  = "bm_slicea_staging_building_$stamp"
foreach ($db in $identityDb, $buildingDb) {
    & docker exec $container createdb -U postgres $db
    if ($LASTEXITCODE -ne 0) { throw "createdb $db failed" }
}
```

Nếu chưa có container: `docker run -d --name bm-p21-pg-15432 -e POSTGRES_PASSWORD=postgres -p 127.0.0.1:15432:5432 postgres:16`

### 1. Migrate + seed admin

```powershell
$env:ConnectionStrings__Default = "Host=127.0.0.1;Port=15432;Database=$identityDb;Username=postgres;Password=postgres"
$env:ConnectionStrings__BuildingManagement = "Host=127.0.0.1;Port=15432;Database=$buildingDb;Username=postgres;Password=postgres"
$env:NgocTrang__LocalBootstrap__AdminPassword = '<password >= 16 chars, GIỮ KÍN>'

dotnet run --project src\NgocTrangHouseManagementSystem.DbMigrator
```

### 1b. Migrate schema BuildingManagement (DbMigrator KHÔNG làm bước này)

DbMigrator chỉ migrate DbContext Identity/`Default`. Schema BM (bảng
`Buildings`/`Floors`/`Rooms`/`BuildingOperatorAccessGrants`...) là một
DbContext + chuỗi migration riêng và **phải được áp dụng thủ công**:

```powershell
# Chạy TỪ THƯ MỤC DbMigrator (design-time factory đọc appsettings.json
# từ thư mục hiện tại để lấy connection string).
Push-Location src\NgocTrangHouseManagementSystem.DbMigrator
dotnet ef database update `
    --project ..\..\modules\buildingmanagement\src\BuildingManagement.EntityFrameworkCore\BuildingManagement.EntityFrameworkCore.csproj `
    --startup-project .\NgocTrangHouseManagementSystem.DbMigrator.csproj `
    --context BuildingManagementDbContext
Pop-Location
```

Không có bước này thì `POST /api/building-management/building` sẽ trả 500
với lỗi SQL `relation "Buildings" does not exist`.

Admin id do GuidGenerator seed nên **khác nhau mỗi DB** — phải đọc lại:

```powershell
# PS 5.1 strips quotes from native args: pipe SQL through stdin.
$adminId = ('SELECT "Id"::text FROM "AbpUsers" WHERE "UserName" = ''admin'' LIMIT 1' |
    & docker exec -i $container psql -U postgres -d $identityDb -t -A).Trim()

foreach ($name in
    'BuildingManagement.Buildings.Read',
    'BuildingManagement.Buildings.Manage',
    'BuildingManagement.Buildings.BootstrapOwner',
    'BuildingManagement.Buildings.AssignOperators',
    'BuildingManagement.Buildings.RevokeOperators',
    'BuildingManagement.Floors.Read',
    'BuildingManagement.Floors.Manage',
    'BuildingManagement.Rooms.Read',
    'BuildingManagement.Rooms.Manage') {
    ("INSERT INTO ""AbpPermissionGrants"" (""Id"", ""Name"", ""ProviderName"", ""ProviderKey"", ""TenantId"") " +
     "VALUES (gen_random_uuid()::uuid, '$name', 'U', '$adminId', NULL)") |
        & docker exec -i $container psql -U postgres -d $identityDb -t -A
}
```

Ghi chú: cột `Id` của `AbpPermissionGrants` **không có default** — bắt buộc
`gen_random_uuid()::uuid`. `ProviderName='U'` = user-level grant, tương đương
`IPermissionManager.SetForUserAsync`.

### 3. Khởi động Host với activation

Cùng cửa sổ PowerShell (env đã set từ bước 1) — thêm:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Staging'
$env:DOTNET_ENVIRONMENT = 'Staging'
$env:ASPNETCORE_URLS = 'https://localhost:44311'
$env:BuildingManagement__SliceAStaging__Enabled = 'true'
$env:BuildingManagement__SliceAStaging__Activation = 'I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES'
$env:BuildingManagement__Bootstrap__RootUserIds__0 = $adminId   # KEY CÓ CHỈ MỤC, không phải scalar

dotnet run --project src\NgocTrangHouseManagementSystem.HttpApi.Host
```

Nếu Host throw `One explicitly designated root account is required for
staging bootstrap` → bạn set `RootUserIds` như scalar thay vì `__0`.
Nếu Host throw `Slice A requires TWO distinct isolated...` → kiểm tra hai
connection string trỏ hai DB `bm_slicea_staging_*` khác nhau.

### 4. Vite proxy

```powershell
$env:VITE_PROXY_TARGET = 'https://localhost:44311'
cd frontend; npm run dev
```

### 5. Dùng

Đăng nhập bằng `admin` + password đã set ở bước 1. Màn Buildings: **tạo
Building đầu tiên** (POST này tự issue grant cho root — atomic), rồi tạo
Floor/Room. Cả ba màn Buildings/FloorPlan/Rooms giờ trả 200 + dữ liệu thật.
A random building id (không thuộc phạm vi) vẫn bị từ chối — gate vẫn chặt
theo scope, chỉ hết deny-all.

## Giới hạn

- Đây là **lane staging cô lập** (opt-in bằng token) — không mở production
- Bảng grant + hai DB dùng-một-lần không tự xoá: xoá tay bằng
  `docker exec $container dropdb -U postgres <db>` khi không dùng nữa
- payOS, webhook, bank transfer: KHÔNG đụng (giữ nguyên HOLD)
- Mọi instance production phải giữ deny-all mặc định

## Tham chiếu kỹ thuật

- Module activation: `src\NgocTrangHouseManagementSystem.HttpApi.Host\NgocTrangHouseManagementSystemHttpApiHostModule.cs` (dòng 136+)
- Grant persistence: `BuildingManagementDbContext.OperatorBuildingGrants` + `BuildingOperatorAccessGrantDesignConfiguration`
- Atomic first-owner grant: `EfBuildingOwnerBootstrapAtomicWriter.CreateWithOwnerAtomicallyAsync`
- Assign/revoke grants runtime: `SliceAStagingOperatorGrantsController` (`/api/slice-a-staging/operator-grants`)
- E2E tự động (lane disposable 44321/5273, không đụng dev instance):
  `etc\scripts\verify-p43-slicea-staging-gate.ps1`
