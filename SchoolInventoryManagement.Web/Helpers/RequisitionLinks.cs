using Microsoft.AspNetCore.Mvc;

namespace SchoolInventoryManagement.Web.Helpers
{
    public static class RequisitionLinks
    {
        // Back to a requisition, where its next step is (BaseController.
        // RequisitionBack): on Requisition (approvals), opened; else its page.
        public static string RequisitionBackUrl(this IUrlHelper url, int id, string? returnTo) =>
            BorrowReturn.Parse(returnTo) is int borrowId ? BorrowReturn.PanelUrl(url, borrowId)
            : returnTo == "approvals"
                ? url.Action("Pending", "NewItemRequests", new { open = id }) + "#n-" + id
                : url.Action("Details", "NewItemRequests", new { id })!;
    }
}
