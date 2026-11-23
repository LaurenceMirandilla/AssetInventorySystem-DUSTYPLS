using System;
using System.Collections.Generic;
using System.Linq;

namespace SchoolInventoryManagement.BLL.DTOs
{
    // A department's budget for one year, January to December (Scripts/036,
    // yearly since Scripts/037): what it may spend on purchase orders, and
    // what its requisitions' orders dated in that year add up to.
    public class DepartmentBudgetDTO
    {
        public int DepartmentID { get; set; }
        public string DepartmentName { get; set; } = null!;
        public string BranchName { get; set; } = "";
        public bool IsProperty { get; set; }
        public int Year { get; set; }
        public decimal AnnualBudget { get; set; }
        public decimal Spent { get; set; }
        public int OrderCount { get; set; }
        public decimal Left => AnnualBudget - Spent;
        public bool IsOver => Spent > AnnualBudget;
        // Share of the budget spent, 0-100 (more than 100 when over).
        public int Percent => AnnualBudget <= 0 ? (Spent > 0 ? 100 : 0) : (int)Math.Round(Spent / AnnualBudget * 100);
        // The year's purchase orders, newest first (the Budgets page).
        public List<BudgetOrderDTO> Orders { get; set; } = new();
    }

    public class BudgetOrderDTO
    {
        public int PurchaseOrderID { get; set; }
        public string PONumber { get; set; } = null!;
        public DateTime OrderDate { get; set; }
        public string SupplierName { get; set; } = null!;
        public int NewItemRequestID { get; set; }
        public string Items { get; set; } = "";
        public decimal Total { get; set; }
        // Not printed yet: still counted, as the money is set aside for it.
        public bool IsDraft { get; set; }
    }

    // Every department's budget for one year.
    public class BudgetYearDTO
    {
        public int Year { get; set; }
        public bool IsCurrentYear { get; set; }
        // Can change a department's annual budget (Principal, Administrator).
        public bool CanEdit { get; set; }
        public List<DepartmentBudgetDTO> Departments { get; set; } = new();
        public decimal TotalBudget => Departments.Sum(d => d.AnnualBudget);
        public decimal TotalSpent => Departments.Sum(d => d.Spent);
        public int OverCount => Departments.Count(d => d.IsOver);
    }
}
