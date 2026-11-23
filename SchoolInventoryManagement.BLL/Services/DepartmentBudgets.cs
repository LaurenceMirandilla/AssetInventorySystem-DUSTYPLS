using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SchoolInventoryManagement.BLL.DTOs;
using SchoolInventoryManagement.DAL.Context;
using SchoolInventoryManagement.DAL.Entities;
using SchoolInventoryManagement.DAL.Entities.Enums;

namespace SchoolInventoryManagement.BLL.Services
{
    // Department budgets (Scripts/036, yearly since Scripts/037). A purchase
    // order is charged to the department of the requisition it is for, in
    // the year of its order date (January to December): its items, plus any
    // tax, shipping and other costs on orders made before those were taken
    // off the form. Cancelled orders, and orders for a requisition that was
    // rejected or cancelled, are not counted. Nothing is stored per year, so
    // each year starts from zero on its own.
    internal static class DepartmentBudgets
    {
        // Each purchase order dated in the year, with its department and
        // total. exceptOrderId leaves one out (the order being edited).
        internal static async Task<List<(int DepartmentID, BudgetOrderDTO Order)>> OrdersInYearAsync(
            ApplicationDbContext context, int year, int? departmentId = null, int? exceptOrderId = null)
        {
            var start = new DateTime(year, 1, 1);
            var end = start.AddYears(1);
            var rows = await context.PurchaseOrders.AsNoTracking()
                .Where(o => o.OrderDate >= start && o.OrderDate < end
                            && o.Status != PurchaseOrderStatus.Cancelled
                            && o.Request.RequestStatus != NewItemStatus.Rejected
                            && o.Request.RequestStatus != NewItemStatus.Cancelled
                            && (departmentId == null || o.Request.DepartmentID == departmentId)
                            && (exceptOrderId == null || o.PurchaseOrderID != exceptOrderId))
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.PurchaseOrderID)
                .Select(o => new
                {
                    o.Request.DepartmentID,
                    o.PurchaseOrderID,
                    o.PONumber,
                    o.OrderDate,
                    o.SupplierName,
                    o.NewItemRequestID,
                    o.Status,
                    Items = o.Lines.OrderBy(l => l.PurchaseOrderLineID)
                        .Select(l => new { Name = l.ItemName ?? l.Item.ItemName, l.Quantity }).ToList(),
                    Lines = o.Lines.Sum(l => (decimal?)(l.Quantity * l.UnitPrice)) ?? 0m,
                    Extra = o.Tax + o.ShippingCost + o.OtherCost
                })
                .ToListAsync();
            return rows.Select(r => (r.DepartmentID, new BudgetOrderDTO
            {
                PurchaseOrderID = r.PurchaseOrderID,
                PONumber = r.PONumber,
                OrderDate = r.OrderDate,
                SupplierName = r.SupplierName,
                NewItemRequestID = r.NewItemRequestID,
                Items = string.Join(", ", r.Items.Select(i => $"{i.Name} ×{i.Quantity}")),
                Total = r.Lines + r.Extra,
                IsDraft = r.Status == PurchaseOrderStatus.Draft
            })).ToList();
        }

        // One department's budget for the year.
        internal static async Task<DepartmentBudgetDTO> ForDepartmentAsync(
            ApplicationDbContext context, int departmentId, int year, int? exceptOrderId = null)
        {
            var dept = await context.Departments.AsNoTracking()
                .Where(d => d.DepartmentID == departmentId)
                .Select(d => new { d.DepartmentID, d.DepartmentName, Branch = d.Branch.BranchName, d.IsProperty, d.AnnualBudget })
                .FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException("Department not found.");
            var orders = await OrdersInYearAsync(context, year, departmentId, exceptOrderId);
            return new DepartmentBudgetDTO
            {
                DepartmentID = dept.DepartmentID,
                DepartmentName = dept.DepartmentName,
                BranchName = dept.Branch,
                IsProperty = dept.IsProperty,
                Year = year,
                AnnualBudget = dept.AnnualBudget,
                Spent = orders.Sum(o => o.Order.Total),
                OrderCount = orders.Count,
                Orders = orders.Select(o => o.Order).ToList()
            };
        }
    }
}
