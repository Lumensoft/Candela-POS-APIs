using Candela.Modules.Sales.Returns.Dtos;
using Candela.Platform.Legacy;
using Candela.Shared.Exceptions;

namespace Candela.Modules.Sales.Returns;

/// <summary>
/// Ported from the net48 ReturnsController. validate and preview keep their exact SQL and
/// their exact branch logic; PostReturn is forwarded whole.
/// </summary>
public sealed class ReturnsService(IReturnsRepository repo, ILegacyHostClient legacy) : IReturnsService
{
    public async Task<ValidateReturnResponse> ValidateAsync(int invoiceNo, int shopId,
        int sourceShopId, CancellationToken ct)
    {
        // dal.IsValidInvoiceForReturn — 422 with the net48 wording when it fails.
        if (!await repo.IsValidInvoiceForReturnAsync(sourceShopId, invoiceNo, ct))
            throw new BusinessRuleException(
                "Invoice not found, already fully returned, or belongs to a different shop");

        var customerCode = await repo.GetCustomerAgainstInvoiceAsync(sourceShopId, invoiceNo, ct);
        var sale = await repo.QuerySaleHeaderAsync(sourceShopId, invoiceNo, ct);
        var items = await repo.QuerySaleItemsAsync(sourceShopId, invoiceNo, ct);

        // Saved rows may be in Candela's convention (pack lines per pack, tax kept apart); hand the web
        // return screen per-single values, tax included.
        foreach (var item in items)
            SaleLineConvention.ToPerSingle(item);

        return new ValidateReturnResponse
        {
            CustomerCode = customerCode ?? "",
            Sale = sale,
            Items = items,
        };
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> PreviewAsync(ReturnPreviewRequest req,
        int shopId, int sourceShopId, CancellationToken ct)
    {
        var productIds = (req.Items ?? new())
            .Select(i => i.ProductItemId).Where(id => id > 0).Distinct().ToList();

        var origLines = await repo.QuerySaleItemsForPreviewAsync(sourceShopId, req.SaleId, productIds, ct);
        foreach (var line in origLines.Values)
            SaleLineConvention.ToPerSingle(line);   // same per-single, tax-included shape as /validate
        var result = new List<Dictionary<string, object?>>();

        // Pre-compute total return qty per discount_id across ALL items in this request
        // (X + Y items combined). Candela stores the same discount_id on both the trigger
        // item (X) and the free item (Y); using the group total instead of the individual
        // Y qty fixes buy-N-get-M promotions where X and Y are different products.
        var discountGroupQty = new Dictionary<int, double>();
        foreach (var ri in req.Items ?? new())
        {
            if (!origLines.TryGetValue(ri.ProductItemId, out var ol)) continue;
            int did = Convert.ToInt32(ol["discount_id"]);
            if (did <= 0) continue;
            if (!discountGroupQty.ContainsKey(did)) discountGroupQty[did] = 0;
            discountGroupQty[did] += ri.Qty;
        }

        foreach (var reqItem in req.Items ?? new())
        {
            if (!origLines.TryGetValue(reqItem.ProductItemId, out var orig))
                continue;

            int discountId = Convert.ToInt32(orig["discount_id"]);
            double unitPrice = Convert.ToDouble(orig["unit_price"]);
            double unitDiscount = Convert.ToDouble(orig["unit_discount"]);
            double priceAfterDisc = Convert.ToDouble(orig["price_after_discount"]);

            // For discounts that carry a buy-qty threshold (discount_duration > 0), revert
            // to full unit_price when the total returned qty for this discount group
            // (X + Y combined) falls below threshold + 1.
            if (discountId > 0 && unitDiscount > 0)
            {
                double threshold = await repo.GetDiscountDurationAsync(discountId, ct);
                if (threshold > 0)
                {
                    double groupQty = discountGroupQty.ContainsKey(discountId)
                        ? discountGroupQty[discountId]
                        : reqItem.Qty;
                    if (groupQty < threshold + 1)
                    {
                        unitDiscount = 0;
                        priceAfterDisc = unitPrice;
                    }
                }
            }

            var item = new Dictionary<string, object?>(orig)
            {
                ["unit_discount"] = unitDiscount,
                ["price_after_discount"] = priceAfterDisc,
            };
            result.Add(item);
        }

        return result;
    }

    public Task<LegacyRawResponse> ForwardPostReturnAsync(string rawJsonBody, CancellationToken ct)
        => legacy.SendRawAsync(HttpMethod.Post, "returns", rawJsonBody, ct);

    public Task<LegacyRawResponse> ForwardUpdateReturnAsync(int returnId, string rawJsonBody, CancellationToken ct)
        => legacy.SendRawAsync(HttpMethod.Put, $"returns/{returnId}", rawJsonBody, ct);

    public async Task<ReturnEditResponse?> GetReturnForEditAsync(int returnId, int shopId, CancellationToken ct)
    {
        var header = await repo.QueryReturnHeaderAsync(shopId, returnId, ct);
        if (header is null) return null;

        if (Convert.ToBoolean(header["is_voided"]))
            throw new BusinessRuleException("Sale not found or already voided.");

        int    originalId = Convert.ToInt32(header["returning_invoice_no"]);
        double netTotal   = Convert.ToDouble(header["net_total"]);
        if (originalId == 0 && netTotal >= 0)   // not linked to an invoice and not a refund: an ordinary sale
            throw new BusinessRuleException("This invoice is not a return.");

        var response = new ReturnEditResponse
        {
            Return = header,
            Lines = await repo.QueryReturnLinesAsync(shopId, returnId, ct),
        };

        // A free return has no original invoice — the screen prices it from the live quote.
        if (originalId <= 0) return response;

        int sourceShopId = Convert.ToInt32(header["source_shop_id"]);
        if (sourceShopId <= 0) sourceShopId = shopId;
        header["source_shop_id"] = sourceShopId;

        response.Sale = await repo.QuerySaleHeaderAsync(sourceShopId, originalId, ct);

        var items = await repo.QuerySaleItemsAsync(sourceShopId, originalId, ct);
        foreach (var item in items)
            SaleLineConvention.ToPerSingle(item);

        // How many units may still be returned = original quantity − returned by the OTHER returns.
        var returnedByOthers = await repo.GetReturnedQtyExcludingAsync(originalId, returnId, ct);
        var originalQty = new Dictionary<int, double>();
        foreach (var item in items)
        {
            int pid = Convert.ToInt32(item["product_item_id"]);
            originalQty[pid] = originalQty.GetValueOrDefault(pid) + Math.Abs(Convert.ToDouble(item["quantity"]));
        }
        foreach (var item in items)
        {
            int pid = Convert.ToInt32(item["product_item_id"]);
            item["max_qty"] = Math.Max(0, originalQty[pid] - returnedByOthers.GetValueOrDefault(pid));
        }

        response.Items = items;
        return response;
    }
}
