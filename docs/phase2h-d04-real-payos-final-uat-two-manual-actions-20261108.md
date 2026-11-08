# D04/payOS — nghiệm thu giao dịch thật: chỉ 2 thao tác người dùng (08/11/2026)

> **CHƯA ĐƯỢC CHUYỂN TIỀN Ở THỜI ĐIỂM VIẾT TÀI LIỆU NÀY.**
> Full D04 *isolated* tests PASS ở `66a62dc19d1546fa5fc30c5c4784112f41968df3`
> nhưng Host vận hành D04-B còn thiếu nghiệm thu đăng nhập native OpenIddict,
> database least privilege đủ cả 3 DB, kho sao kê bảo vệ thực, và môi trường
> staging nhận webhook công khai HTTPS. Trong khi các điều kiện còn HOLD,
> không tạo giao dịch tiền thật chỉ để “ép” nghiệm thu.
>
> Theo https://payos.vn/docs/moi-truong-test/ , payOS **không có sandbox
> thanh toán riêng**, thử end-to-end với payOS là giao dịch trên API production.
> Giữ dữ liệu phòng trọ/hợp đồng thử tách biệt và tài khoản thụ hưởng do bạn kiểm soát.

## Những việc trợ lý/kỹ thuật phải hoàn tất trước khi gửi tín hiệu "được phép thử tiền"

- [ ] Môi trường staging chuyên dụng, DB thực nghiệm tách khỏi production;
      D04-B không kích hoạt trong Host nếu chưa có kho sao kê bảo vệ và
      Native ABP/OpenIddict thực.
- [ ] Dùng **hai tài khoản đăng nhập ABP thật**: nhân viên có quyền kiểm tra
      sao kê, Owner khác người kiểm tra có quyền phê duyệt; kiểm tra thu hồi
      quyền, tài khoản bị khóa, sai tòa nhà và tenant.
- [ ] Kho tài liệu riêng có mã hóa, secret/KMS quản lý, ACL, sao lưu, chính
      sách lưu giữ; hash SHA-256 đọc lại trùng trước khi chấp nhận attestation.
- [ ] Kịch bản crash/restart thực; ACK chỉ có một Receipt/Outbox và một
      DepositSettlement; không tự tạo lần thu thứ hai.
- [ ] PostgreSQL runtime roles không phải `postgres` superuser; quyền
      giới hạn cho Identity, Building và payOS Staging đã được thử thật.
- [ ] payOS channel cấu hình đúng **ngân hàng nhận thử nghiệm**; Client ID,
      API Key, Checksum Key lưu trên server secrets, **không gửi vào chat/git**.
      Tích hợp create-link/webhook đã được kiểm tra chữ ký; URL nhận webhook
      HTTPS truy cập được từ payOS, và test confirm-webhook đã đạt.
- [ ] Tạo **một hợp đồng thử riêng** có tiền cọc nhỏ, thanh toán
      `ContractDeposit`/Payment Pending và intent có giá trị **chính xác**
      bằng số tiền cọc. Giá trị phải hợp lệ theo payOS và không liên quan
      đến hợp đồng khách thật. **Không được gửi số tiền tự chọn khác intent.**
- [ ] Cung cấp cho người thực hiện một màn hình riêng ghi:
      mã đơn `orderCode`, số tiền VND, tên người thụ hưởng,
      link/QR do **hệ thống Ngọc Trang** tạo (không phải QR tùy ý),
      cùng trạng thái ban đầu `Pending`. Khóa chống tạo lại link trùng.

## Thao tác thủ công 1 — bạn chuyển **một** khoản tiền thử

1. Chỉ khi kỹ thuật đã xác nhận **tất cả điều kiện trên PASS**, mở trang
   giao dịch thử từ hệ thống Ngọc Trang (không sử dụng link do người lạ gửi).
   Kiểm tra mã đơn `orderCode`, số tiền và tài khoản nhận, không dùng dữ
   liệu/số tiền của khách trọ thật.
2. Mở **ứng dụng ngân hàng chuyển tiền của chính bạn**. Quét QR/link payOS,
   tự kiểm tra lại **tên thụ hưởng, ngân hàng và số tiền đúng bằng intent**.
   Nếu bất kỳ thông tin nào khác: hủy, không chuyển.
3. Xác nhận chuyển tiền **chính xác một lần**. Không chuyển thêm khi website
   chậm, webhook trễ hoặc xuất hiện lỗi timeout.
4. Giữ chứng từ chuyển thành công trong ứng dụng ngân hàng; ghi mã đơn,
   số tiền và thời gian thực hiện. Không đăng ảnh chứa số tài khoản đầy đủ,
   số dư, CCCD, OTP, API Key hay mật khẩu lên GitHub/ChatGPT.

**Kỳ vọng hệ thống:** webhooks payOS phải xác thực chữ ký/checksum trước
khi ghi intent `ConfirmedPendingPosting`, KHÔNG tự ghi Payment `Completed`
chỉ vì redirect hoặc webhook; nếu webhook trùng thì vẫn đúng một intent.
Webhook có thể đến chậm, cần xử lý retry/idempotency.

## Thao tác thủ công 2 — xác minh **tiền đã vào** bằng sao kê ngân hàng độc lập

1. Trực tiếp mở ứng dụng/website **ngân hàng của tài khoản nhận tiền** đã
   liên kết kênh payOS, không dựa vào ảnh khách gửi hay chỉ trạng thái
   `PAID` từ payOS.
2. Trong lịch sử giao dịch/sao kê, tìm **giao dịch ghi CÓ thực tế** tương
   ứng với số tiền, thời gian, nội dung/mã tham chiếu và tài khoản nhận
   của intent. Kiểm tra trạng thái đã hạch toán; nếu chưa thấy thì tiếp tục
   chờ và đối soát, không duyệt.
3. Xuất chứng từ PDF/sao kê chính thức hoặc ảnh giao dịch nhận từ app ngân
   hàng. Dữ liệu thật chỉ được tải lên **kho tài liệu bảo vệ của staging**
   sau khi hệ thống có xác thực, mã hóa và quyền truy cập phù hợp; trước
   thời điểm đó bạn chỉ giữ trong máy/tài khoản ngân hàng của mình.
4. Trên màn hình nghiệp vụ (khi đã được cung cấp), nhân viên có quyền
   `ReviewBankEvidence` đối chiếu và xác nhận; tài khoản Owner **khác
   người** có `ApproveD04Posting` phê duyệt. Nếu bạn đóng cả hai vai
   trong UAT thì vẫn phải dùng **hai tài khoản độc lập** và ghi nhận rõ
   đây là thử nghiệp vụ, không phải kiểm soát hai người độc lập trong vận hành.
5. Chỉ khi đủ bằng chứng ngân hàng và phê duyệt mới POST; hệ thống cần
   tự xác nhận đúng **một** Payment Completed, một DepositSettlement,
   một Receipt Journal, một Outbox và `Posted/ACK` bền vững. Retry/replay
   không tạo lần thu thứ hai. Nếu bất kỳ trường nào không khớp: **HOLD**.

## Chỉ gửi lại cho trợ lý những dữ liệu đã ẩn thông tin cá nhân

- Mã đơn/intent thử (có thể che bớt khi chia sẻ công khai), số tiền,
  giờ Việt Nam, trạng thái chuyển tiền **thành công hay thất bại**.
- Kết quả **đã thấy khoản tiền ghi Có trên sao kê: Có/Chưa**, trạng thái
  Review/Owner/Payment/Outbox/ACK, thư mục log đã được kiểm tra secrets.
- Không gửi OTP, mật khẩu, số thẻ, CCCD, bộ API keys, full PDF sao kê hay
  ảnh hiển thị số tài khoản/số dư không được che.

## Nếu gặp sự cố

- PayOS hiện thành công nhưng ngân hàng **chưa** ghi Có: tiếp tục HOLD,
  không tạo Payment Completed, không chuyển lại tiền chỉ để thử.
- Ghi Có ngân hàng nhưng webhook thất lạc: không tạo order/QR mới;
  điều tra với `orderCode` và `reference`, đối soát lại giao dịch cũ.
- Hai giao dịch ghi Có trùng, sai số tiền, sai tài khoản: dừng thủ tục
  tự động, chuyển xử lý sai lệch/hoàn tiền thủ công theo quy định.
- Hết thời gian nhưng không có sao kê: ghi nhận WAITING_BANK_PROOF và
  không kích hoạt phê duyệt/ghi sổ.

## Nguồn chính thức

- https://payos.vn/docs/moi-truong-test/ — không có sandbox riêng.
- https://payos.vn/docs/ — checkout → ngân hàng → webhook.
- https://payos.vn/docs/du-lieu-tra-ve/webhook/ — dữ liệu và chữ ký.
- https://payos.vn/docs/api/ — API tạo link và confirm-webhook.

**Quyết định:** chưa kích hoạt live payment hoặc production. Người dùng chỉ
chịu trách nhiệm 2 thao tác ngân hàng sau khi tất cả kỹ thuật đều green;
những chốt cần code/kiểm thử không được chuyển thành “nghiệm thu tay”.
