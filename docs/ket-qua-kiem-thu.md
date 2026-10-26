# Kết quả kiểm thử

## Lần xác minh hiện tại — 26/10/2026

Các lệnh dưới đây đã chạy trên worktree hiện tại, với Docker Compose của dự án và dữ liệu demo local.

| Kiểm tra | Kết quả |
| --- | --- |
| Build toàn solution .NET 10 (`dotnet build Tourbooking.sln -m:1`) | Thành công, 0 lỗi và 0 cảnh báo |
| Kiểm thử ranh giới, URL và SQLite | 31/31 đạt |
| Docker Compose cấu hình và build các image | Thành công |
| Backend vật lý | 5/5 healthy |
| Gateway readiness | HTTP 200 |
| SQLite integrity và bản ghi nghiệp vụ sau hợp nhất | Đạt; 12 tài khoản, 3 tour, 11 booking, 6 giao dịch, 1 yêu cầu hỗ trợ, 46 thông báo |
| Backup sau hợp nhất | 38 file; checksum hợp lệ |
| Backup tám database cũ chuyển sang staging năm database | Thành công; integrity check đạt |

### Lệnh tái chạy trên Windows

Từ thư mục gốc dự án, dùng PowerShell:

```powershell
dotnet build Tourbooking.sln -m:1
dotnet test tests\RabbitMq.Tests\RabbitMq.Tests.csproj --filter "FullyQualifiedName~DataIntegrityTests|FullyQualifiedName~ServiceBoundaryTests|FullyQualifiedName~ServiceUrlHelperTests"
.\scripts\Compose.ps1 --profile test build tests
.\scripts\Compose.ps1 --profile test run --rm tests
.\scripts\Test-Resilience.ps1
.\scripts\Compose.ps1 ps
```

Các integration/resilience tests tạo dữ liệu thử trong SQLite đang mount; chỉ chạy trên dữ liệu development/demo. `Test-Resilience.ps1` dừng RabbitMQ trong lúc kiểm tra.

Test workflow đăng ký người dùng và tạo booking/yêu cầu hỗ trợ thử trong các database đang mount. Không chạy trên dữ liệu production. Không dùng `down -v` để dọn test vì lệnh đó xóa volume Docker.

## Phạm vi chưa được xác nhận trong lần này

Không chạy thử tải cao, diễn tập Backup/Restore trên bản sao riêng, thử failover phần cứng hoặc xác nhận thanh toán qua nhà cung cấp thật. Kiến trúc và các giới hạn này được mô tả trong [kiến trúc SQLite](kien-truc-sqlite.md). Các kết quả kiểm thử cũ của kiến trúc SQL Server không được dùng làm bằng chứng cho phiên bản hiện tại.
