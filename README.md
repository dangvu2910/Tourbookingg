# Tour Booking — SQLite riêng cho từng service

Ứng dụng gồm Gateway và 8 backend. Mỗi backend có một file SQLite riêng, chỉ mount thư mục dữ liệu của chính mình. RabbitMQ chuyển sự kiện Booking–Payment và Support–Notification. Gateway không cần database.

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

`data/sqlite/migration-report.json` chứa số bản ghi và checksum đã kiểm chứng. Database SQL Server gốc cùng volume backup được giữ lại. Công cụ không xóa hoặc tự thay dữ liệu nguồn bằng dữ liệu mẫu.

## Database riêng

| Backend | File trong dự án | Dữ liệu sở hữu |
| --- | --- | --- |
| Identity | `services/Identity/data/identity.db` | Tài khoản, vai trò, thông tin đăng nhập |
| Catalog | `services/Catalog/data/catalog.db` | Tour |
| Booking | `services/Booking/data/booking.db` | Đơn đặt tour |
| Payment | `services/Payment/data/payment.db` | Giao dịch |
| Review | `services/Review/data/review.db` | Đánh giá và bình chọn |
| Support | `services/Support/data/support.db` | Yêu cầu hỗ trợ và hội thoại |
| Reporting | `services/Reporting/data/reporting.db` | Bản sao chỉ đọc phục vụ báo cáo |
| Notification | `services/Notification/data/notification.db` | Thông báo và khóa chống trùng |

Mỗi service chỉ ghi bảng nghiệp vụ mình sở hữu. Để giữ các màn hình hiện có, backend còn có bản sao chỉ đọc của dữ liệu cần tham chiếu, làm mới qua HTTP nội bộ. Các service vẫn dùng chung model EF và thư viện persistence. Không dùng chung file database, nhưng chưa tách hoàn toàn model độc lập.

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
Database hiện nằm tại `services/<Service>/data/<service>.db`, ví dụ `services/Booking/data/booking.db`. Các thư mục này bị loại khỏi Git và Docker build context. Khi chạy Docker, mỗi thư mục vẫn mount vào `/data` của đúng service. Báo cáo migration cũ vẫn giữ tại `data/sqlite/migration-report.json`.

Migration mặc định chuyển dữ liệu đã kiểm chứng vào từng service. Dùng `-ExportOnly -Destination data/sqlite-trial-2` nếu chỉ muốn xuất một bản thử riêng, không cài vào service. Backup gom tám thư mục database vào cấu trúc `sqlite/<service>` trong bản backup; Restore phân phối lại vào từng service.