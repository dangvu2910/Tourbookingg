# Kịch bản trình bày Tourbooking phân tán

## Chuẩn bị

Từ PowerShell tại thư mục dự án, chạy `.\scripts\Initialize-Environment.ps1` nếu chưa có `.env`, sau đó `.\scripts\Compose.ps1 up -d --build`. Chạy integration tests trước buổi trình bày bằng `.\scripts\Compose.ps1 --profile test build tests` và `.\scripts\Compose.ps1 --profile test run --rm tests`. Mở website tại `http://localhost:8080`, RabbitMQ Management tại `http://localhost:15672` và kiểm tra trạng thái bằng `.\scripts\Compose.ps1 ps`. Đọc thông tin đăng nhập từ `.env`; không chiếu file này lên màn hình.

## Trình bày trong 10 đến 12 phút

1. **Bài toán và mục tiêu, 1 phút:** website đặt tour; tách tiến trình để xử lý chức năng riêng; thông báo bất đồng bộ không chặn đặt tour.
2. **Kiến trúc, 2 phút:** trình bày sơ đồ trong báo cáo. Có Gateway, 5 backend và RabbitMQ. Booking + Payment + Reporting cùng backend/file SQLite; Review + Support cùng backend/file SQLite. Các chức năng và URL cũ vẫn giữ. Gateway không có database; các project vẫn dùng chung thư viện/model và một số bản sao đọc.
3. **Luồng nghiệp vụ, 2 phút:** đăng ký khách hàng, chọn tour, đặt cho ngày trong tương lai, mở QR thanh toán và báo đã chuyển khoản. Đăng nhập Admin trong cửa sổ riêng để xác nhận. Kiểm tra đơn Confirmed và thông báo mới.
4. **Giao tiếp và định danh, 1 phút:** mở `/api/identity/me`, `/api/bookings`, `/api/notifications`. Chỉ GUID event, Docker DNS và correlation ID trong log; giải thích cookie được chia sẻ giữa các dịch vụ.
5. **Đồng bộ, 1 phút:** trình bày test booking idempotency và rowversion. Giải thích giao dịch outbox và vì sao publisher có thể gửi lại nhưng thông báo không bị nhân đôi.
6. **Chịu lỗi, 2 phút:** dừng RabbitMQ bằng `.\scripts\Compose.ps1 stop rabbitmq`, tạo booking hoặc yêu cầu hỗ trợ, thấy nghiệp vụ và outbox vẫn được lưu. Bật lại bằng `.\scripts\Compose.ps1 start rabbitmq`, theo dõi worker gửi sự kiện và Notification nhận message. Giải thích retry, manual ACK và DLQ.
7. **Sao lưu, 1 phút:** chạy `.\scripts\Backup.ps1`, chỉ thư mục database SQLite, auth keys, ảnh, broker definitions và manifest SHA256. Mô tả Restore yêu cầu `-Apply`, giữ dữ liệu trước restore riêng và nên diễn tập trên bản thử.
8. **Giới hạn và hướng mở rộng, 1 phút:** tách database theo domain, bổ sung JWT nếu cần client ngoài trình duyệt, replica broker/SQL, metric tập trung, HTTPS và cổng thanh toán thực.

## Câu hỏi thường gặp

**Vì sao dùng RabbitMQ?** Thông báo không cần chạy trong request đặt tour. Queue tách vòng đời xử lý và giữ message khi consumer tạm dừng.

**Đã là microservices độc lập hoàn toàn chưa?** Chưa. Mỗi backend có database SQLite riêng và sở hữu dữ liệu ghi, nhưng vẫn dùng chung model EF/hạ tầng và các bản sao đọc để giữ màn hình hiện tại. Bước tiếp theo là tách model theo bounded context.

**Vì sao cần outbox?** Nếu chỉ publish sau khi commit SQL, tiến trình có thể chết trước khi publish. Outbox được commit cùng booking rồi worker gửi sau.

**Có exactly-once không?** Vận chuyển ít nhất một lần; EventId unique tạo hiệu ứng lưu thông báo không trùng. Không cam kết exactly-once cho mọi tác dụng ngoài hệ thống.

**Đã thu tiền thật chưa?** Chưa. QR và mã giao dịch phục vụ chuyển khoản, Admin xác nhận thủ công. Việc khách bấm đã thanh toán không tự đánh dấu Paid.

**Có tự động failover database không?** Chưa. Có healthcheck, retry và restart tiến trình; mỗi file SQLite và RabbitMQ vẫn là một node đơn trong môi trường demo.
