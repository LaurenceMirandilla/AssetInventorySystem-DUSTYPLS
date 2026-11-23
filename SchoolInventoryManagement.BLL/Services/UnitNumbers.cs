using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SchoolInventoryManagement.BLL.Services
{
    // "Name - Unit N" on registered assets counts each MODEL's own units, so
    // the first unit of a model is Unit 1 whatever its category's code
    // number is (ITE-0002 can be "Gigabyte ... Motherboard - Unit 1"). A
    // model's next number carries on after the highest "Unit N" its units
    // already have -- older units were numbered by code, so nothing is
    // named twice -- and never below how many units it has.
    public static class UnitNumbers
    {
        private static readonly Regex UnitSuffix = new(@"\s-\sUnit\s(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static int Next(IEnumerable<string?> assetNames)
        {
            var names = assetNames.ToList();
            var highest = names
                .Select(n => n is null ? null : UnitSuffix.Match(n))
                .Where(m => m is { Success: true })
                .Select(m => int.TryParse(m!.Groups[1].Value, out var x) ? x : 0)
                .DefaultIfEmpty(0)
                .Max();
            return Math.Max(highest, names.Count) + 1;
        }
    }
}
