using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SchoolInventoryManagement.BLL.DTOs;
using SchoolInventoryManagement.BLL.Interfaces;
using SchoolInventoryManagement.DAL.Constants;
using SchoolInventoryManagement.DAL.Context;
using SchoolInventoryManagement.DAL.Entities;
using SchoolInventoryManagement.DAL.Entities.Enums;

namespace SchoolInventoryManagement.BLL.Services
{
    // The Purchasing Officer's purchase orders (Scripts/031): what a
    // requisition is missing, with the supplier and prices, to print and
    // bring to the supplier. A requisition can have several (two suppliers,
    // or a second purchase when Property finds something short).
    //
    // An order is a draft until it is printed (Scripts/037). A draft can be
    // edited or cancelled; printing locks it for good. Once everything the
    // requisition has to buy is on printed orders, it waits for the
    // supplier and Property receives it. An order cannot take its
    // department over its budget for the year.
    public class PurchasingService : IPurchasingService
    {
        private readonly ApplicationDbContext _context;
        private readonly INewItemRequestService _requests;

        private static readonly string[] BuyerRoles = { RoleNames.PurchasingOfficer, RoleNames.Administrator };
        private static readonly string[] ReaderRoles =
            { RoleNames.PurchasingOfficer, RoleNames.AssetOfficer, RoleNames.Principal, RoleNames.Administrator };

        public PurchasingService(ApplicationDbContext context, INewItemRequestService requests)
        {
            _context = context;
            _requests = requests;
        }

        private async Task<User> EnsureRoleAsync(int userId, string[] roles, string message)
        {
            var user = await PermissionHelper.GetUserOrThrowAsync(_context, userId);
            PermissionHelper.EnsureIsActive(user);
            if (!roles.Contains(user.Role.RoleName))
                throw new UnauthorizedAccessException(message);
            return user;
        }

        // A requisition waiting to be bought, with its lines.
        private async Task<NewItemRequest> LoadToBuyAsync(int requestId, int actingUserId)
        {
            var request = await _context.NewItemRequests
                .Include(n => n.Items)
                .Include(n => n.Department).ThenInclude(d => d.Branch)
                .Include(n => n.RequestedByUser).ThenInclude(u => u.Role)
                .Include(n => n.DeliverToLocation)
                .FirstOrDefaultAsync(n => n.NewItemRequestID == requestId)
                ?? throw new KeyNotFoundException("Requisition not found.");
            if (request.RequestedByUserID == actingUserId)
                throw new UnauthorizedAccessException("You cannot buy for your own requisition. Another Purchasing Officer, or an Administrator, does.");
            if (request.RequestStatus != NewItemStatus.Procuring)
                throw new InvalidOperationException($"Requisition #{requestId} is not waiting to be bought.");
            return request;
        }

        // How many of each line are on orders already (drafts and printed;
        // cancelled ones count for nothing). exceptOrderId leaves one order
        // out: the one being edited.
        private Task<Dictionary<int, int>> OrderedAsync(IEnumerable<int> lineIds, int? exceptOrderId = null, bool printedOnly = false)
        {
            var ids = lineIds.ToList();
            return _context.PurchaseOrderLines
                .Where(l => ids.Contains(l.NewItemRequestItemID)
                            && l.Order.Status != PurchaseOrderStatus.Cancelled
                            && (!printedOnly || l.Order.Status == PurchaseOrderStatus.Printed)
                            && (exceptOrderId == null || l.PurchaseOrderID != exceptOrderId))
                .GroupBy(l => l.NewItemRequestItemID)
                .Select(g => new { LineId = g.Key, Quantity = g.Sum(l => l.Quantity) })
                .ToDictionaryAsync(x => x.LineId, x => x.Quantity);
        }

        // The order form: the whole requisition, the lines that can go on the
        // order (an edited order's own lines too, up to what its other orders
        // leave), and the department's budget for the year, the order being
        // edited left out (Scripts/037).
        private async Task<PurchaseOrderFormDTO> BuildFormAsync(NewItemRequest request, PurchaseOrder? order)
        {
            var itemIds = request.Items.Select(i => i.NewItemRequestItemID).ToList();
            var elsewhere = await OrderedAsync(itemIds, order?.PurchaseOrderID);
            var onThis = (order?.Lines ?? new List<PurchaseOrderLine>())
                .GroupBy(l => l.NewItemRequestItemID)
                .ToDictionary(g => g.Key, g => g.First());

            var lines = request.Items
                .Where(i => i.ToBuy > 0)
                .OrderBy(i => i.NewItemRequestItemID)
                .Select(i => new PurchaseOrderFormLineDTO
                {
                    NewItemRequestItemID = i.NewItemRequestItemID,
                    ItemName = i.ItemName,
                    Description = i.Description,
                    Unit = i.Unit,
                    Asked = i.Quantity,
                    ToBuy = i.ToBuy,
                    Ordered = elsewhere.GetValueOrDefault(i.NewItemRequestItemID)
                })
                .Where(l => l.Left > 0 || onThis.ContainsKey(l.NewItemRequestItemID))
                .ToList();
            if (lines.Count == 0)
                throw new InvalidOperationException("Everything to buy on this requisition is already on a purchase order.");

            // "On orders" on the requisition's own table counts every order.
            var allOrdered = order is null ? elsewhere : await OrderedAsync(itemIds);

            var form = new PurchaseOrderFormDTO
            {
                NewItemRequestID = request.NewItemRequestID,
                PurchaseOrderID = order?.PurchaseOrderID,
                PONumber = order?.PONumber,
                RowVersion = request.RowVersion,
                DepartmentID = request.DepartmentID,
                RequestedByUserName = $"{request.RequestedByUser.FirstName} {request.RequestedByUser.LastName}",
                RequesterRole = request.RequestedByUser.Role?.RoleName,
                DepartmentName = request.Department.DepartmentName,
                BranchName = request.Department.Branch?.BranchName,
                ForStock = request.Department.IsProperty,
                DeliverTo = request.DeliverToLocation?.LocationName,
                RequestedOn = request.RequestDate,
                NeededBy = request.NeededBy,
                Reason = request.Reason,
                ItemKind = request.ItemKind,
                Lines = lines,
                // Everything on the requisition, the in-stock lines too.
                AllItems = request.Items.OrderBy(i => i.NewItemRequestItemID).Select(i => new PurchaseOrderFormLineDTO
                {
                    NewItemRequestItemID = i.NewItemRequestItemID,
                    ItemName = i.ItemName,
                    Description = i.Description,
                    Unit = i.Unit,
                    Asked = i.Quantity,
                    ToBuy = i.ToBuy,
                    Ordered = allOrdered.GetValueOrDefault(i.NewItemRequestItemID)
                }).ToList(),
                Suppliers = await SupplierListAsync(),
                Budget = await DepartmentBudgets.ForDepartmentAsync(_context, request.DepartmentID,
                    (order?.OrderDate ?? PhilippineTime.Today).Year, order?.PurchaseOrderID)
            };

            if (order is not null)
            {
                var items = request.Items.ToDictionary(i => i.NewItemRequestItemID);
                form.Current = new CreatePurchaseOrderDTO
                {
                    NewItemRequestID = request.NewItemRequestID,
                    PurchaseOrderID = order.PurchaseOrderID,
                    SupplierName = order.SupplierName,
                    SupplierAddress = order.SupplierAddress,
                    SupplierContact = order.SupplierContact,
                    OrderDate = order.OrderDate,
                    Notes = order.Notes,
                    Lines = onThis.Values.Select(l => new CreatePurchaseOrderLineDTO
                    {
                        NewItemRequestItemID = l.NewItemRequestItemID,
                        ItemName = l.ItemName ?? (items.TryGetValue(l.NewItemRequestItemID, out var i) ? i.ItemName : null),
                        Description = l.Description ?? (items.TryGetValue(l.NewItemRequestItemID, out var d) ? d.Description : null),
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice
                    }).ToList()
                };
            }
            return form;
        }

        public async Task<PurchaseOrderFormDTO> GetOrderFormAsync(int requestId, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators make purchase orders.");
            var request = await LoadToBuyAsync(requestId, actingUserId);
            return await BuildFormAsync(request, null);
        }

        // An order that can still be changed or cancelled: a draft (not
        // printed), for a requisition still being bought, and not for the
        // signed-in user's own requisition.
        private async Task<PurchaseOrder> LoadDraftAsync(int purchaseOrderId, int actingUserId, string doing)
        {
            var order = await _context.PurchaseOrders
                .Include(o => o.Lines)
                .Include(o => o.Request).ThenInclude(n => n.Items)
                .Include(o => o.Request).ThenInclude(n => n.Department).ThenInclude(d => d.Branch)
                .Include(o => o.Request).ThenInclude(n => n.RequestedByUser).ThenInclude(u => u.Role)
                .Include(o => o.Request).ThenInclude(n => n.DeliverToLocation)
                .FirstOrDefaultAsync(o => o.PurchaseOrderID == purchaseOrderId)
                ?? throw new KeyNotFoundException("Purchase order not found.");
            if (order.Request.RequestedByUserID == actingUserId)
                throw new UnauthorizedAccessException($"You cannot {doing} an order for your own requisition. Another Purchasing Officer, or an Administrator, does.");
            if (order.Status == PurchaseOrderStatus.Cancelled)
                throw new InvalidOperationException($"{order.PONumber} was cancelled.");
            if (order.Status == PurchaseOrderStatus.Printed)
                throw new InvalidOperationException($"{order.PONumber} was printed, so it can no longer be changed or cancelled.");
            if (order.Request.RequestStatus is NewItemStatus.Rejected or NewItemStatus.Cancelled)
                throw new InvalidOperationException(
                    $"Requisition #{order.NewItemRequestID} was {(order.Request.RequestStatus == NewItemStatus.Rejected ? "rejected" : "cancelled")}, so {order.PONumber} can no longer be changed.");
            if (order.Request.RequestStatus != NewItemStatus.Procuring)
                throw new InvalidOperationException($"Requisition #{order.NewItemRequestID} is no longer being bought, so {order.PONumber} can no longer be changed.");
            return order;
        }

        public async Task<PurchaseOrderFormDTO> GetEditFormAsync(int purchaseOrderId, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators change purchase orders.");
            var order = await LoadDraftAsync(purchaseOrderId, actingUserId, "change");
            return await BuildFormAsync(order.Request, order);
        }

        private static string? Clean(string? text, int max, string what)
        {
            var t = text?.Trim();
            if (string.IsNullOrEmpty(t))
                return null;
            if (t.Length > max)
                throw new ArgumentException($"The {what} can be at most {max} characters.");
            return t;
        }

        // The order's head: supplier, date and notes, checked.
        private sealed class OrderHead
        {
            public string Supplier = "";
            public string? Address, Contact, Notes;
            public DateTime OrderDate;
        }

        private static OrderHead ReadHead(CreatePurchaseOrderDTO dto) => new OrderHead
        {
            Supplier = Clean(dto.SupplierName, 150, "supplier name") ?? throw new ArgumentException("Name the supplier."),
            Address = Clean(dto.SupplierAddress, 250, "supplier address"),
            Contact = Clean(dto.SupplierContact, 100, "supplier contact"),
            Notes = Clean(dto.Notes, 500, "note"),
            OrderDate = dto.OrderDate == default ? PhilippineTime.Today : dto.OrderDate.Date
        };

        // The lines put on the order (quantity above 0), checked against what
        // is left to buy once the requisition's other orders are counted.
        private static List<CreatePurchaseOrderLineDTO> ReadLines(CreatePurchaseOrderDTO dto,
            Dictionary<int, NewItemRequestItem> items, Dictionary<int, int> orderedElsewhere, bool editing)
        {
            var lines = (dto.Lines ?? new List<CreatePurchaseOrderLineDTO>()).Where(l => l.Quantity > 0).ToList();
            if (lines.Count == 0)
                throw new ArgumentException(editing
                    ? "Keep at least one item on the order. To drop the whole order, cancel it instead."
                    : "Put at least one item on the order, with how many.");
            if (lines.GroupBy(l => l.NewItemRequestItemID).Any(g => g.Count() > 1))
                throw new ArgumentException("An item is on the order twice.");
            foreach (var line in lines)
            {
                if (!items.TryGetValue(line.NewItemRequestItemID, out var item))
                    throw new ArgumentException("An item on the order is not on this requisition.");
                var left = item.ToBuy - orderedElsewhere.GetValueOrDefault(item.NewItemRequestItemID);
                if (line.Quantity > left)
                    throw new ArgumentException($"{item.ItemName}: only {Math.Max(0, left)} still to buy.");
                if (line.UnitPrice < 0 || line.UnitPrice > 99_999_999.99m)
                    throw new ArgumentException($"{item.ItemName}: enter a unit price from 0.");
                line.ItemName = Clean(line.ItemName, 150, "item name");
                line.Description = Clean(line.Description, 500, "item description");
            }
            return lines;
        }

        // No order may take the department over its budget for the year
        // (Scripts/037). The order being edited is left out of what was
        // already spent.
        private async Task EnsureWithinBudgetAsync(int departmentId, DateTime orderDate, List<CreatePurchaseOrderLineDTO> lines, int? exceptOrderId)
        {
            var total = lines.Sum(l => l.Quantity * Math.Round(l.UnitPrice, 2));
            var budget = await DepartmentBudgets.ForDepartmentAsync(_context, departmentId, orderDate.Year, exceptOrderId);
            if (budget.Spent + total > budget.AnnualBudget)
            {
                var left = Math.Max(0, budget.AnnualBudget - budget.Spent);
                throw new ArgumentException(
                    $"Over budget: this order is ₱{total:N2}, but the {budget.DepartmentName} has only ₱{left:N2} left of its " +
                    $"₱{budget.AnnualBudget:N2} budget for {budget.Year} (₱{budget.Spent:N2} already on purchase orders). " +
                    "Lower the quantities or prices, or ask the Principal or an Administrator to raise the budget on the Budgets page.");
            }
        }

        // The supplier on the list, added to it when new; its address and
        // contact fill in what was left blank, and new ones are kept.
        private async Task<Supplier> ListedSupplierAsync(OrderHead head)
        {
            var listed = await _context.Suppliers.FirstOrDefaultAsync(s => s.Name == head.Supplier);
            if (listed is null)
            {
                listed = new Supplier { Name = head.Supplier, Address = head.Address, Contact = head.Contact, CreatedAt = PhilippineTime.Now };
                _context.Suppliers.Add(listed);
            }
            else
            {
                head.Address ??= listed.Address;
                head.Contact ??= listed.Contact;
                listed.Address = head.Address;
                listed.Contact = head.Contact;
                head.Supplier = listed.Name;
            }
            return listed;
        }

        // A line keeps its own name and description only when they say
        // something other than the requisition.
        private static void FillLine(PurchaseOrderLine line, CreatePurchaseOrderLineDTO l, NewItemRequestItem item)
        {
            line.ItemName = l.ItemName is null || l.ItemName == item.ItemName ? null : l.ItemName;
            line.Description = l.Description is null || l.Description == item.Description ? null : l.Description;
            line.Quantity = l.Quantity;
            line.UnitPrice = Math.Round(l.UnitPrice, 2);
        }

        // A consumable's price is what Purchasing paid for it on the last
        // printed order: Reports and the supply's page read it from here.
        private async Task SetSupplyPricesAsync(PurchaseOrder order)
        {
            var prices = order.Lines
                .Select(l => new { l.UnitPrice, Item = order.Request.Items.FirstOrDefault(i => i.NewItemRequestItemID == l.NewItemRequestItemID) })
                .Where(x => x.Item?.SupplyID != null)
                .GroupBy(x => x.Item!.SupplyID!.Value)
                .ToDictionary(g => g.Key, g => g.Last().UnitPrice);
            if (prices.Count == 0)
                return;
            foreach (var supply in await _context.Supplies.Where(s => prices.Keys.Contains(s.SupplyID)).ToListAsync())
                supply.UnitPrice = prices[supply.SupplyID];
        }

        public async Task<PurchaseOrderDTO> CreateOrderAsync(CreatePurchaseOrderDTO dto, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators make purchase orders.");
            var request = await LoadToBuyAsync(dto.NewItemRequestID, actingUserId);
            var head = ReadHead(dto);

            var items = request.Items.ToDictionary(i => i.NewItemRequestItemID);
            var ordered = await OrderedAsync(items.Keys);
            var lines = ReadLines(dto, items, ordered, editing: false);
            await EnsureWithinBudgetAsync(request.DepartmentID, head.OrderDate, lines, null);

            // PO-2026-0001: the year and a running number.
            var prefix = $"PO-{head.OrderDate.Year}-";
            var numbers = await _context.PurchaseOrders
                .Where(o => o.PONumber.StartsWith(prefix))
                .Select(o => o.PONumber)
                .ToListAsync();
            var next = numbers.Select(n => int.TryParse(n[prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max() + 1;

            var listed = await ListedSupplierAsync(head);

            // A draft: nothing moves on until it is printed.
            var order = new PurchaseOrder
            {
                Supplier = listed,
                PONumber = $"{prefix}{next:0000}",
                NewItemRequestID = request.NewItemRequestID,
                SupplierName = head.Supplier,
                SupplierAddress = head.Address,
                SupplierContact = head.Contact,
                OrderDate = head.OrderDate,
                Notes = head.Notes,
                Status = PurchaseOrderStatus.Draft,
                CreatedByUserID = actingUserId,
                CreatedAt = PhilippineTime.Now
            };
            foreach (var l in lines)
            {
                var line = new PurchaseOrderLine { NewItemRequestItemID = l.NewItemRequestItemID };
                FillLine(line, l, items[l.NewItemRequestItemID]);
                order.Lines.Add(line);
            }
            _context.PurchaseOrders.Add(order);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException) when (_context.Entry(order).State == EntityState.Added)
            {
                // Two orders numbered at the same moment.
                throw new ConcurrencyConflictException("Another purchase order was saved at the same moment. Please try again.");
            }

            return await GetOrderAsync(order.PurchaseOrderID, actingUserId);
        }

        public async Task<PurchaseOrderDTO> UpdateOrderAsync(CreatePurchaseOrderDTO dto, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators change purchase orders.");
            var order = await LoadDraftAsync(dto.PurchaseOrderID ?? 0, actingUserId, "change");
            var request = order.Request;
            var head = ReadHead(dto);

            var items = request.Items.ToDictionary(i => i.NewItemRequestItemID);
            var elsewhere = await OrderedAsync(items.Keys, order.PurchaseOrderID);
            var lines = ReadLines(dto, items, elsewhere, editing: true);
            await EnsureWithinBudgetAsync(request.DepartmentID, head.OrderDate, lines, order.PurchaseOrderID);

            order.Supplier = await ListedSupplierAsync(head);
            order.SupplierName = head.Supplier;
            order.SupplierAddress = head.Address;
            order.SupplierContact = head.Contact;
            order.OrderDate = head.OrderDate;
            order.Notes = head.Notes;

            // Lines changed, added, or taken off (set to 0).
            var keep = lines.Select(l => l.NewItemRequestItemID).ToHashSet();
            foreach (var gone in order.Lines.Where(l => !keep.Contains(l.NewItemRequestItemID)).ToList())
                _context.PurchaseOrderLines.Remove(gone);
            foreach (var l in lines)
            {
                var line = order.Lines.FirstOrDefault(x => x.NewItemRequestItemID == l.NewItemRequestItemID);
                if (line is null)
                {
                    line = new PurchaseOrderLine { NewItemRequestItemID = l.NewItemRequestItemID };
                    order.Lines.Add(line);
                }
                FillLine(line, l, items[l.NewItemRequestItemID]);
            }
            await _context.SaveChangesAsync();

            return await GetOrderAsync(order.PurchaseOrderID, actingUserId);
        }

        // Printing a draft (Scripts/037): it is locked from now on, its prices
        // become the consumables' prices, and once everything the requisition
        // has to buy is on printed orders, it waits for the supplier.
        // Printing a printed order again changes nothing. Returns whether
        // the requisition moved on.
        public async Task<bool> PrintOrderAsync(int purchaseOrderId, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators print purchase orders.");
            var order = await _context.PurchaseOrders
                .Include(o => o.Lines)
                .Include(o => o.Request).ThenInclude(n => n.Items)
                .FirstOrDefaultAsync(o => o.PurchaseOrderID == purchaseOrderId)
                ?? throw new KeyNotFoundException("Purchase order not found.");
            if (order.Status == PurchaseOrderStatus.Printed)
                return false;
            if (order.Status == PurchaseOrderStatus.Cancelled)
                throw new InvalidOperationException($"{order.PONumber} was cancelled, so it cannot be printed.");
            if (order.Request.RequestedByUserID == actingUserId)
                throw new UnauthorizedAccessException("You cannot print an order for your own requisition. Another Purchasing Officer, or an Administrator, does.");
            if (order.Request.RequestStatus != NewItemStatus.Procuring)
                throw new InvalidOperationException($"Requisition #{order.NewItemRequestID} is no longer being bought, so {order.PONumber} cannot be printed.");

            order.Status = PurchaseOrderStatus.Printed;
            order.PrintedAt = PhilippineTime.Now;
            order.PrintedByUserID = actingUserId;
            await SetSupplyPricesAsync(order);
            await _context.SaveChangesAsync();

            // Everything to buy on printed orders: on to the supplier.
            var request = order.Request;
            var printed = await OrderedAsync(request.Items.Select(i => i.NewItemRequestItemID), printedOnly: true);
            var stillLeft = request.Items.Sum(i => Math.Max(0, i.ToBuy - printed.GetValueOrDefault(i.NewItemRequestItemID)));
            if (stillLeft > 0)
                return false;
            var suppliers = await _context.PurchaseOrders
                .Where(o => o.NewItemRequestID == request.NewItemRequestID && o.Status == PurchaseOrderStatus.Printed)
                .OrderBy(o => o.PurchaseOrderID)
                .Select(o => o.PONumber + " · " + o.SupplierName)
                .ToListAsync();
            await _requests.AdvanceRequestAsync(request.NewItemRequestID, request.RowVersion, actingUserId, string.Join(", ", suppliers));
            return true;
        }

        // Cancelling a draft (Scripts/037): kept on the list, marked
        // Cancelled, and counted for nothing; its items are to buy again.
        public async Task<int> CancelOrderAsync(int purchaseOrderId, string? reason, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators cancel purchase orders.");
            var order = await LoadDraftAsync(purchaseOrderId, actingUserId, "cancel");
            order.Status = PurchaseOrderStatus.Cancelled;
            order.CancelledAt = PhilippineTime.Now;
            order.CancelledByUserID = actingUserId;
            order.CancelReason = Clean(reason, 500, "reason");
            await _context.SaveChangesAsync();
            return order.NewItemRequestID;
        }

        // Receipts (Scripts/037): a photo or PDF from the supplier, on any
        // order that was not cancelled.
        public async Task AddAttachmentAsync(int purchaseOrderId, string fileUrl, string fileName, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators attach receipts.");
            var order = await _context.PurchaseOrders.FirstOrDefaultAsync(o => o.PurchaseOrderID == purchaseOrderId)
                ?? throw new KeyNotFoundException("Purchase order not found.");
            if (order.Status == PurchaseOrderStatus.Cancelled)
                throw new InvalidOperationException($"{order.PONumber} was cancelled.");
            var name = (fileName ?? "").Trim();
            if (name.Length == 0) name = "receipt";
            if (name.Length > 200) name = name[^200..];
            _context.PurchaseOrderAttachments.Add(new PurchaseOrderAttachment
            {
                PurchaseOrderID = purchaseOrderId,
                FileUrl = fileUrl,
                FileName = name,
                UploadedByUserID = actingUserId,
                UploadedAt = PhilippineTime.Now
            });
            await _context.SaveChangesAsync();
        }

        // Takes a receipt off. Returns its file, for the caller to delete.
        public async Task<string> RemoveAttachmentAsync(int attachmentId, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators remove receipts.");
            var file = await _context.PurchaseOrderAttachments.FirstOrDefaultAsync(a => a.AttachmentID == attachmentId)
                ?? throw new KeyNotFoundException("Receipt not found.");
            _context.PurchaseOrderAttachments.Remove(file);
            await _context.SaveChangesAsync();
            return file.FileUrl;
        }

        public async Task<PurchaseOrderDTO> GetOrderAsync(int purchaseOrderId, int actingUserId)
        {
            var actor = await EnsureRoleAsync(actingUserId, ReaderRoles,
                "Only Purchasing, Property, the Principal and Administrators see purchase orders.");

            var o = await _context.PurchaseOrders.AsNoTracking()
                .Where(x => x.PurchaseOrderID == purchaseOrderId)
                .Select(x => new
                {
                    x.PurchaseOrderID, x.PONumber, x.OrderDate, x.SupplierName, x.SupplierAddress, x.SupplierContact,
                    x.Notes, x.CreatedAt, Extras = x.Tax + x.ShippingCost + x.OtherCost,
                    x.Status, x.PrintedAt, x.CancelledAt, x.CancelReason,
                    PrintedBy = x.PrintedByUser != null ? x.PrintedByUser.FirstName + " " + x.PrintedByUser.LastName : null,
                    CancelledBy = x.CancelledByUser != null ? x.CancelledByUser.FirstName + " " + x.CancelledByUser.LastName : null,
                    BranchAddress = x.Request.Department.Branch.Address,
                    BranchContact = x.Request.Department.Branch.ContactInfo,
                    PreparedBy = x.CreatedByUser.FirstName + " " + x.CreatedByUser.LastName,
                    PreparedRole = x.CreatedByUser.Role.RoleName,
                    x.NewItemRequestID,
                    x.Request.RequestStatus,
                    x.Request.RequestedByUserID,
                    x.Request.DepartmentID,
                    Requester = x.Request.RequestedByUser.FirstName + " " + x.Request.RequestedByUser.LastName,
                    RequesterRole = x.Request.RequestedByUser.Role.RoleName,
                    Department = x.Request.Department.DepartmentName,
                    ForStock = x.Request.Department.IsProperty,
                    x.Request.NeededBy,
                    x.Request.RequestDate,
                    x.Request.Reason,
                    Room = x.Request.DeliverToLocation != null ? x.Request.DeliverToLocation.LocationName : null,
                    BranchID = x.Request.Department.BranchID,
                    Branch = x.Request.Department.Branch.BranchName,
                    Lines = x.Lines.OrderBy(l => l.PurchaseOrderLineID).Select(l => new PurchaseOrderLineDTO
                    {
                        ItemName = l.ItemName ?? l.Item.ItemName,
                        Description = l.Description ?? l.Item.Description,
                        Unit = l.Item.Unit,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice
                    }).ToList(),
                    Attachments = x.Attachments.OrderBy(a => a.AttachmentID).Select(a => new PurchaseOrderAttachmentDTO
                    {
                        AttachmentID = a.AttachmentID,
                        FileUrl = a.FileUrl,
                        FileName = a.FileName,
                        UploadedByName = a.UploadedByUser.FirstName + " " + a.UploadedByUser.LastName,
                        UploadedAt = a.UploadedAt
                    }).ToList()
                })
                .FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException("Purchase order not found.");

            // The supplier delivers to the Property Department of the branch.
            var room = await _context.Departments
                .Where(d => d.BranchID == o.BranchID && d.IsProperty)
                .Select(d => d.MainLocation != null ? d.MainLocation.LocationName : null)
                .FirstOrDefaultAsync();

            var buyer = BuyerRoles.Contains(actor.Role.RoleName) && o.RequestedByUserID != actingUserId;
            var draft = o.Status == PurchaseOrderStatus.Draft && o.RequestStatus == NewItemStatus.Procuring;
            return new PurchaseOrderDTO
            {
                PurchaseOrderID = o.PurchaseOrderID,
                PONumber = o.PONumber,
                OrderDate = o.OrderDate,
                SupplierName = o.SupplierName,
                SupplierAddress = o.SupplierAddress,
                SupplierContact = o.SupplierContact,
                Notes = o.Notes,
                PreparedByName = o.PreparedBy,
                PreparedByRole = o.PreparedRole,
                CreatedAt = o.CreatedAt,
                NewItemRequestID = o.NewItemRequestID,
                RequestStatus = o.RequestStatus,
                RequestedByUserName = o.Requester,
                RequesterRole = o.RequesterRole,
                DepartmentName = o.Department,
                ForStock = o.ForStock,
                RequestedOn = o.RequestDate,
                Reason = o.Reason,
                RequesterRoom = o.Room,
                NeededBy = o.NeededBy,
                BranchName = o.Branch,
                BranchAddress = o.BranchAddress,
                BranchContact = o.BranchContact,
                Extras = o.Extras,
                DeliverTo = room is null ? "Property Department" : $"Property Department, {room.Trim()}",
                Lines = o.Lines,
                Status = o.Status,
                PrintedAt = o.PrintedAt,
                PrintedByName = o.PrintedBy,
                CancelledAt = o.CancelledAt,
                CancelledByName = o.CancelledBy,
                CancelReason = o.CancelReason,
                Attachments = o.Attachments,
                CanEdit = buyer && draft,
                CanCancel = buyer && draft,
                CanPrint = buyer && draft,
                CanAttach = BuyerRoles.Contains(actor.Role.RoleName) && o.Status != PurchaseOrderStatus.Cancelled,
                Budget = await DepartmentBudgets.ForDepartmentAsync(_context, o.DepartmentID, o.OrderDate.Year)
            };
        }

        // Every purchase order, newest first, for Purchasing's list.
        public async Task<List<PurchaseOrderListDTO>> GetOrdersAsync(int actingUserId)
        {
            var actor = await EnsureRoleAsync(actingUserId, ReaderRoles,
                "Only Purchasing, Property, the Principal and Administrators see purchase orders.");
            var buyer = BuyerRoles.Contains(actor.Role.RoleName);
            var rows = await _context.PurchaseOrders.AsNoTracking()
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.PurchaseOrderID)
                .Select(o => new
                {
                    o.PurchaseOrderID, o.PONumber, o.OrderDate, o.SupplierName, o.NewItemRequestID,
                    o.Request.RequestStatus,
                    o.Request.RequestedByUserID,
                    o.Status, o.PrintedAt,
                    Attachments = o.Attachments.Count(),
                    Requester = o.Request.RequestedByUser.FirstName + " " + o.Request.RequestedByUser.LastName,
                    Department = o.Request.Department.DepartmentName,
                    Items = o.Lines.OrderBy(l => l.PurchaseOrderLineID).Select(l => new PurchaseOrderLineDTO
                    {
                        ItemName = l.ItemName ?? l.Item.ItemName,
                        Description = l.Description ?? l.Item.Description,
                        Unit = l.Item.Unit,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice
                    }).ToList(),
                    Extra = o.Tax + o.ShippingCost + o.OtherCost,
                    Branch = o.Request.Department.Branch.BranchName,
                    o.CreatedByUserID,
                    PreparedBy = o.CreatedByUser.FirstName + " " + o.CreatedByUser.LastName
                })
                .ToListAsync();
            return rows.Select(o => new PurchaseOrderListDTO
            {
                PurchaseOrderID = o.PurchaseOrderID,
                PONumber = o.PONumber,
                OrderDate = o.OrderDate,
                SupplierName = o.SupplierName,
                NewItemRequestID = o.NewItemRequestID,
                RequestStatus = o.RequestStatus,
                RequestedByUserName = o.Requester,
                DepartmentName = o.Department,
                Items = string.Join(", ", o.Items.Select(i => $"{i.ItemName} ×{i.Quantity}")),
                Total = o.Items.Sum(i => i.Quantity * i.UnitPrice) + o.Extra,
                PreparedByUserID = o.CreatedByUserID,
                PreparedByName = o.PreparedBy,
                BranchName = o.Branch,
                Lines = o.Items,
                Extras = o.Extra,
                Status = o.Status,
                PrintedAt = o.PrintedAt,
                Attachments = o.Attachments,
                CanEdit = buyer && o.Status == PurchaseOrderStatus.Draft && o.RequestStatus == NewItemStatus.Procuring
                          && o.RequestedByUserID != actingUserId
            }).ToList();
        }

        // ---- Suppliers (Scripts/033) --------------------------------------------
        private Task<List<SupplierDTO>> SupplierListAsync() =>
            _context.Suppliers.AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new SupplierDTO
                {
                    SupplierID = s.SupplierID,
                    Name = s.Name,
                    Address = s.Address,
                    Contact = s.Contact,
                    Notes = s.Notes,
                    // Cancelled orders count for nothing (Scripts/037).
                    Orders = s.Orders.Count(o => o.Status != PurchaseOrderStatus.Cancelled),
                    Spent = s.Orders.Where(o => o.Status != PurchaseOrderStatus.Cancelled)
                        .SelectMany(o => o.Lines).Sum(l => (decimal?)(l.Quantity * l.UnitPrice)) ?? 0m,
                    LastOrder = s.Orders.Where(o => o.Status != PurchaseOrderStatus.Cancelled).Max(o => (DateTime?)o.OrderDate)
                })
                .ToListAsync();

        public async Task<List<SupplierDTO>> GetSuppliersAsync(int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, ReaderRoles, "Only Purchasing, Property, the Principal and Administrators see suppliers.");
            return await SupplierListAsync();
        }

        public async Task<SupplierDetailDTO> GetSupplierAsync(int supplierId, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, ReaderRoles, "Only Purchasing, Property, the Principal and Administrators see suppliers.");
            var s = (await SupplierListAsync()).FirstOrDefault(x => x.SupplierID == supplierId)
                ?? throw new KeyNotFoundException("Supplier not found.");
            var orders = await _context.PurchaseOrders.AsNoTracking()
                .Where(o => o.SupplierID == supplierId && o.Status != PurchaseOrderStatus.Cancelled)
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.PurchaseOrderID)
                .Select(o => new SupplierOrderDTO
                {
                    PurchaseOrderID = o.PurchaseOrderID,
                    PONumber = o.PONumber,
                    OrderDate = o.OrderDate,
                    NewItemRequestID = o.NewItemRequestID,
                    Lines = o.Lines.OrderBy(l => l.PurchaseOrderLineID).Select(l => new PurchaseOrderLineDTO
                    {
                        ItemName = l.ItemName ?? l.Item.ItemName,
                        Description = l.Description ?? l.Item.Description,
                        Unit = l.Item.Unit,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice
                    }).ToList()
                })
                .ToListAsync();
            return new SupplierDetailDTO
            {
                SupplierID = s.SupplierID, Name = s.Name, Address = s.Address, Contact = s.Contact, Notes = s.Notes,
                Orders = s.Orders, Spent = s.Spent, LastOrder = s.LastOrder, OrderList = orders
            };
        }

        public async Task<int> SaveSupplierAsync(SaveSupplierDTO dto, int actingUserId)
        {
            await EnsureRoleAsync(actingUserId, BuyerRoles, "Only Purchasing Officers and Administrators edit suppliers.");
            string? Clean(string? text, int max, string what)
            {
                var t = text?.Trim();
                if (string.IsNullOrEmpty(t)) return null;
                if (t.Length > max) throw new ArgumentException($"The {what} can be at most {max} characters.");
                return t;
            }
            var name = Clean(dto.Name, 150, "name") ?? throw new ArgumentException("Name the supplier.");
            if (await _context.Suppliers.AnyAsync(s => s.Name == name && s.SupplierID != (dto.SupplierID ?? 0)))
                throw new ArgumentException($"{name} is already on the list.");

            Supplier supplier;
            if (dto.SupplierID is int id)
                supplier = await _context.Suppliers.FirstOrDefaultAsync(s => s.SupplierID == id)
                    ?? throw new KeyNotFoundException("Supplier not found.");
            else
            {
                supplier = new Supplier { CreatedAt = PhilippineTime.Now };
                _context.Suppliers.Add(supplier);
            }
            supplier.Name = name;
            supplier.Address = Clean(dto.Address, 250, "address");
            supplier.Contact = Clean(dto.Contact, 100, "contact");
            supplier.Notes = Clean(dto.Notes, 500, "note");
            await _context.SaveChangesAsync();
            return supplier.SupplierID;
        }
    }
}
