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
}
