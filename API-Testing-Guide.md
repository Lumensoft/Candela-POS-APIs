# POS API — Complete Testing Guide

## How to use this guide

1. Replace `YOUR_SHOP_ID`, `YOUR_USER_ID` with real IDs from your database.
2. Replace `PRODUCT_ITEM_ID_*` with real `Product_Item_ID` values from `tblProductItem`.
3. Replace `CUSTOMER_ID_*` with real `member_id` values from `tblMemberInfo`.
4. Every request **except** `POST /auth/login` requires `Authorization: Bearer <token>`.
5. Run the **DB verification queries** after write operations to confirm data was persisted.

---

## Base URL

```
http://<server>/api
```

---

## 1. Authentication

### POST /auth/login

Validates credentials against `tblSecurityUser`, claims a tablet slot from `tblComputerList`,
and returns a JWT.

```http
POST /api/auth/login
Content-Type: application/json
```

```json
{
  "username": "admin",
  "password": "your_password",
  "device_id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

> `device_id` is the Android device ID (or any stable UUID for testing).  
> First login with a new `device_id` claims a free slot in `tblComputerList` (`istablet=1, deviceid IS NULL`).

**Expected response:**

```json
{
  "success": true,
  "data": {
    "token": "eyJhbGci...",
    "user_id": 1,
    "user_name": "Admin User",
    "shop_id": 1,
    "shop_name": "Main Branch",
    "pos_code": "POS1"
  }
}
```

**DB verification:**

```sql
SELECT computer_id, shop_id, POS_code, deviceid, isactive
FROM tblComputerList
WHERE deviceid = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
```

---

### POST /auth/refresh

Issues a new JWT with a fresh expiry. The old token is blocklisted immediately.  
Call this ~5 minutes before the current token expires.

```http
POST /api/auth/refresh
Authorization: Bearer <current_token>
```

*(no body)*

**Expected response:**

```json
{
  "success": true,
  "data": {
    "token": "eyJhbGci...NEW..."
  }
}
```

**DB verification:**

```sql
-- Old token signature should appear here
SELECT token_sig, blocked_at, expires_at
FROM tblPOSTokenBlocklist
ORDER BY blocked_at DESC;
```

---

### POST /auth/logout

Blocklists the current token so it can no longer be used.

```http
POST /api/auth/logout
Authorization: Bearer <token>
```

*(no body)*

**Expected response:**

```json
{ "success": true, "data": { "logged_out": true } }
```

**Verify revocation — re-use the same token:**

```http
GET /api/sales
Authorization: Bearer <same_old_token>
```

Expected: `401 Token has been revoked`

---

## 2. Masters

### GET /masters/products

Returns the full product list. Add `?since=` for delta sync (only items changed after that timestamp).

```http
GET /api/masters/products
Authorization: Bearer <token>
```

**With delta filter:**

```http
GET /api/masters/products?since=2024-01-01T00:00:00
Authorization: Bearer <token>
```

---

## 3. Customers

### POST /customers

Creates a new walk-in customer at POS. Auto-generates `member_id` and `member_no` as MAX+1 per shop.

```http
POST /api/customers
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "member_name": "Ahmed Khan",
  "member_type_id": 1,
  "phone_mobile": "03001234567",
  "phone_res": "",
  "email": "",
  "address": "",
  "allow_credit": false,
  "credit_limit": 0
}
```

**Expected response:**

```json
{
  "success": true,
  "data": {
    "member_id": 1042,
    "member_no": 1042,
    "shop_id": 1
  }
}
```

**DB verification:**

```sql
SELECT member_id, member_no, member_name, phone_mobile, status, EnteredDate
FROM tblMemberInfo
WHERE member_id = 1042;
```

---

## 4. Gift Cards

### GET /gift-cards/{no}/balance

Returns the current balance for a gift card.

```http
GET /api/gift-cards/GC-000123/balance
Authorization: Bearer <token>
```

**Expected response:**

```json
{
  "success": true,
  "data": {
    "card_no": "GC-000123",
    "balance": 1500.00,
    "status": "Sold",
    "member_name": "Ahmed Khan",
    "phone_mobile": "03001234567"
  }
}
```

**DB verification:**

```sql
SELECT SUM(Top_up_Amt) AS balance
FROM tblGiftCardLedger
WHERE card_no = 'GC-000123';
```

---

### POST /gift-cards/topup

Tops up a gift card. Inserts into `tblGiftCardLedger`, updates `tbldefCards`, and optionally
records a cash account transaction.

```http
POST /api/gift-cards/topup
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "card_no": "GC-000123",
  "topup_amount": 2000.00,
  "cash_amount": 2000.00,
  "card_amount": 0,
  "exp_days": 365,
  "member_name": "Ahmed Khan",
  "phone_mobile": "03001234567"
}
```

**Expected response:**

```json
{
  "success": true,
  "data": {
    "card_no": "GC-000123",
    "new_balance": 3500.00
  }
}
```

**DB verification:**

```sql
SELECT TOP 5 * FROM tblGiftCardLedger WHERE card_no = 'GC-000123' ORDER BY ledger_id DESC;
SELECT card_no, status, exp_date FROM tbldefCards WHERE card_no = 'GC-000123';
```

---

## 5. Sales

### GET /sales

Paginated invoice search. Supports free-text, date range, and exact invoice number filters.

```http
GET /api/sales?page=1&page_size=20
Authorization: Bearer <token>
```

**With filters:**

```http
GET /api/sales?q=Ahmed&from=2026-07-01&to=2026-07-08&page=1&page_size=10
Authorization: Bearer <token>
```

```http
GET /api/sales?invoice_no=12345
Authorization: Bearer <token>
```

**Expected response:**

```json
{
  "success": true,
  "data": {
    "total": 150,
    "page": 1,
    "page_size": 20,
    "items": [
      {
        "sale_id": 12345,
        "sale_date": "2026-07-08T10:30:00",
        "customer_name": "Ahmed Khan",
        "net_total": 500.00,
        "payment_type": "Cash",
        "is_voided": false,
        "salesperson": "Admin User"
      }
    ]
  }
}
```

---

### POST /sales

Finalizes a sale via `SaleAndReturnDAL.Add()`. Always call `/quote` first and echo back its
per-item values.

```http
POST /api/sales
Authorization: Bearer <token>
Content-Type: application/json
```

#### 5a — Simple Cash Sale

```json
{
  "client_txn_guid": "550e8400-e29b-41d4-a716-446655440000",
  "sale_date": "2026-07-08T10:30:00",
  "customer_id": 0,
  "walk_in_name": "",
  "payment_type": "cash",
  "gross_total": 500.00,
  "net_total": 500.00,
  "customer_discount": 0,
  "marketing_discount": 0,
  "vat_amount": 0,
  "additional_tax": 0,
  "adjustment_amount": 0,
  "cash_amount": 500.00,
  "card_amount": 0,
  "credit_card_id": 0,
  "credit_amount": 0,
  "gift_card_no": "",
  "gift_card_amount": 0,
  "salesperson_id": 0,
  "comments": "",
  "holding_sale_id": 0,
  "coupon_no": "",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 2,
      "unit_rate": 250.00,
      "tagged_price": 250.00,
      "unit_discount": 0,
      "customer_discount_per_unit": 0,
      "marketing_discount": 0,
      "loyalty_cash_discount": 0,
      "vat_value": 0,
      "vat_factor": 0,
      "vat_type": "",
      "price_include_vat": false,
      "additional_tax_percent": 0,
      "additional_tax": 0,
      "gross_amount": 500.00,
      "net_amount": 500.00,
      "discount_id": 0,
      "disc_category": "",
      "batch_no": "",
      "con_factor": 1,
      "pack_size": 0,
      "nested_item_id": 0,
      "discount_from_tag_price": false
    }
  ]
}
```

**Expected response:**

```json
{ "success": true, "data": { "sale_id": 12345 } }
```

**DB verification:**

```sql
SELECT TOP 1 sale_id, net_total, payment_type, IsVoided FROM tblSales ORDER BY sale_id DESC;
SELECT * FROM tblSalesLineItems WHERE sale_id = 12345;
SELECT * FROM tblShopProductInventory WHERE product_item_id = PRODUCT_ITEM_ID_1 AND shop_id = 1;
```

---

#### 5b — Sale with Coupon

```json
{
  "client_txn_guid": "660e8400-e29b-41d4-a716-446655440001",
  "sale_date": "2026-07-08T11:00:00",
  "customer_id": 0,
  "payment_type": "cash",
  "gross_total": 450.00,
  "net_total": 450.00,
  "customer_discount": 0,
  "marketing_discount": 0,
  "vat_amount": 0,
  "additional_tax": 0,
  "adjustment_amount": 0,
  "cash_amount": 450.00,
  "card_amount": 0,
  "credit_card_id": 0,
  "credit_amount": 0,
  "gift_card_no": "",
  "gift_card_amount": 0,
  "salesperson_id": 0,
  "comments": "",
  "holding_sale_id": 0,
  "coupon_no": "TESTCOUPON123",
  "items": [
    {
      "product_item_id": COUPON_PRODUCT_ID,
      "quantity": 1,
      "unit_rate": 500.00,
      "tagged_price": 500.00,
      "unit_discount": 50.00,
      "customer_discount_per_unit": 0,
      "marketing_discount": 0,
      "loyalty_cash_discount": 0,
      "vat_value": 0,
      "vat_factor": 0,
      "vat_type": "",
      "price_include_vat": false,
      "additional_tax_percent": 0,
      "additional_tax": 0,
      "gross_amount": 450.00,
      "net_amount": 450.00,
      "discount_id": 0,
      "disc_category": "C",
      "batch_no": "",
      "con_factor": 1,
      "pack_size": 0,
      "nested_item_id": 0,
      "discount_from_tag_price": false
    }
  ]
}
```

```sql
-- Coupon should be marked used
SELECT CouponNo, Status FROM tblCouponDtl WHERE CouponNo = 'TESTCOUPON123';
```

---

#### 5c — Gift Card Payment

```json
{
  "client_txn_guid": "770e8400-e29b-41d4-a716-446655440002",
  "sale_date": "2026-07-08T12:00:00",
  "customer_id": 0,
  "payment_type": "split",
  "gross_total": 300.00,
  "net_total": 300.00,
  "customer_discount": 0,
  "marketing_discount": 0,
  "vat_amount": 0,
  "additional_tax": 0,
  "adjustment_amount": 0,
  "cash_amount": 100.00,
  "card_amount": 0,
  "credit_card_id": 0,
  "credit_amount": 0,
  "gift_card_no": "GC-000123",
  "gift_card_amount": 200.00,
  "salesperson_id": 0,
  "comments": "",
  "holding_sale_id": 0,
  "coupon_no": "",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 1,
      "unit_rate": 300.00,
      "tagged_price": 300.00,
      "unit_discount": 0,
      "customer_discount_per_unit": 0,
      "marketing_discount": 0,
      "loyalty_cash_discount": 0,
      "vat_value": 0,
      "vat_factor": 0,
      "vat_type": "",
      "price_include_vat": false,
      "additional_tax_percent": 0,
      "additional_tax": 0,
      "gross_amount": 300.00,
      "net_amount": 300.00,
      "discount_id": 0,
      "disc_category": "",
      "batch_no": "",
      "con_factor": 1,
      "pack_size": 0,
      "nested_item_id": 0,
      "discount_from_tag_price": false
    }
  ]
}
```

```sql
-- Gift card balance should decrease by 200
SELECT SUM(Top_up_Amt) AS balance FROM tblGiftCardLedger WHERE card_no = 'GC-000123';
```

---

#### 5d — Credit Sale

```json
{
  "client_txn_guid": "880e8400-e29b-41d4-a716-446655440003",
  "sale_date": "2026-07-08T13:00:00",
  "customer_id": CUSTOMER_ID_WITH_CREDIT,
  "payment_type": "credit",
  "gross_total": 800.00,
  "net_total": 800.00,
  "customer_discount": 0,
  "marketing_discount": 0,
  "vat_amount": 0,
  "additional_tax": 0,
  "adjustment_amount": 0,
  "cash_amount": 0,
  "card_amount": 0,
  "credit_card_id": 0,
  "credit_amount": 800.00,
  "gift_card_no": "",
  "gift_card_amount": 0,
  "salesperson_id": 0,
  "comments": "Credit sale",
  "holding_sale_id": 0,
  "coupon_no": "",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 2,
      "unit_rate": 400.00,
      "tagged_price": 400.00,
      "unit_discount": 0,
      "customer_discount_per_unit": 0,
      "marketing_discount": 0,
      "loyalty_cash_discount": 0,
      "vat_value": 0,
      "vat_factor": 0,
      "vat_type": "",
      "price_include_vat": false,
      "additional_tax_percent": 0,
      "additional_tax": 0,
      "gross_amount": 800.00,
      "net_amount": 800.00,
      "discount_id": 0,
      "disc_category": "",
      "batch_no": "",
      "con_factor": 1,
      "pack_size": 0,
      "nested_item_id": 0,
      "discount_from_tag_price": false
    }
  ]
}
```

---

#### 5e — Idempotency Test

Submit the exact same request twice with the same `client_txn_guid`.
Second call must return the **same `sale_id`** without inserting a duplicate row.

```json
{ "client_txn_guid": "550e8400-e29b-41d4-a716-446655440000" }
```

**Expected on second call:**

```json
{ "success": true, "data": { "sale_id": 12345, "idempotent": true } }
```

```sql
-- Must still be exactly 1 row for this GUID
SELECT COUNT(*) FROM tblSales WHERE client_txn_guid = '550e8400-e29b-41d4-a716-446655440000';
```

---

### DELETE /sales/{id}

Voids a sale via `SaleAndReturnDAL.VoidSale()`. Reverses inventory. Sets `IsVoided = 1`.

```http
DELETE /api/sales/12345
Authorization: Bearer <token>
```

**Expected response:**

```json
{ "success": true, "data": { "voided_sale_id": 12345 } }
```

**Error cases:**
- `404` — sale_id not found for this shop
- `409` — already voided

**DB verification:**

```sql
SELECT sale_id, IsVoided, net_total FROM tblSales WHERE sale_id = 12345;
-- Inventory should be restored
SELECT * FROM tblShopProductInventory WHERE product_item_id = PRODUCT_ITEM_ID_1 AND shop_id = 1;
```

---

## 6. Returns

### GET /returns/validate

Validates an invoice for return and returns the original sale details + per-item returnable quantities.
Call this before showing the return screen to the cashier.

```http
GET /api/returns/validate?invoice_no=12345
Authorization: Bearer <token>
```

**Expected response:**

```json
{
  "success": true,
  "data": {
    "sale_id": 12345,
    "sale_date": "2026-07-08T10:30:00",
    "customer_id": 0,
    "customer_name": "Walk-in",
    "net_total": 500.00,
    "payment_type": "Cash",
    "items": [
      {
        "product_item_id": PRODUCT_ITEM_ID_1,
        "item_name": "Product A",
        "quantity": 2,
        "already_returned": 0,
        "returnable_qty": 2,
        "unit_rate": 250.00,
        "unit_discount": 0
      }
    ]
  }
}
```

**Error cases:**
- `404` — invoice not found or already fully returned
- `422` — invoice not eligible for return (voided, return restrictions, etc.)

---

### POST /returns

Processes a return via `SaleAndReturnDAL.Add()` with negative quantities.
Per-item over-return is validated before the DAL call.

```http
POST /api/returns
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "original_sale_id": 12345,
  "return_date": "2026-07-08T14:00:00",
  "customer_id": 0,
  "payment_type": "cash",
  "return_amount": 250.00,
  "cash_refund": 250.00,
  "gift_card_refund": 0,
  "gift_card_no": "",
  "comments": "Customer returned item",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 1,
      "unit_rate": 250.00,
      "unit_discount": 0,
      "customer_discount_per_unit": 0,
      "vat_value": 0,
      "vat_factor": 0,
      "gross_amount": 250.00,
      "net_amount": 250.00,
      "batch_no": ""
    }
  ]
}
```

**Expected response:**

```json
{ "success": true, "data": { "return_sale_id": 12346 } }
```

**Error cases:**
- `409` — idempotency: same return already processed
- `422` — qty exceeds returnable amount for an item
- `404` — original invoice invalid for return

**DB verification:**

```sql
-- Return sale (negative qty)
SELECT sale_id, SaleReturningNo, net_total, is_return_item FROM tblSales WHERE sale_id = 12346;
SELECT product_item_id, qty FROM tblSalesLineItems WHERE sale_id = 12346;
-- Inventory restored for returned item
SELECT * FROM tblShopProductInventory WHERE product_item_id = PRODUCT_ITEM_ID_1 AND shop_id = 1;
```

---

## 7. Holds (Parked Sales)

### GET /holds

Returns all parked sales for the current shop with their line items.

```http
GET /api/holds
Authorization: Bearer <token>
```

**Expected response:**

```json
{
  "success": true,
  "data": [
    {
      "holding_sale_id": 55,
      "customer_id": 0,
      "net_total": 750.00,
      "held_at": "2026-07-08T09:15:00",
      "items": [
        {
          "product_item_id": PRODUCT_ITEM_ID_1,
          "item_name": "Product A",
          "quantity": 3,
          "unit_rate": 250.00
        }
      ]
    }
  ]
}
```

---

### POST /holds

Parks (holds) the current cart via `SaleAndReturnDAL.AddToHold()`. The cashier can recall it later.

```http
POST /api/holds
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "customer_id": 0,
  "net_total": 750.00,
  "gross_total": 750.00,
  "comments": "Customer stepped out",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 3,
      "unit_rate": 250.00,
      "unit_discount": 0,
      "gross_amount": 750.00,
      "net_amount": 750.00
    }
  ]
}
```

**Expected response:**

```json
{ "success": true, "data": { "holding_sale_id": 55 } }
```

**DB verification:**

```sql
SELECT * FROM tblSalesHolding WHERE holding_sale_id = 55;
SELECT * FROM tblSalesLineItemsHolding WHERE holding_sale_id = 55;
```

---

### DELETE /holds/{id}

Discards a parked sale (e.g. when recalled into a live sale or cancelled).

```http
DELETE /api/holds/55
Authorization: Bearer <token>
```

**Expected response:**

```json
{ "success": true, "data": { "deleted_holding_sale_id": 55 } }
```

**Error cases:**
- `404` — hold not found for this shop

**DB verification:**

```sql
SELECT COUNT(*) FROM tblSalesHolding WHERE holding_sale_id = 55;         -- must be 0
SELECT COUNT(*) FROM tblSalesLineItemsHolding WHERE holding_sale_id = 55; -- must be 0
```

---

## 8. Hardware

### POST /hardware/drawer

Opens the cash drawer by sending an ESC/POS kick command (`0x1B 0x70 0x00 0x32 0xFA`) via TCP
to the receipt printer. The printer must have a drawer cable on its RJ11 port.

```http
POST /api/hardware/drawer
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "printer_ip": "192.168.1.20",
  "printer_port": 9100
}
```

> `printer_port` is optional — defaults to `9100`.

**Expected response:**

```json
{
  "success": true,
  "printer_ip": "192.168.1.20",
  "printer_port": 9100,
  "pos_code": "POS1"
}
```

**Error cases:**
- `400` — `printer_ip` missing
- `502` — TCP connection to printer failed (printer off, wrong IP, port blocked)

**Manual test:** The physical drawer should pop open immediately.

---

## 9. Print

### POST /print

Renders a sale receipt and sends it to the thermal printer via TCP.  
Uses Candela's `spSalesInvoiceNew` stored procedure (same data source as the Candela 3-inch
text receipt) — no RDLC or ReportViewer required.

```http
POST /api/print
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "sale_id": 12345,
  "printer_ip": "192.168.1.20",
  "printer_port": 9100,
  "copies": 1,
  "is_duplicate": false
}
```

> - `printer_port` — optional, defaults to `9100`  
> - `copies` — optional, defaults to `1`, max `5`  
> - `is_duplicate` — `true` prints "Duplicate" in the receipt header (same as Candela reprint mode)

**Expected response:**

```json
{
  "success": true,
  "sale_id": 12345,
  "printer_ip": "192.168.1.20",
  "printer_port": 9100,
  "copies": 1
}
```

**Error cases:**
- `400` — `sale_id` or `printer_ip` missing
- `404` — no invoice data found for this `sale_id` in current shop
- `502` — TCP connection to printer failed

**Reprint test:**

```json
{
  "sale_id": 12345,
  "printer_ip": "192.168.1.20",
  "copies": 1,
  "is_duplicate": true
}
```

Receipt should print with **"Duplicate"** in the header.

---

## 10. POS Till Management

### POST /pos/cash-skim

Records a mid-shift cash removal from the till drawer.  
Mirrors `frmSkimCashPopUp → POSCashManagmentDAL.ReceiveSkimCash()` with `Type = Skimmed`.

The DAL auto-manages the shift record: finds the open `tblPOSCashManagement` row for this
POS/shop (or creates one), then inserts a `tblPOSCashManagementDetail` row.

```http
POST /api/pos/cash-skim
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "amount": 50000.00,
  "notes": "Mid-shift pull to safe"
}
```

> `notes` is optional.

**Expected response:**

```json
{
  "success": true,
  "amount": 50000.00,
  "notes": "Mid-shift pull to safe",
  "pos_code": "POS1",
  "shop_id": 1,
  "skimmed_at": "2026-07-08 14:30:00"
}
```

**Error cases:**
- `400` — `amount` missing or ≤ 0

**DB verification:**

```sql
-- Skim detail row
SELECT TOP 1 * FROM tblPOSCashManagementDetail
WHERE Type = 'Skimmed'
ORDER BY POSCashManagementDetailID DESC;

-- Running skim total for this POS session
SELECT POSCode, CashSkimmed FROM tblPOSCashManagement
WHERE IsClosed = 0 AND POSCode = 'POS1' AND ShopID = 1;
```

---

### GET /pos/shift-status

Full till-shift cash breakdown, mirrors `frmPOSCashManagement`'s live drawer summary.
Same calculation as `cash-status` above, plus a `last_closed` block (previous shift's
counted cash and difference) when no shift is currently open — used to drive the
"Open Till" vs live-summary state on the Shift Management screen.

```http
GET /api/pos/shift-status
Authorization: Bearer <token>
```

**Expected response (shift open):**

```json
{
  "success": true,
  "shift_open": true,
  "opening": 20000.00,
  "cash_received": 5000.00,
  "cash_sales": 12500.00,
  "net_sales": 12500.00,
  "cash_skimmed": 3000.00,
  "available_cash": 34500.00,
  "shift_since": "2026-08-03 09:00",
  "pos_code": "POS1",
  "shop_id": 1,
  "last_closed": null
}
```

**Expected response (no shift open):**

```json
{
  "success": true,
  "shift_open": false,
  "opening": 0,
  "cash_received": 0,
  "cash_sales": 0,
  "net_sales": 0,
  "cash_skimmed": 0,
  "available_cash": 0,
  "shift_since": null,
  "pos_code": "POS1",
  "shop_id": 1,
  "last_closed": {
    "closed_at": "2026-08-02 21:15:00",
    "cash_counted": 41000.00,
    "cash_difference": -500.00
  }
}
```

---

### POST /pos/shift-open

Explicitly opens a till shift for this POS/shop. Idempotent — if a shift is already
open, just returns its opening cash instead of erroring. Internally calls the same
`POSCashManagmentDAL.CloseShift()` the DAL already uses to auto-open a shift on the
first Received/Skimmed movement — this just exposes it as an explicit user action so
"Open Till" appears as a real step in the UI instead of happening silently.

```http
POST /api/pos/shift-open
Authorization: Bearer <token>
```

**Expected response:**

```json
{
  "success": true,
  "already_open": false,
  "opened_at": "2026-08-03 09:00:00",
  "opening": 20000.00,
  "pos_code": "POS1",
  "shop_id": 1
}
```

**DB verification:**

```sql
SELECT TOP 1 * FROM tblPOSCashManagement
WHERE IsClosed = 0 AND POSCode = 'POS1' AND ShopID = 1
ORDER BY POSCashManagementID DESC;
```

---

### POST /pos/cash-receive

Records a mid-shift cash addition to the till drawer (float top-up from the shop
safe). Mirrors `cash-skim` above exactly, with `Type = Received`.

```http
POST /api/pos/cash-receive
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "amount": 5000.00,
  "notes": "Float top-up from shop safe"
}
```

**Expected response:**

```json
{
  "success": true,
  "amount": 5000.00,
  "notes": "Float top-up from shop safe",
  "pos_code": "POS1",
  "shop_id": 1,
  "received_at": "2026-08-03 11:15:00"
}
```

**Error cases:**
- `400` — `amount` missing or ≤ 0

**DB verification:**

```sql
SELECT TOP 1 * FROM tblPOSCashManagementDetail
WHERE Type = 'Received'
ORDER BY POSCashManagementDetailID DESC;
```

---

### POST /pos/shift-close

Closes the currently open till shift. Records physical cash counted (a single total,
or a denomination breakdown that's summed into the total) and cash submitted, then
computes `CashDifference` via `POSCashManagmentDAL.UpdateShiftClosing` — the same
formula as `frmPOSCashManagement`'s "Cash / Shift Closing" panel:

```
CashDifference = CashCounted - (Opening + CashReceived + NetSales - CashSkimmed)
```

`denominations` is optional; when present it's persisted to `tblPOSShiftCashCount`
purely as a physical-count audit trail — `cash_counted` remains the authoritative
figure used for reconciliation regardless of whether it came from a quick total or a
denomination breakdown.

```http
POST /api/pos/shift-close
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "cash_counted": 34000.00,
  "cash_submitted": 30000.00,
  "notes": "End of shift closing",
  "denominations": {
    "d5000": 6, "d1000": 4, "d500": 0, "d100": 0,
    "d50": 0, "d20": 0, "d10": 0, "d5": 0, "other": 0
  }
}
```

> `denominations` may be omitted entirely for a quick single-total close.

**Expected response:**

```json
{
  "success": true,
  "closed_at": "2026-08-03 21:00:00",
  "cash_counted": 34000.00,
  "cash_submitted": 30000.00,
  "closing_cash": 4000.00,
  "net_sales": 12500.00,
  "opening": 20000.00,
  "cash_received": 5000.00,
  "cash_skimmed": 3000.00,
  "cash_difference": -500.00,
  "pos_code": "POS1",
  "shop_id": 1
}
```

**Error cases:**
- `400` — `cash_counted` missing or negative
- `422` — no open shift to close for this till

**DB verification:**

```sql
-- Closed shift row
SELECT TOP 1 * FROM tblPOSCashManagement
WHERE IsClosed = 1 AND POSCode = 'POS1' AND ShopID = 1
ORDER BY POSCashManagementID DESC;

-- Opening-balance audit row written by UpdateShiftClosing
SELECT TOP 1 * FROM tblPosOpeningBalance
WHERE Pos_name = 'POS1' ORDER BY pos_open_balance_id DESC;

-- Denomination breakdown (only if denominations was sent)
SELECT TOP 1 * FROM tblPOSShiftCashCount
WHERE ShopID = 1 AND POSCode = 'POS1' ORDER BY id DESC;
```

---

## 11. Quote API — Discount & Tax Parity Scenarios

All requests:

```http
POST /api/sales/quote
Authorization: Bearer <token>
Content-Type: application/json
```

---

### SCENARIO 1 — Simple Sale (no discount, no VAT)

**Configure in Candela:** Sale & Return → walk-in → product with no discount, no VAT configured.

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `gross_total` = `net_total` = `Round(Rate × 2, amountRound)`. All discounts and VAT = 0.

---

### SCENARIO 2 — SKU Flat Amount Discount

**Configure in Candela:** Configuration → Discount → Type = Flat Amount, e.g. 50. Assign to product.

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 3, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].unit_discount` = 50. `total_discount` = 150.

---

### SCENARIO 3 — SKU Percentage Discount

**Configure in Candela:** Configuration → Discount → Type = Percentage, e.g. 10%.

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].unit_discount` = `Round(Rate × 10%, 6)`.

---

### SCENARIO 4 — Buy-X-Get-Y Free

**Configure in Candela:** Discount → Type = Buy X Get Y Free. Buy 2 of Product A → Get 1 of Product B free.

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_X, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 0 },
    { "product_item_id": PRODUCT_ITEM_ID_Y, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[1].disc_category` = `"Y"`, `items[1].unit_discount` = Rate of Y. `gross_total` = Rate_X × 2.

---

### SCENARIO 5 — Qty-Based Tier Discount

**Configure in Candela:** Discount → Tier Price. Buy 1–5 → 5% off, Buy 6+ → 10% off.

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 6, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].unit_discount` matches the tier-10% rate for qty 6.

---

### SCENARIO 6 — Pack Selling

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 1, "unit_discount": 0, "pack_size": 6, "con_factor": 6 }
  ]
}
```

---

### SCENARIO 7 — Standard Customer (type 0, no customer discount)

> **Config required:** `CustomerDiscType = "0"` in `tblRCMSConfiguration`.  
> **Customer setup:** Use a member type whose `discount_percentage = 0` AND `Multiple_Customer_Disc = False` in RCMS config (or the type has no entries in `comments` when `Multiple_Customer_Disc = True`).  
> If `discount_percentage > 0`, the customer discount WILL be applied (matching Candela behaviour) — this scenario specifically tests the zero-discount case.

```json
{
  "customer_id": CUSTOMER_ID_TYPE0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```

**Verify:** `customer_discount` = 0. `items[0].customer_discount_per_unit` = 0.

---

### SCENARIO 8 — Employee Discount (type 1)

> **Config required:** `CustomerDiscType = "1"` in `tblRCMSConfiguration` (system-wide — conflicts with Scenario 7; run on a separate environment or change config between tests).  
> **Member type setup:** `IsEmployeeDiscOn = True`, `discount_percentage = 15`, `QtyLimit` large enough that the test cart fits within the remaining allowance.

```json
{
  "customer_id": CUSTOMER_ID_EMPLOYEE,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```

**Verify:** `items[0].customer_discount_per_unit` = `Round(Rate × 15%, amountRound)`.  
`customer_discount` = `items[0].customer_discount_per_unit × 2` (per-line rounding then sum — NOT `Round(Rate × 15% × 2)`; those can differ by 1 cent for fractional rates).

> If `QtyPurchased >= QtyLimit` Candela returns 0 discount. If `QtyPurchased + CartQty > QtyLimit` it also returns 0 (all-or-nothing). Confirm the test customer has room under the limit.

---

### SCENARIO 9 — Loyalty Cash Discount

> **Config required:** `MemberClubPointSystemAvailable = "true"` in `tblRCMSConfiguration`. `CustomerDiscType` must NOT be `"1"`.  
> **Group policy setup:** Customer's member type must be linked to a group policy with `Cash_Discount > 0` in `tbldefGroupPolicyDetail` for the product's `line_item_id` (department).

```json
{
  "customer_id": CUSTOMER_ID_LOYALTY,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_LOYALTY, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```

**Verify:** `is_loyalty_on` = true.  
`items[0].loyalty_cash_discount_per_unit` = `Round(Rate × cashDiscPct%, amountRound)`  
where `cashDiscPct` = the `Cash_Discount` column in `tbldefGroupPolicyDetail` (NOT the `Points` column — those are different fields used only for earned-points calculation).  
`earned_points` = `Round((Rate − loyaltyCashDisc) × pointsPct% / 100, amountRound) × qty`.

---

### SCENARIO 10 — Loyalty Blocked by `blockCustDiscOnUnitDisc`

**Configure in Candela:** RCMS Configuration → DiscountPriority = Product.

```json
{
  "customer_id": CUSTOMER_ID_LOYALTY,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_WITH_DISCOUNT, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].unit_discount` > 0. `items[0].loyalty_cash_discount_per_unit` = 0. `customer_discount` = 0.

---

### SCENARIO 11 — Marketing Discount

**Configure in Candela:** Configuration → Marketing Discount → date range today, amount 100, All Items.

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 },
    { "product_item_id": PRODUCT_ITEM_ID_2, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `marketing_discount` = 100. Each item's `marketing_disc_per_unit` is a proportional share.

---

### SCENARIO 12 — Coupon

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "coupon_code": "TESTCOUPON123",
  "items": [
    { "product_item_id": COUPON_PRODUCT_ID, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].disc_category` = `"C"`. `customer_discount` = 0. `coupon_discount` = applied amount.

---

### SCENARIO 13 — Add-on VAT (`PriceIncludesVAT = False`)

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_WITH_VAT_ID, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].vat_factor` = 17. `net_total` = `gross_total + vat_amount`.

---

### SCENARIO 14 — Shop-Based VAT (`isShopBasedVAT = True`)

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].vat_factor` = shop rate (not product rate). `vat_amount` = `Rate × 2 × shopVAT%`.

---

### SCENARIO 15 — Card-Based VAT (`chkSaleTaxOnCard = True`)

```json
{
  "customer_id": 0,
  "payment_type": "card",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].vat_factor` = card tax rate. `vat_amount` differs from cash scenario.

---

### SCENARIO 16 — Price Includes VAT (`PriceIncludesVAT = True`)

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": TAG_PRICE_PRODUCT_ID, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `items[0].price_include_vat` = true. `net_total` = `gross_total` (VAT not added again).

---

### SCENARIO 17 — Slab VAT

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `vat_amount` = result of slab lookup on `gross_total`. Per-item `vat_value` = 0.

---

### SCENARIO 18 — Additional Tax Formula-1 (per item, folded into VAT)

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_WITH_VAT_ID, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `vat_amount` = stdVAT + addlTax combined. `additional_tax` = 0.

---

### SCENARIO 19 — Additional Tax Formula-2 (on net total, shown separately)

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_WITH_VAT_ID, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 0 }
  ]
}
```

**Verify:** `vat_amount` = standard VAT only. `additional_tax` = `net_total_before_addl × addlPct%` (separate).

---

### SCENARIO 20 — Combined: SKU Discount + Loyalty + VAT

> **Config required:** Loyalty ON, no discount-priority blocking, `IsSubtractUnitDiscount = True` in `tblShopConfiguration` (affects VAT base; see notes below).

```json
{
  "customer_id": CUSTOMER_ID_LOYALTY,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_WITH_DISCOUNT_AND_VAT_ID, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```

**Verify (assuming `IsSubtractUnitDiscount=True`, `IsSubtractCustomerDiscount=False`, no marketing discount):**

- `items[0].unit_discount` = `Round(Rate × skuDiscPct%, 6)` (6 places, `frmSaleAndReturn.vb:25748`)
- `items[0].loyalty_cash_discount_per_unit` = `Round((Rate − unitDisc) × cashDiscPct%, amountRound)`  
  where `cashDiscPct` = `Cash_Discount` column from `tbldefGroupPolicyDetail` — NOT the points %. When the product has a promo SKU discount (`discountId > 0`) and neither blocking flag is set, `Cash_Dis_During_Sales` is used instead (`frmSaleAndReturn.vb:25888`).
- `items[0].vat_value` depends on `IsSubtractUnitDiscount`:
  - `True` → VAT base = `Rate − unitDisc` → `vat_value = Round((Rate − unitDisc) × vatPct%, amountRound)`
  - `False` → VAT base = `Rate` → `vat_value = Round(Rate × vatPct%, amountRound)`
- `net_total` = `gross_total − customer_discount − marketing_discount + vat_amount`  
  (`gross_total` in the response is already `SUM((Rate − unitDisc) × qty)`, i.e. unit discounts are baked in)

---

### SCENARIO 21 — Flat Customer Discount (`Multiple_Customer_Disc = False`) ⬅ NEW

> **Verifies Bug G fix.** Before the fix, `customer_discount` was always 0 for this case.  
> **Config required:** `Multiple_Customer_Disc = False` in `tblRCMSConfiguration`. Customer's `tblDefMemberTypes.discount_percentage = 5` (or any non-zero value). `CustomerDiscType = "0"` or `"2"`. Loyalty OFF.

```json
{
  "customer_id": CUSTOMER_ID_FLAT_DISC,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_1, "quantity": 2, "unit_discount": 0, "pack_size": 0, "con_factor": 1 },
    { "product_item_id": PRODUCT_ITEM_ID_2, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```

**Verify:**
- `items[0].customer_discount_per_unit` = `Round(Rate1 × 5%, 4)` (hardcoded 4 d.p. — `frmSaleAndReturn.vb:13491`)
- `items[1].customer_discount_per_unit` = `Round(Rate2 × 5%, 4)`
- Both products get the same flat % regardless of department — Candela cross-joins ALL `tbldeflineitems` with `discount_percentage` when `Multiple_Customer_Disc=False` (`CustomerTypeDAL.vb:1644`)
- `customer_discount` = sum of per-unit amounts × quantities

**DB query to confirm setup:**
```sql
SELECT discount_percentage FROM tblDefMemberTypes
WHERE member_type_id = (SELECT member_type_id FROM tblMemberInfo WHERE member_id = CUSTOMER_ID_FLAT_DISC);

SELECT config_value FROM tblRCMSConfiguration WHERE config_name = 'Multiple_Customer_Disc';
```

---

### SCENARIO 22 — Type "3" Customer with Loyalty ON ⬅ NEW

> **Verifies Bug H fix.** Before the fix, type "3" was excluded from the loyalty path (same exclusion as type "1") and returned `loyalty_cash_discount_per_unit = 0`. Candela only excludes type "1" (`frmSaleAndReturn.vb:25879`).  
> **Config required:** `CustomerDiscType = "3"` in `tblRCMSConfiguration`. Loyalty ON. Customer has a loyalty group policy with `Cash_Discount > 0`.

```json
{
  "customer_id": CUSTOMER_ID_TYPE3_LOYALTY,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_LOYALTY, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```

**Verify:**
- `is_loyalty_on` = true
- `items[0].loyalty_cash_discount_per_unit` > 0 (loyalty cash disc applied — NOT zero)
- `items[0].customer_discount_per_unit` = same as `loyalty_cash_discount_per_unit` (they are the same amount; loyalty IS the customer disc for this type)
- `earned_points` > 0

---

### SCENARIO 23 — `DiscountPriority = Customer` Blocks SKU Disc (Flat Disc Customer) ⬅ NEW

> **Verifies Bug K fix.** Before the fix, `customerHasLineDiscounts` was `false` when `Multiple_Customer_Disc=False` (map was empty), so SKU disc was never blocked even with `DiscountPriority=Customer`.  
> **Config required:** `DiscountPriority = "Customer"` in `tblRCMSConfiguration`. `Multiple_Customer_Disc = False`. Customer has `discount_percentage = 10%`. Product has an active SKU discount.

```json
{
  "customer_id": CUSTOMER_ID_FLAT_DISC,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_WITH_SKU_DISCOUNT_ID, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```

**Verify:**
- `items[0].unit_discount` = 0 (SKU disc blocked because customer has a flat discount and `DiscountPriority=Customer`)
- `items[0].customer_discount_per_unit` = `Round(Rate × 10%, 4)` (customer disc applied instead)
- Compare against Candela: ring the same product for the same customer — SKU disc column should be 0, CustDisc column should be 10%

---

### SCENARIO 24 — Multi-Shop Loyalty (Customer Registered at Different Shop) ⬅ NEW

> **Verifies Bug I fix.** Before the fix, loyalty DAL calls used the POS terminal's `shop_id` instead of the customer's registered `shop_id` (`tblMemberInfo.shop_id`). In a single-shop setup both are the same so the bug is invisible. In multi-shop setups the group policy join fails and `loyalty_cash_discount_per_unit` returns 0.  
> **Setup required:** Customer registered (created) at Shop 1. POS terminal JWT issued for Shop 2. Loyalty group policy configured at Shop 1.

```json
{
  "customer_id": CUSTOMER_REGISTERED_AT_SHOP_1,
  "payment_type": "cash",
  "items": [
    { "product_item_id": PRODUCT_ITEM_ID_LOYALTY, "quantity": 1, "unit_discount": 0, "pack_size": 0, "con_factor": 1 }
  ]
}
```
*(Send this request with a Bearer token issued for Shop 2)*

**Verify:**
- `items[0].loyalty_cash_discount_per_unit` > 0 (group policy found via customer's home shop, not POS shop)

**DB query to confirm the scenario:**
```sql
-- Customer's registered shop
SELECT shop_id FROM tblMemberInfo WHERE member_id = CUSTOMER_REGISTERED_AT_SHOP_1;

-- Group policy must exist under shop_id = 1 (customer's shop), not shop_id = 2 (POS shop)
SELECT * FROM tbldefGroupPolicyDetail
WHERE GroupPolicyID IN (
    SELECT GroupPolicyID FROM tblDefGroupPolicy
    WHERE MemberTypeID = (SELECT member_type_id FROM tblMemberInfo WHERE member_id = CUSTOMER_REGISTERED_AT_SHOP_1)
);
```

---

## 12. Common Error Responses

| HTTP Status | Meaning | Fix |
|---|---|---|
| 400 | Missing required field | Check JSON body — required field absent or zero |
| 401 | Missing / expired / revoked token | Re-login or refresh token |
| 404 | Record not found | Verify IDs exist in DB for current shop |
| 409 | Conflict (already voided, already returned, idempotent hit) | Check response `error` field |
| 422 | Business rule violation (over-return qty, credit limit, etc.) | Check response `error` field |
| 502 | Printer TCP connection failed | Check printer IP, port, and network |
| 500 | Unhandled DAL/DB error | Check server logs — usually a missing DB field or constraint |
