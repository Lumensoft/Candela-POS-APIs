using Candela.Platform.Data;

namespace Candela.Modules.Configuration.Masters;

/// <summary>
/// Every query is copied verbatim from the net48 MastersController — column aliases,
/// join order, the conditional delta and TOP fragments, and the parameter names.
///
/// The old controller's private Run() helper (open connection, fill a DataTable, project
/// each row into a dictionary) is exactly what IDb.QueryRowsAsync does, so Run is gone
/// and nothing else changed. Dapper drops parameters the SQL does not mention, which is
/// what lets @since be passed unconditionally while the delta clause is only appended
/// when there is a value.
/// </summary>
public sealed class MastersRepository(IDb db) : IMastersRepository
{
    public Task<IReadOnlyList<Dictionary<string, object?>>> GetProductsAsync(int shopId,
        DateTime? since, CancellationToken ct)
    {
        const string sql = @"
SELECT
    pi.Product_Item_ID                           AS product_item_id,
    CASE WHEN li.isassortmentenabled = 1
         THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
         ELSE pd.product_code END                AS product_code,
    pd.item_name,
    pd.line_item_id,
    isnull(li.field_name, '')                    AS line_item,
    isnull(pi.CustomerSKUCode,  '')              AS barcode,
    isnull(pi.CustomerSKUCode2, '')              AS barcode2,
    isnull(pi.CustomerSKUCode3, '')              AS barcode3,
    isnull(pi.CustomerSKUCode4, '')              AS barcode4,
    isnull(pi.CustomerSKUCode5, '')              AS barcode5,
    isnull(pp.product_price, 0)                 AS price,
    isnull(pd.Average_cost, 0)                  AS avg_cost,
    isnull(pd.Pur_Con_factor, 0)               AS con_factor,
    isnull(pd.vat, 0)                           AS vat,
    isnull(pd.vat_type, '')                     AS vat_type,
    isnull(pd.NotForDiscount, 0)                AS not_for_discount,
    isnull(pd.Allow_Price_Change, 0)            AS allow_price_change,
    isnull(pd.Tax_At_Retail_Price, 0)           AS tax_at_retail_price,
    isnull(pd.Sale_Tax, 0)                      AS sale_tax,
    isnull(pd.custom_discount, 0)               AS custom_discount,
    isnull(pd.Basic_Designed, 1)                AS basic_designed,
    isnull(inv.quantity, 0)                     AS stock_qty,
    isnull(pd.IsUserDefine, 0)                  AS is_user_define,
    CASE WHEN blk.Block_Product_Id IS NOT NULL THEN 1 ELSE 0 END AS is_blocked_for_sale,
    CASE WHEN isnull(pd.product_life_type, 0) > 0 THEN 1 ELSE 0 END AS is_control_drug,
    CASE WHEN EXISTS (
        SELECT 1 FROM tblDefProductAssembly pa
        WHERE pa.Product_Item_ID_Assembly = pi.Product_Item_ID
    ) THEN 1 ELSE 0 END                         AS is_assembly,
    pd.entereddate,
    pd.editeddate
FROM tblProductItem pi
JOIN tblDefProducts pd      ON pd.product_id       = pi.product_id
JOIN tblDefSizes sz         ON sz.size_id          = pi.size_id          AND sz.line_item_id  = pd.line_item_id
JOIN tblDefCombinitions cl  ON cl.combinition_id   = pi.combinition_id   AND cl.line_item_id  = pd.line_item_id
JOIN tblDefLineItems li     ON li.line_item_id     = pd.line_item_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pi.Product_Item_ID
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pi.Product_Item_ID AND inv.shop_id = @shopId
LEFT JOIN tblBlockPrdctsForSale blk
       ON blk.product_item_id = pi.Product_Item_ID AND blk.shopID = @shopId
WHERE isnull(pd.status, 1) = 1";

        const string delta = " AND (pd.entereddate >= @since OR pd.editeddate >= @since)";

        return db.QueryRowsAsync(sql + (since.HasValue ? delta : ""),
            new { shopId, since }, ct);
    }

    // Mirrors Candela's uiCtrlProductCode.SetSelectedProductInfo scan lookup:
    //   1st priority: product_code match (Candela DataView filter [Product Code] = @q, CI)
    //   2nd priority: CustomerSKUCode 1–5 match (Candela [Customer SKU Code] 1–5 filter)
    // SQL Server's default CI_AS collation makes both lookups case-insensitive, exactly
    // matching the VB.NET DataTable.CaseSensitive = false (default) behaviour in Candela.
    public Task<IReadOnlyList<Dictionary<string, object?>>> ScanProductAsync(int shopId, string q,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP 1
    pi.Product_Item_ID                           AS product_item_id,
    CASE WHEN li.isassortmentenabled = 1
         THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
         ELSE pd.product_code END                AS product_code,
    pd.item_name,
    pd.line_item_id,
    isnull(li.field_name, '')                    AS line_item,
    isnull(pi.CustomerSKUCode,  '')              AS barcode,
    isnull(pi.CustomerSKUCode2, '')              AS barcode2,
    isnull(pi.CustomerSKUCode3, '')              AS barcode3,
    isnull(pi.CustomerSKUCode4, '')              AS barcode4,
    isnull(pi.CustomerSKUCode5, '')              AS barcode5,
    isnull(pp.product_price, 0)                 AS price,
    isnull(pd.Average_cost, 0)                  AS avg_cost,
    isnull(pd.Pur_Con_factor, 0)               AS con_factor,
    isnull(pd.vat, 0)                           AS vat,
    isnull(pd.vat_type, '')                     AS vat_type,
    isnull(pd.NotForDiscount, 0)                AS not_for_discount,
    isnull(pd.Allow_Price_Change, 0)            AS allow_price_change,
    isnull(pd.Tax_At_Retail_Price, 0)           AS tax_at_retail_price,
    isnull(pd.Sale_Tax, 0)                      AS sale_tax,
    isnull(pd.custom_discount, 0)               AS custom_discount,
    isnull(pd.Basic_Designed, 1)                AS basic_designed,
    isnull(inv.quantity, 0)                     AS stock_qty,
    isnull(pd.IsUserDefine, 0)                  AS is_user_define,
    CASE WHEN blk.Block_Product_Id IS NOT NULL THEN 1 ELSE 0 END AS is_blocked_for_sale,
    CASE WHEN isnull(pd.product_life_type, 0) > 0 THEN 1 ELSE 0 END AS is_control_drug,
    CASE WHEN EXISTS (
        SELECT 1 FROM tblDefProductAssembly pa
        WHERE pa.Product_Item_ID_Assembly = pi.Product_Item_ID
    ) THEN 1 ELSE 0 END                         AS is_assembly,
    pd.entereddate,
    pd.editeddate
FROM tblProductItem pi
JOIN tblDefProducts pd      ON pd.product_id       = pi.product_id
JOIN tblDefSizes sz         ON sz.size_id          = pi.size_id          AND sz.line_item_id  = pd.line_item_id
JOIN tblDefCombinitions cl  ON cl.combinition_id   = pi.combinition_id   AND cl.line_item_id  = pd.line_item_id
JOIN tblDefLineItems li     ON li.line_item_id     = pd.line_item_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pi.Product_Item_ID
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pi.Product_Item_ID AND inv.shop_id = @shopId
LEFT JOIN tblBlockPrdctsForSale blk
       ON blk.product_item_id = pi.Product_Item_ID AND blk.shopID = @shopId
WHERE isnull(pd.status, 1) = 1
  AND (
    CASE WHEN li.isassortmentenabled = 1
         THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
         ELSE pd.product_code END = @q
    OR pi.CustomerSKUCode  = @q
    OR pi.CustomerSKUCode2 = @q
    OR pi.CustomerSKUCode3 = @q
    OR pi.CustomerSKUCode4 = @q
    OR pi.CustomerSKUCode5 = @q
  )
ORDER BY
    CASE WHEN CASE WHEN li.isassortmentenabled = 1
                   THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
                   ELSE pd.product_code END = @q THEN 0 ELSE 1 END";

        return db.QueryRowsAsync(sql, new { shopId, q }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> SearchProductAsync(int shopId,
        string? barcode, string? code, CancellationToken ct)
    {
        // Search by CustomerSKUCode (barcode) OR product_code.
        const string sql = @"
SELECT TOP 10
    pi.Product_Item_ID AS product_item_id,
    pd.product_code,
    pd.item_name,
    isnull(pi.CustomerSKUCode,  '') AS barcode,
    isnull(pi.CustomerSKUCode2, '') AS barcode2,
    isnull(pp.product_price, 0)    AS price,
    isnull(pd.Average_cost, 0)    AS avg_cost,
    isnull(pd.Pur_Con_factor, 0)  AS con_factor,
    isnull(pd.IsUserDefine, 0)    AS is_user_define,
    isnull(pd.vat, 0)              AS vat,
    isnull(pd.vat_type, '')        AS vat_type,
    isnull(pd.NotForDiscount, 0)   AS not_for_discount,
    isnull(inv.quantity, 0)        AS stock_qty,
    CASE WHEN EXISTS (
        SELECT 1 FROM tblDefProductAssembly pa
        WHERE pa.Product_Item_ID_Assembly = pi.Product_Item_ID
    ) THEN 1 ELSE 0 END            AS is_assembly
FROM tblProductItem pi
JOIN tblDefProducts pd ON pd.product_id = pi.product_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pi.Product_Item_ID
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pi.Product_Item_ID AND inv.shop_id = @shopId
WHERE isnull(pd.status, 1) = 1
  AND (  (@barcode IS NOT NULL AND (pi.CustomerSKUCode  = @barcode
                                 OR pi.CustomerSKUCode2 = @barcode))
      OR (@code    IS NOT NULL AND pd.product_code = @code))";

        // Blank means "not supplied", and the SQL tests the parameter for NULL — so an
        // empty string has to become null here, exactly as the original's DBNull did.
        return db.QueryRowsAsync(sql, new
        {
            shopId,
            barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode,
            code = string.IsNullOrWhiteSpace(code) ? null : code
        }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetCustomersAsync(int shopId,
        DateTime? since, string? q, CancellationToken ct)
    {
        // Mirrors Candela's CustomerDAL.PopulateCustomerList shop-user branch: a customer
        // is visible here if their member type is IsVisibleOnAllShops (or that flag is unset,
        // which defaults to visible-everywhere), or they were registered at this shop.
        // A member type flagged IsSecurityOn is additionally suppressed from this general
        // list once it's cross-shop visible — those customers are only meant to be reached by
        // an explicit card/barcode scan (a separate, unfiltered lookup), never browsed/searched.
        // ?since= for delta sync (IndexedDB initial/incremental load).
        // ?q=     for live search fallback when the app gets a miss in IndexedDB.
        //         Matches member_name, phone_no, or mobile_no — TOP 50 for UX speed.
        // Joins member type for discount_pct and customer_disc_type used in the sale screen.
        // credit_outstanding mirrors SaleAndReturnDAL.CalculateOutstanding: tblSales.memberShopID /
        // tblMemberReceipts.MemberShop_id tag which customer a transaction belongs to (their home
        // shop), independently of which physical shop it happened at — so this must filter on the
        // customer's own shop_id (m.shop_id), never the viewing shop (@shopId), or a cross-shop
        // customer's balance comes back as whatever they happened to transact at this shop (often 0).
        bool isSearch = !string.IsNullOrWhiteSpace(q);

        string sql = @"
SELECT" + (isSearch ? " TOP 50" : "") + @"
    m.member_id,
    m.member_name,
    m.shop_id,
    isnull(m.phone_Res,    '')  AS phone,
    isnull(m.phone_Mobile, '')  AS mobile,
    isnull(m.email,        '')  AS email,
    isnull(m.credit_limit, 0)  AS credit_limit,
    isnull(m.allow_credit, 0)  AS allow_credit,
    isnull(m.comments,     '')  AS comments,
    isnull(m.member_type_id, 0) AS member_type_id,
    isnull(mt.discount_percentage, 0)  AS discount_pct,
    m.end_date,
    m.entereddate,
    m.editeddate,
    isnull((SELECT SUM(s.NT_amount) FROM tblSales s
            WHERE s.member_id = m.member_id AND s.isCreditSale = 1 AND s.memberShopID = m.shop_id), 0)
    - isnull((SELECT SUM(r.amount) FROM tblMemberReceipts r
              WHERE r.member_id = m.member_id AND r.MemberShop_id = m.shop_id), 0)
    AS credit_outstanding
FROM tblMemberInfo m
LEFT JOIN tblDefMemberTypes mt ON mt.member_type_id = m.member_type_id
WHERE (isnull(mt.IsVisibleOnAllShops, 0) = 1 OR mt.IsVisibleOnAllShops IS NULL OR m.shop_id = @shopId)
  AND NOT (isnull(mt.IsSecurityOn, 0) = 1 AND isnull(mt.IsVisibleOnAllShops, 0) = 1)";

        if (since.HasValue)
            sql += " AND (m.entereddate >= @since OR m.editeddate >= @since)";

        if (isSearch)
            sql += @"
  AND (m.member_name  LIKE @q
    OR m.phone_Res    LIKE @q
    OR m.phone_Mobile LIKE @q)";

        return db.QueryRowsAsync(sql, new
        {
            shopId,
            since,
            q = isSearch ? "%" + q!.Trim() + "%" : null
        }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetEmployeesAsync(int shopId,
        DateTime? since, CancellationToken ct)
    {
        // tblDefShopEmployees — salesperson list for the salesperson assign modal.
        // Actual column names: field_name (display name) and field_Code (code).
        // No isActive column — filter by IsSalesperson when set.
        // Delta columns: EnteredDate / EditedDate.
        const string sql = @"
SELECT
    e.shop_employee_id,
    isnull(e.field_name,  '') AS employee_name,
    isnull(e.field_Code,  '') AS employee_code,
    e.shop_id,
    e.EnteredDate,
    e.EditedDate
FROM tblDefShopEmployees e
WHERE e.shop_id = @shopId";

        const string delta = " AND (e.EnteredDate >= @since OR e.EditedDate >= @since)";

        return db.QueryRowsAsync(sql + (since.HasValue ? delta : ""), new { shopId, since }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetCreditCardsAsync(int shopId,
        DateTime? since, CancellationToken ct)
    {
        // tblDefCreditCards holds every card type defined system-wide, but Candela's
        // desktop app only offers the subset mapped to the current shop via
        // tblDefShopCreditCards (frmSaleAndReturn's card-type dropdown mirrors this).
        // Real columns: credit_card_id, field_name, EnteredDate (no isActive, no editeddate).
        const string sql = @"
SELECT
    c.credit_card_id,
    isnull(c.field_name, '') AS credit_card_name,
    c.EnteredDate
FROM tblDefCreditCards c
INNER JOIN tblDefShopCreditCards sc
    ON sc.credit_card_id = c.credit_card_id AND sc.shop_id = @shopId";

        const string delta = " WHERE c.EnteredDate >= @since";

        return db.QueryRowsAsync(sql + (since.HasValue ? delta : "") + " ORDER BY c.sort_order",
            new { shopId, since }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetMemberTypesAsync(DateTime? since,
        CancellationToken ct)
    {
        // tblDefMemberTypes — customer tier definitions.
        // customer_disc_type drives which discount branch applies in /quote.
        const string sql = @"
SELECT
    mt.member_type_id,
    isnull(mt.field_name, '')           AS type_name,
    isnull(mt.discount_percentage, 0)   AS discount_percentage,
    isnull(mt.IsEmployeeDiscOn, 0)      AS is_employee_disc_on,
    isnull(mt.QtyLimit, 0)             AS qty_limit,
    isnull(mt.DurationMonths, 1)       AS duration_months,
    mt.EnteredDate,
    mt.editeddate
FROM tblDefMemberTypes mt";

        const string delta = " WHERE (mt.entereddate >= @since OR mt.editeddate >= @since)";

        return db.QueryRowsAsync(sql + (since.HasValue ? delta : ""), new { since }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetCustomerGroupsAsync(CancellationToken ct)
    {
        // tblDefMembershipGroups — customer group master (Group dropdown in customer form).
        // No timestamp column — always return full set (table is small).
        const string sql = @"
SELECT
    group_id,
    isnull(field_name,  '')  AS group_name,
    isnull(field_code,  '')  AS group_code,
    isnull(sort_order,  0)   AS sort_order,
    isnull(group_discount, 0) AS group_discount
FROM tblDefMembershipGroups
ORDER BY sort_order, field_name";

        return db.QueryRowsAsync(sql, null, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetPaymentMethodsAsync(CancellationToken ct)
    {
        // Mobile payment providers — read from tblRCMSConfiguration.
        // Returns one row per configured provider with its enabled flag.
        // App hides the Mobile tab when all providers are disabled.
        const string sql = @"
SELECT config_name AS provider, config_value AS value
FROM   tblRCMSConfiguration
WHERE  config_name IN ('FonePayEnabled', 'AlifPayEnabled',
                       'FonePayMerchantId', 'AlifPayMerchantId',
                       '543PayEnabled', '543PayMerchantId')";

        return db.QueryRowsAsync(sql, null, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetConfigAsync(int shopId,
        CancellationToken ct)
    {
        // Merges tblShopConfiguration (shop-specific) and tblRCMSConfiguration (global).
        // Shop config overrides RCMS when the same key exists in both.
        // Both tables are small — always return all rows.
        // The ?since= param is accepted for API consistency but config has no reliable
        // timestamp column, so we always return the full set (acceptable — config is tiny).
        const string sql = @"
SELECT config_name AS config_key, config_value, 'shop' AS source
FROM   tblShopConfiguration
WHERE  shop_id = @shopId

UNION ALL

SELECT config_name AS config_key, config_value, 'rcms' AS source
FROM   tblRCMSConfiguration";

        return db.QueryRowsAsync(sql, new { shopId }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetLineItemsAsync(CancellationToken ct)
    {
        // Product departments / categories from tblDefLineItems.
        // Excludes service line items (non-product departments).
        // Ordered by sort_order so category tabs appear in the same order as Candela.
        const string sql = @"
SELECT
    li.line_item_id,
    isnull(li.field_name, '') AS field_name,
    isnull(li.field_code, '') AS field_code,
    isnull(li.sort_order, 0)  AS sort_order,
    isnull(li.IsAllowDecimal, 1) AS is_allow_decimal
FROM tblDefLineItems li
WHERE isnull(li.IsServiceLineItem, 0) = 0
ORDER BY li.sort_order, li.field_name";

        return db.QueryRowsAsync(sql, null, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetBatchesAsync(int productItemId,
        CancellationToken ct)
    {
        // Sum all batch movements for this product; positive net = stock available.
        // Ordered by ExpiryDate ascending = FEFO (First Expired, First Out) order.
        const string sql = @"
SELECT
    a.BatchNo                              AS batch_no,
    a.ExpiryDate                           AS expiry_date,
    a.ProductItemID                        AS product_item_id,
    a.Quantity                             AS available_qty,
    CASE
        WHEN a.ExpiryDate IS NULL THEN 0
        WHEN a.ExpiryDate < GETDATE() THEN 1
        ELSE 0
    END                                    AS is_expired
FROM (
    SELECT
        sum(Quantity)  AS Quantity,
        BatchNo,
        ExpiryDate,
        ProductItemID
    FROM tblbatchdetail
    WHERE ProductItemID = @productItemId
    GROUP BY BatchNo, ExpiryDate, ProductItemID
) a
WHERE a.Quantity > 0
ORDER BY a.ExpiryDate ASC";

        return db.QueryRowsAsync(sql, new { productItemId }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetAssemblyItemsAsync(int productItemId,
        int shopId, CancellationToken ct)
    {
        const string sql = @"
SELECT
    pa.Product_Item_ID_Part              AS product_item_id,
    pd.Product_number                    AS product_code,
    pd.item_name,
    CAST(pa.Quantity AS FLOAT)           AS quantity,
    ISNULL(pp.product_price, 0)          AS retail_price,
    ISNULL(inv.quantity, 0)              AS stock_qty
FROM tblDefProductAssembly pa
INNER JOIN tblProductItem  pi ON pi.Product_Item_ID = pa.Product_Item_ID_Part
INNER JOIN tblDefProducts  pd ON pd.product_id      = pi.product_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pa.Product_Item_ID_Part
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pa.Product_Item_ID_Part
      AND inv.shop_id = @shopId
WHERE pa.Product_Item_ID_Assembly = @productItemId
ORDER BY pd.item_name";

        return db.QueryRowsAsync(sql, new { productItemId, shopId }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetBlockedProductsAsync(int shopId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT product_item_id
FROM   tblBlockPrdctsForSale
WHERE  shopID = @shopId";

        return db.QueryRowsAsync(sql, new { shopId }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetPromotionProductsAsync(CancellationToken ct)
    {
        // Mirrors frmSaleAndReturn.vb:17716 (showAlert): PromotedProductItemId is the item
        // that triggers the alert when added to the cart; ProductItemId is the cross-sell
        // target whose stock is checked before the toast is shown. Unlike showAlert's SQL
        // (which only checks ToDate), this also requires Fromdate to have started — only
        // currently-active promotions sync down; the client re-checks both bounds anyway
        // at add-to-cart time since a promotion can start/expire between syncs.
        const string sql = @"
SELECT
    PromotionId             AS promotion_id,
    ProductItemId            AS product_item_id,
    PromotedProductItemId   AS promoted_product_item_id,
    Fromdate                AS from_date,
    ToDate                   AS to_date,
    isnull(PromotionMessage, '') AS promotion_message
FROM tblPromotionProducts
WHERE Fromdate <= GETDATE() AND ToDate >= CAST(GETDATE() AS DATE)";

        return db.QueryRowsAsync(sql, null, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetStrProductsAsync(string strNo,
        int shopId, CancellationToken ct)
    {
        // Mirrors SaleAndReturnDAL.funGetStrProducts — frmSaleAndReturn.vb:22651
        // Loads product + quantity from a Stock Transfer Request by STR number.
        // Alternate product codes (CustomerSKUCode) are accepted same as Candela.
        const string sql = @"
SELECT
    sli.product_item_id,
    pd.item_name,
    pd.product_code,
    isnull(pi.CustomerSKUCode, '') AS barcode,
    sli.qty,
    isnull(pp.product_price, 0)   AS price
FROM tblSTR s
JOIN tblSTRLineItems sli ON sli.STR_id = s.STR_id
JOIN tblProductItem pi   ON pi.Product_Item_ID = sli.product_item_id
JOIN tblDefProducts pd   ON pd.product_id = pi.product_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = sli.product_item_id
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
WHERE s.STR_no = @strNo
  AND s.to_shop_id = @shopId";

        return db.QueryRowsAsync(sql, new { strNo, shopId }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetShopsAsync(CancellationToken ct)
    {
        const string sql =
            "SELECT shop_id, shop_name, isnull(shop_code,'') AS shop_code " +
            "FROM tblDefShops " +
            "ORDER BY shop_name";

        return db.QueryRowsAsync(sql, null, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetDepartmentsAsync(int shopId,
        CancellationToken ct)
    {
        const string sql =
            "SELECT shop_department_id, field_name " +
            "FROM tblDefShopDepartments " +
            "WHERE shop_id = @shopId " +
            "ORDER BY field_name";

        return db.QueryRowsAsync(sql, new { shopId }, ct);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAdjustmentReasonsAsync(
        CancellationToken ct)
    {
        const string sql =
            "SELECT ReasonID, ReasonDescription, ISNULL(sort_order,0) AS sort_order " +
            "FROM TblDefAdjustmentReason " +
            "ORDER BY ISNULL(sort_order,0), ReasonID";

        return ToReasonRows(await db.QueryRowsAsync(sql, null, ct));
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetReturnReasonsAsync(
        CancellationToken ct)
    {
        const string sql =
            "SELECT ReasonID, ReasonDescription, ISNULL(sort_order,0) AS sort_order " +
            "FROM TblDefReturnReasons " +
            "ORDER BY ISNULL(sort_order,0), ReasonID";

        return ToReasonRows(await db.QueryRowsAsync(sql, null, ct));
    }

    /// <summary>
    /// The two reason endpoints are the only ones that renamed their columns in C# rather
    /// than in SQL — the original read ReasonID / ReasonDescription off the reader and put
    /// them into a dictionary under snake_case keys. Renaming them in the SELECT instead
    /// would have been tidier but would change the SQL, so the rename stays here.
    /// </summary>
    private static IReadOnlyList<Dictionary<string, object?>> ToReasonRows(
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var list = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
            list.Add(new Dictionary<string, object?>
            {
                ["reason_id"] = row["ReasonID"],
                ["reason_description"] = row["ReasonDescription"],
                ["sort_order"] = row["sort_order"],
            });
        return list;
    }
}
