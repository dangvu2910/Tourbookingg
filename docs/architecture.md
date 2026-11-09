# Kiến trúc

Luồng public: **HAProxy → Kong x2 → API backends x2**, hoặc **Kong → YARP Gateway x2 → MVC backends x2**. HAProxy cũng cân bằng các HTTP call nội bộ qua listener private.

| Backend | Sở hữu | SQL Express hiện tại |
| --- | --- | --- |
| Identity | Người dùng, vai trò, cookie/JWT | `identity` |
| Catalog | Tour, giá, giữ chỗ | `catalog` |
| Booking | Đơn, thanh toán, báo cáo | `booking` |
| Customer | Đánh giá; chat/hỗ trợ trong Mongo | `customer`, `ChatDB.conversations` |
| Notification | Thông báo theo EventId | `NotificationDB.notifications` |

SQL Docker mới dùng database riêng `IdentityDB`, `CatalogDB`, `BookingDB`, `CustomerDB`. SQL Express đang dùng giữ nguyên `TourBookingDB`, phân quyền theo schema. Backend không mở database/schema của backend khác. Schema `_read` chỉ là bản sao tối thiểu qua HTTP nội bộ có khóa service; user summary không có mật khẩu, security stamp hoặc vai trò để cấp quyền. JWT được kiểm tra tại Identity; cookie dùng chung key ring và định kỳ xác minh lại. Điểm vào public chặn API nội bộ.

## Đặt tour và thanh toán

1. Catalog giữ chỗ theo tour/ngày, tối đa 20 khách; RequestId chống yêu cầu lặp. Transaction và SQL application lock bảo vệ giữa nhiều replica.
2. Booking lưu đơn/payment cùng transaction, dùng giá và snapshot do Catalog chốt. Tác vụ giữ chỗ xử lý xác nhận, hủy và khôi phục lịch cũ khi đổi lịch thất bại; không giữ transaction SQL trong lúc gọi HTTP.
3. Khách báo đã chuyển khoản dùng trạng thái mới nhất. Gửi lại không tạo payment hoặc ghi đè mã giao dịch đã nhận. Trang chờ đối soát ẩn QR và nhắc không chuyển tiền lần nữa. Nút này không tự thu tiền.
4. Admin đối soát rồi đánh dấu Paid/cấp vé. Hủy lặp giữ RefundPending/Refunded. Hủy/đổi lịch/duyệt vẫn bảo vệ version và CSRF.

## Sự kiện và chịu lỗi

SQL ghi nghiệp vụ/outbox cùng transaction. Dispatcher claim lease dưới khóa SQL, publish có xác nhận broker rồi đánh dấu đã gửi. Delivery vẫn là at-least-once: Notification dùng EventId làm `_id`; chat cập nhật reply và pending event trong cùng Mongo document. Khóa SQL bảo vệ việc thay bản sao đọc giữa các replica.

- Một replica mất: health check loại nó và instance còn lại phục vụ. Request đang bay vẫn có thể thất bại; không tự chuyển tiền lại.
- Cả service mất: chức năng phụ thuộc trả 502/503/504 hữu hạn; phần không phụ thuộc tiếp tục chạy. Projection có thể dùng cache cũ, không dùng cache để bán vượt chỗ.
- RabbitMQ mất: SQL/outbox và đọc thông báo vẫn hoạt động; sự kiện chờ gửi lại khi broker phục hồi.
- Identity mất: cookie còn hiệu lực có thể tiếp tục theo chính sách hiện có; login mới và xác thực Bearer cần Identity. Không bỏ qua kiểm tra Bearer.
- Database mất: phần cần database đó ngừng nhận traffic; không giả báo ghi thành công.

Kong dùng upstream pools, active/passive health check và timeout hữu hạn. Kong/HAProxy không tự retry payment command. HA ở đây bảo vệ lỗi process/container trên **một máy**: HAProxy, SQL Express, MongoDB, RabbitMQ, Docker host và ổ đĩa vẫn là điểm lỗi đơn; không có cam kết multi-host/database cluster hoặc zero-downtime.

Tham khảo: [Kong health checks](https://developer.konghq.com/gateway/traffic-control/health-checks-circuit-breakers/), [HAProxy DNS](https://www.haproxy.com/documentation/haproxy-configuration-tutorials/proxying-essentials/dns-resolution/).
