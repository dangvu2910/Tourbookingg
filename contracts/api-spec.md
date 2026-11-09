# API v1 và tương thích MVC

Kong là entry point tại `http://localhost:8080` trong `compose.hybrid.yaml`.
Kong giữ nguyên prefix khi chuyển `/api/v1/...` tới backend. Không public `/internal` hoặc `/service-info`; Admin API của Kong tắt.

| Public route | Backend | Hiện hỗ trợ |
| --- | --- | --- |
| `/api/v1/auth/me` | Identity | GET, thông tin principal đã xác thực |
| `/api/v1/auth/login` | Identity | POST email/password, trả Bearer token 15 phút, giới hạn 10/phút |
| `/api/v1/auth/logout` | Identity | POST với Bearer, thu hồi token/phiên bằng security stamp, 204 |
| `/api/v1/catalog` | Catalog | GET, query và page, 20 tour/trang |
| `/api/v1/catalog/{id}` | Catalog | GET, tour công khai |
| `/api/v1/bookings` | Booking | GET, tối đa 100 booking của chính caller |
| `/api/v1/bookings/{id}` | Booking | GET, booking thuộc caller |
| `/api/v1/bookings/outbox` | Booking | GET, thống kê outbox, chỉ Admin |
| `/api/v1/chat` | Customer | GET, tối đa 200 hội thoại của caller |
| `/api/v1/chat/{id}/messages?after=0` | Customer | GET, tin nhắn có ID lớn hơn cursor, kiểm tra owner |
| `/api/v1/notifications` | Notification | GET, tối đa 100 thông báo của caller |

API v1 dùng envelope `success`, `data`, `error`, `meta`, tên trường `snake_case`, timestamp UTC và `X-Correlation-ID` chuẩn hóa. Catalog trả currency `VND`. Catalog có tổng số bản ghi; các danh sách giới hạn không giả định tổng số. Tin nhắn dùng cursor `after`.

Các route `/api/tours`, `/api/identity`, `/api/bookings`, `/api/notifications` và form MVC được giữ để tương thích. JSON legacy giữ camelCase và định dạng cũ. API v1 hỗ trợ Bearer JWT từ auth/login; xác thực chữ ký, issuer, audience, hạn dùng và kiểm tra lại quyền tại Identity. Login trả `data.access_token`, `data.token_type`, `data.expires_in`. Đăng ký, tạo booking, thanh toán và gửi chat vẫn qua MVC với cookie Identity đã ký và CSRF token; chưa có command API cho các nghiệp vụ này.

MVC tạo booking dùng `RequestId` (GUID), ràng buộc unique trong BookingDB. Cùng ID với caller/payload khác trả 409; lặp lại payload cũ trả cùng booking. Form hủy/đổi lịch và duyệt booking có version và trả 409 khi dữ liệu thay đổi. Riêng thông báo chuyển khoản của khách kiểm tra trạng thái hiện tại, tự tải lại khi có cập nhật đồng thời và nhận lại yêu cầu đã ghi nhận mà không tạo thanh toán mới. Thao tác này chỉ báo đã chuyển khoản, không tự thu tiền hoặc đánh dấu đã nhận tiền. Đây là hợp đồng MVC hiện có, không phải triển khai command API `Idempotency-Key`.

API nội bộ `/internal/data/{owner}` dùng `X-Service-Key`; `/internal/auth/session` còn yêu cầu signed cookie. Client bên ngoài không được truyền khóa nội bộ qua Kong hoặc web gateway.
