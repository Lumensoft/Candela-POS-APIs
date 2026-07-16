using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using CandelaPOS.Models;
using DAL;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/sales")]
    public class QuoteController : ApiController
    {
        // POST api/sales/quote
        // Server-authoritative price/discount/VAT calculation for a cart.
        // Called when the cashier clicks Compute — not on every cart change.
        [HttpPost, Route("quote")]
        public HttpResponseMessage Quote([FromBody] QuoteRequest req)
        {
            if (req == null || req.Items == null || req.Items.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "items cannot be empty" });

            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

            try
            {
                var cfg     = LoadConfig(shopId);
                var rcmsCfg = CandelaBootstrap.GetRCMSConfig();
                var now     = DateTime.Now;
                bool isCard = (req.PaymentType ?? "").ToLower() == "card";

                // ── tblShopConfiguration ──────────────────────────────────────────────
                bool   isShopBasedVAT   = Eq(cfg, "isShopBasedVAT",   "True");
                bool   priceIncludesVAT = Eq(cfg, "PriceIncludesVAT", "True");
                double configVatPct     = Dbl(cfg, "VATPercentage");
                bool   taxOnCard        = Eq(cfg, "chkSaleTaxOnCard",  "True");
                double cardTaxPct       = Dbl(cfg, "txtSaleTaxPercentOnCard");
                double addlSaleTaxPct   = Dbl(cfg, "Addtional_Sale_Tax");  // note: typo is Candela's
                bool   addlTaxOnNetTotal= Dbl(cfg, "Additional_Sale_Tax_Formula") == 2;

                // IsSubtract* — control VAT BASE, not net-total arithmetic.
                // frmSaleAndReturn.vb:14529-14578
                bool subtractUnitDisc = Eq(cfg, "IsSubtractUnitDiscount",     "True");
                bool subtractCustDisc = Eq(cfg, "IsSubtractCustomerDiscount",  "True");
                bool subtractMktDisc  = Eq(cfg, "IsSubtractMarketingDiscount", "True");

                // When PriceIncludesVAT=True AND isShowTagPrice=True, Candela divides Rate
                // by (1+vatFactor/100) to extract the ex-VAT base before computing VAT.
                // Standard shops have isShowTagPrice=False, so Rate IS the ex-VAT base.
                // frmSaleAndReturn.vb:14802-14806; tblShopConfiguration key = "isShowTagPrice"
                bool isShowTagPrice   = Eq(cfg, "isShowTagPrice", "True");

                // ── tblRCMSConfiguration ──────────────────────────────────────────────
                string discPriority          = rcmsCfg.TryGetValue("DiscountPriority", out var dp) ? dp : "";
                bool   blockCustDiscOnUnitDisc = string.Equals(discPriority, "Product",  StringComparison.OrdinalIgnoreCase);
                bool   blockUnitDiscOnCustDisc = string.Equals(discPriority, "Customer", StringComparison.OrdinalIgnoreCase);

                // BlockProductDiscount_Under_CustomerPrice (frmSaleAndReturn.vb:25795, 25847)
                // Only active when RetailPriceMethodology=3. Calls GetCustomerTypeBasedSKUPrice()
                // per line; if the customer type has a specific price for that SKU, discount is zeroed.
                bool blockBelowCustPrice =
                    string.Equals(rcmsCfg.TryGetValue("BlockProductDiscount_Under_CustomerPrice", out var bbcp) ? bbcp : "",
                        "true", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rcmsCfg.TryGetValue("RetailPriceMethodology", out var rpm) ? rpm : "1", "3");

                bool isSlabVAT = string.Equals(
                    rcmsCfg.TryGetValue("VATType", out var vts) ? vts : "", "SLABS",
                    StringComparison.OrdinalIgnoreCase);

                bool isLoyaltyOn = string.Equals(
                    rcmsCfg.TryGetValue("MemberClubPointSystemAvailable", out var loyStr) ? loyStr : "",
                    "true", StringComparison.OrdinalIgnoreCase);

                // CustomerDiscType: "1"=employee, "3"=qty-based employee variant, "0"/"2"=standard
                // frmSaleAndReturn.vb:13406
                string customerDiscType = rcmsCfg.TryGetValue("CustomerDiscType", out var cdt) ? cdt : "0";
                // Candela line 25879: loyalty path excludes only type "1" — type "3" IS included.
                bool   useLoyaltyCashDisc = isLoyaltyOn && req.CustomerId > 0 && customerDiscType != "1";

                // ESD integration (FBR/Pakistan): VAT base = MAX(Rate, RRP).
                // frmSaleAndReturn.vb:14585-14613; config key in tblRCMSConfiguration.
                bool isESDEnabled = Eq(rcmsCfg, "IsESDIntegrationEnabled", "True");

                // Rounding precision — tblRCMSConfiguration:
                //   isEnableDecimalPoint = decimal places for monetary amounts (stored as numeric, e.g. "2")
                //   IsEnableQty          = decimal places for quantities (display-only; quantities are
                //                          caller-supplied so qtyRound is not used server-side here)
                // Primary case: numeric string parsed directly.
                // "True"/"False" are legacy fallbacks in Candela's ModUtlGlobalFunctions.vb:812-819.
                string dpRaw  = rcmsCfg.TryGetValue("isEnableDecimalPoint", out var dpRawVal) ? dpRawVal : "2";
                int amountRound = int.TryParse(dpRaw, out int dpInt)
                    ? dpInt
                    : string.Equals(dpRaw, "False", StringComparison.OrdinalIgnoreCase) ? 0 : 2;

                // When Multiple_Customer_Disc=True, customer type stores per-item disc %
                // in tblDefMemberTypes.comments ("itemId,pct|itemId,pct|...").
                // CustomerTypeDAL.vb:1595-1623
                bool isMultipleCustDisc = Eq(rcmsCfg, "Multiple_Customer_Disc", "True");

                double custDiscPct = useLoyaltyCashDisc ? 0 : GetCustomerDiscountPct(req.CustomerId);

                // ── Gap 6: resolve coupon ─────────────────────────────────────────────
                // Coupon is cart-level. /quote only validates — marking Used happens at /sales.
                // frmCouponPopUp.vb:98, frmSaleAndReturn.vb:16601
                CouponInfo coupon     = null;
                Dictionary<int, double> couponLines = null;  // product_item_id → Disc_per
                string appliedCouponNo = null;

                if (!string.IsNullOrWhiteSpace(req.CouponCode))
                {
                    coupon = LookupCoupon(req.CouponCode, now);
                    if (coupon == null)
                        return Request.CreateResponse((HttpStatusCode)422,
                            new { error = $"Coupon '{req.CouponCode}' is invalid, expired, or already used." });

                    couponLines     = GetCouponLineItems(coupon.CouponId);
                    appliedCouponNo = req.CouponCode;
                }

                // ── Employee / per-item customer discount pre-computation ─────────────
                // Must run before Pass 1 because employee qty check needs total cart qty.
                // empInfo is always fetched when a customer is present so MemberShopId is
                // available for the loyalty DAL calls (frmSaleAndReturn.vb:25889 uses
                // SelectedCustomerShopID, the customer's home shop, not the POS shop).
                double empDiscPct = 0;                                   // type "1"/"3" qty-limited %
                var    multiItemDiscMap = new Dictionary<int, double>(); // lineItemId (dept) → disc% (Multiple_Customer_Disc=True)
                CustomerTypeDetails empInfo = null;

                if (req.CustomerId > 0)
                {
                    empInfo = GetCustomerTypeDetails(req.CustomerId);

                    if (!useLoyaltyCashDisc && empInfo != null)
                    {
                    if (customerDiscType == "1" || customerDiscType == "3")
                    {
                        // Employee / qty-limited discount: same global % but only if within QtyLimit.
                        // frmSaleAndReturn.vb:37283-37316, 37350-37368
                            if (empInfo.IsEmployeeDiscOn && custDiscPct > 0)
                        {
                            double qtyPurchased = GetQtyPurchased(req.CustomerId, empInfo.MemberShopId, empInfo.DurationMonths);
                            double cartQtyForEmpLimit = req.Items.Sum(i => i.Quantity);
                            if (qtyPurchased < empInfo.QtyLimit && qtyPurchased + cartQtyForEmpLimit <= empInfo.QtyLimit)
                                empDiscPct = custDiscPct;
                            // -1 case (partial): disc = 0 (all-or-nothing, ResetDiscountOnQty)
                        }
                    }
                        else if (isMultipleCustDisc && empInfo.MemberTypeId > 0)
                    {
                        // Per-item disc % from tblDefMemberTypes.comments.
                        // CustomerTypeDAL.vb:1595-1656; filter: [Customer Type ID]=X AND [Line Item ID]=Y
                            multiItemDiscMap = GetMultipleItemDiscounts(empInfo.MemberTypeId);
                    }
                }
                }

                // Customer's member type ID — reuse empInfo to avoid a separate DB call.
                // Passed to GetSKUDiscountValue() so campaign discounts gated by customer
                // type are correctly applied or skipped. frmSaleAndReturn.vb:25748.
                int customerMemberTypeId = empInfo?.MemberTypeId ?? 0;

                // Customer's registered shop — loyalty DAL uses this to traverse the group
                // policy link (tblMemberInfo.shop_id → tblDefGroupPolicy → tbldefGroupPolicyDetail).
                // Falls back to POS shop when customer has no shop association.
                // frmSaleAndReturn.vb:25889: Me.txtCustomer.SelectedCustomerShopID
                int custShopId = empInfo != null && empInfo.MemberShopId > 0
                    ? empInfo.MemberShopId : shopId;

                // ── Pass 1: resolve per-item SKU discount + loyalty + coupon ──────────
                var states          = new List<ItemState>();
                double runningCouponTotal = 0;  // running sum of applied coupon disc * qty (for cap)

                // _TotalQty is used by GetSKUDiscountValue for tier-price discounts (discount_type=10).
                // DAL:1039: "From_Qty <= _TotalQty <= To_Qty" — threshold is per-product, not cart-wide.
                // TotalQuantity = sum of Qty (× ConFactor for packs) of the same product_item_id.
                // frmSaleAndReturn.vb:25720-25736
                var productQtyMap = new Dictionary<int, double>();
                foreach (var itm in req.Items)
                {
                    double effQty = itm.ConFactor > 1 ? itm.Quantity * itm.ConFactor : itm.Quantity;
                    if (!productQtyMap.ContainsKey(itm.ProductItemId))
                        productQtyMap[itm.ProductItemId] = 0;
                    productQtyMap[itm.ProductItemId] += effQty;
                }

                foreach (var item in req.Items)
                {
                    var p = LookupProduct(item.ProductItemId, shopId, now);
                    if (p == null)
                        return Request.CreateResponse((HttpStatusCode)422,
                            new { error = $"Product item {item.ProductItemId} not found" });

                    // OverrideUnitRate: cashier price-override from ItemDetailModal (Price Override tab).
                    // Mirrors frmSaleAndReturn.vb grid UnitRate column edit — all downstream
                    // calculations (VAT, discounts, net) use this rate instead of the DB price.
                    double unitRate  = item.OverrideUnitRate ?? p.Price;
                    // When card payment with shop-based VAT, Changetax() replaces VatFactor
                    // with the card tax rate (ratio = txtSaleTaxPercentOnCard).
                    // frmSaleAndReturn.vb:25201-25215; card button only visible when isShopBasedVAT.
                    double vatFactor = (isCard && taxOnCard && isShopBasedVAT) ? cardTaxPct
                                       : isShopBasedVAT ? configVatPct
                                       : p.Vat;
                    bool   vatIsPercent = isShopBasedVAT
                        || string.IsNullOrEmpty(p.VatType) || p.VatType == "0"
                        || string.Equals(p.VatType, "Percentage", StringComparison.OrdinalIgnoreCase);

                    int    discountId   = 0;
                    int    qtyOfX       = 0;
                    bool   isBuyXGetY   = false;
                    int    discType     = 0;
                    double unitDisc     = 0;
                    string discCategory = "";
                    double loyaltyCashDisc = 0;
                    double custDiscUnit    = 0;
                    double loyaltyPct      = 0;
                    bool   isCouponLine    = false;

                    // ── Gap 6: coupon takes highest priority ───────────────────────────
                    // Coupon replaces unit disc and zeroes customer disc.
                    // frmSaleAndReturn.vb:16668-16674
                    // couponLines is keyed by line_item_id (department), not product_item_id.
                    double discPer;
                    if (coupon != null
                        && couponLines != null
                        && couponLines.TryGetValue(p.LineItemId, out discPer)
                        && discPer > 0)
                    {
                        double couponDiscUnit = unitRate * discPer / 100.0;
                        double couponLineTotal = couponDiscUnit * item.Quantity;

                        if (runningCouponTotal >= coupon.DiscAmtLimit)
                        {
                            couponDiscUnit = 0;  // cap already reached on a previous line
                        }
                        else if (runningCouponTotal + couponLineTotal > coupon.DiscAmtLimit)
                        {
                            // Partial: remaining limit spread across this line's qty
                            double remaining = coupon.DiscAmtLimit - runningCouponTotal;
                            couponDiscUnit   = remaining / item.Quantity;
                            couponLineTotal  = remaining;
                        }

                        if (couponDiscUnit > 0)
                        {
                            runningCouponTotal += couponLineTotal;
                            unitDisc     = couponDiscUnit;
                            custDiscUnit = 0;
                            discCategory = "C";
                            isCouponLine = true;
                        }
                    }
                    else
                    {
                        // ── Bug A fix: "Customer" priority — zero SKU disc when customer has line discounts ──
                        // DiscountPriority="Customer" → blnBlockUnitDiscOnCustDisc=True.
                        // Candela zeroes UnitDiscount (and skips GetSKUDiscountValue) when a customer with
                        // applicable member-type line discounts is present.
                        // frmSaleAndReturn.vb:25694–25695 (UpdateItemCalculations) and 15629–15646 (customer load).
                        // Also true when Multiple_Customer_Disc=False and a flat % applies —
                        // in that case multiItemDiscMap is empty but custDiscPct covers all departments.
                        bool customerHasLineDiscounts = req.CustomerId > 0
                            && (multiItemDiscMap.Count > 0 || empDiscPct > 0
                                || (!isMultipleCustDisc && custDiscPct > 0));
                        bool skipSkuLookup = blockUnitDiscOnCustDisc && customerHasLineDiscounts;

                        // ── Normal SKU discount ────────────────────────────────────────
                        if (item.UnitDiscount > 0)
                        {
                            // Caller-supplied manual discount (equivalent to cashier typing in the grid).
                            // Not suppressed by DiscountPriority — only the system lookup is blocked.
                            string dtype = (item.DiscountType ?? "flat").ToLower();
                            unitDisc = dtype == "percent"
                                ? unitRate * (item.UnitDiscount / 100.0)
                                : item.UnitDiscount;
                        }
                        else if (!skipSkuLookup && p.NotForDiscount == 0)
                        {
                            // IsPack=true when selling in packs (PackSize>0).
                            // DAL:901-902: when discount_type="2" && IsPack, _DiscountAmount *= PackSize.
                            bool isPack = item.PackSize > 0;
                            double productTotalQty = productQtyMap.TryGetValue(item.ProductItemId, out double pq)
                                ? pq : item.Quantity;
                            unitDisc = GetSKUDiscountValue(
                                shopId, item.ProductItemId, now,
                                unitRate, item.Quantity, ref discountId,
                                req.CutPiece, isLoyaltyOn, customerMemberTypeId,
                                ref qtyOfX, ref isBuyXGetY,
                                isPack, item.PackSize, ref discType,
                                productTotalQty, false, false);
                            // Candela rounds SKU discount to pvtUnitDiscountRounding=6 places.
                            // frmSaleAndReturn.vb:25748
                            unitDisc = Math.Round(unitDisc, 6, MidpointRounding.AwayFromZero);

                            if (isBuyXGetY && discType == 8)
                                discCategory = qtyOfX > 0 ? "X" : "Y";
                            else if (isBuyXGetY && discType == 9)
                                discCategory = "N";
                            else if (isBuyXGetY && discType == 10)
                                discCategory = "M";
                            else if (discType == 11)
                                discCategory = "Q";
                        }
                        // When skipSkuLookup=true: unitDisc stays 0 and discountId stays 0 — customer disc wins.

                        // BlockProductDiscount_Under_CustomerPrice (frmSaleAndReturn.vb:25795-25807, 25847-25857)
                        // Zero discount when RetailPriceMethodology=3 AND the customer type has a specific
                        // price defined for this SKU in tblDefProductPriceCustomerBased.
                        // Applies to both auto SKU discount and cashier-entered manual overrides.
                        // Not applied to coupon lines — this block is inside the else/non-coupon branch.
                        if (blockBelowCustPrice && customerMemberTypeId > 0 && unitDisc > 0)
                        {
                            double custTypeSKUPrice = item.NestedItemId > 0
                                ? GetCustomerTypeBasedNestedSKUPrice(item.ProductItemId, item.NestedItemId, customerMemberTypeId)
                                : GetCustomerTypeBasedSKUPrice(customerMemberTypeId, item.ProductItemId);
                            if (custTypeSKUPrice > 0)
                            {
                                unitDisc     = 0;
                                discountId   = 0;
                                discCategory = "";
                            }
                        }

                        // ── Loyalty cash discount or member-type customer disc ──────────────────────────────
                        if (useLoyaltyCashDisc)
                        {
                            // Bug D fix: promotional rate flag.
                            // When a promo SKU discount is present AND neither blocking flag is set, Candela
                            // reads Cash_Dis_During_Sales / Points_Dis_During_Sales instead of the normal rate.
                            // frmSaleAndReturn.vb:25888
                            bool fetchPromoRate = unitDisc > 0 && discountId > 0
                                                  && !blockCustDiscOnUnitDisc && !blockUnitDiscOnCustDisc;

                            // Bug B + C fix: ReadCashDiscountPer for cash amount (not ReadLoyalityPointsPercentage),
                            // keyed by p.LineItemId (department), not product_item_id.
                            // Bug I fix: pass custShopId (customer's registered shop), not POS shopId.
                            // frmSaleAndReturn.vb:25889; DAL:12655 — reads Cash_Discount / Cash_Dis_During_Sales.
                            double cashDiscPct = (double)ReadCashDiscountPer(
                                req.CustomerId, custShopId, p.LineItemId, fetchPromoRate);

                            // Bug C fix: points% also uses LineItemId (department).
                            // Bug I fix: custShopId here too — same group-policy join path. DAL:12749.
                            // loyaltyPct is stored on ItemState for the earned-points formula in Pass 3.
                            loyaltyPct = (double)ReadLoyalityPointsPercentage(
                                req.CustomerId, custShopId, p.LineItemId, fetchPromoRate);

                            // Blocking: DiscountPriority="Product" AND a unit disc is active → cash disc = 0.
                            // Also: cashDiscPct=0 → no cash disc (same as Candela's dblCustomerDiscountAmt=0 check).
                            // NotForDiscount: frmSaleAndReturn.vb:25859-25863 — dblCustomUnitDiscount forced to 0.
                            // frmSaleAndReturn.vb:25933–25934
                            if (cashDiscPct > 0 && !(blockCustDiscOnUnitDisc && unitDisc > 0)
                                && p.NotForDiscount == 0)
                                loyaltyCashDisc = Math.Round((unitRate - unitDisc) * (cashDiscPct / 100.0),
                                    amountRound, MidpointRounding.AwayFromZero);
                            custDiscUnit = loyaltyCashDisc;
                        }
                        else if ((customerDiscType == "1" || customerDiscType == "3") && empDiscPct > 0
                                 && p.NotForDiscount == 0)
                        {
                            // Employee / qty-limited discount: global % applied to (Rate-UnitDisc).
                            // frmSaleAndReturn.vb:37350-37368 (CalculateDiscountOnQty)
                            if (!blockCustDiscOnUnitDisc || unitDisc == 0)
                                custDiscUnit = Math.Round((unitRate - unitDisc) * (empDiscPct / 100.0), amountRound,
                                    MidpointRounding.AwayFromZero);
                        }
                        else if (p.NotForDiscount == 0
                                 // Bug F fix: multiItemDiscMap is keyed by lineItemId (department), not productItemId.
                                 // tblDefMemberTypes.comments stores "lineItemId,pct|..." pairs. frmSaleAndReturn.vb:13432.
                                 && multiItemDiscMap.TryGetValue(p.LineItemId, out double itemDiscPct))
                        {
                            // Per-item customer discount (Multiple_Customer_Disc=True).
                            // Rounding: Candela line 13491 hardcodes 4 decimal places (not gintAmountRound).
                            // frmSaleAndReturn.vb:13425-13430, 13447/13491.
                            if (!blockCustDiscOnUnitDisc || unitDisc == 0)
                                custDiscUnit = Math.Round((unitRate - unitDisc) * (itemDiscPct / 100.0), 4,
                                    MidpointRounding.AwayFromZero);
                        }
                        else if (!isMultipleCustDisc && custDiscPct > 0 && p.NotForDiscount == 0)
                        {
                            // Flat customer type discount — Multiple_Customer_Disc=False path.
                            // GetLineItemDiscount() cross-joins ALL departments with the same discount_percentage:
                            //   SELECT Line_Item_ID, @Disc FROM tbldeflineitems  (CustomerTypeDAL.vb:1644)
                            // Every product in every department effectively receives custDiscPct.
                            // Rounding: same hardcoded 4 as the True-branch above (line 13491).
                            if (!blockCustDiscOnUnitDisc || unitDisc == 0)
                                custDiscUnit = Math.Round((unitRate - unitDisc) * (custDiscPct / 100.0), 4,
                                    MidpointRounding.AwayFromZero);
                    }
                    }

                    // OverrideUnitDiscount: cashier manual discount from ItemDetailModal (Discount tab).
                    // Replaces the auto-computed SKU/promotional unitDisc when set, but never
                    // overrides a coupon line — coupon takes absolute priority (isCouponLine=true).
                    // Mirrors manual grid-column edit behaviour: customer/loyalty discounts
                    // (custDiscUnit) are still computed on top using the already-set unitDisc.
                    if (!isCouponLine && item.OverrideUnitDiscount.HasValue)
                    {
                        unitDisc     = item.OverrideUnitDiscount.Value;
                        discCategory = "";   // clear promo category — this is a cashier override
                        discountId   = 0;
                    }

                    states.Add(new ItemState
                    {
                        Item            = item,
                        Product         = p,
                        UnitRate        = unitRate,
                        VatFactor       = vatFactor,
                        VatIsPercent    = vatIsPercent,
                        UnitDisc        = unitDisc,
                        CustDiscUnit    = custDiscUnit,
                        LoyaltyCashDisc = loyaltyCashDisc,
                        LoyaltyPct      = loyaltyPct,
                        DiscountId      = discountId,
                        DiscCategory    = discCategory,
                        IsBuyXGetY      = isBuyXGetY,
                        QtyOfX          = qtyOfX,
                        DiscType        = discType,
                        IsCouponLine    = isCouponLine
                    });
                }

                // ── Pass 2: Buy-X-Get-Y cross-item discount ───────────────────────────
                // (only runs when no coupon — coupon has already overridden unit disc)
                if (coupon == null)
                    ApplyXYDiscounts(states);

                // Fail fast if coupon was valid but none of the cart items matched it.
                if (coupon != null && !states.Any(s => s.IsCouponLine))
                    return Request.CreateResponse((HttpStatusCode)422,
                        new { error = $"Coupon '{req.CouponCode}' is not applicable to any item in the cart." });

                // ── Pre-pass: compute totals needed before the VAT pass ───────────────
                // Gap 8 requires mktDisc and absoluteTotal BEFORE computing per-line VAT.
                // absoluteTotal = sum of (unitRate - unitDisc) * qty, mirrors Candela's
                // [Total] column used in the MarketDiscValue expression (frmSaleAndReturn.vb:26606)
                double preTotalGross    = 0;
                double preTotalDiscount = 0;
                double preTotalCustDisc = 0;
                double absoluteTotal    = 0;

                foreach (var s in states)
                {
                    preTotalGross    += s.UnitRate     * s.Item.Quantity;
                    preTotalDiscount += s.UnitDisc     * s.Item.Quantity;
                    preTotalCustDisc += s.CustDiscUnit * s.Item.Quantity;
                    absoluteTotal    += (s.UnitRate - s.UnitDisc) * s.Item.Quantity;
                }

                // Marketing discount — base = absoluteTotal - customer discount.
                // frmSaleAndReturn.vb:12596/12738: txtGrossTotal = sum of grid [GrossTotal] column
                // = sum((Rate-UnitDisc)*Qty) = absoluteTotal, not sum(Rate*Qty).
                bool   isApplicableOnAll = false;
                double mktDisc = SaleAndReturnDAL.GetMarketingDiscountValue(
                    shopId, now, absoluteTotal - preTotalCustDisc, ref isApplicableOnAll);

                // Gap 1: when mkt_applicable_for="Selected", discount applies only to specific SKUs.
                // Get the matching discount_id and its eligible product_item_ids.
                // When isApplicableOnAll=True, eligibleMktProducts stays null → all items eligible.
                HashSet<int> eligibleMktProducts = null;
                double eligibleMktBase = absoluteTotal;   // denominator for proportional distribution
                if (!isApplicableOnAll && mktDisc > 0)
                {
                    int mktDiscountId = GetActiveMarketingDiscountId(shopId, now);
                    if (mktDiscountId > 0)
                    {
                        eligibleMktProducts = GetMarketingDiscountEligibleItems(mktDiscountId);
                        // Recalculate the base using only eligible items so the proportion is correct.
                        eligibleMktBase = 0;
                        foreach (var s in states)
                            if (eligibleMktProducts.Contains(s.Item.ProductItemId))
                                eligibleMktBase += (s.UnitRate - s.UnitDisc) * s.Item.Quantity;
                    }
                }

                // ── Pass 3: compute VAT, amounts, and totals ──────────────────────────
                var    lines           = new List<QuoteLineResult>();
                double grossTotal      = 0;
                double totalDiscount   = 0;
                double totalCustDisc   = 0;
                double totalVat        = 0;
                double totalAddSaleTax = 0;
                double totalCouponDisc = 0;
                double totalEarnedPoints = 0;  // SUM(Qty × loyaltyPct) — mirrors LoyaltyPointPercentageNet expression at frmSaleAndReturn.vb:2617

                foreach (var s in states)
                {
                    var    item         = s.Item;
                    var    p            = s.Product;
                    double unitRate     = s.UnitRate;
                    double vatFactor    = s.VatFactor;
                    double unitDisc     = s.UnitDisc;
                    double custDiscUnit = s.CustDiscUnit;

                    // Gap 8 / Gap 1: per-unit share of marketing discount.
                    // When isApplicableOnAll=False only eligible SKUs bear the discount;
                    // proportion denominator = eligible items' total only (eligibleMktBase).
                    // frmSaleAndReturn.vb:26606
                    bool   itemEligibleForMkt = eligibleMktProducts == null
                                                || eligibleMktProducts.Contains(item.ProductItemId);
                    double marketDiscUnit = (itemEligibleForMkt && eligibleMktBase > 0)
                        ? (unitRate - unitDisc) * mktDisc / eligibleMktBase
                        : 0;

                    // VAT base — IsSubtract* flags decide whether discounts reduce it.
                    // Only divide when isShowTagPrice=True (tag price already includes VAT that
                    // must be stripped before multiplying by the rate again).
                    // frmSaleAndReturn.vb:14529-14578, 14802-14806
                    double exVatRate = priceIncludesVAT && isShowTagPrice
                        ? unitRate / (1.0 + vatFactor / 100.0)
                        : unitRate;

                    // ESD integration: FBR mandate — VAT base must be >= RRP.
                    // frmSaleAndReturn.vb:14599-14613: iif([Rate]>[RRP],[Rate],[RRP])-[Vat]
                    // RRP is the ex-VAT reference retail price; divide if tag price includes VAT.
                    if (isESDEnabled && p.Rrp > 0)
                    {
                        double rrpBase = priceIncludesVAT && isShowTagPrice && vatFactor > 0
                            ? p.Rrp / (1.0 + vatFactor / 100.0)
                            : p.Rrp;
                        exVatRate = Math.Max(exVatRate, rrpBase);
                    }

                    // When isShowTagPrice=True, discount amounts inside the tag price must also
                    // be divided by (1+vat%) before subtracting from exVatRate.
                    // frmSaleAndReturn.vb:14532-14534, 14544-14548, 14573-14576
                    double divFactor = (priceIncludesVAT && isShowTagPrice && vatFactor > 0)
                        ? (1.0 + vatFactor / 100.0)
                        : 1.0;

                    double vatBase = exVatRate;
                    if (subtractUnitDisc) vatBase -= unitDisc      / divFactor;
                    if (subtractCustDisc) vatBase -= custDiscUnit  / divFactor;
                    if (subtractMktDisc)  vatBase -= marketDiscUnit / divFactor;  // Gap 8
                    vatBase = Math.Max(vatBase, 0);

                    // Per-unit VAT
                    // Gap 1: "Value" type → p.Vat is a fixed amount, not a percentage
                    // frmSaleAndReturn.vb:14639
                    double vatValue = 0;
                    if (!isSlabVAT && vatFactor > 0)
                    {
                        if (p.TaxAtRetailPrice)
                            vatValue = unitRate * vatFactor / 100.0;
                        else if (!s.VatIsPercent && !isShopBasedVAT)
                            vatValue = p.Vat;
                        else
                            vatValue = vatBase * vatFactor / 100.0;
                    }

                    // Gap 3: Additional Sale Tax formula 1 — per-item
                    // Column expression: ([Addtional_Vat_Percent]/100)*(([Qty]*[Rate])+[VatValue])
                    // Per unit = (pct/100) * (Rate + VatChargedPerUnit), NOT discounted price.
                    // frmSaleAndReturn.vb:14786
                    double priceAfterDisc = Math.Max(unitRate - unitDisc - custDiscUnit, 0);
                    double addSaleTax = addlSaleTaxPct > 0 && !addlTaxOnNetTotal
                        ? (unitRate + vatValue) * addlSaleTaxPct / 100.0
                        : 0;

                    // Net amount per line.
                    // When isShowTagPrice=True, VAT is embedded in the tag price — don't add it.
                    // In all other cases (PIV=False or PIV+!showTag), VAT is an add-on.
                    // Card tax flows through vatValue (vatFactor replaced above) — no separate term.
                    // frmSaleAndReturn.vb:13046, 13073-13077
                    bool vatEmbedded  = priceIncludesVAT && isShowTagPrice;
                    double addlForNet = vatEmbedded ? addSaleTax : vatValue + addSaleTax;
                    double netAmount  = (priceAfterDisc + addlForNet) * item.Quantity;

                    lines.Add(new QuoteLineResult
                    {
                        ProductItemId              = item.ProductItemId,
                        ItemName                   = p.ItemName,
                        Quantity                   = item.Quantity,
                        UnitRate                   = unitRate,
                        TaggedPrice                = unitRate,
                        UnitDiscount               = unitDisc,
                        CustomerDiscountPerUnit    = custDiscUnit,
                        LoyaltyCashDiscountPerUnit = s.LoyaltyCashDisc,
                        VatValue                   = vatValue,
                        VatFactor                  = vatFactor,
                        VatType                    = p.VatType,
                        PriceIncludeVat            = priceIncludesVAT,
                        AdditionalTax              = addSaleTax,
                        // Gap 3: echo the percent so /sales can store it in tblSalesLineItems.
                        // Formula 2 tax applies at invoice level, not per line.
                        AdditionalTaxPercent       = (!addlTaxOnNetTotal) ? addlSaleTaxPct : 0,
                        NetAmount                  = netAmount,
                        DiscCategory               = s.DiscCategory,
                        DiscountId                 = s.DiscountId,
                        MarketingDiscPerUnit       = marketDiscUnit,
                        // Gap 4: DAL uses this to reverse discounts against tag price correctly.
                        // frmSaleAndReturn.vb:14802-14806
                        DiscountFromTagPrice       = isShowTagPrice && unitDisc > 0
                    });

                    grossTotal        += unitRate      * item.Quantity;
                    totalDiscount     += unitDisc      * item.Quantity;
                    totalCustDisc     += custDiscUnit  * item.Quantity;
                    totalVat          += vatValue      * item.Quantity;
                    totalAddSaleTax   += addSaleTax    * item.Quantity;
                    // Bug E fix: earned points formula mirrors frmSaleAndReturn.vb:25993.
                    // LoyaltyPointPercentage (per unit) = Round(((Rate-UnitDisc) - LoyaltyCashDisc) * PointsPct/100, N)
                    // LoyaltyPointPercentageNet (per line) = LoyaltyPointPercentage * Qty
                    // s.LoyaltyPct = points% from ReadLoyalityPointsPercentage (DAL:12749).
                    // s.LoyaltyCashDisc = cash disc already deducted from (Rate-UnitDisc) per unit.
                    double loyaltyPointsPerUnit = s.LoyaltyPct > 0
                        ? Math.Round((unitRate - unitDisc - s.LoyaltyCashDisc) * s.LoyaltyPct / 100.0,
                              amountRound, MidpointRounding.AwayFromZero)
                        : 0;
                    totalEarnedPoints += loyaltyPointsPerUnit * item.Quantity;
                    if (s.IsCouponLine)
                        totalCouponDisc += unitDisc    * item.Quantity;
                }

                // ── Gap 7: Slab VAT ───────────────────────────────────────────────────
                // When VATType="SLABS", replace per-item VAT with invoice-level slab lookup.
                // Candela's dblGrossTotal for GetTaxBySlab = gross - unit discounts.
                // frmSaleAndReturn.vb:13084-13098
                if (isSlabVAT)
                    totalVat = new SaleAndReturnDAL().GetTaxBySlab(grossTotal - totalDiscount);

                // ── Net total ─────────────────────────────────────────────────────────
                // frmSaleAndReturn.vb:13023-13046, 13052-13082
                // PIV=True AND isShowTagPrice=True: VAT embedded in price — not added.
                // !PIV OR PIV+!isShowTagPrice: VAT is an add-on.
                //   !PIV:             adds totalVat + totalFormula1Tax  (formula1 baked via dblVatValue line 12991)
                //   PIV+!isShowTagPrice: adds totalVat only             (Candela strips formula1 at line 13070)
                double totalMktDisc = mktDisc + req.ManualMarketingDiscount;
                double netTotal = grossTotal - totalDiscount - totalCustDisc - totalMktDisc;
                if (isSlabVAT)
                    netTotal += totalVat + totalAddSaleTax;
                else if (!priceIncludesVAT)
                    netTotal += totalVat + totalAddSaleTax;
                else if (!isShowTagPrice)          // PIV=True but !showTag — VAT still added, formula1 not
                    netTotal += totalVat;

                // Gap 3: Additional Sale Tax formula 2 — on net total
                // frmSaleAndReturn.vb:13103-13115
                double addlTaxF2 = 0;
                if (addlSaleTaxPct > 0 && addlTaxOnNetTotal)
                {
                    // Candela reads txtNetTotal.Text (already rounded) as the tax base.
                    // frmSaleAndReturn.vb:13109: NetTotalwithAdditionalVaT = Val(txtNetTotal.Text)
                    double roundedNet = Math.Round(netTotal, amountRound, MidpointRounding.AwayFromZero);
                    addlTaxF2 = roundedNet * addlSaleTaxPct / 100.0;
                    netTotal  = roundedNet + addlTaxF2;
                }

                // E5: AutoRounding — CR#6898, frmSaleAndReturn.vb:13210-13385, 3464
                // Reads AutoRounding from tblRCMSConfiguration and computes the rounding delta.
                // Returned as suggested_adjustment; frontend auto-applies it as adjustment_amount.
                // /sales skips AdjustmentLimit / ShowAdjustmentReason when AutoRounding is set.
                double autoRoundNet  = Math.Round(netTotal, amountRound, MidpointRounding.AwayFromZero);
                double suggestedAdj  = 0;
                string autoRoundAlgo = rcmsCfg.TryGetValue("AutoRounding", out var ara) ? (ara ?? "").Trim() : "";
                if (!string.IsNullOrEmpty(autoRoundAlgo))
                    suggestedAdj = ComputeAutoRounding(autoRoundAlgo, autoRoundNet, amountRound);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<QuoteResult>.Ok(new QuoteResult
                    {
                        Items             = lines,
                        // Candela txtGrossTotal = SUM((Rate-UnitDisc)*Qty), not SUM(Rate*Qty).
                        // frmSaleAndReturn.vb:26634; dblGrossTotal at line 12596.
                        GrossTotal        = Math.Round(grossTotal - totalDiscount, amountRound, MidpointRounding.AwayFromZero),
                        TotalDiscount     = Math.Round(totalDiscount,           amountRound, MidpointRounding.AwayFromZero),
                        CustomerDiscount  = Math.Round(totalCustDisc,           amountRound, MidpointRounding.AwayFromZero),
                        MarketingDiscount = Math.Round(totalMktDisc,            amountRound, MidpointRounding.AwayFromZero),
                        // Formula-1 (per-item) addl tax is folded into txtVAT by Candela at line 12991.
                        // Formula-2 (on net total) is NOT folded in — reported separately.
                        // Slab VAT overrides txVAT entirely and is independent of addl tax.
                        VatAmount         = Math.Round(!isSlabVAT && !addlTaxOnNetTotal
                                                ? totalVat + totalAddSaleTax
                                                : totalVat,                     amountRound, MidpointRounding.AwayFromZero),
                        AdditionalTax     = Math.Round(addlTaxOnNetTotal ? addlTaxF2 : 0.0,
                                                                                amountRound, MidpointRounding.AwayFromZero),
                        NetTotal          = autoRoundNet,
                        IsLoyaltyOn       = isLoyaltyOn && req.CustomerId > 0,
                        CouponNo          = appliedCouponNo,
                        CouponDiscount    = Math.Round(totalCouponDisc,         amountRound, MidpointRounding.AwayFromZero),
                        EarnedPoints      = Math.Round(totalEarnedPoints,       amountRound, MidpointRounding.AwayFromZero),
                        SuggestedAdjustment = Math.Round(suggestedAdj,          amountRound, MidpointRounding.AwayFromZero)
                    }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("QuoteController.Quote error: {0}", ex);
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred.", detail = ex.Message, type = ex.GetType().Name });
            }
        }

        // ── Gap 5: Buy-X-Get-Y-Free second pass ──────────────────────────────────────
        // Reset Y-item discounts then recompute based on actual X quantities present.
        // Mirrors frmSaleAndReturn.vb:34528 ApplyXYDiscount()
        private static void ApplyXYDiscounts(List<ItemState> states)
        {
            foreach (var s in states)
                if (s.DiscCategory == "Y") s.UnitDisc = 0;

            var groups = states
                .Where(s => (s.DiscCategory == "X" || s.DiscCategory == "Y")
                            && s.DiscountId != 0)
                .GroupBy(s => s.DiscountId);

            foreach (var grp in groups)
            {
                var xItems = grp.Where(s => s.DiscCategory == "X").ToList();
                // Gap 2: NotForDiscount items excluded from Y eligibility
                // frmSaleAndReturn.vb:34549, 34589, 34631
                var yItems = grp.Where(s => s.DiscCategory == "Y"
                                            && s.Product.NotForDiscount == 0).ToList();
                if (yItems.Count == 0) continue;

                double totalXQty      = xItems.Sum(s => s.Item.Quantity);
                double reqXQty        = ReadQuantityOfX(grp.Key);
                double perOfY         = ReadPerOfY(grp.Key);

                if (reqXQty <= 0 || totalXQty < reqXQty) continue;

                double qtyForDiscount = Math.Floor(totalXQty / reqXQty);
                double counter        = 0;

                foreach (var y in yItems)
                {
                    if (counter >= qtyForDiscount) break;
                    double qty = y.Item.Quantity;

                    if (qty <= qtyForDiscount - counter)
                    {
                        y.UnitDisc = perOfY * y.UnitRate / 100.0;
                        counter   += qty;
                    }
                    else
                    {
                        double eligibleQty = qtyForDiscount - counter;
                        y.UnitDisc = (perOfY * y.UnitRate * eligibleQty) / 100.0 / qty;
                        counter   += eligibleQty;
                    }
                }
            }
        }

        // ── Coupon helpers ────────────────────────────────────────────────────────────

        private CouponInfo LookupCoupon(string couponCode, DateTime saleDate)
        {
            // frmCouponPopUp.vb:98 — validate status, date range, get CouponID and Disc_Amt_limit
            const string sql = @"
SELECT cd.CouponID, cm.Disc_Amt_limit
FROM   tblCouponDtl    cd
JOIN   tblCouponMaster cm ON cm.couponID = cd.couponID
WHERE  cd.CouponNo = @code
  AND  cd.Status   = 'Active'
  AND  @saleDate BETWEEN cm.ValidFrom AND cm.ValidTo";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@code",     couponCode);
                cmd.Parameters.AddWithValue("@saleDate", saleDate.Date);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new CouponInfo
                    {
                        CouponId     = reader.GetInt32(0),
                        DiscAmtLimit = reader.IsDBNull(1) ? double.MaxValue : Safe(reader[1])
                    };
                }
            }
        }

        private Dictionary<int, double> GetCouponLineItems(int couponId)
        {
            // frmSaleAndReturn.vb:16601 — LineItemID = product_item_id
            const string sql =
                "SELECT LineItemID, Disc_per FROM tblCouponlineitemDtl WHERE coupnID = @id";

            var result = new Dictionary<int, double>();
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@id", couponId);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        result[Convert.ToInt32(reader[0])] =
                            reader.IsDBNull(1) ? 0 : Safe(reader[1]);
            }
            return result;
        }

        // ── Product / config helpers ──────────────────────────────────────────────────

        // Mirrors SaleAndReturnDAL.GetSKURate (DAL:685-735):
        //   1. Shop-based price: tblDefProductPriceShopBased (when configured per-shop)
        //   2. Standard price: MAX(product_price_id) WHERE start_date <= saleDate;
        //      falls back to MAX WHERE end_date IS NULL when no dated record exists.
        //      Applies FloatingPrice * Exchange_Rate for currency-linked products (DAL:702-714).
        private ProductInfo LookupProduct(int productItemId, int shopId, DateTime saleDate)
        {
            // Step 1: shop-based price override (DAL:716-718)
            double shopPrice = GetShopBasedPrice(productItemId, shopId);

            // Step 2: standard price + product attributes
            string dateStr = saleDate.ToString("dd-MMM-yyyy");
            const string sql = @"
SELECT TOP 1
    pd.item_name,
    CASE WHEN ISNULL(pd.FloatingPrice, 0) = 1
         THEN pp.product_price * ISNULL(fp.Exchange_Rate, 1)
         ELSE pp.product_price
    END                                  AS price,
    ISNULL(pp.RRP, 0)                    AS rrp,
    ISNULL(pd.vat, 0)                    AS vat,
    ISNULL(pd.vat_type, '')              AS vat_type,
    ISNULL(pd.Tax_At_Retail_Price, 0)    AS tax_at_retail_price,
    ISNULL(pd.NotForDiscount, 0)         AS not_for_discount,
    ISNULL(pd.line_item_id, 0)           AS line_item_id
FROM tblProductItem pi
INNER JOIN tblDefProducts pd ON pd.product_id = pi.product_id
INNER JOIN tblDefProductPrice pp ON pp.product_item_id = pi.Product_Item_ID
LEFT OUTER JOIN (SELECT Exchange_Rate, currency_id FROM tblCurrencyRates WHERE End_date IS NULL) fp
             ON pd.CurrencyID = fp.Currency_ID
WHERE pi.Product_Item_ID = @id
  AND pp.product_price_id =
      CASE WHEN (SELECT ISNULL(MAX(product_price_id), 0)
                 FROM tblDefProductPrice
                 WHERE CONVERT(datetime, CONVERT(varchar, start_date, 107), 107)
                       <= CONVERT(datetime, @dateStr, 107)
                   AND product_item_id = @id) = 0
           THEN (SELECT MAX(product_price_id)
                 FROM tblDefProductPrice
                 WHERE end_date IS NULL AND product_item_id = @id)
           ELSE (SELECT MAX(product_price_id)
                 FROM tblDefProductPrice
                 WHERE CONVERT(datetime, CONVERT(varchar, start_date, 107), 107)
                       <= CONVERT(datetime, @dateStr, 107)
                   AND product_item_id = @id)
      END
ORDER BY pp.product_price_id DESC";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@id",      productItemId);
                cmd.Parameters.AddWithValue("@dateStr", dateStr);
                using (var dt = new DataTable())
                {
                    new SqlDataAdapter(cmd).Fill(dt);
                    if (dt.Rows.Count == 0) return null;
                    var r = dt.Rows[0];
                    return new ProductInfo
                    {
                        ItemName         = r["item_name"].ToString(),
                        // Shop-based price wins when configured; otherwise standard/floating price
                        Price            = shopPrice > 0 ? shopPrice : Safe(r["price"]),
                        Rrp              = Safe(r["rrp"]),
                        Vat              = Safe(r["vat"]),
                        VatType          = r["vat_type"].ToString(),
                        TaxAtRetailPrice = Safe(r["tax_at_retail_price"]) == 1,
                        NotForDiscount   = (int)Safe(r["not_for_discount"]),
                        LineItemId       = (int)Safe(r["line_item_id"])
                    };
                }
            }
        }

        // Returns per-shop price override when tblDefProductPriceShopBased has an entry.
        // Mirrors DAL:716-718. Returns 0 when no shop-based price is configured.
        private double GetShopBasedPrice(int productItemId, int shopId)
        {
            const string sql =
                "SELECT TOP 1 Price FROM tblDefProductPriceShopBased " +
                "WHERE item_id = @id AND shop_id = @shopId";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@id",     productItemId);
                cmd.Parameters.AddWithValue("@shopId", shopId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Safe(result) : 0;
            }
        }

        private Dictionary<string, string> LoadConfig(int shopId)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(
                    "SELECT config_name, config_value FROM tblShopConfiguration WHERE shop_id = @shopId", con);
                cmd.Parameters.AddWithValue("@shopId", shopId);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        d[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
            }
            return d;
        }

        // CustomerDAL.vb:4246-4270 — tblDefProductPriceCustomerBased stores customer-type-based
        // prices. Returns the price if the type has a specific override for this SKU; 0 otherwise.
        // Nested-item variant — queries tblDefNestedProductPriceCustomerBased.
        // Mirrors CustomerDAL.GetCustomerTypeBasedNestedSKUPrice (frmSaleAndReturn.vb:25802).
        private double GetCustomerTypeBasedNestedSKUPrice(int productItemId, int nestedItemId, int customerTypeId)
        {
            if (customerTypeId <= 0) return 0;
            const string sql = "SELECT TOP(1) Price FROM tblDefNestedProductPriceCustomerBased " +
                               "WHERE item_id = @itemId AND Nested_ID = @nestedId AND customer_id = @typeId";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@itemId",   productItemId);
                cmd.Parameters.AddWithValue("@nestedId", nestedItemId);
                cmd.Parameters.AddWithValue("@typeId",   customerTypeId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToDouble(result) : 0;
            }
        }

        // Used by BlockProductDiscount_Under_CustomerPrice to decide whether to zero the discount.
        private double GetCustomerTypeBasedSKUPrice(int customerTypeId, int productItemId)
        {
            if (customerTypeId <= 0) return 0;
            const string sql = "SELECT Price FROM tblDefProductPriceCustomerBased " +
                               "WHERE customer_id = @typeId AND Item_id = @skuId";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@typeId", customerTypeId);
                cmd.Parameters.AddWithValue("@skuId",  productItemId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToDouble(result) : 0;
            }
        }

        // Returns the customer's member_type_id from tblMemberInfo.
        // Passed to GetSKUDiscountValue() as CustomerTypeId — mirrors Candela line 25748:
        // txtCustomer.CustomerTypeID is the member type, not the customer ID.
        private int GetCustomerMemberTypeId(int customerId)
        {
            if (customerId <= 0) return 0;
            const string sql = "SELECT ISNULL(member_type_id, 0) FROM tblMemberInfo WHERE member_id = @id";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@id", customerId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
        }

        private double GetCustomerDiscountPct(int customerId)
        {
            if (customerId <= 0) return 0;
            const string sql = @"
SELECT isnull(mt.discount_percentage, 0)
FROM tblMemberInfo m
JOIN tblDefMemberTypes mt ON mt.member_type_id = m.member_type_id
WHERE m.member_id = @customerId";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@customerId", customerId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Safe(result) : 0;
            }
        }

        private static double Safe(object val)
        {
            if (val == null || val == DBNull.Value) return 0;
            return double.TryParse(val.ToString(), out double d) ? d : 0;
        }

        private static bool Eq(Dictionary<string, string> cfg, string key, string val)
            => cfg.TryGetValue(key, out var v)
               && string.Equals(v, val, StringComparison.OrdinalIgnoreCase);

        private static double Dbl(Dictionary<string, string> cfg, string key)
            => cfg.TryGetValue(key, out var v) && double.TryParse(v, out double d) ? d : 0;

        // E5: AutoRounding algorithms — CR#6898, frmSaleAndReturn.vb:13218-13385
        // Input: netTotal already rounded to amountRound decimal places.
        // Returns delta (roundedValue - netTotal); frontend adds this as adjustment_amount.
        private static double ComputeAutoRounding(string algo, double netTotal, int amountRound)
        {
            switch (algo.ToUpperInvariant())
            {
                case "NEAREST ZERO/FIVE":
                {
                    // vb:13218-13278: only fires when the last decimal digit is non-zero.
                    // multiplyFactor = "2" + (amountRound-1) × "0"s  (e.g., amountRound=2 → 20).
                    // Steps: absNet × factor → if fractional: + 0.5 → Truncate → ÷ factor → diff.
                    if (amountRound <= 0) return 0;
                    double absNet = Math.Abs(netTotal);
                    // Check whether last decimal digit is zero (skip if so — vb:13228)
                    long scaled = (long)Math.Round(absNet * Math.Pow(10, amountRound), 0, MidpointRounding.AwayFromZero);
                    if (scaled % 10 == 0) return 0;
                    // factor = "2" padded to amountRound chars: 2, 20, 200, ...
                    int factor = (int)(2 * Math.Pow(10, amountRound - 1));
                    double product = absNet * factor;
                    // Only continue when multiplication still has a fractional part (vb:13247)
                    if (Math.Abs(product - Math.Round(product, 0, MidpointRounding.AwayFromZero)) < 1e-9)
                        return 0;
                    double netRounded = Math.Truncate(product + 0.5) / factor;
                    double diff = netRounded - absNet;
                    // vb:13266-13268: negate diff for return (negative netTotal)
                    return netTotal < 0 ? -diff : diff;
                }

                case "NEAREST 25 CENTS":
                {
                    // vb:13281-13346: slab-based rounding to nearest 0.25
                    double absNet   = Math.Abs(netTotal);
                    double whole    = Math.Truncate(absNet);
                    double frac     = Math.Round(absNet - whole, 2, MidpointRounding.AwayFromZero);
                    double fracRnd;
                    if      (frac <= 0.12) fracRnd = 0.00;
                    else if (frac <= 0.37) fracRnd = 0.25;
                    else if (frac <= 0.62) fracRnd = 0.50;
                    else if (frac <= 0.87) fracRnd = 0.75;
                    else                   fracRnd = 1.00;
                    double diff = (whole + fracRnd) - absNet;
                    // vb:13327-13328: negate diff for return (negative netTotal)
                    return netTotal < 0 ? -diff : diff;
                }

                case "ROUND TO ZERO":
                    // vb:13361-13368: simple integer rounding (AwayFromZero)
                    return Math.Round(Math.Abs(netTotal), 0, MidpointRounding.AwayFromZero)
                           * Math.Sign(netTotal == 0 ? 1 : netTotal)
                           - netTotal;

                case "NEAREST TEN":
                    // vb:13369-13378: round to nearest 10
                    return Math.Round(Math.Abs(netTotal) / 10.0, 0, MidpointRounding.AwayFromZero)
                           * 10.0
                           * Math.Sign(netTotal == 0 ? 1 : netTotal)
                           - netTotal;

                default:
                    return 0;
            }
        }

        // ── Inner types ───────────────────────────────────────────────────────────────

        private class CouponInfo
        {
            public int    CouponId     { get; set; }
            public double DiscAmtLimit { get; set; }
        }

        private class ProductInfo
        {
            public string ItemName         { get; set; }
            public double Price            { get; set; }
            public double Rrp              { get; set; }
            public double Vat              { get; set; }
            public string VatType          { get; set; }
            public bool   TaxAtRetailPrice { get; set; }
            public int    NotForDiscount   { get; set; }
            public int    LineItemId       { get; set; }
        }

        private class ItemState
        {
            public QuoteItem    Item            { get; set; }
            public ProductInfo  Product         { get; set; }
            public double       UnitRate        { get; set; }
            public double       VatFactor       { get; set; }
            public bool         VatIsPercent    { get; set; }
            public double       UnitDisc        { get; set; }
            public double       CustDiscUnit    { get; set; }
            public double       LoyaltyCashDisc { get; set; }
            public double       LoyaltyPct      { get; set; }  // raw % from ReadLoyalityPointsPercentage — used for earned-points calc
            public int          DiscountId      { get; set; }
            public string       DiscCategory    { get; set; }
            public bool         IsBuyXGetY      { get; set; }
            public int          QtyOfX          { get; set; }
            public int          DiscType        { get; set; }
            public bool         IsCouponLine    { get; set; }
        }

        // Replaces SaleAndReturnDAL.GetSKUDiscountValue — identical SQL and logic.
        // IsReplicatedShop (DAL:14105) eliminated: POS shops are never replicated so the
        // non-replicated shop-filter path always applies. All readers use using blocks.
        // SaleAndReturnDAL.vb:803
        private static double GetSKUDiscountValue(
            int _shopID, int _ProductItemID, DateTime _SaleDateTime,
            double _dblItemPrice, double _dblQty, ref int _DiscountID,
            bool _ApplyCutPieceDiscount, bool IsLoyalityClub, int CustomerTypeId,
            ref int QtyofX, ref bool IsBuyXGetYFreeDisc,
            bool IsPack, double PackSize, ref int DiscountType,
            double _TotalQty, bool IsNonPaymentTill, bool LoyalityClub_ForNPTill)
        {
            // Cut piece path has no DB calls in the VB.NET version — delegate directly, no leak.
            // SaleAndReturnDAL.vb:809-820
            if (_ApplyCutPieceDiscount)
                return SaleAndReturnDAL.GetSKUDiscountValue(
                    _shopID, _ProductItemID, _SaleDateTime, _dblItemPrice, _dblQty,
                    ref _DiscountID, true, IsLoyalityClub, CustomerTypeId,
                    ref QtyofX, ref IsBuyXGetYFreeDisc, IsPack, PackSize, ref DiscountType,
                    _TotalQty, IsNonPaymentTill, LoyalityClub_ForNPTill);

            double _DiscountAmount = 0;

            // CR#6574: Build shop filter WhereClause — always apply (non-replicated path).
            // SaleAndReturnDAL.vb:827-850
            string strDiscountIds = "";
            string strSQL = "select discount_id from tbldefdiscountshops where shop_id=" + _shopID;
            using (var _con0 = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                _con0.Open();
                using (var _cmd0 = new SqlCommand(strSQL, _con0))
                using (var objDiscounts = _cmd0.ExecuteReader())
                {
                    while (objDiscounts.Read())
                        strDiscountIds += objDiscounts["discount_id"] + ",";
                }
            }
            if (strDiscountIds.Length > 0)
                strDiscountIds = strDiscountIds.Substring(0, strDiscountIds.Length - 1);

            string WhereClause = strDiscountIds != ""
                ? " and (discount_id in (" + strDiscountIds + ") OR which_shops='ALL')"
                : " and  which_shops='ALL'";

            // CR#5959 main discount query — SaleAndReturnDAL.vb:855-860
            strSQL = "SELECT discount_id, discount_name, charged_to [Charged To], discount_type, discount, which_products [Which Products],which_shops,discount_duration, isnull(Membership_Type,0) as Membership_Type, isnull(which_Customer_Type,'Selected') as which_Customer_Type, Is_Campaign " +
                " FROM tblDefDiscounts" +
                " WHERE  (convert(datetime,'" + _SaleDateTime.ToString("dd-MMM-yyyy") + "',107)   BETWEEN Convert(datetime,discount_start_date,107) AND  Convert(datetime,discount_end_date,107))     " +
                " and ( Convert(varchar,Convert(datetime,'" + _SaleDateTime.ToString("hh:m:ss tt") + "'),108)  BETWEEN CONVERT(varchar, discount_start_time, 108) AND CONVERT(varchar, discount_end_time, 108)) " +
                " and Days_of_Weeks Like '%" + _SaleDateTime.ToString("dddd") + "%'  And charged_to = '1' " +
                WhereClause;

            bool blnIsProductFound = false;
            bool blnIsShopFound    = false;

            using (var mainCon = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                mainCon.Open();
                using (var mainCmd = new SqlCommand(strSQL, mainCon))
                using (var objDiscountDR = mainCmd.ExecuteReader())
                {
                    if (objDiscountDR.HasRows)
                    {
                        while (objDiscountDR.Read())
                        {
                            if (blnIsProductFound && blnIsShopFound) break; // GoTo lblReturn

                            if (objDiscountDR["Charged To"].ToString() == "1")
                            {
                                if (objDiscountDR["Which Products"].ToString() == "All")
                                {
                                    _DiscountID     = Convert.ToInt32(objDiscountDR["discount_id"]);
                                    _DiscountAmount = CalculateDiscountValue(
                                        objDiscountDR["discount_type"].ToString(),
                                        Safe(objDiscountDR["discount"]),
                                        _dblItemPrice, _dblQty,
                                        Safe(objDiscountDR["discount_duration"]));
                                    blnIsProductFound = true;

                                    if (objDiscountDR["discount_type"].ToString() == "2" && IsPack && PackSize != 0)
                                        _DiscountAmount *= PackSize;
                                    if (objDiscountDR["discount_type"].ToString() == "3")
                                        DiscountType = 3;
                                }
                                else if (objDiscountDR["Which Products"].ToString() == "Selected" &&
                                         Convert.ToInt32(objDiscountDR["discount_type"]) != 10)
                                {
                                    // Issue#3073 — isnull(b.discount,0); SaleAndReturnDAL.vb:920-925
                                    strSQL = "SELECT b.product_item_id, b.discount_type, isnull(b.discount,0) as discount,b.discCategory   " +
                                        " FROM tblDefDiscounts a, tblDefDiscountProducts b " +
                                        " WHERE (convert(datetime,'" + _SaleDateTime.ToString("dd-MMM-yyyy") + "',107)    BETWEEN  convert(datetime, a.discount_start_date, 107) AND  convert(datetime, a.discount_end_date,107))  " +
                                        " and (b.discount_id = " + objDiscountDR["discount_id"] + ")" +
                                        " and b.product_item_id ='" + _ProductItemID + "'" +
                                        " and a.discount_id = b.discount_id ";

                                    using (var con2 = new SqlConnection(CandelaBootstrap.ConnectionString))
                                    {
                                        con2.Open();
                                        using (var cmd2 = new SqlCommand(strSQL, con2))
                                        using (var objSelectedDiscountDR = cmd2.ExecuteReader())
                                        {
                                            if (objSelectedDiscountDR.HasRows)
                                            {
                                                objSelectedDiscountDR.Read();

                                                if (!objSelectedDiscountDR.IsDBNull(objSelectedDiscountDR.GetOrdinal("DiscCategory")))
                                                {
                                                    string discCat = objSelectedDiscountDR["DiscCategory"].ToString();

                                                    if (discCat == "X")
                                                    {
                                                        DiscountType       = 8;
                                                        _DiscountID        = Convert.ToInt32(objDiscountDR["discount_id"]);
                                                        QtyofX             = (int)Convert.ToInt64(objDiscountDR["discount_duration"]);
                                                        IsBuyXGetYFreeDisc = true;
                                                    }
                                                    else if (discCat == "N")
                                                    {
                                                        DiscountType       = 9;
                                                        _DiscountID        = Convert.ToInt32(objDiscountDR["discount_id"]);
                                                        QtyofX             = (int)Convert.ToInt64(objDiscountDR["discount_duration"]);
                                                        IsBuyXGetYFreeDisc = true;
                                                    }
                                                    else if (discCat == "Y")
                                                    {
                                                        DiscountType       = 8;
                                                        _DiscountID        = Convert.ToInt32(objDiscountDR["discount_id"]);
                                                        _DiscountAmount    = Safe(objSelectedDiscountDR["discount"]);
                                                        IsBuyXGetYFreeDisc = true;
                                                        QtyofX             = 0;
                                                    }
                                                    else if (discCat == "Q")
                                                    {
                                                        DiscountType = 11;
                                                        _DiscountID  = Convert.ToInt32(objDiscountDR["discount_id"]);
                                                    }

                                                    // CR#6574 — SaleAndReturnDAL.vb:961-991
                                                    if (discCat == "X" || discCat == "N" || discCat == "Y")
                                                    {
                                                        if (objDiscountDR["which_shops"].ToString() == "Selected")
                                                        {
                                                            strSQL = "SELECT a.discount_type, a.discount" +
                                                                "  FROM tblDefDiscounts a, tblDefDiscountShops  b" +
                                                                " WHERE  convert(datetime,'" + _SaleDateTime.ToString("dd-MMM-yyyy") + "',107)   BETWEEN CONVERT(datetime, a.discount_start_date, 107) AND CONVERT(datetime, a.discount_end_date, 107) " +
                                                                " and (b.discount_id = " + objDiscountDR["discount_id"] + ")" +
                                                                "AND shop_id = '" + _shopID + "'" +
                                                                "AND a.discount_id = b.discount_id";

                                                            using (var con3 = new SqlConnection(CandelaBootstrap.ConnectionString))
                                                            {
                                                                con3.Open();
                                                                using (var cmd3 = new SqlCommand(strSQL, con3))
                                                                using (var objSelectedShopDR = cmd3.ExecuteReader())
                                                                {
                                                                    if (!objSelectedShopDR.HasRows)
                                                                    {
                                                                        _DiscountID        = 0;
                                                                        _DiscountAmount    = 0;
                                                                        blnIsShopFound     = false;
                                                                        _DiscountID        = 0;
                                                                        QtyofX             = 0;
                                                                        IsBuyXGetYFreeDisc = false;
                                                                    }
                                                                    else
                                                                    {
                                                                        blnIsShopFound = true;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            blnIsShopFound = true;
                                                        }

                                                        return _DiscountAmount; // Exit Function
                                                    }
                                                }

                                                _DiscountID = Convert.ToInt32(objDiscountDR["discount_id"]);
                                                if (objSelectedDiscountDR["discount_type"].ToString() != "1")
                                                {
                                                    // Issue#1558 / 6481 / 6596 — SaleAndReturnDAL.vb:997-1013
                                                    if (Convert.ToInt32(objSelectedDiscountDR["discount_type"]) == 3)
                                                    {
                                                        _DiscountAmount = CalculateDiscountValue(
                                                            objSelectedDiscountDR["discount_type"].ToString(),
                                                            Safe(objSelectedDiscountDR["discount"]),
                                                            _dblItemPrice, _dblQty,
                                                            Safe(objDiscountDR["discount_duration"]));
                                                        DiscountType = 3;
                                                    }
                                                    else
                                                    {
                                                        _DiscountAmount = CalculateDiscountValue(
                                                            objSelectedDiscountDR["discount_type"].ToString(),
                                                            Safe(objDiscountDR["discount"]),
                                                            _dblItemPrice, _dblQty,
                                                            Safe(objSelectedDiscountDR["discount"]));
                                                    }
                                                }
                                                else
                                                {
                                                    _DiscountAmount = _dblItemPrice - Safe(objSelectedDiscountDR["discount"]);
                                                }
                                                blnIsProductFound = true;

                                                // CR#5755 — SaleAndReturnDAL.vb:1021-1024
                                                if (objSelectedDiscountDR["discount_type"].ToString() == "2" && IsPack && PackSize != 0)
                                                    _DiscountAmount *= PackSize;
                                            }
                                            else
                                            {
                                                _DiscountID       = 0;
                                                _DiscountAmount   = 0;
                                                blnIsProductFound = false;
                                            }
                                        }
                                    }
                                }
                                else if (objDiscountDR["Which Products"].ToString() == "Selected" &&
                                         Convert.ToInt32(objDiscountDR["discount_type"]) == 10)
                                {
                                    // Issue#1558 tier pricing — SaleAndReturnDAL.vb:1033-1060
                                    strSQL = "SELECT b.product_item_id, a.discount_type, b.Price as [discount],a.discount_category  [DiscCategory] " +
                                        " FROM tblDefDiscounts a, tblDefDiscountProductsTier b " +
                                        " WHERE (convert(datetime,'" + _SaleDateTime.ToString("dd-MMM-yyyy") + "',107)    BETWEEN  convert(datetime, a.discount_start_date, 107) AND  convert(datetime, a.discount_end_date,107))  " +
                                        " and (b.Discount_ID = " + objDiscountDR["discount_id"] + ")" +
                                        " and b.product_item_id ='" + _ProductItemID + "'" +
                                        " and " + Math.Floor(_TotalQty) + " >= From_Qty and " + Math.Floor(_TotalQty) + " <= To_Qty " +
                                        " and a.discount_id = b.discount_id ";

                                    using (var con2 = new SqlConnection(CandelaBootstrap.ConnectionString))
                                    {
                                        con2.Open();
                                        using (var cmd2 = new SqlCommand(strSQL, con2))
                                        using (var objSelectedDiscountDR = cmd2.ExecuteReader())
                                        {
                                            if (objSelectedDiscountDR.HasRows)
                                            {
                                                objSelectedDiscountDR.Read();
                                                // Tier price is a replacement price, not a flat discount.
                                                // Discount = original price − tier price. SaleAndReturnDAL.vb:1033-1060.
                                                double tierPrice = Safe(objSelectedDiscountDR["discount"]);
                                                if (tierPrice > 0)
                                                    _DiscountAmount = _dblItemPrice - tierPrice;
                                                DiscountType       = 10;
                                                _DiscountID        = Convert.ToInt32(objDiscountDR["discount_id"]);
                                                IsBuyXGetYFreeDisc = true;
                                                blnIsProductFound  = true;
                                            }
                                            else
                                            {
                                                _DiscountID       = 0;
                                                _DiscountAmount   = 0;
                                                blnIsProductFound = false;
                                            }
                                        }
                                    }
                                }
                            }

                            // Shop check — SaleAndReturnDAL.vb:1066-1089
                            if (objDiscountDR["which_shops"].ToString() == "Selected")
                            {
                                strSQL = "SELECT a.discount_type, a.discount" +
                                    "  FROM tblDefDiscounts a, tblDefDiscountShops  b" +
                                    " WHERE  convert(datetime,'" + _SaleDateTime.ToString("dd-MMM-yyyy") + "',107)   BETWEEN CONVERT(datetime, a.discount_start_date, 107) AND CONVERT(datetime, a.discount_end_date, 107) " +
                                    " and (b.discount_id = " + objDiscountDR["discount_id"] + ")" +
                                    "AND shop_id = '" + _shopID + "'" +
                                    "AND a.discount_id = b.discount_id";

                                using (var con2 = new SqlConnection(CandelaBootstrap.ConnectionString))
                                {
                                    con2.Open();
                                    using (var cmd2 = new SqlCommand(strSQL, con2))
                                    using (var objSelectedShopDR = cmd2.ExecuteReader())
                                    {
                                        if (!objSelectedShopDR.HasRows)
                                        {
                                            _DiscountID     = 0;
                                            _DiscountAmount = 0;
                                            blnIsShopFound  = false;
                                        }
                                        else
                                        {
                                            blnIsShopFound = true;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                blnIsShopFound = true;
                            }

                            // Loyalty / campaign customer type check — SaleAndReturnDAL.vb:1103-1166
                            if (IsLoyalityClub)
                            {
                                if (objDiscountDR["Is_Campaign"].ToString().ToUpper() == "TRUE")
                                {
                                    if (objDiscountDR["which_Customer_Type"].ToString() == "Selected")
                                    {
                                        strSQL = "SELECT a.discount_type, a.discount" +
                                            "  FROM tblDefDiscounts a, tblDefDiscountCustomerType  b" +
                                            " WHERE  convert(datetime,'" + _SaleDateTime.ToString("dd-MMM-yyyy") + "',107)   BETWEEN CONVERT(datetime, a.discount_start_date, 107) AND CONVERT(datetime, a.discount_end_date, 107) " +
                                            " and (b.discount_id = " + objDiscountDR["discount_id"] + ")" +
                                            "AND Customer_Type_ID = '" + CustomerTypeId + "'" +
                                            "AND a.discount_id = b.discount_id";

                                        using (var con2 = new SqlConnection(CandelaBootstrap.ConnectionString))
                                        {
                                            con2.Open();
                                            using (var cmd2 = new SqlCommand(strSQL, con2))
                                            using (var objSelectedCustomerDR = cmd2.ExecuteReader())
                                            {
                                                if (!objSelectedCustomerDR.HasRows)
                                                {
                                                    _DiscountID     = 0;
                                                    _DiscountAmount = 0;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else if (IsNonPaymentTill && LoyalityClub_ForNPTill)
                            {
                                // Issue#4920 — SaleAndReturnDAL.vb:1138-1163
                                if (objDiscountDR["which_Customer_Type"].ToString() == "Selected")
                                {
                                    strSQL = "SELECT a.discount_type, a.discount" +
                                        "  FROM tblDefDiscounts a, tblDefDiscountCustomerType  b" +
                                        " WHERE  convert(datetime,'" + _SaleDateTime.ToString("dd-MMM-yyyy") + "',107)   BETWEEN CONVERT(datetime, a.discount_start_date, 107) AND CONVERT(datetime, a.discount_end_date, 107) " +
                                        " and (b.discount_id = " + objDiscountDR["discount_id"] + ")" +
                                        "AND Customer_Type_ID = '" + CustomerTypeId + "'" +
                                        "AND a.discount_id = b.discount_id";

                                    using (var con2 = new SqlConnection(CandelaBootstrap.ConnectionString))
                                    {
                                        con2.Open();
                                        using (var cmd2 = new SqlCommand(strSQL, con2))
                                        using (var objSelectedCustomerDR = cmd2.ExecuteReader())
                                        {
                                            if (!objSelectedCustomerDR.HasRows)
                                            {
                                                _DiscountID     = 0;
                                                _DiscountAmount = 0;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return _DiscountAmount; // lblReturn:
                }
            }
        }

        // SaleAndReturnDAL.vb:1577 — Private Shared, cannot be called from C#; inlined here.
        // Exact same 7-type formula logic, translated directly.
        private static double CalculateDiscountValue(string DiscountType, double DiscountFigure, double ProductPrice, double Qty, double DiscountUnit)
        {
            if (DiscountType == "1") return ProductPrice - DiscountFigure;
            if (DiscountType == "2") return DiscountUnit;
            if (DiscountType == "3") return ProductPrice * (DiscountFigure / 100.0);
            if (DiscountType == "4" && Qty >= DiscountFigure) return ProductPrice * (DiscountUnit / 100.0);
            if (DiscountType == "5" && ProductPrice > DiscountUnit) return ProductPrice * (DiscountFigure / 100.0);
            if (DiscountType == "6")
            {
                if (Math.Floor(Qty / DiscountFigure + 1) > 0)
                    return Math.Round((ProductPrice * Math.Floor(Qty / (DiscountUnit + 1))) / Qty, 6);
                return 0;
            }
            if (DiscountType == "7" && Qty >= DiscountFigure) return DiscountUnit;
            return 0;
        }

        // Replaces SaleAndReturnDAL.ReadQuantityOfX — identical SQL, reader properly closed.
        // SaleAndReturnDAL.vb:12829
        private static double ReadQuantityOfX(int intDiscountId)
        {
            double DblQuantityofX = 0.0;
            string strSQL = "SELECT discount_duration FROM tblDefDiscounts WHERE  Discount_id=" + intDiscountId;
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                using (var cmd = new SqlCommand(strSQL, con))
                using (var dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                        if (!dr.IsDBNull(dr.GetOrdinal("discount_duration")))
                            DblQuantityofX = Convert.ToDouble(dr["discount_duration"]);
                }
            }
            return DblQuantityofX;
        }

        // Replaces SaleAndReturnDAL.ReadPerOfY — identical SQL, reader properly closed.
        // SaleAndReturnDAL.vb:12857
        private static double ReadPerOfY(int intDiscountId)
        {
            double DblPerOfY = 0.0;
            string strSQL = "select top 1 discount as DiscPer from tbldefdiscountproducts where discount_id=" + intDiscountId + " and discCategory='Y'";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                using (var cmd = new SqlCommand(strSQL, con))
                using (var dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                        if (!dr.IsDBNull(dr.GetOrdinal("DiscPer")))
                            DblPerOfY = Convert.ToDouble(dr["DiscPer"]);
                }
            }
            return DblPerOfY;
        }

        // Replaces SaleAndReturnDAL.ReadCashDiscountPer — identical SQL and logic, reader properly closed.
        // SaleAndReturnDAL.vb:12655
        private decimal ReadCashDiscountPer(int customerId, int shopId, int lineItemId, bool fetchPromo)
        {
            if (fetchPromo)
            {
                decimal promo = RunGroupPolicyQuery("Cash_Dis_During_Sales", customerId, shopId, lineItemId);
                if (promo > 0) return promo;
            }
            return RunGroupPolicyQuery("Cash_Discount", customerId, shopId, lineItemId);
        }

        // Replaces SaleAndReturnDAL.ReadLoyalityPointsPercentage — identical SQL and logic, reader properly closed.
        // SaleAndReturnDAL.vb:12749
        private decimal ReadLoyalityPointsPercentage(int customerId, int shopId, int lineItemId, bool fetchPromo)
        {
            if (fetchPromo)
            {
                decimal promo = RunGroupPolicyQuery("Points_Dis_During_Sales", customerId, shopId, lineItemId);
                if (promo > 0) return promo;
            }
            return RunGroupPolicyQuery("Points", customerId, shopId, lineItemId);
        }

        // Shared runner for the 4-table group-policy JOIN used by ReadCashDiscountPer and
        // ReadLoyalityPointsPercentage. colName is a hardcoded constant at every call site.
        private decimal RunGroupPolicyQuery(string colName, int customerId, int shopId, int lineItemId)
        {
            string sql = $@"
SELECT ISNULL(tbldefGroupPolicyDetail.{colName}, 0)
FROM   tblMemberInfo
INNER JOIN tblDefMemberTypes       ON tblMemberInfo.member_type_id       = tblDefMemberTypes.member_type_id
INNER JOIN tblDefGroupPolicy       ON tblDefMemberTypes.member_type_id   = tblDefGroupPolicy.member_type_id
INNER JOIN tbldefGroupPolicyDetail ON tblDefGroupPolicy.Group_policyID   = tbldefGroupPolicyDetail.Group_policyID
WHERE tblMemberInfo.member_id               = @customerId
  AND tblMemberInfo.shop_id                 = @shopId
  AND tbldefGroupPolicyDetail.Line_Item_ID  = @lineItemId";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@customerId", customerId);
                cmd.Parameters.AddWithValue("@shopId",     shopId);
                cmd.Parameters.AddWithValue("@lineItemId", lineItemId);
                using (var dr = cmd.ExecuteReader())
                    return dr.Read() ? Convert.ToDecimal(dr[0]) : 0m;
            }
        }

        private class CustomerTypeDetails
        {
            public bool   IsEmployeeDiscOn { get; set; }
            public double QtyLimit         { get; set; }
            public int    DurationMonths   { get; set; }
            public int    MemberTypeId     { get; set; }
            public int    MemberShopId     { get; set; }
        }

        // Returns employee/qty-limit fields for the customer's member type.
        // frmSaleAndReturn.vb:14310-14335 (drCustomerType / drQtyPurchase loading)
        private CustomerTypeDetails GetCustomerTypeDetails(int customerId)
        {
            if (customerId <= 0) return null;
            const string sql = @"
SELECT isnull(mt.IsEmployeeDiscOn, 0)  AS is_emp_disc,
       isnull(mt.QtyLimit, 0)          AS qty_limit,
       isnull(mt.DurationMonths, 1)    AS duration_months,
       mt.member_type_id,
       isnull(m.shop_id, 0)            AS member_shop_id
FROM tblMemberInfo m
JOIN tblDefMemberTypes mt ON mt.member_type_id = m.member_type_id
WHERE m.member_id = @customerId";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@customerId", customerId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    int empOrd = reader.GetOrdinal("is_emp_disc");
                    int durOrd = reader.GetOrdinal("duration_months");
                    return new CustomerTypeDetails
                    {
                        IsEmployeeDiscOn = !reader.IsDBNull(empOrd) && Convert.ToBoolean(reader[empOrd]),
                        QtyLimit         = Safe(reader["qty_limit"]),
                        DurationMonths   = reader.IsDBNull(durOrd) ? 1 : Convert.ToInt32(reader[durOrd]),
                        MemberTypeId     = Convert.ToInt32(reader["member_type_id"]),
                        MemberShopId     = (int)Safe(reader["member_shop_id"])
                    };
                }
            }
        }

        // Returns qty already purchased by this customer within the discount window.
        // frmSaleAndReturn.vb:14324: sum(qty) over DurationMonths rolling from start of month.
        private double GetQtyPurchased(int customerId, int memberShopId, int durationMonths)
        {
            if (customerId <= 0) return 0;
            var endDate    = DateTime.Now;
            int safeMonths = Math.Max(durationMonths, 1);
            var startDate  = endDate.AddMonths(-(safeMonths - 1));
            startDate = new DateTime(startDate.Year, startDate.Month, 1);
            const string sql = @"
SELECT isnull(sum(sli.qty), 0) AS QtyPurchase
FROM tblSales s
JOIN tblSalesLineItems sli ON s.sale_id = sli.sale_id AND sli.shop_id = s.shop_id
WHERE s.member_id      = @customerId
  AND s.MemberShopId   = @memberShopId
  AND s.sale_date >= @startDate
  AND s.sale_date <= @endDate";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@customerId",   customerId);
                cmd.Parameters.AddWithValue("@memberShopId", memberShopId);
                cmd.Parameters.AddWithValue("@startDate",    startDate.ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@endDate",      endDate.ToString("yyyy-MM-dd HH:mm:ss"));
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Safe(result) : 0;
            }
        }

        // Returns the discount_id of the active marketing discount for this shop/datetime,
        // using the same SQL as SaleAndReturnDAL.GetMarketingDiscountValue (DAL:1206-1210).
        // Called only when isApplicableOnAll=False to get eligible product list.
        private int GetActiveMarketingDiscountId(int shopId, DateTime saleDate)
        {
            string dayPattern = "%" + saleDate.ToString("dddd") + "%";
            string dateStr    = saleDate.ToString("dd-MMM-yyyy");
            string timeStr    = saleDate.ToString("HH:mm");
            const string sql =
                "SELECT TOP 1 d.discount_id " +
                "FROM tblDefDiscounts D " +
                "INNER JOIN tblDefDiscountShops DS ON D.discount_id = DS.discount_id " +
                "WHERE (CONVERT(datetime, @dateStr, 107) " +
                "       BETWEEN CONVERT(datetime, discount_start_date, 107) " +
                "           AND CONVERT(datetime, discount_end_date,   107)) " +
                "  AND (CONVERT(varchar, CONVERT(datetime, @timeStr), 108) " +
                "       BETWEEN CONVERT(varchar, discount_start_time, 108) " +
                "           AND CONVERT(varchar, discount_end_time,   108)) " +
                "  AND Days_of_Weeks LIKE @dayPat " +
                "  AND charged_to = '2' " +
                "  AND DS.shop_id = @shopId " +
                "  AND mkt_applicable_for = 'Selected'";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@dateStr", dateStr);
                cmd.Parameters.AddWithValue("@timeStr", timeStr);
                cmd.Parameters.AddWithValue("@dayPat",  dayPattern);
                cmd.Parameters.AddWithValue("@shopId",  shopId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
        }

        // Returns the set of product_item_ids eligible for a "Selected" marketing discount.
        // tblDefDiscountProducts stores per-SKU entries for non-blanket discounts.
        private HashSet<int> GetMarketingDiscountEligibleItems(int discountId)
        {
            var result = new HashSet<int>();
            if (discountId <= 0) return result;
            const string sql = "SELECT product_item_id FROM tblDefDiscountProducts WHERE discount_id = @id";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@id", discountId);
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                        result.Add(Convert.ToInt32(rdr["product_item_id"]));
            }
            return result;
        }

        // Returns per-item discount % map for a member type when Multiple_Customer_Disc=True.
        // CustomerTypeDAL.vb:1595-1623: comments field = "itemId,pct|itemId,pct|..."
        private Dictionary<int, double> GetMultipleItemDiscounts(int memberTypeId)
        {
            var result = new Dictionary<int, double>();
            if (memberTypeId <= 0) return result;

            const string sql = "SELECT isnull(comments,'') FROM tblDefMemberTypes WHERE member_type_id = @typeId";
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@typeId", memberTypeId);
                var obj = cmd.ExecuteScalar();
                var comments = obj?.ToString() ?? "";
                if (string.IsNullOrEmpty(comments)) return result;

                foreach (var part in comments.Split('|'))
                {
                    int comma = part.IndexOf(',');
                    if (comma <= 0) continue;
                    if (!int.TryParse(part.Substring(0, comma).Trim(), out int itemId)) continue;
                    if (!double.TryParse(part.Substring(comma + 1).Trim(), out double pct)) continue;
                    result[itemId] = pct;
                }
            }
            return result;
        }
    }
}
