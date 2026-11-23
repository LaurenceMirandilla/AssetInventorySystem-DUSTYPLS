using System.Collections.Generic;
using System.Threading.Tasks;
using SchoolInventoryManagement.BLL.DTOs;

namespace SchoolInventoryManagement.BLL.Interfaces
{
    // Purchase orders for what requisitions are missing (Scripts/031).
    public interface IPurchasingService
    {
        // The requisition's lines still to put on an order.
        Task<PurchaseOrderFormDTO> GetOrderFormAsync(int requestId, int actingUserId);

        // Saves the order (PO-yyyy-nnnn) as a draft (Scripts/037). Refused
        // when it would take the department over its budget for the year.
        Task<PurchaseOrderDTO> CreateOrderAsync(CreatePurchaseOrderDTO dto, int actingUserId);

        // A draft to edit: the form, with what is on the order now in Current.
        Task<PurchaseOrderFormDTO> GetEditFormAsync(int purchaseOrderId, int actingUserId);

        // Saves the edited draft (dto.PurchaseOrderID). Lines at 0 come off it.
        Task<PurchaseOrderDTO> UpdateOrderAsync(CreatePurchaseOrderDTO dto, int actingUserId);

        // Prints a draft: locked from then on. When everything the
        // requisition has to buy is on printed orders, it waits for the
        // supplier (returns true).
        Task<bool> PrintOrderAsync(int purchaseOrderId, int actingUserId);

        // Cancels a draft: kept, marked Cancelled; its items are to buy
        // again. Returns the requisition it was for.
        Task<int> CancelOrderAsync(int purchaseOrderId, string? reason, int actingUserId);

        // Receipts (photos or PDFs) on an order.
        Task AddAttachmentAsync(int purchaseOrderId, string fileUrl, string fileName, int actingUserId);
        Task<string> RemoveAttachmentAsync(int attachmentId, int actingUserId);

        Task<PurchaseOrderDTO> GetOrderAsync(int purchaseOrderId, int actingUserId);

        // Every purchase order, newest first.
        Task<List<PurchaseOrderListDTO>> GetOrdersAsync(int actingUserId);

        // Suppliers (Scripts/033): the list with how much was ordered from
        // each, one supplier with its orders and prices, and adding or
        // editing one. Returns the supplier's id.
        Task<List<SupplierDTO>> GetSuppliersAsync(int actingUserId);
        Task<SupplierDetailDTO> GetSupplierAsync(int supplierId, int actingUserId);
        Task<int> SaveSupplierAsync(SaveSupplierDTO dto, int actingUserId);
    }
}
