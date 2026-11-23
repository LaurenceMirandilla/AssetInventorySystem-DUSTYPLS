using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SchoolInventoryManagement.DAL.Context;
using SchoolInventoryManagement.DAL.Entities.Enums;

namespace SchoolInventoryManagement.Web.Helpers
{
    // The number on Property's "Incoming deliveries" button: requisitions
    // ordered from a supplier and not received yet (NewItemRequests/Incoming).
    // Not one's own: nobody receives their own requisition.
    public static class IncomingDeliveries
    {
        public static Task<int> CountAsync(ApplicationDbContext context, int userId) =>
            context.NewItemRequests.CountAsync(n => n.RequestStatus == NewItemStatus.AwaitingArrival
                                                    && n.RequestedByUserID != userId);
    }
}
