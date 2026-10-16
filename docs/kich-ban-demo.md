# Kịch bản trình bày Tourbooking phân tán

## Chuẩn bị

Chạy hệ thống theo README và chạy integration tests trước buổi trình bày. Mở website tại localhost:8080, RabbitMQ Management tại localhost:15672 và terminal chạy `scripts/Compose.ps1 ps`. Đọc tài khoản admin/RabbitMQ từ `.env`; không chiếu file mật khẩu lên màn hình.

## Trình bày trong 10 đến 12 phút

1. **Bài toán và mục tiêu, 1 phút:** website đặt tour; tách tiến trình để xử lý chức năng riêng; thông báo bất đồng bộ không chặn đặt tour.
2. **Kiến trúc, 2 phút:** trình bày sơ đồ trong báo cáo. Chỉ 5 container ứng dụng, SQL và RabbitMQ; nói rõ 3 dịch vụ nghiệp vụ vẫn dùng chung database để giữ hệ thống gốc, Notification có database riêng.
3. **Luồng nghiệp vụ, 2 phút:** đăng ký khách hàng, chọn tour, đặt cho ngày trong tương lai, mở QR thanh toán và báo đã chuyển khoản. Đăng nhập Admin trong cửa sổ riêng để xác nhận. Kiểm tra đơn Confirmed và thông báo mới.
4. **Giao tiếp và định danh, 1 phút:** mở `/api/identity/me`, `/api/bookings`, `/api/notifications`. Chỉ GUID event, Docker DNS và correlation ID trong log; giải thích cookie được chia sẻ giữa các dịch vụ.
5. **Đồng bộ, 1 phút:** trình bày test booking idempotency và rowversion. Giải thích giao dịch outbox và vì sao publisher có thể gửi lại nhưng thông báo không bị nhân đôi.
6. **Chịu lỗi, 2 phút:** dừng RabbitMQ bằng `scripts/Compose.ps1 stop rabbitmq`, tạo booking, thấy booking lưu thành công. Bật lại `scripts/Compose.ps1 start rabbitmq`, theo dõi worker phát pending và Notification nhận sự kiện. Giải thích retry, manual ACK và DLQ.
7. **Sao lưu, 1 phút:** chạy `scripts/Backup.ps1`, chỉ file `.bak`, manifest checksum và kết quả VERIFYONLY. Mô tả Restore có ghi đè; không khôi phục trên dữ liệu đang demo nếu chưa chủ động chuẩn bị.
8. **Giới hạn và hướng mở rộng, 1 phút:** tách database theo domain, bổ sung JWT nếu cần client ngoài trình duyệt, replica broker/SQL, metric tập trung, HTTPS và cổng thanh toán thực.

## Câu hỏi thường gặp

**Vì sao dùng RabbitMQ?** Thông báo không cần chạy trong request đặt tour. Queue tách vòng đời xử lý và giữ message khi consumer tạm dừng.

**Đã là microservices độc lập hoàn toàn chưa?** Chưa. Đây là hệ thống nhiều dịch vụ/tiến trình dùng chung thư viện và database nghiệp vụ; bước tách dữ liệu hoàn toàn được nêu là hướng phát triển.

**Vì sao cần outbox?** Nếu chỉ publish sau khi commit SQL, tiến trình có thể chết trước khi publish. Outbox được commit cùng booking rồi worker gửi sau.

**Có exactly-once không?** Vận chuyển ít nhất một lần; EventId unique tạo hiệu ứng lưu thông báo không trùng. Không cam kết exactly-once cho mọi tác dụng ngoài hệ thống.

**Đã thu tiền thật chưa?** Chưa. QR và mã giao dịch phục vụ chuyển khoản, Admin xác nhận thủ công. Việc khách bấm đã thanh toán không tự đánh dấu Paid.

**Có tự động failover database không?** Chưa. Có healthcheck, retry và restart tiến trình; SQL và RabbitMQ vẫn là node đơn trong môi trường demo.
