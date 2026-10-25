# Kết quả kiểm thử bản SQLite

## Chuyển dữ liệu nguồn

Đã chuyển từ SQL Server đang chạy trong Docker vào tám file SQLite riêng. Dữ liệu nguồn tại thời điểm chuyển:

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

Đã kiểm tra HTTP 200 cho trang chủ, danh sách tour, chi tiết tour, liên hệ và đăng nhập. Health của Gateway và tám backend đều thành công trên bản SQLite.

## Giới hạn xác minh

Đây là kiểm thử chức năng với tải nhỏ, chưa đo tải đồng thời cao, failover hoặc chạy nhiều replica trên một file SQLite. Script backup/restore/replay đã cập nhật và kiểm tra cú pháp; chưa diễn tập khôi phục ghi đè dữ liệu người dùng. Chưa triển khai kho chỗ theo lịch khởi hành hay thanh toán qua cổng tự động.

Các service có database riêng nhưng vẫn dùng chung model EF, kèm bản sao chỉ đọc qua HTTP nội bộ. Chi tiết và giới hạn ràng buộc dữ liệu trong [kiến trúc SQLite](kien-truc-sqlite.md).
