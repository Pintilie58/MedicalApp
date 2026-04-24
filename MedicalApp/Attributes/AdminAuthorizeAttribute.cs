using MedicalApp.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Attributes
{
    /// <summary>
    /// Restricts access to the Admin dashboard.
    /// Requirements (all must be true):
    ///   * session has "UserEmail"
    ///   * user exists in DB, is NOT blocked, and has IsAdmin = true.
    /// Redirects to Home/Index otherwise.
    /// </summary>
    public class AdminAuthorizeAttribute : Attribute, IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var email = context.HttpContext.Session.GetString("UserEmail");

            if (string.IsNullOrEmpty(email))
            {
                context.Result = new RedirectToActionResult("Index", "Home", null);
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null || user.IsBlocked || !user.IsAdmin)
            {
                context.Result = new RedirectToActionResult("Index", "Home", null);
                return;
            }

            await next();
        }
    }
}
