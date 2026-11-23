using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SchoolInventoryManagement.BLL.DTOs;
using SchoolInventoryManagement.BLL.Interfaces;
using SchoolInventoryManagement.DAL.Constants;
using SchoolInventoryManagement.DAL.Context;

namespace SchoolInventoryManagement.BLL.Services
{
    // The Budgets page (Scripts/036, yearly since Scripts/037): each
    // department's annual budget and what its purchase orders add up to in
    // the year.
    public class BudgetService : IBudgetService
    {
        private readonly ApplicationDbContext _context;

        private static readonly string[] ReaderRoles = { RoleNames.Principal, RoleNames.PurchasingOfficer, RoleNames.Administrator };
        private static readonly string[] EditorRoles = { RoleNames.Principal, RoleNames.Administrator };

        public BudgetService(ApplicationDbContext context)
        {
            _context = context;
        }

        private async Task<string> RoleOfAsync(int userId, string[] roles, string message)
        {
            var user = await PermissionHelper.GetUserOrThrowAsync(_context, userId);
            PermissionHelper.EnsureIsActive(user);
            if (!roles.Contains(user.Role.RoleName))
                throw new UnauthorizedAccessException(message);
            return user.Role.RoleName;
        }

        public async Task<BudgetYearDTO> GetYearAsync(int year, int actingUserId)
        {
            var role = await RoleOfAsync(actingUserId, ReaderRoles,
                "Only the Principal, Purchasing Officers and Administrators see department budgets.");

            var departments = await _context.Departments.AsNoTracking()
                .OrderBy(d => d.Branch.BranchName).ThenByDescending(d => d.IsProperty).ThenBy(d => d.DepartmentName)
                .Select(d => new { d.DepartmentID, d.DepartmentName, Branch = d.Branch.BranchName, d.IsProperty, d.AnnualBudget })
                .ToListAsync();
            var orders = (await DepartmentBudgets.OrdersInYearAsync(_context, year))
                .GroupBy(o => o.DepartmentID)
                .ToDictionary(g => g.Key, g => g.Select(o => o.Order).ToList());

            return new BudgetYearDTO
            {
                Year = year,
                IsCurrentYear = year == PhilippineTime.Today.Year,
                CanEdit = EditorRoles.Contains(role),
                Departments = departments.Select(d =>
                {
                    var mine = orders.TryGetValue(d.DepartmentID, out var list) ? list : new List<BudgetOrderDTO>();
                    return new DepartmentBudgetDTO
                    {
                        DepartmentID = d.DepartmentID,
                        DepartmentName = d.DepartmentName,
                        BranchName = d.Branch,
                        IsProperty = d.IsProperty,
                        Year = year,
                        AnnualBudget = d.AnnualBudget,
                        Spent = mine.Sum(o => o.Total),
                        OrderCount = mine.Count,
                        Orders = mine
                    };
                }).ToList()
            };
        }

        public async Task SetAnnualBudgetAsync(int departmentId, decimal amount, int actingUserId)
        {
            await RoleOfAsync(actingUserId, EditorRoles,
                "Only the Principal and Administrators change department budgets.");
            if (amount < 0 || amount > 9_999_999_999.99m)
                throw new ArgumentException("Enter an annual budget from 0.");
            var dept = await _context.Departments.FirstOrDefaultAsync(d => d.DepartmentID == departmentId)
                ?? throw new KeyNotFoundException("Department not found.");
            dept.AnnualBudget = Math.Round(amount, 2);
            await _context.SaveChangesAsync();
        }
    }
}
