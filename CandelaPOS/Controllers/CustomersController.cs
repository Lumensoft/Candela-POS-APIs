using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/masters")]
    public class CustomersController : ApiController
    {
        // GET api/masters/customers?q=ali
        // GET api/masters/customers?since=2026-01-01T00:00:00
        [HttpGet, Route("customers")]
        public HttpResponseMessage GetCustomers(
            [FromUri] string q     = null,
            [FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();

            int shopId = (int)Request.Properties["shop_id"];

            try
            {
                var rows = QueryCustomers(shopId, q, since);
                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success = true, count = rows.Count, data = rows });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        private List<Dictionary<string, object>> QueryCustomers(
            int shopId, string q, string since)
        {
            // Base query — fields the POS needs for credit sales + loyalty
            const string sql = @"
SELECT
    m.member_id,
    m.shop_id,
    m.member_no,
    m.member_name,
    isnull(m.alternate_card_no, '') AS barcode,
    isnull(m.phone_mobile, '')      AS phone_mobile,
    isnull(m.email, '')             AS email,
    isnull(m.cust_Address, '')      AS address,
    isnull(m.allow_credit, 0)       AS allow_credit,
    isnull(m.credit_limit, 0)       AS credit_limit,
    m.member_type_id,
    isnull(mt.field_name, '')       AS member_type_name,
    isnull(m.status, '')            AS status,
    m.expiry_date,
    RIGHT('000' + s.ShopMembershipCode, 3)
        + '-' + RIGHT('000000' + CONVERT(varchar(6), m.member_no), 6)
        + '-' + RIGHT('0'      + CONVERT(varchar(2), m.card_duplicate_no), 2)
        AS member_code,
    m.EnteredDate,
    m.EditedDate
FROM tblMemberInfo m
JOIN tblDefShops s         ON s.shop_id        = m.shop_id
LEFT JOIN tblDefMemberTypes mt ON mt.member_type_id = m.member_type_id
WHERE m.shop_id = @shopId
  AND isnull(m.status, 'Activate') = 'Activate'
  AND (m.expiry_date IS NULL OR m.expiry_date >= GETDATE())";

            const string searchClause =
                " AND (m.member_name LIKE @q" +
                "   OR m.alternate_card_no LIKE @q" +
                "   OR CONVERT(varchar, m.member_no) LIKE @q)";

            const string deltaClause =
                " AND (m.EnteredDate >= @since OR m.EditedDate >= @since)";

            bool hasSearch = !string.IsNullOrWhiteSpace(q);
            bool hasDelta  = !string.IsNullOrEmpty(since) &&
                             DateTime.TryParse(since, out _);

            string fullSql = sql
                + (hasSearch ? searchClause : "")
                + (hasDelta  ? deltaClause  : "");

            // Search without date filter: limit results; delta/full load: no limit
            if (hasSearch && !hasDelta)
                fullSql += " ORDER BY m.member_name OFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(fullSql, con);
                cmd.Parameters.AddWithValue("@shopId", shopId);

                if (hasSearch)
                    cmd.Parameters.AddWithValue("@q", "%" + q + "%");

                if (hasDelta)
                {
                    DateTime.TryParse(since, out DateTime sinceDate);
                    cmd.Parameters.AddWithValue("@since", sinceDate);
                }

                var list = new List<Dictionary<string, object>>();
                using (var dt = new DataTable())
                {
                    using (var adapter = new SqlDataAdapter(cmd))
                        adapter.Fill(dt);

                    foreach (DataRow row in dt.Rows)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (DataColumn col in dt.Columns)
                            dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                        list.Add(dict);
                    }
                }
                return list;
            }
        }
    }
}
