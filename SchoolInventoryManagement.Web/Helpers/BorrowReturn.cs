using System;
using Microsoft.AspNetCore.Mvc;

namespace SchoolInventoryManagement.Web.Helpers
{
    // Receiving what is bought for a borrow (Scripts/032) from the borrow's
    // own panel on Borrowed: returnTo = "borrow-1042" sends every step
    // (received, register, put in stock) back to borrow request #1042.
    public static class BorrowReturn
    {
        public static string For(int borrowRequestId) => "borrow-" + borrowRequestId;

        public static int? Parse(string? returnTo) =>
            returnTo is not null && returnTo.StartsWith("borrow-")
            && int.TryParse(returnTo.AsSpan("borrow-".Length), out var id) ? id : null;

        // The borrow's panel on Borrowed (Waiting for approval).
        public static string PanelUrl(IUrlHelper url, int borrowRequestId) =>
            url.Action("Pending", "AssetRequests") + "#a-" + borrowRequestId;
    }
}
