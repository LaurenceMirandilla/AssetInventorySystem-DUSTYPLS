using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SchoolInventoryManagement.BLL.DTOs;
using SchoolInventoryManagement.BLL.Interfaces;
using SchoolInventoryManagement.DAL.Constants;
using SchoolInventoryManagement.Web.Helpers;

namespace SchoolInventoryManagement.Web.Controllers
{
    // Purchasing (Scripts/031): requisitions with something to buy, what is
    // missing on each, and the purchase orders the Purchasing Officer brings
    // to suppliers. The services check every rule; these attributes only
    // keep people off pages they could never use.
    [Authorize]
    public class PurchasingController : BaseController
    {
        private const string BuyerRoles = RoleNames.PurchasingOfficer + "," + RoleNames.Administrator;
        private const string ReaderRoles = RoleNames.PurchasingOfficer + "," + RoleNames.AssetOfficer + ","
            + RoleNames.Principal + "," + RoleNames.Administrator;

        private readonly INewItemRequestService _requestService;
        private readonly IPurchasingService _purchasing;
        private readonly IDepartmentService _departments;
        private readonly IWebHostEnvironment _env;

        // Receipts attached to purchase orders (Scripts/037).
        private const string ReceiptFolder = "uploads/receipts";

        public PurchasingController(INewItemRequestService requestService, IPurchasingService purchasing,
            IDepartmentService departments, IWebHostEnvironment env)
        {
            _requestService = requestService;
            _purchasing = purchasing;
            _departments = departments;
            _env = env;
        }

        private int CurrentUserId =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // The order's lines from the form: Quantity_, Price_, Name_ and
        // Desc_{lineId} for each requisition line on it.
        private static List<CreatePurchaseOrderLineDTO> ReadLines(IFormCollection form) =>
            form.Keys
                .Where(k => k.StartsWith("Quantity_"))
                .Select(k => int.TryParse(k["Quantity_".Length..], out var lineId) ? lineId : 0)
                .Where(lineId => lineId > 0)
                .Select(lineId => new CreatePurchaseOrderLineDTO
                {
                    NewItemRequestItemID = lineId,
                    ItemName = form[$"Name_{lineId}"].ToString(),
                    Description = form[$"Desc_{lineId}"].ToString(),
                    Quantity = int.TryParse(form[$"Quantity_{lineId}"], out var q) ? q : 0,
                    UnitPrice = decimal.TryParse(form[$"Price_{lineId}"].ToString().Replace(",", ""), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var p) ? p : -1
                })
                .ToList();

        // Back to Purchasing, on its purchase orders, with this one picked
        // out (Open purchase order and Edit from there).
        private IActionResult ToOrder(int purchaseOrderId) =>
            Redirect(Url.Action(nameof(Index), new { tab = "orders", po = purchaseOrderId }) + "#o-" + purchaseOrderId);

        // GET /Purchasing : to buy (with what is missing), and ordered.
        [Authorize(Roles = BuyerRoles)]
        // tab = buy / ordered / orders (the purchase orders); po = one just
        // saved, picked out on the list.
        public async Task<IActionResult> Index(string? tab, int? po)
        {
            var requests = await _requestService.GetPurchasingQueueAsync(CurrentUserId);
            ViewBag.Orders = await _purchasing.GetOrdersAsync(CurrentUserId);
            // Department -> branch, for the Branch filter.
            ViewBag.DepartmentBranches = (await _departments.GetAllDepartmentsAsync())
                .ToDictionary(d => d.DepartmentID, d => d.BranchName);
            ViewBag.Tab = tab is "ordered" or "orders" ? tab : "buy";
            ViewBag.NewOrder = po;
            return View(requests);
        }

        // GET /Purchasing/CreateOrder?requestId=5
        [Authorize(Roles = BuyerRoles)]
        public async Task<IActionResult> CreateOrder(int requestId)
        {
            try
            {
                ViewBag.Form = await _purchasing.GetOrderFormAsync(requestId, CurrentUserId);
                return View(new CreatePurchaseOrderDTO { NewItemRequestID = requestId, OrderDate = PhilippineTime.Today });
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                TempData["ErrorMessage"] = UserMessageFor(ex);
                return RedirectToAction(nameof(Index));
            }
        }

        // POST /Purchasing/CreateOrder : Quantity_{lineId} and Price_{lineId}
        // for each line on the order.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [OneTimeSubmit]
        [Authorize(Roles = BuyerRoles)]
        public async Task<IActionResult> CreateOrder(CreatePurchaseOrderDTO model, IFormCollection form)
        {
            model.PurchaseOrderID = null;
            model.Lines = ReadLines(form);

            try
            {
                var order = await _purchasing.CreateOrderAsync(model, CurrentUserId);
                TempData["StatusMessage"] = $"{order.PONumber} saved as a draft. Open it and print it to bring to the supplier; " +
                    "once printed it can no longer be changed or cancelled.";
                return ToOrder(order.PurchaseOrderID);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                HandleServiceException(ex);
                try
                {
                    ViewBag.Form = await _purchasing.GetOrderFormAsync(model.NewItemRequestID, CurrentUserId);
                }
                catch (Exception again)
                {
                    TempData["ErrorMessage"] = UserMessageFor(again);
                    return RedirectToAction(nameof(Index));
                }
                ViewBag.Entered = model.Lines.ToDictionary(l => l.NewItemRequestItemID);
                return View(model);
            }
        }

        // GET /Purchasing/EditOrder/5 : the same form, filled in with the
        // order, while it is a draft (not printed).
        [Authorize(Roles = BuyerRoles)]
        public async Task<IActionResult> EditOrder(int id)
        {
            try
            {
                var form = await _purchasing.GetEditFormAsync(id, CurrentUserId);
                ViewBag.Form = form;
                ViewBag.Entered = form.Current!.Lines.ToDictionary(l => l.NewItemRequestItemID);
                return View("CreateOrder", form.Current);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                TempData["ErrorMessage"] = UserMessageFor(ex);
                return ToOrder(id);
            }
        }

        // POST /Purchasing/EditOrder/5 : lines set to 0 come off the order.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [OneTimeSubmit]
        [Authorize(Roles = BuyerRoles)]
        public async Task<IActionResult> EditOrder(int id, CreatePurchaseOrderDTO model, IFormCollection form)
        {
            model.PurchaseOrderID = id;
            model.Lines = ReadLines(form);

            try
            {
                var order = await _purchasing.UpdateOrderAsync(model, CurrentUserId);
                TempData["StatusMessage"] = $"{order.PONumber} updated.";
                return ToOrder(order.PurchaseOrderID);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                HandleServiceException(ex);
                try
                {
                    ViewBag.Form = await _purchasing.GetEditFormAsync(id, CurrentUserId);
                }
                catch (Exception again)
                {
                    TempData["ErrorMessage"] = UserMessageFor(again);
                    return ToOrder(id);
                }
                ViewBag.Entered = model.Lines.ToDictionary(l => l.NewItemRequestItemID);
                return View("CreateOrder", model);
            }
        }

        // GET /Purchasing/Order/5 : the order, ready to print. print=1 opens
        // the browser's print box (right after Print was clicked).
        [Authorize(Roles = ReaderRoles)]
        public async Task<IActionResult> Order(int id, bool print = false)
        {
            try
            {
                ViewBag.AutoPrint = print;
                return View(await _purchasing.GetOrderAsync(id, CurrentUserId));
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // POST /Purchasing/PrintOrder/5 : a draft becomes printed (locked),
        // then the print box opens. A printed order just prints again.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = BuyerRoles)]
        public async Task<IActionResult> PrintOrder(int id)
        {
            try
            {
                var movedOn = await _purchasing.PrintOrderAsync(id, CurrentUserId);
                if (movedOn)
                    TempData["StatusMessage"] = "Printed. Everything missing on the requisition is ordered now; it waits for the supplier, and the Property Department receives it.";
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = UserMessageFor(ex);
                return RedirectToAction(nameof(Order), new { id });
            }
            return RedirectToAction(nameof(Order), new { id, print = true });
        }

        // POST /Purchasing/CancelOrder/5 : a draft only. Its items are to buy
        // again.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = BuyerRoles)]
        public async Task<IActionResult> CancelOrder(int id, string? reason)
        {
            try
            {
                var requestId = await _purchasing.CancelOrderAsync(id, reason, CurrentUserId);
                TempData["StatusMessage"] = $"Purchase order cancelled. Its items are to buy again on requisition #{requestId}.";
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = UserMessageFor(ex);
            }
            return ToOrder(id);
        }

        // POST /Purchasing/AttachReceipt/5 : a photo or PDF of the supplier's
        // receipt. back = "order" returns to the order page.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = BuyerRoles)]
        [RequestSizeLimit(FileUploads.MaxRequestBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = FileUploads.MaxRequestBytes)]
        public async Task<IActionResult> AttachReceipt(int id, IFormFile? receipt, string? back)
        {
            try
            {
                if (receipt is null || receipt.Length == 0)
                    throw new ArgumentException("Choose a photo or PDF of the receipt.");
                FileUploads.Validate(receipt, FileUploads.DocumentExtensions, "receipt");
                var url = await FileUploads.SaveAsync(receipt, _env.WebRootPath, ReceiptFolder);
                try
                {
                    await _purchasing.AddAttachmentAsync(id, url!, System.IO.Path.GetFileName(receipt.FileName), CurrentUserId);
                }
                catch
                {
                    DeleteUpload(url);
                    throw;
                }
                TempData["StatusMessage"] = "Receipt attached.";
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = UserMessageFor(ex);
            }
            return back == "order" ? RedirectToAction(nameof(Order), new { id }) : ToOrder(id);
        }

        // POST /Purchasing/RemoveReceipt : takes a receipt off the order.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = BuyerRoles)]
        public async Task<IActionResult> RemoveReceipt(int attachmentId, int id, string? back)
        {
            try
            {
                DeleteUpload(await _purchasing.RemoveAttachmentAsync(attachmentId, CurrentUserId));
                TempData["StatusMessage"] = "Receipt removed.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = UserMessageFor(ex);
            }
            return back == "order" ? RedirectToAction(nameof(Order), new { id }) : ToOrder(id);
        }

        // A saved receipt file, removed from wwwroot. Only files under the
        // receipts folder are ever touched.
        private void DeleteUpload(string? url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("/" + ReceiptFolder + "/", StringComparison.Ordinal))
                return;
            var path = System.IO.Path.Combine(_env.WebRootPath, url.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar));
            try
            {
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch (System.IO.IOException)
            {
                // Left behind; harmless.
            }
        }
    }
}
