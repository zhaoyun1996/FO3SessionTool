# Tài liệu API và luật của 3Sea (suy ra từ capture)

Tài liệu này bổ sung cho `README.md`: định dạng yêu cầu/phản hồi của từng API, luật ép thẻ, thuật toán của tool và các lỗi đã gặp. Mọi thứ do suy ra từ việc bắt gói (xem README mục 2) và đọc code; server không công bố tài liệu.

**Quy ước độ tin cậy**
- **[capture]**: mẫu lấy trực tiếp từ bản capture game (đã che bí mật).
- **[code]**: định dạng mà `GameApi.cs` đang gửi/đọc và chạy được với server, nhưng bản capture hiện tại không còn mẫu thô (capture bị xóa mỗi lần chạy lại `run_game_capture.bat`).

Gốc API game: `{gameBase}` = `http://<ip>:18090/lang-vi`. Mọi gọi game dùng cookie phiên `connect.sid` và `fo3sid` (do server đặt sau bước `sign`). Giá trị tiền (EP/ES) là số nguyên.

---

## 1. Đăng nhập

### 1.1. Launcher: `POST https://3sea.online/web/api/launcher/login` [code]

Header: `User-Agent: 3seaLauncher/1.0.5`, `Content-Type: application/json`.

Lần đầu:
```json
{ "username": "<tài khoản>", "password": "<mật khẩu>",
  "hwid": { "board": "…", "cpu": "…", "disk": "…", "id": "…", "machine": "…" } }
```

Phản hồi:
- Đang xếp hàng: `{"result":false,"code":"QUEUE","position":N,"waiting":M,"ticket":"…"}`.
- Lỗi khác: `{"result":false,"code":"<MÃ LỖI>","reason":"…"}`.
- Thành công: `{"result":true,"token":"<48 ký tự>"}`.

Các lần hỏi lại khi đang xếp hàng **chỉ gửi** `{"hwid":{…},"ticket":"…"}` (không gửi lại mật khẩu), cách nhau `queuePollSeconds` (mặc định 5 giây), cho đến khi có `token`. Khóa trong `hwid` được gửi theo thứ tự chữ cái.

### 1.2. Game: `POST {gameBase}/publisher/garena/sign` [capture]

```json
// yêu cầu
{ "token": "<token launcher>" }
// phản hồi
{ "result": "ok", "token": "<48 ký tự>", "uid": 4823 }
```
Server trả cookie phiên trong `Set-Cookie`. Từ đây mọi API gọi kèm cookie này.

### 1.3. Kiểm tra và giữ phiên

- `GET /account` [capture]: hết phiên → `{"result":false,"reason":"NOT_SIGNED"}`. Dùng API này để biết phiên còn sống (`/transfer/search` vẫn chạy được khi chưa đăng nhập nên không chứng minh được gì).
- `GET /sign/ping` [capture]: `{"result":true}`. Game gửi khoảng 10 phút một lần; tool làm tương tự.
- `GET /info/server/config` [capture]: `{"redirector":{"host":"<ip>","port":28003},"notification":{…},"region":"ID","status":"ok"}`. Cổng 28003 là kênh TCP thô của game (không dùng trong tool). Host ở đây cho biết địa chỉ server hiện tại.

---

## 2. Nhân vật, kho, ví: `GET /coach/me` [capture]

Phản hồi (rút gọn):
```json
{
  "id": "00000000000012d700000001", "name": "vvkiet", "teamid": 243,
  "money": { "ep": 2085931050, "es": 5812 },
  "team": {
    "squad": { "positions": [
      { "id": "77c3…", "spid": 91005479, "pos": 0, "starting": [0,0,0.5,0.015] },
      { "id": "6db1…", "spid": 91182521, "pos": 28 }
    ]},
    "players": [
      { "_id": "e524…", "id": "e524…", "spid": 91190871, "grade": 5, "price": 87000000,
        "jerseyname": "Neymar Jr", "state": "first", "levelinfo": {"level":5,"exp":0,"to":23000} }
    ]
  }
}
```

Luật đọc kho (đúng với `GameApi.ParseInventory`):
- Mỗi thẻ là một phần tử trong `team.players` với `id` (hoặc `_id`) duy nhất, `spid`, `grade` (mức +N), `price` (giá trị thẻ), `jerseyname` (tên ngắn).
- **Đội hình chính**: id có trong `squad.positions` với `pos` ≠ 28. **Dự bị**: `pos` = 28 (7 thẻ). **Chưa đăng ký**: không có trong `positions` và không có trường `state`.
- Tên đầy đủ không có trong API này (chỉ có `jerseyname`); tool lấy từ fifaaddict theo `spid`.
- Số EP/ES nằm ở `money.ep`/`money.es`. Nhiều API (mua, ép, giao dịch) trả kèm `resources:[{"uri":"/coach/me","body":{…}}]` có cùng cấu trúc, nên tool cập nhật ví từ chính phản hồi đó mà không cần gọi lại.

---

## 3. Chợ và mua thẻ

### 3.1. `GET /transfer/search/{spid}` [capture]
```json
[ { "spid": 65014907, "grade": 1, "price": 6410, "count": 99,
    "expandme": "player", "levelinfo": {"level":1,"exp":0,"to":2000} } ]
```
Mỗi phần tử là giá thấp nhất đang bán cho một mức thẻ (`grade`), kèm số lượng (`count`). Tool lấy phần tử `grade` = 1 có giá thấp nhất.

### 3.2. `POST /transfer/purchase/{spid}/{grade}/{price}/purchase` [capture]
Body: `{}` (`Content-Type: application/json`). `price` trong đường dẫn **phải đúng bằng giá niêm yết hiện tại** (lấy từ `search` ngay trước đó).

Phản hồi thành công:
```json
{ "result": true,
  "player": { "_id": "8a57…", "id": "8a57…", "spid": 65014907, "grade": 1, "price": 5340, "jerseyname": "Bizzarri" },
  "resources": [ { "uri": "/coach/me", "body": { … } } ] }
```
`player._id` là id thẻ mới, cần để ép tiếp. Lưu ý giá trả thực tế (ở `search`) có thể khác `player.price` (giá trị thẻ ghi trên thẻ).

---

## 4. Ép thẻ (`enhance2.5`) [code]

`EnhanceTail` luôn là `0/-1/111` (game luôn gửi đúng như vậy; tool bắt chước nguyên văn).

### 4.1. Xem trước: `GET /player/{mainId}/enhance2.5/{materialId}/preview/0/-1/111`
Trường tool đọc: `grade` (mức thẻ sau khi ép), `price`, `probability` (xác suất thành công), `enhancepay` (EP phải trả), `needes.es` (ES cần).

### 4.2. Thực hiện: `POST /player/{mainId}/enhance2.5/{materialId}/0/-1/111`
Body: `{"_method":"PUT"}`, `Content-Type: application/json` (đúng như vậy, không có `charset`). Phản hồi: `result` (gọi được hay không), `success` (có ép thành công không), `resultgrade` (mức thẻ sau ép), kèm `resources` có `/coach/me` mới.

- `mainId` là thẻ chính (còn lại sau khi ép), `materialId` là nguyên liệu (bị tiêu thụ).
- Ép thành công = `result` và `success` đều `true` và `resultgrade` bằng mức mong muốn.

---

## 5. Giao dịch (đổi thẻ lấy cầu thủ + EP)

### 5.1. Xin phương án: `POST /players/trade2/proposal` [capture]
```json
// yêu cầu: chỉ cần id các thẻ
{ "players": ["97b0a05ee86450ffa1570781", "a12538ae6d1795e6bf88f9ef"] }
// phản hồi (props = 3 phương án; resources như các API khác)
{ "result": true,
  "props": [
    { "spid": 91158023, "grade": 2, "price": 12200000, "teamid": 111072, "ep": 984000000 },
    { "spid": 91020801, "grade": 2, "price": 12200000, "teamid": 111072, "ep": 1140000000 },
    { "spid": 91155862, "grade": 2, "price":  9410000, "teamid": 111072, "ep": 1130000000 } ] }
```
Mỗi phương án là một cầu thủ (`spid`, `grade`, `price` = giá trị thẻ) cộng EP tiền tươi (`ep`). Có thể thiếu `ep` ở một phương án (chỉ nhận cầu thủ). Tool tính giá trị phương án = `ep` + `price`. API này **chưa đổi thẻ**.

Dạng cũ hơn, vẫn gặp trong capture: `POST /trade3/proposal` với body `{"id":"default_A01"|"event_A01","slotnum":N,"playerids":[{"id","price","grade","spid"}…]}`; phản hồi cùng dạng `props`. Tool hiện **không dùng** dạng này.

### 5.2. Chốt: `POST /trade3/contract` [capture]
```json
{ "selectnum": 1, "slotnum": 0 }
```
`selectnum` = chỉ số (từ 0) của phương án trong `props`; `slotnum` = 0 với luồng `trade2`. Phản hồi `{"result":true,"resources":[…]}`. **Không hoàn tác**: các thẻ gửi đi bị tiêu thụ.

### 5.3. Giới hạn: `GET /trade3/staticvalues` [capture]
```json
{ "playertrade3_limit": { "popup_price": 1000000000, "max_trade": 1000000000, "max": 5000000000 },
  "playertrade3_normal": { "0": 3, "1": 3, … "10": 3 } }
```
- `max_trade` = 1 tỷ EP cho một lần giao dịch; `max` = 5 tỷ (trần ví); `popup_price` = 1 tỷ (game hiện cảnh báo).
- Tool chặn không cho chọn thẻ làm tổng giá trị vượt 1 tỷ.
- Ý nghĩa của `playertrade3_normal` (số 3 cho mỗi mức thẻ) chưa chắc chắn.

### 5.4. Quan sát về giá trị phương án
Với đầu vào lớn, phương án thường là một cầu thủ giá trị thấp cộng nhiều EP. Tổng nhận về từng thấy từ khoảng 82% đến 99% giá trị đầu vào (luồng `trade2`/gói `default`) và tới khoảng 137% (gói `event_A01` ở ô 2). Công thức chính xác chưa xác định được.

### 5.5. Khác (không dùng trong tool)
`GET /players/trade2/info` (bảng mốc giá và cầu thủ mẫu của người khác), `GET /account/trade2/usage` (`{"remainsec":0,"usedcount":6,"maxcount":-1}`), `GET /static/cache/TRADE_SPECIALPLAYERS` (`[]`).

---

## 6. Nguồn dữ liệu ngoài game

| Nguồn | Dùng để |
|---|---|
| `http://cdn.3sea.online/cdn/externalAssets/players/p{spid}.png` | Ảnh cầu thủ 128x128, nền trong suốt (tool vẽ nền riêng) |
| `https://en.fifaaddict.com/fo3player.php?id={spid}` | Tên đầy đủ, quốc gia, CLB, chiều cao/cân nặng, chân thuận, vị trí |
| `https://en.fifaaddict.com/fo3db.php?q=player&name=…` | Tìm cầu thủ theo tên |
| `https://fifaaddict.com/assets/img/badged.png?20171201` | Bộ icon mùa giải (cắt theo toạ độ X của từng mùa) |
| `https://flagcdn.com/w40/{iso}.png` | Cờ quốc gia |

Dữ liệu game dạng `data/*.big` đã mã hóa nên không dùng được; vì vậy cầu thủ không có trong kho phải tra bên ngoài.

---

## 7. Luật ép thẻ và thuật toán của tool

### 7.1. Rule ghép
Mỗi mức đích N (từ +2 đến +10) có hai nguyên liệu A và B, mỗi nguyên liệu là một mức thẻ **thấp hơn N**. Mặc định A = B = N−1 (hai thẻ +(N−1) ghép thành +N). Một thẻ +N cần số thẻ +1 là `need[N] = need[A] + need[B]` với `need[1] = 1`. Với rule mặc định: +2 cần 2, +3 cần 4, +4 cần 8, +5 cần 16, … +10 cần 512 thẻ +1.

Số lần ghép từng mức, với số thẻ hiện có từng mức: nếu A = B thì `lần = số_thẻ[A] / 2`; ngược lại `lần = min(số_thẻ[A], số_thẻ[B])`.

Luật về xác suất thành công và chi phí EP/ES nằm ở server (xem `probability` và `enhancepay` ở API xem trước); tool không tự tính, chỉ đọc từ `preview`.

### 7.2. Quy trình (`UpgradeRunner`)
1. **Nguồn thẻ:**
   - *Mua mới*: lặp `search` → lấy thẻ +1 rẻ nhất → nếu có giá tối đa và giá cao hơn thì chờ (tối đa 25 lần) → `purchase`. Tối đa 5 lần mua lỗi liên tiếp thì dừng.
   - *Thẻ trong kho*: bỏ qua bước mua.
2. **Quét kho (1b):** đọc `/coach/me` (thử tối đa 3 lần), gom **mọi thẻ chưa nằm trong đội hình** của cầu thủ đó với mức từ +1 đến mức đích, kể cả thẻ có sẵn từ trước, rồi mới ghép. Nếu không đọc được kho khi đang mua, dùng các thẻ vừa mua; ở chế độ kho thì báo lỗi.
3. **Ghép từng mức** từ +2 đến mức đích theo rule: `preview` → chờ như người đọc → `enhance`.
4. **Khôi phục khi lỗi (`EnhanceWithRecovery`):** mỗi lần ghép thử tối đa 3 lần; nếu một lần lỗi thì chờ 6 đến 12 giây rồi đọc lại kho. Nếu thẻ chính đã đúng mức và nguyên liệu đã mất thì coi như ghép thành công, không ghép lại (tránh tiêu nguyên liệu hai lần).
5. **Ước tính chi phí:** tool cộng dồn tiền mua và `enhancepay`; khi phản hồi không có `money` thì tự ước tính.

### 7.3. Nhịp độ
Tool chèn thời gian chờ ngẫu nhiên giữa các thao tác (ví dụ 0,8 đến 2 giây trước khi mua, 2 đến 4,5 giây trước khi ép, thỉnh thoảng nghỉ 6 đến 14 giây) và nhân với hệ số "Tốc độ" đã chọn, để nhịp thao tác giống người.

---

## 8. Lỗi đã gặp và cách xử lý

| Hiện tượng | Nguyên nhân | Xử lý |
|---|---|---|
| `HttpClient.Timeout` khi gọi game | Server đổi địa chỉ (từng `72.62.247.145` → `216.236.2.193`); địa chỉ cũ không phản hồi ở mọi cổng | Bắt lại game, lấy IP ở `info/server/config` hoặc ở dòng HTTP, sửa `gameBase`. Addon `game_capture.py` nhận game theo **cổng 18090**, không theo IP |
| `The 'Domain'='<ip>' part of the cookie is invalid` khi khôi phục phiên | Cookie lưu kèm `Domain` là địa chỉ IP, .NET từ chối | Khi nạp cookie không gán `Domain` (tool đã sửa) |
| `NOT_SIGNED` ở `/account` | Phiên hết hạn hoặc bị đăng nhập nơi khác | Đăng nhập lại, hoặc dùng "Lấy phiên từ game" |
| Ép trả lỗi nhưng thật ra đã áp dụng | Lỗi mạng giữa chừng | Đọc lại kho và so sánh trước khi ép lại |
| Kho đọc lỗi khi vừa mua | Server chậm | Thử lại 3 lần, rồi dùng thẻ vừa mua |
| Mua thất bại vì giá | `price` trong đường dẫn không khớp giá hiện tại | Luôn `search` ngay trước khi `purchase` |
| Ví không cập nhật sau thao tác | Một số phản hồi lỗi không có `money` | Gọi lại `/coach/me` định kỳ (tool làm mỗi 60 giây khi không chạy ép) |

---

## 9. Cách cập nhật tài liệu này khi game đổi

1. Chạy `capture\run_game_capture.bat` (Administrator), làm lại thao tác bị lỗi trong game.
2. Mở `game_capture_redacted.txt`, tìm đường dẫn liên quan và so với các mẫu ở trên.
3. Sửa `GameApi.cs` cho khớp, thêm mẫu mới vào đây và ghi rõ mẫu nào là **[capture]**.

## Bán thẻ lên thị trường chuyển nhượng [capture 2026-11-12, Rivaldo +2]

- `GET /transfer/price/{spid}/{grade}` → `{"default":15900000,"lower":12700000,"upper":19100000,"lowest":0,"registrationFee":159000,"spid":…,"grade":…}`. Khung giá được phép đăng bán: **`upper` là giá cao nhất**, `lower` là thấp nhất; `registrationFee` là phí đăng ký (1% giá mặc định).
- `POST /transfer/offer` body `{"price":19100000,"playerobjectid":"<id thẻ trong coach/me>"}` → `{"result":true,"resources":[{"uri":"/transfer/my/offer/list","body":{"offers":[{"id","spid","grade","price","status":"waiting","issuedat","completefee":{"base":3820000},"limitpricemin","limitpricemax"}]}}]}`.
- `GET /transfer/my/offer/list` → `{"offerexpireoffset":86400000,"offers":[…]}`: danh sách đang bán; chào bán hết hạn sau 24 giờ (`offerexpireoffset`).
- Tham chiếu: `GET /transfer/search/{spid}` (rao hiện tại, `[]` nếu chưa ai bán), `GET /transfer/top/50`.
- Chưa bắt được: lúc thẻ bán xong (tiền nhận, phí `completefee`) và cách hủy lệnh bán.

### Nhận tiền khi thẻ bán xong [capture 2026-11-12]

- `POST /transfer/my/offer/completed/{offerId}` body `{"_method":"DELETE"}` (`offerId` = `_id` của lệnh trong `/transfer/my/offer/list`, không phải id thẻ) → `{"result":true,"ep":15280000,"resources":[{"uri":"/coach/me","body":{…}}]}`. `ep` là số EP nhận được = giá bán trừ `completefee` (20%): 19.100.000 − 3.820.000 = 15.280.000. `resources` mang số dư mới.
- Game biết thẻ bán xong qua thông báo đẩy trên cổng TCP 28003 (không phải REST). Tool không đọc cổng này mà hỏi lại `/transfer/my/offer/list`.
- `status` của lệnh trong `/transfer/my/offer/list`: **`"waiting"`** khi chưa ai mua, **`"sold"`** khi đã bán (đọc từ server thật, 2 lệnh Ronaldo +5 giá 229.000.000, `completefee.base` 45.800.000). Lệnh đã bán nằm trong danh sách cho đến khi bạn nhận tiền bằng `POST /transfer/my/offer/completed/{_id}`. Chưa biết `status` của lệnh hết hạn. Tool coi `sold` (và `completed`) là nhận tiền được; trạng thái khác chỉ ghi nhật ký.
