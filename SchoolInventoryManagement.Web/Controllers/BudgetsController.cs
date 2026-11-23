using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SchoolInventoryManagement.BLL.Interfaces;
using SchoolInventoryManagement.DAL.Constants;
using SchoolInventoryManagement.Web.Helpers;

namespace SchoolInventoryManagement.Web.Controllers
{
    // Department budgets (Scripts/036): what each department may spend on
    // purchase orders in a year (January to December, Scripts/037), and what
    // it has spent. The Principal, Purchasing Officers and Administrators see
    // it; the Principal and Administrators change a department's amount.
    [Authorize(Roles = ReaderRoles)]
    public class BudgetsController : BaseController
    {
        private const string ReaderRoles = RoleNames.Principal + "," + RoleNames.PurchasingOfficer + "," + RoleNames.Administrator;
        private const string EditorRoles = RoleNames.Principal + "," + RoleNames.Administrator;

        private readonly IBudgetService _budgets;

        public BudgetsController(IBudgetService budgets)
        {
            _budgets = budgets;
        }

        private int CurrentUserId =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // GET /Budgets[?year=2025] : this year, or an earlier one.
        public async Task<IActionResult> Index(int? year)
        {
            var thisYear = PhilippineTime.Today.Year;
            // No years ahead: nothing can have been spent there yet.
            var shown = year is int y && y >= 2000 && y <= thisYear ? y : thisYear;
            try
            {
                return View(await _budgets.GetYearAsync(shown, CurrentUserId));
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }

        // POST /Budgets/Set : a department's annual budget.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = EditorRoles)]
        public async Task<IActionResult> Set(int departmentId, string? amount, int? year)
        {
            try
            {
                if (!decimal.TryParse((amount ?? "").Replace(",", "").Replace("₱", "").Trim(), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var value))
                    throw new ArgumentException("Enter the annual budget in pesos, e.g. 600000.");
                await _budgets.SetAnnualBudgetAsync(departmentId, value, CurrentUserId);
                TempData["StatusMessage"] = $"Annual budget set to ₱{Math.Round(value, 2):N2}.";
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = UserMessageFor(ex);
            }
            return Redirect(Url.Action(nameof(Index), new { year }) + "#d-" + departmentId);
        }
    }
}
