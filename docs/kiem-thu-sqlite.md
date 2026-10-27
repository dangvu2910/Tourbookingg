# Kết quả kiểm thử bản SQLite

## Chuyển dữ liệu nguồn

Lúc chuyển từ SQL Server, dữ liệu được xuất thành tám SQLite riêng. Sau đó các database được hợp nhất thành năm backend vật lý; các file cũ được giữ nguyên để có thể khôi phục. Dữ liệu tại thời điểm xuất:

| Dữ liệu | Số bản ghi |
| --- | ---: |
| Tài khoản | 12 |
| Vai trò | 2 |
| Gán vai trò | 12 |
| Tour | 3 |
| Booking | 11 |
| Giao dịch | 6 |
| Thông báo | 45 |
| Yêu cầu hỗ trợ, phản hồi, đánh giá | 0 |

Các bảng Identity còn lại được kiểm tra, kể cả khi rỗng. Mỗi bảng nghiệp vụ và bản sao được đối chiếu số bản ghi cùng checksum giá trị. Công cụ chạy kiểm tra integrity SQLite; giữ nguyên dữ liệu và backup SQL Server nguồn. Báo cáo chi tiết ở `data/sqlite/migration-report.json`.

Database nguồn đã dừng sau khi kiểm chứng, không bị xóa. Dữ liệu phát sinh khi chạy kiểm thử nằm trong SQLite; vì vậy số bản ghi hiện tại có thể lớn hơn bảng trên.

## Kiểm thử tự động

Lệnh đã chạy thành công:

```powershell
./scripts/Compose.ps1 --profile test build tests
./scripts/Compose.ps1 --profile test run --rm --no-deps tests
```

**35/35 kiểm thử thành công**, gồm:

- Ranh giới service và liên kết URL giữa các backend.
- Đăng ký, chia sẻ cookie xác thực, phân quyền và bảo vệ CSRF.
- Tạo booking không trùng khi gửi lại RequestId.
- Chờ giao dịch Payment được tạo qua sự kiện, xác nhận chuyển khoản, admin duyệt booking và nhận thông báo.
- Gửi yêu cầu Support, admin phản hồi và Notification thông báo cho đúng chủ yêu cầu.
- Chặn API nội bộ tại Gateway và không lộ controller của service khác.
- Rollback dữ liệu Booking và Outbox cùng nhau.
- Chống ghi đè phiên bản cũ trên SQLite.
- Chống thông báo trùng, chống tạo giao dịch trùng khi nhận lại sự kiện.
- Chặn service ghi bảng thuộc service khác.
- RabbitMQ gửi/nhận message thực tế.

## Xác minh sau khi gom backend — 26/10/2026

- 5 backend vật lý (Identity, Catalog, Booking, Customer, Notification), Gateway và RabbitMQ đều healthy trong lần kiểm thử được ghi nhận.
- `dotnet build Tourbooking.sln -m:1`: thành công, 0 lỗi và 0 cảnh báo.
- Kiểm thử ranh giới, URL và SQLite: 31/31 đạt.
- `docker-compose config -q`: thành công.
- SQLite integrity: `ok` cho năm database. Dữ liệu giữ nguyên: 12 tài khoản, 3 tour, 11 booking, 6 giao dịch, 1 yêu cầu hỗ trợ và 46 thông báo.
- Gateway trả HTTP 200 cho trang chủ, đăng nhập, danh sách tour, health và ảnh tour mẫu. Các trang Booking, Payment, Review, Support và Admin được chuyển tới đúng nhóm host.
- Backup sau khi gom gồm 38 file; toàn bộ checksum được xác minh. Backup tám database cũ cũng đã được chuyển thử sang staging năm database và qua integrity check.

## Giới hạn xác minh

Đây là kiểm thử chức năng với tải nhỏ, chưa đo tải đồng thời cao, failover hoặc chạy nhiều replica trên một file SQLite. Backup đã chạy thực tế; chưa diễn tập Restore ghi đè dữ liệu người dùng. Chưa triển khai kho chỗ theo lịch khởi hành hay thanh toán qua cổng tự động.

Các service có database riêng nhưng vẫn dùng chung model EF, kèm bản sao chỉ đọc qua HTTP nội bộ. Chi tiết và giới hạn ràng buộc dữ liệu trong [kiến trúc SQLite](kien-truc-sqlite.md).
