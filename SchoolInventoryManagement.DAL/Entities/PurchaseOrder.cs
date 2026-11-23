using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SchoolInventoryManagement.DAL.Entities
{
    // A purchase order: what the Purchasing Officer brings to a supplier to
    // buy the missing items of one requisition (Scripts/031). A requisition
    // can have more than one (two suppliers, or a second purchase when
    // Property finds something short).
    public class PurchaseOrder
    {
        [Key]
        public int PurchaseOrderID { get; set; }

        // PO-2026-0001: the year and a running number.
        [Required]
        [MaxLength(20)]
        public string PONumber { get; set; } = null!;

        public int NewItemRequestID { get; set; }

        // The supplier on the list (Scripts/033); the name, address and
        // contact below are what the order was printed with.
        public int? SupplierID { get; set; }

        [Required]
        [MaxLength(150)]
        public string SupplierName { get; set; } = null!;

        [MaxLength(250)]
        public string? SupplierAddress { get; set; }

        [MaxLength(100)]
        public string? SupplierContact { get; set; }

        // The date on the order (a calendar date, no time).
        public DateTime OrderDate { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }

        // Shipping terms and costs on the printed order (Scripts/034), all
        // optional. Tax, shipping and other are added to the items' subtotal.
        [MaxLength(100)]
        public string? ShipVia { get; set; }
        [MaxLength(100)]
        public string? Fob { get; set; }
        [MaxLength(150)]
        public string? ShippingTerms { get; set; }
        [Column(TypeName = "decimal(12,2)")]
        public decimal Tax { get; set; }
        [Column(TypeName = "decimal(12,2)")]
        public decimal ShippingCost { get; set; }
        [Column(TypeName = "decimal(12,2)")]
        public decimal OtherCost { get; set; }

        public int CreatedByUserID { get; set; }
        public DateTime CreatedAt { get; set; }

        // Draft until the Purchasing Officer prints it (Scripts/037).
        // Printing locks it: no more editing or cancelling, and only then
        // does the requisition wait for the supplier. A draft can be
        // cancelled; it is kept, marked Cancelled, and counts for nothing.
        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = PurchaseOrderStatus.Draft;
        public DateTime? PrintedAt { get; set; }
        public int? PrintedByUserID { get; set; }
        public DateTime? CancelledAt { get; set; }
        public int? CancelledByUserID { get; set; }
        [MaxLength(500)]
        public string? CancelReason { get; set; }

        [ForeignKey(nameof(PrintedByUserID))]
        public User? PrintedByUser { get; set; }

        [ForeignKey(nameof(CancelledByUserID))]
        public User? CancelledByUser { get; set; }

        // Receipts from the supplier: photos or PDFs.
        public ICollection<PurchaseOrderAttachment> Attachments { get; set; } = new List<PurchaseOrderAttachment>();

        [ForeignKey(nameof(NewItemRequestID))]
        public NewItemRequest Request { get; set; } = null!;

        [ForeignKey(nameof(CreatedByUserID))]
        public User CreatedByUser { get; set; } = null!;

        [ForeignKey(nameof(SupplierID))]
        public Supplier? Supplier { get; set; }

        public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
    }

    public static class PurchaseOrderStatus
    {
        public const string Draft = "Draft";
        public const string Printed = "Printed";
        public const string Cancelled = "Cancelled";
    }

    // A receipt (photo or PDF) attached to a purchase order (Scripts/037).
    public class PurchaseOrderAttachment
    {
        [Key]
        public int AttachmentID { get; set; }

        public int PurchaseOrderID { get; set; }

        [Required]
        [MaxLength(300)]
        public string FileUrl { get; set; } = null!;

        // The name it had on the uploader's device.
        [Required]
        [MaxLength(200)]
        public string FileName { get; set; } = null!;

        public int UploadedByUserID { get; set; }
        public DateTime UploadedAt { get; set; }

        [ForeignKey(nameof(PurchaseOrderID))]
        public PurchaseOrder Order { get; set; } = null!;

        [ForeignKey(nameof(UploadedByUserID))]
        public User UploadedByUser { get; set; } = null!;
    }

    // One item on a purchase order: a line of the requisition, how many are
    // bought and at what price.
    public class PurchaseOrderLine
    {
        [Key]
        public int PurchaseOrderLineID { get; set; }

        public int PurchaseOrderID { get; set; }
        public int NewItemRequestItemID { get; set; }

        // What the supplier is asked for, as written on the order form
        // (Scripts/035); null = the requisition's own name / description.
        [MaxLength(150)]
        public string? ItemName { get; set; }
        [MaxLength(500)]
        public string? Description { get; set; }

        public int Quantity { get; set; }

        [Column(TypeName = "decimal(12,2)")]
        public decimal UnitPrice { get; set; }

        [ForeignKey(nameof(PurchaseOrderID))]
        public PurchaseOrder Order { get; set; } = null!;

        [ForeignKey(nameof(NewItemRequestItemID))]
        public NewItemRequestItem Item { get; set; } = null!;
    }

    // A unit the Property Officer picked from Property's stock to send out
    // on a requisition (Scripts/031). It is held for the requisition
    // (In transit) until the requester confirms receiving it, then it is
    // their department's. Units bought for a requisition are linked by
    // Asset.NewItemRequestID instead.
    public class NewItemRequestUnit
    {
        public int NewItemRequestID { get; set; }
        public int AssetID { get; set; }

        // The line it fills.
        public int NewItemRequestItemID { get; set; }

        public int PickedByUserID { get; set; }
        public DateTime PickedAt { get; set; }

        [ForeignKey(nameof(NewItemRequestID))]
        public NewItemRequest Request { get; set; } = null!;

        [ForeignKey(nameof(AssetID))]
        public Asset Asset { get; set; } = null!;

        [ForeignKey(nameof(NewItemRequestItemID))]
        public NewItemRequestItem Item { get; set; } = null!;

        [ForeignKey(nameof(PickedByUserID))]
        public User PickedByUser { get; set; } = null!;
    }
}
