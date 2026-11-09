# Hướng dẫn API đặt tour

## Route và hợp đồng
Public qua Kong: /api/v1/auth, /api/v1/catalog, /api/v1/bookings, /api/v1/chat, /api/v1/notifications. Route nội bộ /internal/v1/... không expose public. Quy tắc strip/preserve prefix phải khớp upstream; xem contracts/api-spec.md trước khi code.

Danh từ số nhiều, kebab-case trong URL; GET đọc, POST tạo/thao tác nghiệp vụ, PATCH cập nhật một phần. DELETE không mặc nhiên là soft-delete: chọn theo vòng đời và yêu cầu lịch sử. 204 không có body.

## Header
- Accept: application/json cho API JSON.
- Content-Type: application/json khi body là JSON; multipart, download, webhook áp dụng content type đúng contract.
- Authorization: Bearer cho route có bảo vệ; danh sách public gồm login/đăng ký và tour công khai theo thiết kế.
- X-Correlation-ID được tạo hoặc chuẩn hóa nếu thiếu/sai, trả lại response, truyền sang HTTP/message.
- Idempotency-Key bắt buộc cho tạo booking, giữ chỗ và payment command. Scope theo caller + operation; cùng key khác payload trả 409.

## Envelope JSON
Wire dùng snake_case. Các thành phần success, data, error, meta luôn có trong response JSON được chuẩn hóa:
```json
{
  "success": true,
  "data": {"id": "booking-example", "status": "AWAITING_PAYMENT"},
  "error": null,
  "meta": {"correlation_id": "correlation-example", "timestamp": "2026-11-04T09:00:00Z"}
}
```
Ví dụ lỗi:
```json
{
  "success": false,
  "data": null,
  "error": {"code": "INSUFFICIENT_CAPACITY", "message": "Không đủ chỗ cho lịch khởi hành.", "details": {"requested_quantity": 2}},
  "meta": {"correlation_id": "correlation-example", "timestamp": "2026-11-04T09:00:00Z"}
}
```
List thêm meta.pagination với page, page_size, total_items hoặc dùng cursor contract riêng. Giới hạn page_size, sort ổn định; không giả định tổng số nếu không tính.

## Mã lỗi
| HTTP | Code | Ý nghĩa |
| --- | --- | --- |
| 400 | INVALID_REQUEST | JSON/định dạng request không hợp lệ |
| 401 | UNAUTHENTICATED | Thiếu hoặc sai token |
| 403 | PERMISSION_DENIED | Không đủ quyền |
| 404 | RESOURCE_NOT_FOUND | Không tồn tại/không hiển thị theo policy |
| 409 | INSUFFICIENT_CAPACITY | Không đủ chỗ |
| 409 | INVALID_STATE_TRANSITION | Trạng thái không cho phép thao tác |
| 409 | IDEMPOTENCY_CONFLICT | Key cũ nhưng payload khác |
| 409 | CONCURRENT_CONFLICT | Version không khớp |
| 422 | VALIDATION_FAILED | Input có cấu trúc hợp lệ nhưng sai quy tắc |
| 503 | DEPENDENCY_UNAVAILABLE | Dependency không sử dụng được |
| 504 | DEPENDENCY_TIMEOUT | Hết budget chờ upstream đồng bộ |
| 500 | INTERNAL_ERROR | Lỗi không dự kiến |

Nếu thao tác đã được chấp nhận xử lý bền vững, trả 202 và endpoint tra cứu trạng thái thay vì coi timeout Saga là thất bại cuối cùng. Không lộ stack trace, SQL hoặc secret.

## Thời gian và tiền
Thời điểm là ISO 8601 UTC. Ngày lịch thuần và múi giờ lịch khởi hành là khái niệm riêng; giữ timezone IANA nếu nghiệp vụ cần. Với VND dùng số nguyên đơn vị đồng, kèm currency=VND; số tiền do server tính từ giá owner đã chốt. Nếu thêm tiền tệ khác, chốt quy ước minor units và làm tròn. Không dùng float cho tiền.

## Gọi nội bộ
URL từ cấu hình, DNS Docker; timeout theo end-to-end budget, 2–5 giây chỉ là giá trị khởi đầu cần đo. Retry hữu hạn, backoff + jitter cho thao tác an toàn hoặc đã triển khai idempotency, không chỉ vì request có header key. Batch lookup thay vòng lặp từng ID.

Client Infrastructure trả DTO typed. Application quyết định lỗi nghiệp vụ; không đổi dependency lỗi thành mảng rỗng. Kiểm tra JWT issuer/audience/chữ ký/hạn, scope và ownership tại service đích. Rate-limit login, message và payment endpoint theo nhu cầu.

Health endpoint public chỉ lộ thông tin tối thiểu; readiness chi tiết giới hạn truy cập. Websocket/SSE và webhook có contract riêng, không ép envelope HTTP JSON lên mọi frame/callback.

