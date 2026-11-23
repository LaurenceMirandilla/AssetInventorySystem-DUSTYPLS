using System;
using System.Globalization;
using SchoolInventoryManagement.DAL.Constants;

namespace SchoolInventoryManagement.Web.Helpers
{
    // Warranty is picked as a date on the register and edit forms ("Warranty
    // until") and kept in Asset.WarrantyInformation as yyyy-MM-dd. Older
    // assets may still hold text typed before ("1 year, until Mar 2027"):
    // that is shown as it is, and kept until a date is picked.
    public static class WarrantyDates
    {
        public static bool TryParse(string? value, out DateTime date) =>
            DateTime.TryParseExact((value ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

        // "Until Mar 15, 2027", or the old text.
        public static string? Display(string? value) =>
            TryParse(value, out var d) ? $"Until {d:MMM d, yyyy}" : value;

        public static bool IsExpired(string? value) =>
            TryParse(value, out var d) && d.Date < PhilippineTime.Today;

        // Text from before warranty became a date, or null.
        public static string? OldText(string? value) =>
            string.IsNullOrWhiteSpace(value) || TryParse(value, out _) ? null : value.Trim();
    }
}
