using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SchoolInventoryManagement.DAL.Context;

namespace SchoolInventoryManagement.Web.Helpers
{
    // What Purchasing bought for a requisition, line by line from its
    // purchase orders: shown on Bulk register, Add stock and New supply when
    // they are opened for that requisition, so Property can fill them in
    // from what actually arrived (_BoughtItems).
    public class BoughtLine
    {
        public int NewItemRequestItemID { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public string? Unit { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount => Quantity * UnitPrice;
        public int PurchaseOrderID { get; set; }
        public string PONumber { get; set; } = null!;
        public string SupplierName { get; set; } = null!;
        public DateTime OrderDate { get; set; }
    }

    public static class BoughtItems
    {
        public static Task<List<BoughtLine>> ForRequisitionAsync(ApplicationDbContext context, int requestId) =>
            context.PurchaseOrderLines.AsNoTracking()
                .Where(l => l.Order.NewItemRequestID == requestId && l.Order.Status != SchoolInventoryManagement.DAL.Entities.PurchaseOrderStatus.Cancelled)
                .OrderBy(l => l.Order.OrderDate).ThenBy(l => l.PurchaseOrderLineID)
                .Select(l => new BoughtLine
                {
                    NewItemRequestItemID = l.NewItemRequestItemID,
                    // As written on the purchase order (Scripts/035), else the requisition's.
                    Name = l.ItemName ?? l.Item.ItemName,
                    Description = l.Description ?? l.Item.Description,
                    Unit = l.Item.Unit,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    PurchaseOrderID = l.PurchaseOrderID,
                    PONumber = l.Order.PONumber,
                    SupplierName = l.Order.SupplierName,
                    OrderDate = l.Order.OrderDate
                })
                .ToListAsync();
    }
}
