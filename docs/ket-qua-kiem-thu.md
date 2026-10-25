# Kết quả kiểm thử

## Lần xác minh hiện tại — 25/10/2026

Các lệnh dưới đây đã chạy trên worktree hiện tại, với Docker Compose của dự án và dữ liệu demo local.

| Kiểm tra | Kết quả |
| --- | --- |
| Build toàn solution .NET 10 (`dotnet build Tourbooking.sln --no-restore`) | Thành công, 0 lỗi và 0 cảnh báo |
| Kiểm thử unit/biên dịch vụ (`DataIntegrityTests`, `ServiceBoundaryTests`, `ServiceUrlHelperTests`, `SharedSessionValidationTests`) | 33/33 đạt |
| Build image test trong Docker Compose | Thành công |
| Integration test đầy đủ trong Compose, gồm RabbitMQ và workflow HTTP qua Gateway | 39/39 đạt |
| Resilience test: tạo booking khi RabbitMQ dừng, sau đó khởi động broker và nhận lại sự kiện | Đạt |
| Gateway và tám backend sau khi kiểm thử | Healthy |

### Lệnh tái chạy trên Windows

Từ thư mục gốc dự án, dùng PowerShell:

```powershell
dotnet build Tourbooking.sln --no-restore
dotnet test tests\RabbitMq.Tests\RabbitMq.Tests.csproj --no-build --filter "FullyQualifiedName~DataIntegrityTests|FullyQualifiedName~ServiceBoundaryTests|FullyQualifiedName~ServiceUrlHelperTests|FullyQualifiedName~SharedSessionValidationTests"
.\scripts\Compose.ps1 --profile test build tests
.\scripts\Compose.ps1 --profile test run --rm tests
.\scripts\Test-Resilience.ps1
.\scripts\Compose.ps1 ps
```

Integration test cần chạy trong Compose: test dùng DNS nội bộ như `payment`, RabbitMQ và thông tin admin được truyền qua `.env`. Chạy `dotnet test` trực tiếp trên Windows không thiết lập các phụ thuộc đó nên không phải phép xác minh integration hợp lệ.

Test workflow đăng ký người dùng và tạo booking/yêu cầu hỗ trợ thử trong các database đang mount. Chỉ chạy trên dữ liệu development/demo; không chạy trên dữ liệu production. Không dùng `down -v` để dọn test vì lệnh đó xóa volume Docker.

## Phạm vi chưa được xác nhận trong lần này

Không chạy thử tải cao, diễn tập Backup/Restore trên bản sao riêng, thử failover phần cứng hoặc xác nhận thanh toán qua nhà cung cấp thật. Kiến trúc và các giới hạn này được mô tả trong [kiến trúc SQLite](kien-truc-sqlite.md). Các kết quả kiểm thử cũ của kiến trúc SQL Server không được dùng làm bằng chứng cho phiên bản hiện tại.
