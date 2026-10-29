# Tour Booking

Ứng dụng web đặt tour, chạy bằng Docker Compose. Gateway là cổng truy cập web tại `http://localhost:8080`; các backend xử lý nghiệp vụ và RabbitMQ chạy riêng.

## Yêu cầu

- Windows với PowerShell
- Docker Desktop (bao gồm Docker Engine và Docker Compose) đang chạy
- .NET SDK 10 nếu muốn chạy test trực tiếp trên máy

Nếu Docker đã chạy nhưng chưa có lệnh `docker compose`, có thể cài Compose độc lập bằng script:

```powershell
.\scripts\Setup-Compose.ps1
```

## Chạy ứng dụng

Từ thư mục dự án:

```powershell
.\scripts\Initialize-Environment.ps1
.\scripts\Compose.ps1 up -d --build
.\scripts\Compose.ps1 ps
```

Mở [http://localhost:8080](http://localhost:8080). Thông tin đăng nhập quản trị nằm trong file `.env` được tạo ở bước đầu; giữ kín file này và không commit nó.

Để dừng toàn bộ ứng dụng mà vẫn giữ dữ liệu:

```powershell
.\scripts\Compose.ps1 down
```

Để dừng một backend, ví dụ Catalog:

```powershell
.\scripts\Compose.ps1 stop catalog
```

Gateway và các backend còn lại vẫn chạy, nhưng những chức năng cần Catalog sẽ tạm thời không dùng được. Giữ `gateway` chạy để truy cập web. Tên backend có thể dừng riêng gồm `identity`, `catalog`, `booking`, `customer` và `notification`. Dừng RabbitMQ chỉ làm chậm các thông báo bất đồng bộ; Booking và Customer không cần RabbitMQ để khởi động.

## Các service và dữ liệu

| Thành phần | Chức năng | Database |
| --- | --- | --- |
| Gateway | Cổng web công khai, trang chủ độc lập, phục vụ ảnh dùng chung và định tuyến request | Không có |
| Identity | Tài khoản và xác thực | `services/Identity/data/identity.db` |
| Catalog | Tour | `services/Catalog/data/catalog.db` |
| Booking | Booking, Payment và Reporting | `services/booking/data/booking.db` |
| Customer | Review và Support | `services/customer/data/customer.db` |
| Notification | Thông báo | `services/Notification/data/notification.db` |
| RabbitMQ | Chuyển sự kiện giữa các service | Docker volume `rabbit-data` |

Booking gom Booking, Payment và Reporting; Customer gom Review và Support. Các URL công khai hiện có được Gateway định tuyến tới backend sở hữu chức năng đó. Tạo đặt tour sẽ ghi đồng thời booking và thông tin chuyển khoản trong cùng một transaction để người dùng xem được ngay, không phụ thuộc RabbitMQ. Khi user báo đã chuyển khoản, booking được cập nhật sang chờ admin; admin xác nhận hoặc hủy booking và sự kiện trạng thái được gửi bất đồng bộ về trang thông báo của user. RabbitMQ vẫn dùng cho thông báo booking và hỗ trợ; nếu broker tạm dừng, dữ liệu nghiệp vụ vẫn lưu trong outbox để gửi lại sau. Trang chủ tĩnh và CSS riêng của trang chủ thuộc Gateway nên vẫn vào được khi Catalog tắt; các trang tour vẫn cần Catalog. CSS/JS nằm trong `services/<Backend>/wwwroot`, được đóng gói riêng vào image của backend và truy cập qua URL `/assets/<backend>/...`; Gateway chuyển yêu cầu tới đúng backend. Vì vậy CSS của Admin không phụ thuộc Catalog đang chạy. Khi thay đổi CSS hoặc layout, hãy build lại image của Gateway và các backend liên quan.

Nếu cần bổ sung tour mẫu cho Catalog, bật `Database__SeedDemo=true` riêng cho service này. Seed chỉ thêm tour còn thiếu theo tên, không thay đổi tour hiện có.

## Kiểm thử

Chạy test .NET:

```powershell
dotnet test tests/RabbitMq.Tests/RabbitMq.Tests.csproj
```

Chạy integration test trong Docker Compose:

```powershell
.\scripts\Compose.ps1 --profile test run --rm tests
```

Integration test cần stack đã khởi chạy và dùng cấu hình/tài khoản trong `.env`. Chỉ chạy trên môi trường local hoặc test.

Các form hủy vé, đổi lịch, duyệt và báo chuyển khoản gửi kèm phiên bản booking lúc mở trang. Nếu booking đã thay đổi, request từ trang cũ nhận HTTP 409 và yêu cầu tải lại; không ghi đè thao tác trước đó. Thiếu hoặc sai phiên bản cũng bị từ chối. Lỗi CSRF vẫn trả HTTP 400.

Kiểm thử hai phiên đăng nhập trên stack riêng (cần Node.js 22 trở lên; dữ liệu test dùng volume riêng, cổng `18081`):

```powershell
docker build -t tourbooking-booking-concurrency-check:latest --build-arg SERVICE=BookingPlatform --build-arg SERVICE_DIR=booking .
.\scripts\Compose.ps1 -f tests/compose.concurrency.yaml up -d
$env:TOURBOOKING_ISOLATED_TESTS = '1'
$env:TOURBOOKING_CONCURRENCY_URL = 'http://localhost:18081'
$env:TOURBOOKING_TEST_ADMIN_EMAIL = 'admin@concurrency.example.test'
$env:TOURBOOKING_TEST_ADMIN_PASSWORD = 'Isolated!Password123'
try {
    node tests/booking-concurrency.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Concurrency checks failed.' }
}
finally { .\scripts\Compose.ps1 -f tests/compose.concurrency.yaml down -v }
```

Thông tin đăng nhập trên chỉ dành cho stack test riêng. Script từ chối chạy trên cổng web thông thường `8080`.

## Backup và khôi phục

Kiểm tra cô lập khi Catalog ngừng hoạt động (stack phải đang chạy):

```powershell
.\scripts\Test-ServiceIsolation.ps1
```

Script dừng Catalog, kiểm tra các backend còn lại và tài nguyên CSS qua Gateway, sau đó tự bật lại Catalog và kiểm tra API tour phục hồi. Chức năng cần dữ liệu tour vẫn phụ thuộc Catalog.

Tạo backup:

```powershell
.\scripts\Backup.ps1
```

Khôi phục từ một thư mục backup sau khi kiểm tra đúng đường dẫn:

```powershell
.\scripts\Restore.ps1 -BackupDirectory backups\sqlite-20261027-120000 -Apply
```

Thay đường dẫn ví dụ bằng thư mục backup thực tế. Khôi phục sẽ thay database hiện tại; script kiểm tra checksum và lưu dữ liệu đang có trước khi thay. Không xóa các thư mục `services\*\data` hoặc Docker volumes nếu cần giữ database, khóa xác thực, ảnh tour và dữ liệu RabbitMQ.

## Chuyển dữ liệu SQL Server cũ

SQL Server không cần thiết để chạy cấu hình SQLite hiện tại. Chỉ dùng `compose.sqlserver.yaml` và script bên dưới khi cần chuyển dữ liệu từ hệ thống SQL Server cũ:

```powershell
.\scripts\Migrate-ToSqlite.ps1
```

Script yêu cầu SQL Server nguồn đang chạy, giữ nguyên nguồn và không ghi đè database SQLite đã tồn tại. Xem [hướng dẫn chuyển đổi SQL Server cũ](docs/legacy-sqlserver.md) trước khi thực hiện.

## Tài liệu

- [Kiến trúc và giới hạn SQLite](docs/kien-truc-sqlite.md)
- [Kịch bản demo](docs/kich-ban-demo.md)
- [Kiểm thử SQLite](docs/kiem-thu-sqlite.md)
