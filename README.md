# Tour Booking — SQLite theo backend group

Ứng dụng gồm Gateway, RabbitMQ và 5 backend: Identity, Catalog, Booking Platform (Booking + Payment + Reporting), Customer Experience (Review + Support), Notification. Các chức năng vẫn giữ URL cũ; mỗi backend vật lý có một file SQLite riêng. Gateway không có database.

## Chạy với dữ liệu đã chuyển

```powershell
.\scripts\Initialize-Environment.ps1
.\scripts\Compose.ps1 up -d --build
.\scripts\Compose.ps1 ps
```

Mở **http://localhost:8080**. Tài khoản và mật khẩu cũ được giữ sau khi chuyển đổi. Admin dự phòng được cấu hình trong `.env`. Không commit file dữ liệu hoặc `.env`.

## Chuyển một bản dữ liệu SQL Server mới

```powershell
.\scripts\Migrate-ToSqlite.ps1
```

SQL Server nguồn phải đang chạy. Script dừng các service ghi dữ liệu, tạo backup nguồn và chuyển vào thư mục staging. Chỉ khi đối chiếu thành công mới tạo thư mục đích `data/sqlite`. Không chạy lại vào thư mục đã có dữ liệu. Có thể chọn đích thử nghiệm khác bằng `-Destination data/sqlite-trial-2`.

`data/sqlite/migration-report.json` chứa số bản ghi và checksum đã kiểm chứng cho 5 backend. Database SQL Server gốc cùng volume backup được giữ lại. Công cụ không xóa hoặc tự thay dữ liệu nguồn bằng dữ liệu mẫu.

## Database riêng

| Backend | File trong dự án | Dữ liệu sở hữu |
| --- | --- | --- |
| Identity | `services/Identity/data/identity.db` | Tài khoản, vai trò, thông tin đăng nhập |
| Catalog | `services/Catalog/data/catalog.db` | Tour |
| Booking Platform | `services/BookingPlatform/data/booking-platform.db` | Đơn đặt tour, giao dịch; dashboard và xuất báo cáo |
| Customer Experience | `services/CustomerExperience/data/customer-experience.db` | Đánh giá, bình chọn, yêu cầu hỗ trợ và hội thoại |
| Notification | `services/Notification/data/notification.db` | Thông báo và khóa chống trùng |

Mỗi logical owner chỉ ghi bảng nghiệp vụ mình sở hữu; các owner được gom cùng backend chia sẻ một file SQLite. Để giữ màn hình hiện có, backend còn có bản sao chỉ đọc của dữ liệu cần tham chiếu, làm mới qua HTTP nội bộ. Các project vẫn dùng chung model EF và thư viện persistence, chưa tách hoàn toàn bounded context.

Chi tiết luồng sự kiện, lý do thiết kế và giới hạn trong [kiến trúc SQLite](docs/kien-truc-sqlite.md).

## Kiểm thử

```powershell
dotnet test tests/RabbitMq.Tests/RabbitMq.Tests.csproj --filter "FullyQualifiedName~DataIntegrityTests|FullyQualifiedName~ServiceBoundaryTests|FullyQualifiedName~ServiceUrlHelperTests|FullyQualifiedName~SharedSessionValidationTests"
.\scripts\Compose.ps1 --profile test build tests
.\scripts\Compose.ps1 --profile test run --rm tests
```

Kiểm thử dữ liệu dùng SQLite tạm riêng. Kiểm thử workflow tạo tài khoản, booking và hội thoại thử trong hệ thống đang chạy.
Chạy integration test trong Compose để có RabbitMQ, DNS nội bộ và cấu hình admin từ `.env`; không chạy các workflow này trên dữ liệu production.

## Backup và dữ liệu demo

```powershell
.\scripts\Backup.ps1
.\scripts\Restore.ps1 -BackupDirectory backups\ten-ban-backup -Apply
```

Restore thay dữ liệu hiện tại và giữ bản cũ trong thư mục riêng. Script dừng backend trước khi sao chép dữ liệu, giữ cả WAL nếu có. Không xóa các thư mục `services\*\data`, volume auth keys hoặc ảnh tour khi muốn giữ dữ liệu. Replay sự kiện dùng `.\scripts\Replay-Outbox.ps1 -Service booking -SinceUtc <thoi-diem-UTC>`.

Khi chạy mới với database trống, có thể bật `Database__SeedDemo=true` riêng cho Catalog để tạo ba tour mẫu. Không bật seed khi muốn đối chiếu bản chuyển đổi chính xác.

`compose.sqlserver.yaml` và [tài liệu SQL Server cũ](docs/legacy-sqlserver.md) được giữ để tham khảo nguồn. Rollback ứng dụng cần cả code/image cũ; dữ liệu mới trong SQLite không tự đồng bộ ngược sang SQL Server.
Database hiện nằm tại `services/<Backend>/data/<backend>.db`. Source của các backend đã gộp được đặt trong `services/BookingPlatform/Modules` và `services/CustomerExperience/Modules`; các database rời cũ đã bỏ sau khi xác minh backup trước gộp tại `backups/pre-consolidation-20261026-104448`.

Migration mặc định xuất dữ liệu đã kiểm chứng theo 5 backend. Dùng `-ExportOnly -Destination data/sqlite-trial-2` nếu chỉ muốn xuất một bản thử riêng, không cài vào service. Backup lưu 5 thư mục database trong `sqlite/<backend>`; Restore hỗ trợ cả backup 5 backend hiện tại và backup 8 database cũ.

Để gom bộ SQLite đang chạy trước đây, tạo backup trước rồi chạy:

```powershell
.\scripts\Backup.ps1
.\scripts\Consolidate-Sqlite.ps1 -BackupDirectory backups\ten-ban-backup
.\scripts\Consolidate-Sqlite.ps1 -BackupDirectory backups\ten-ban-backup -Apply
```

Lần chạy đầu tạo bản staging đã qua `integrity_check` mà không đổi database hiện tại. Chỉ dùng `-Apply` sau khi kiểm tra staging và backup.