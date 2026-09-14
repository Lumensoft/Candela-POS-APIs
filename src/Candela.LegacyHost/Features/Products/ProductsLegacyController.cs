using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Shared.Errors;
using CandelaPOS.Shared.Logging;
using DAL;
using Model;
using static Utility.Utility;

namespace CandelaPOS.Features.Products
{
    /// <summary>
    /// The only way the .NET 10 API is allowed to write a Product — the Core Product tab
    /// (frmDefProduct.vb, EnumTabPages.TabPgProduct), non-assortment mode: one product,
    /// one SKU, no size/colour matrix. See Candela.Modules.Configuration.Products.
    /// ProductRequest on the API side for what that scope means.
    ///
    /// Writes go through ProductDAL rather than straight SQL for the same reason
    /// CitiesLegacyController does: the DAL writes the SQL log HO/shop replication
    /// replays and the activity log the audit screens read, and it creates the
    /// tblProductItem SKU row (SaveSKUs) that makes a product sellable — none of which a
    /// raw INSERT would do.
    ///
    /// This is a SEPARATE controller from Features/Products/ProductsController.cs, which
    /// is the POS tablet's read-only alternates/batches slice at api/products — routed
    /// under legacy/products instead, and neither touches the other.
    /// </summary>
    [RoutePrefix("legacy/products")]
    public class ProductsLegacyController : ApiController
    {
        // POST legacy/products
        [HttpPost, Route("")]
        public HttpResponseMessage Create([FromBody] LegacyProductRequest req)
        {
            if (req == null) return Invalid("Request body is required.");

            try
            {
                var product = ToModel(req, 0);

                if (!new ProductDAL().Add(product))
                    throw new Exception("ProductDAL.Add returned false.");

                AppLog.Info("Product {0} created by user {1}", product.ProductID, req.UserId);
                return Request.CreateResponse(HttpStatusCode.OK, new { product_id = product.ProductID });
            }
            catch (Exception ex) { return Translate(ex, "ProductsLegacyController.Create"); }
        }

        // PUT legacy/products/{id}
        [HttpPut, Route("{id:int}")]
        public HttpResponseMessage Update(int id, [FromBody] LegacyProductRequest req)
        {
            if (req == null) return Invalid("Request body is required.");
            if (id <= 0) return Invalid("A valid product id is required.");

            try
            {
                var product = ToModel(req, id);

                if (!new ProductDAL().Update(product))
                    throw new Exception("ProductDAL.Update returned false.");

                // ProductDAL.Update's own price step (inside SaveSKUs) only fires when this
                // install's "product_code_only" config is on — false here — and even then it
                // never touches WholeSalePrice at all. The desktop only ever changes price
                // after the first save from its separate Product Price tab (ModifyPrice), so
                // that is what an edit here has to call too, or Retail/Wholesale price would
                // silently stop being editable once a product exists.
                UpdatePrice(product, req);

                AppLog.Info("Product {0} updated by user {1}", id, req.UserId);
                return Request.CreateResponse(HttpStatusCode.OK, new { product_id = id });
            }
            catch (Exception ex) { return Translate(ex, "ProductsLegacyController.Update"); }
        }

        // DELETE legacy/products/{id}
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage Delete(int id, [FromUri] int userId = 0)
        {
            if (id <= 0) return Invalid("A valid product id is required.");

            try
            {
                var product = new Product { ProductID = id };
                StampActivityLog(product, userId, "Delete");

                // frmDefProduct.vb:3809-3813 — refuses while deals, production inventory,
                // PO holds or an assembly still reference this product's SKUs.
                new ProductDAL().IsValidateForDelete(product);

                if (!new ProductDAL().Deleted(product))
                    throw new Exception("ProductDAL.Deleted returned false.");

                AppLog.Info("Product {0} deleted by user {1}", id, userId);
                return Request.CreateResponse(HttpStatusCode.OK, new { product_id = id });
            }
            catch (Exception ex) { return Translate(ex, "ProductsLegacyController.Delete"); }
        }

        /// <summary>
        /// Builds the Model.Product the DAL expects, including the one Size and one
        /// Combination every non-assortment product attaches to (frmDefProduct.vb:4104-
        /// 4116) and the ActivityLog fields FillModel("Product") sets.
        /// </summary>
        private static Product ToModel(LegacyProductRequest req, int productId)
        {
            var product = new Product
            {
                ProductID = productId,
                LineItemID = req.LineItemId,
                ProductCode = (req.ProductCode ?? "").Trim(),
                ProductNumber = (req.ProductCode ?? "").Trim(),
                ItemName = (req.ItemName ?? "").Trim(),
                OtherCode = req.VendorCode ?? "",

                CategoryID = req.CategoryId,
                SubCategoryID = req.SubCategoryId,
                ProductGroupID = req.ProductGroupId,
                AgeGroupsID = req.Variable1Id,
                PackagingCodeId = req.Variable2Id,
                ProductLifeType = req.Variable3Id,
                Gender = req.Variable4Id,
                ValueAdditionByID = req.Variable5Id,
                CalendarSeasonID = req.CalendarSeasonId,

                SupplierID = req.SupplierId,
                AcquireType = req.AcquireType,
                PurchaseType = req.PurchaseType,
                ManufactureType = req.ManufactureType,
                PurConUnit = req.PurConUnit,
                PurConFactor = req.PurConFactor,

                SaleTaxCodeID = req.SaleTaxCodeId,
                // frmDefProduct.vb:4282-4286 — sale-tax-code 2 or 3 forces VAT to zero.
                VAT = (req.SaleTaxCodeId == 2 || req.SaleTaxCodeId == 3) ? 0 : req.Vat,
                // VATType is a String on the model (matching the DB column, which itself
                // holds "0"/"1" text — see ProductRepository.cs's comment on why, on the
                // API side, older rows still hold "Percentage"/"Value" instead).
                VATType = req.VatIsValue ? "1" : "0",
                SaleTax = req.SaleTax,
                TaxAtRetailPrice = req.TaxAtRetailPrice,

                Price = req.Price,
                WholeSalePrice = req.WholeSalePrice,
                AverageCost = req.AverageCost,
                IsUserDefine = req.UserPrice,
                FloatingPrice = req.FloatingPrice,
                CurrencyID = req.FloatingPrice ? req.CurrencyId : 0,

                CostType = 1, // frmDefProduct.vb:4327 — always fixed, never percentage.
                Status = req.Active ? 1 : 0,
                IsDefault = req.IsDefault ? 1 : 0,
                BarcodePrint = req.NoBarcodePrint,
                RFIDEnabled = req.RfidEnabled,
                AllowBelowCost = req.AllowBelowCost,
                AllowPriceChange = req.AllowPriceChange,
                isWebItem = req.IsWebItem,
                ItemNotForDiscount = req.ItemNotForDiscount,
                BasicDesigned = req.IsBasicType ? 1 : 2,

                // OpeningDate is NOT a tblDefProducts column (confirmed against ProductDAL's
                // INSERT/UPDATE, neither lists it) — but it is very much used one level
                // down: ProductDAL.Add's SaveSKUs step writes it as tblDefProductPrice.
                // Start_Date, the initial price's effective date. Left unset it defaults to
                // DateTime.MinValue (VB's Date is a value type), and that INSERT dies with
                // "out-of-range value" the same way an unset EnteredDate/EditedDate would —
                // this is exactly the bug that surfaced once the earlier ones were fixed.
                // Defaulted to today until the Opening Stock tab (a later phase) gives the
                // user an actual field for it.
                OpeningDate = DateTime.Today,
                CreationDate = DateTime.Now,
                NumberOfPieces = req.NumberOfPieces,

                HSCode = req.HsCode ?? "",
                TechnicalDetails = req.TechnicalDetails ?? "",
                Comments = req.Comments ?? "",
                InternalComments = req.InternalComments ?? "",

                EnteredBy = req.UserId,
                EditedBy = req.UserId,
                EnteredDate = DateTime.Now,
                EditedDate = DateTime.Now,

                Sizes = new List<Size> { new Size { SizeID = req.SizeId } },
                Combinations = new List<Combinations> { new Combinations { CombinationID = req.CombinationId } },

                // ProductDAL.Update calls .Length on both right after its UPDATE statement,
                // with no null-guard (Product.vb never defaults them) — Phase 1 never lets
                // the user remove a size/colour, so there is nothing to delete, but the
                // string must exist or Update() throws a NullReferenceException.
                DeleteSizeIDs = "",
                DeleteCombinationIDs = ""
            };

            StampActivityLog(product, req.UserId, productId == 0 ? "Save" : "Update");
            return product;
        }

        /// <summary>
        /// Same two DAL entry points frmDefProduct's Product Price tab uses to change an
        /// existing SKU's price — ProductDAL.ModifyPrice (Action="Update", tblDefProductPrice.
        /// product_price on the still-open row) and ProductDAL.UpdateWholeSale (a plain
        /// product-code lookup, tblProductItem.WholeSalePrice; its 1-row DataTable shape is
        /// literally SKU code + price, from its own Excel-bulk-update origin). Both are
        /// headless — no WinForms — unlike UpdateProductInformation, which pops a progress
        /// bar and cannot run on a server. RRP is left at its 0 default: verified against
        /// live data that no product on this install has ever had one set, so there is
        /// nothing to preserve.
        /// </summary>
        private static void UpdatePrice(Product product, LegacyProductRequest req)
        {
            var productPrice = new Model.ProductPrice
            {
                ProductID = product.ProductID,
                ProductCode = product.ProductCode,
                Action = "Update",
                StartDate = DateTime.Today,
                ProductPrice = req.Price,
                ProductItems = new List<ProductItem>
                {
                    new ProductItem { SizeID = req.SizeId, CombinitionID = req.CombinationId }
                }
            };
            new ProductDAL().ModifyPrice(productPrice);

            var wholesale = new DataTable();
            wholesale.Columns.Add("SkuCode");
            wholesale.Columns.Add("Price");
            wholesale.Rows.Add(product.ProductCode, req.WholeSalePrice);
            new ProductDAL().UpdateWholeSale(wholesale);
        }

        private static void StampActivityLog(Product product, int userId, string action)
        {
            product.ActivityLog.ShopID = -1;
            product.ActivityLog.ScreenTitle = "Product Definition";
            product.ActivityLog.LogGroup = "Configuration";
            product.ActivityLog.Source = "Product Definition";
            product.ActivityLog.UserID = userId;
            product.ActivityLog.FormAction = action;
        }

        /// <summary>
        /// The DAL signals business rules by throwing with one of Candela's own message
        /// constants — passed through with 422, same as CitiesLegacyController.Translate.
        /// Anything else is a real fault: logged in full, answered generically.
        /// </summary>
        private HttpResponseMessage Translate(Exception ex, string context)
        {
            var msg = ex.Message ?? "";

            bool isBusinessRule =
                msg.IndexOf(gstrMsgDuplicateName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf(gstrMsgDuplicateCode, StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf(gstrMsgDependentRecordExist, StringComparison.OrdinalIgnoreCase) >= 0;

            if (isBusinessRule)
            {
                AppLog.Warn("{0} refused: {1}", context, msg);
                return Request.CreateResponse((HttpStatusCode)422, new { error = msg });
            }

            return ApiError.Internal(Request, ex, context);
        }

        private HttpResponseMessage Invalid(string message) =>
            Request.CreateResponse(HttpStatusCode.BadRequest, new { error = message });
    }

    /// <summary>Write payload for a product. Mirrors what frmDefProduct.FillModel("Product") builds.</summary>
    public class LegacyProductRequest
    {
        public int LineItemId { get; set; }
        public string ProductCode { get; set; }
        public string ItemName { get; set; }

        public int CategoryId { get; set; }
        public int SubCategoryId { get; set; }
        public int ProductGroupId { get; set; }
        public int Variable1Id { get; set; }
        public int Variable2Id { get; set; }
        public int Variable3Id { get; set; }
        public int Variable4Id { get; set; }
        public int Variable5Id { get; set; }
        public int CalendarSeasonId { get; set; }

        public int SupplierId { get; set; }
        public int AcquireType { get; set; }
        public int PurchaseType { get; set; }
        public int ManufactureType { get; set; }
        public int PurConUnit { get; set; }
        public double PurConFactor { get; set; }

        public int SaleTaxCodeId { get; set; }
        public double SaleTax { get; set; }
        public double Vat { get; set; }
        public bool VatIsValue { get; set; }
        public bool TaxAtRetailPrice { get; set; }

        public double Price { get; set; }
        public double WholeSalePrice { get; set; }
        public double AverageCost { get; set; }
        public bool UserPrice { get; set; }
        public bool FloatingPrice { get; set; }
        public int CurrencyId { get; set; }

        public bool Active { get; set; }
        public bool IsDefault { get; set; }
        public bool NoBarcodePrint { get; set; }
        public bool RfidEnabled { get; set; }
        public bool AllowBelowCost { get; set; }
        public bool AllowPriceChange { get; set; }
        public bool IsWebItem { get; set; }
        public bool ItemNotForDiscount { get; set; }
        public bool IsBasicType { get; set; }
        public double NumberOfPieces { get; set; }

        public string HsCode { get; set; }
        public string VendorCode { get; set; }
        public string TechnicalDetails { get; set; }
        public string Comments { get; set; }
        public string InternalComments { get; set; }

        /// <summary>The line item's default READONLY Size/Combination — resolved by the API before this call.</summary>
        public int SizeId { get; set; }
        public int CombinationId { get; set; }

        /// <summary>tblSecurityUser.user_id, for EnteredBy/EditedBy and the activity log.</summary>
        public int UserId { get; set; }
    }
}
