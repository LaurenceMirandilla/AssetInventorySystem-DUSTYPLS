using System;
using System.Threading.Tasks;
using SchoolInventoryManagement.BLL.DTOs;

namespace SchoolInventoryManagement.BLL.Interfaces
{
    // Department budgets (Scripts/036): seen by the Principal, Purchasing
    // Officers and Administrators; changed by the Principal and
    // Administrators.
    public interface IBudgetService
    {
        // Every department's budget and spending for a year (January to December).
        Task<BudgetYearDTO> GetYearAsync(int year, int actingUserId);

        // A department's annual budget, from the next purchase order on.
        Task SetAnnualBudgetAsync(int departmentId, decimal amount, int actingUserId);
    }
}
