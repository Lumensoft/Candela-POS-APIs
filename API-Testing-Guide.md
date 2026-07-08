# POS API — Complete Testing Guide

## How to use this guide

1. Replace `YOUR_SHOP_ID` and `YOUR_USER_ID` with real IDs from your database.
2. Replace `PRODUCT_ITEM_ID_*` with real `Product_Item_ID` values from `tblProductItem`.
3. Replace `CUSTOMER_ID_*` with real `member_id` values from `tblMemberInfo`.
4. Every request except `/auth/login` requires the `Authorization: Bearer <token>` header.
5. For each `/quote` scenario, the **"Configure in Candela"** section tells you exactly which
   Candela screen to use and what to set up before calling the API — so you can punch the
   same sale in Candela and compare numbers.

---

## Base URL

```
http://<server>/api
```

---

## 1. Authentication

### POST /auth/login

Validates credentials and returns a JWT token.  
Use the returned `token` as `Authorization: Bearer <token>` on all subsequent requests.

```http
POST /api/auth/login
Content-Type: application/json
```

```json
{
  "username": "admin",
  "password": "your_password",
  "shop_id": 1
}
```

**Expected response:**

```json
{
  "success": true,
  "data": {
    "token": "eyJhbGci...",
    "user_id": 1,
    "user_name": "admin",
    "shop_id": 1,
    "expires_in": 1800
  }
}
```

---

## 2. Masters

### GET /masters/products

Returns the full product list. Use `since` for delta sync (only items changed after that timestamp).

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

## 3. Quote API — All Scenarios

All requests:

```http
POST /api/sales/quote
Authorization: Bearer <token>
Content-Type: application/json
```

---

### SCENARIO 1 — Simple Sale (no discount, no VAT)

**Configure in Candela:**
- Open Candela → **Shop Activities → Sale & Return**
- Do NOT select any customer (walk-in)
- Add any item to the grid — make sure no discount is configured for this product
  (check: `Configuration → Product → [product] → Discount tab` — should be empty)
- Verify `txtGrossTotal` = Rate × Qty, `txtNetTotal` = same

**Candela numbers to match:**
- `txtGrossTotal`, `txtNetTotal`

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `gross_total` = `net_total` = `Round(Rate × 2, amountRound)`
- `total_discount` = 0
- `customer_discount` = 0
- `vat_amount` = 0

---

### SCENARIO 2 — SKU Flat Amount Discount

**Configure in Candela:**
- Go to **Configuration → Discount → Add New**
- Set `Discount Type = Flat Amount`, enter amount e.g. `50`
- Assign to your product under **Products** tab
- In Sale screen: add the product → grid should show `Unit Discount = 50`
- Check `txtTotalDiscount` and `txtGrossTotal`

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 3,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].unit_discount` = 50 (returned by server from DAL)
- `total_discount` = `Round(50 × 3, amountRound)` = 150
- `gross_total` = `Round((Rate − 50) × 3, amountRound)`

---

### SCENARIO 3 — SKU Percentage Discount

**Configure in Candela:**
- Go to **Configuration → Discount → Add New**
- Set `Discount Type = Percentage`, enter e.g. `10` (meaning 10%)
- Assign to your product
- In Sale screen: add the product → `Unit Discount` = Rate × 10%
- Check `txtTotalDiscount`

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].unit_discount` = `Round(Rate × 10%, 6)`
- `gross_total` = `Round((Rate − unitDisc) × 2, amountRound)`

---

### SCENARIO 4 — Buy-X-Get-Y Free

**Configure in Candela:**
- Go to **Configuration → Discount → Add New**
- Set `Discount Type = Buy X Get Y Free`
- Set X product and Y product, e.g. Buy 2 of Product A, Get 1 of Product B free
- In Sale screen: add 2× Product A + 1× Product B
- Candela should show Product B with 100% discount → `Unit Discount = Rate of B`
- Check `txtTotalDiscount` and `txtGrossTotal`

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_X,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    },
    {
      "product_item_id": PRODUCT_ITEM_ID_Y,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].disc_category` = `"X"`
- `items[1].disc_category` = `"Y"`, `unit_discount` = Rate of Y
- `gross_total` = `Round(Rate_X × 2, amountRound)` (Y is free)

---

### SCENARIO 5 — Tier / Qty-Based Discount

**Configure in Candela:**
- Go to **Configuration → Discount → Add New**
- Set `Discount Type = Tier Price` (or Qty Based)
- Set tiers: e.g. Buy 1–5 → 5% off, Buy 6+ → 10% off
- In Sale screen: add 6× of the product → should show 10% discount
- Check `txtTotalDiscount`

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 6,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].unit_discount` matches the tier rate for qty 6

---

### SCENARIO 6 — Pack Selling

**Configure in Candela:**
- Configure a product with a pack size (e.g. pack of 6)
- In Sale screen: add the product as a pack
- Check unit discount reflects the pack discount

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 6,
      "con_factor": 6
    }
  ]
}
```

---

### SCENARIO 7 — Standard Customer (type "0", no customer discount)

**Configure in Candela:**
- Go to **Configuration → Member Types**
- Create/use a member type with `CustomerDiscType = Standard (0)` and a `discount_percentage`
- Assign a customer to this type
- In Sale screen: select this customer, add items
- **Expected:** `txtCustomerDiscount = 0` (Candela does not apply global % for type-0)

```json
{
  "customer_id": CUSTOMER_ID_TYPE0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `customer_discount` = 0 (matches Candela) ✅

---

### SCENARIO 8 — Employee Discount (type "1")

**Configure in Candela:**
- Go to **Configuration → Member Types**
- Create/use a type with `CustomerDiscType = Employee (1)`
- Set `IsEmployeeDiscOn = True`, set `discount_percentage = 15`, set `QtyLimit = 100`
- Assign a customer to this type
- In Sale screen: select this customer → `txtCustomerDiscount` should show 15% of (Rate−UnitDisc)

```json
{
  "customer_id": CUSTOMER_ID_EMPLOYEE,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `customer_discount` = `Round(Round((Rate−0) × 15%, amountRound) × 2, amountRound)`

---

### SCENARIO 9 — Loyalty Cash Discount (no unit discount on item)

**Configure in Candela:**
- Go to **Configuration → Loyalty Club**
- Enable loyalty, set a cash discount % for the product/category (e.g. 5%)
- Assign the customer to a loyalty tier
- In Sale screen: select the loyalty customer, add an item with NO unit discount
- Check `txtCustomerDiscount` = loyalty cash amount

```json
{
  "customer_id": CUSTOMER_ID_LOYALTY,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_LOYALTY,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `is_loyalty_on` = true
- `items[0].loyalty_cash_discount_per_unit` = `Round(Rate × loyaltyPct%, amountRound)`
- `customer_discount` = `Round(loyaltyCashDiscUnit × 2, amountRound)`

---

### SCENARIO 10 — Loyalty Cash Discount (item HAS unit discount)

**Configure in Candela:**
- Same loyalty setup as Scenario 9
- Use a product that ALSO has a SKU discount configured (e.g. 20 flat)
- In Sale screen: select loyalty customer, add the discounted product
- `txtCustomerDiscount` loyalty base = `(Rate − UnitDisc) × loyaltyPct%`

```json
{
  "customer_id": CUSTOMER_ID_LOYALTY,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_WITH_DISCOUNT,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].unit_discount` = (returned by DAL, e.g. 20)
- `items[0].loyalty_cash_discount_per_unit` = `Round((Rate − 20) × loyaltyPct%, amountRound)`
  (base is Rate minus unit discount, NOT Rate alone)

---

### SCENARIO 11 — Loyalty Blocked by `blockCustDiscOnUnitDisc`

**Configure in Candela:**
- Go to **Configuration → RCMS Configuration**
- Set `DiscountPriority = Product`  (this sets `blockCustDiscOnUnitDisc = True`)
- Use a loyalty customer + a product with a SKU discount
- In Sale screen: `txtCustomerDiscount = 0` because product discount takes priority

```json
{
  "customer_id": CUSTOMER_ID_LOYALTY,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_WITH_DISCOUNT,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].unit_discount` > 0
- `items[0].loyalty_cash_discount_per_unit` = 0 (blocked)
- `customer_discount` = 0

---

### SCENARIO 12 — Marketing Discount (all items)

**Configure in Candela:**
- Go to **Configuration → Marketing Discount**
- Add a discount: date range covering today, amount e.g. 100 flat
- Set `Applicable For = All Items`
- In Sale screen: add any items → `txtMarketingDiscount` should show 100

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    },
    {
      "product_item_id": PRODUCT_ITEM_ID_2,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `marketing_discount` = 100 (matches `txtMarketingDiscount`)
- Each item's `marketing_disc_per_unit` = proportional share

---

### SCENARIO 13 — Marketing Discount (selected items only)

**Configure in Candela:**
- Same as Scenario 12 but set `Applicable For = Selected Items`
- Assign specific products under the discount's **Products** tab
- In Sale screen: add eligible + non-eligible items
- Only eligible items bear the discount proportion

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": ELIGIBLE_PRODUCT_ID,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    },
    {
      "product_item_id": NON_ELIGIBLE_PRODUCT_ID,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `marketing_discount` = same total amount
- `items[0].marketing_disc_per_unit` > 0 (eligible)
- `items[1].marketing_disc_per_unit` = 0 (not eligible)

---

### SCENARIO 14 — Coupon

**Configure in Candela:**
- Go to **Configuration → Coupon Master**
- Create a coupon: valid date range covering today, status = ACTIVE
- Assign product items and `Disc_per` (discount %) per item
- Set `Disc_Amt_limit` (total cap)
- In Sale screen: click the Coupon button, enter the coupon code
- Coupon replaces unit discount, customer discount becomes 0

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "coupon_code": "TESTCOUPON123",
  "items": [
    {
      "product_item_id": COUPON_PRODUCT_ID,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `coupon_no` = `"TESTCOUPON123"`
- `items[0].disc_category` = `"C"`
- `items[0].unit_discount` = `Rate × Disc_per%`
- `coupon_discount` = applied coupon total (capped at `Disc_Amt_limit`)
- `customer_discount` = 0

---

### SCENARIO 15 — Standard Add-on VAT (`PriceIncludesVAT = False`)

**Configure in Candela:**
- Go to **Configuration → Shop Configuration**
- Set `PriceIncludesVAT = False`
- Set product VAT to e.g. 17%  
  (**Configuration → Product → [product] → VAT = 17**)
- In Sale screen: add the product → `txtVAT` should show `Rate × 17%`
- `txtNetTotal = txtGrossTotal + txtVAT`

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_WITH_VAT_ID,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].vat_factor` = 17
- `items[0].vat_value` = `Rate × 17%`
- `vat_amount` = `Round(Rate × 17%, amountRound)`
- `net_total` = `gross_total + vat_amount`

---

### SCENARIO 16 — Shop-Based VAT (`isShopBasedVAT = True`)

**Configure in Candela:**
- Go to **Configuration → Shop Configuration**
- Set `isShopBasedVAT = True`, `VATPercentage = 15`
- All products use 15% regardless of their individual VAT setting
- In Sale screen: `txtVAT = SUM(Rate × 15%)`

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].vat_factor` = 15 (shop rate, not product rate)
- `vat_amount` = `Round(Rate × 2 × 15%, amountRound)`

---

### SCENARIO 17 — Card-Based VAT (`chkSaleTaxOnCard = True`)

**Configure in Candela:**
- Go to **Configuration → Shop Configuration**
- Set `isShopBasedVAT = True`, `chkSaleTaxOnCard = True`, `txtSaleTaxPercentOnCard = 5`
- In Sale screen: click Card payment option → `txtVAT` recalculates at 5% instead of shop VAT

```json
{
  "customer_id": 0,
  "payment_type": "card",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].vat_factor` = 5 (card tax rate)
- `vat_amount` differs from cash scenario

---

### SCENARIO 18 — Price Includes VAT with Tag Price (`isShowTagPrice = True`)

**Configure in Candela:**
- Go to **Configuration → Shop Configuration**
- Set `PriceIncludesVAT = True`, `isShowTagPrice = True`
- Product price in DB is the VAT-inclusive tag price (e.g. 117 including 17% VAT)
- In Sale screen: add product at 117 → `txtVAT` = extracted VAT portion, `txtNetTotal` = 117 (VAT already embedded, not added again)

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": TAG_PRICE_PRODUCT_ID,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].price_include_vat` = true
- `items[0].vat_value` = extracted VAT (Rate / 1.17 × 0.17 if 17%)
- `net_total` = `gross_total` (VAT not added again)

---

### SCENARIO 19 — Slab VAT (`VATType = SLABS`)

**Configure in Candela:**
- Go to **Configuration → RCMS Configuration**
- Set `VATType = SLABS`
- Go to **Configuration → Tax Ranges** — define slabs (e.g. 0–500 = 5%, 501–1000 = 8%)
- In Sale screen: subtotal 600 → `txtVAT` = 600 × 8%
- `txtVAT` is invoice-level, not per-item

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `vat_amount` = result of slab lookup on `gross_total`
- Per-item `vat_value` = 0 (slab is invoice-level)

---

### SCENARIO 20 — Additional Tax Formula-1 (per item)

**Configure in Candela:**
- Go to **Configuration → Shop Configuration**
- Set `Additional_Sale_Tax_Formula = 1` (per item)
- Set `Addtional_Sale_Tax = 2` (2% additional)
- In Sale screen: `txtVAT` = standard VAT + 2% of (Rate + VatChargedPerUnit)
- Additional tax is FOLDED INTO `txtVAT`, not shown separately

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_WITH_VAT_ID,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `vat_amount` = `Round(vatValue + addlTax, amountRound)` (combined — matches `txtVAT`)
- `additional_tax` = 0 (formula-1 is folded into `vat_amount`)
- `items[0].additional_tax_percent` = 2

---

### SCENARIO 21 — Additional Tax Formula-2 (on net total)

**Configure in Candela:**
- Go to **Configuration → Shop Configuration**
- Set `Additional_Sale_Tax_Formula = 2` (on net total)
- Set `Addtional_Sale_Tax = 3` (3% additional)
- In Sale screen: `txtVAT` = standard VAT only; additional 3% is applied ON TOP of `txtNetTotal`
- Candela shows it as a separate line below net total

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_WITH_VAT_ID,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `vat_amount` = standard VAT only
- `additional_tax` = `Round(net_total_before_addl × 3%, amountRound)` (separate)
- `net_total` = standard net + `additional_tax`

---

### SCENARIO 22 — Gift Card (payment method only, no effect on totals)

**Configure in Candela:**
- Issue a gift card via **Configuration → Gift Card**
- In Sale screen: add items normally, then in payment area select **Gift Card**, enter the card number
- `txtGrossTotal`, `txtNetTotal`, all discounts, VAT — ALL are IDENTICAL to a cash sale
- Gift card only affects the payment split, not the cart calculation

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_ITEM_ID_1,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

> **Note:** Gift card is a payment tender — you pass it in `/sales` (not `/quote`).
> `/quote` totals are identical whether the customer pays cash or gift card.
> The `/sales` call passes `gift_card_no` and `gift_card_amount`.

---

### SCENARIO 23 — Combined: SKU Discount + Loyalty + VAT

**Configure in Candela:**
- Product has a 10% SKU discount configured
- Loyalty club active, 5% cash discount for this product
- Standard VAT 17%
- In Sale screen: select loyalty customer, add the product
- `UnitDisc = Rate×10%`, `LoyaltyCashDisc = (Rate − UnitDisc)×5%`, `VatValue = (Rate − UnitDisc)×17%`

```json
{
  "customer_id": CUSTOMER_ID_LOYALTY,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_WITH_DISCOUNT_AND_VAT_ID,
      "quantity": 2,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- `items[0].unit_discount` = `Round(Rate × 10%, 6)`
- `items[0].loyalty_cash_discount_per_unit` = `Round((Rate − unitDisc) × 5%, amountRound)`
- `items[0].vat_value` = `Round((Rate − unitDisc) × 17%, amountRound)` (VAT base minus unit disc)
- `gross_total` = `Round((Rate − unitDisc) × 2, amountRound)`
- `customer_discount` = `Round(loyaltyCashDiscUnit × 2, amountRound)`
- `vat_amount` = `Round(vatValue × 2, amountRound)`
- `net_total` = `gross_total − customer_discount + vat_amount`

---

### SCENARIO 24 — ESD / FBR Integration

**Configure in Candela:**
- Go to **Configuration → RCMS Configuration**
- Set `IsESDIntegrationEnabled = True`
- Set product `RRP` (Recommended Retail Price) in **Configuration → Product**
- When Rate < RRP: Candela uses RRP as VAT base (FBR mandate)
- When Rate >= RRP: Candela uses Rate as VAT base

```json
{
  "customer_id": 0,
  "payment_type": "cash",
  "items": [
    {
      "product_item_id": PRODUCT_WITH_RRP_ID,
      "quantity": 1,
      "unit_discount": 0,
      "pack_size": 0,
      "con_factor": 0
    }
  ]
}
```

**Verify in response:**
- When selling below RRP: `items[0].vat_value` = `RRP × vatFactor%` (not Rate × vatFactor%)

---

## 4. Sales API

### POST /sales

Finalizes the sale and persists to `tblSales` + `tblSalesLineItems`.  
Always call `/quote` first and echo back its per-item values into the `/sales` request.

```http
POST /api/sales
Authorization: Bearer <token>
Content-Type: application/json
```

#### 4a — Simple Cash Sale

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
{
  "success": true,
  "data": {
    "sale_id": 12345
  }
}
```

---

#### 4b — Sale with Coupon

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

---

#### 4c — Gift Card Payment

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

---

#### 4d — Credit Sale

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

#### 4e — Idempotency Test

Submit the exact same request twice with the same `client_txn_guid`.  
Second call must return the **same `sale_id`** without inserting a duplicate row.

```json
{
  "client_txn_guid": "550e8400-e29b-41d4-a716-446655440000"
}
```

**Expected on second call:**

```json
{
  "success": true,
  "data": {
    "sale_id": 12345,
    "idempotent": true
  }
}
```

---

## 5. DB Verification Queries

After each `/sales` call, run these in SQL Server Management Studio to confirm the data was saved:

```sql
-- 1. Sale header
SELECT TOP 1 *
FROM tblSales
ORDER BY sale_id DESC;

-- 2. Line items
SELECT *
FROM tblSalesLineItems
WHERE sale_id = <sale_id from response>;

-- 3. Inventory decremented
SELECT *
FROM tblShopProductInventory
WHERE product_item_id = <product_item_id>
  AND shop_id = <shop_id>;

-- 4. Coupon marked Used (if coupon test)
SELECT cd.CouponNo, cd.Status
FROM tblCouponDtl cd
WHERE cd.CouponNo = 'TESTCOUPON123';
```

---

## 6. Common Error Responses

| HTTP Status | Meaning | Fix |
|---|---|---|
| 401 | Missing or expired token | Call `/auth/login` again, use new token |
| 400 | Missing required field | Check JSON body — `items` or `client_txn_guid` missing |
| 404 | Product not found | Verify `product_item_id` exists in `tblProductItem` for your shop |
| 409 | Coupon no longer active | Coupon already used or expired — check `tblCouponDtl.Status` |
| 422 | Credit limit exceeded | Customer's outstanding balance + this sale exceeds `credit_limit` |
| 500 | DAL error | Check server logs — usually a missing DB field or constraint violation |
