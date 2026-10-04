using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Who holds which role on a number. Site admins who manage people may set any; anyone else needs a role allowing it,
/// on that number or site-wide, and may give or take away only roles within what they hold there themselves, so nobody
/// can raise anyone, themselves included, above their own reach.
/// </summary>
public static class NumberPeople
{
    public sealed class SetForm
    {
        public string? Did { get; set; }
        public Guid PersonId { get; set; }
        public Guid RoleId { get; set; }
    }

    public sealed class RemoveForm
    {
        public Guid Id { get; set; }
    }

    /// <summary>
    /// What this person may give or take away on a number, as per-number permissions; <see cref="Permission.All"/> for
    /// a site admin who manages people; null when they may not manage the number at all.
    /// </summary>
    public static async Task<Permission?> AuthorityAsync(TalkWatchDbContext db, RoleManager<IdentityRole<Guid>> roles, ClaimsPrincipal user, string did)
    {
        if (user.Can(Permission.ManagePeople))
        {
            return Permission.All;
        }

        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var me))
        {
            return null;
        }

        var onNumber = Permission.None;
        if (await db.NumberRoles.Where(n => n.UserId == me && n.Did == did).Select(n => (Guid?)n.RoleId).FirstOrDefaultAsync() is { } roleId
            && await roles.FindByIdAsync(roleId.ToString()) is { } role)
        {
            onNumber = await RolePermissions.GetAsync(roles, role);
        }

        var reach = (Permissions.Of(user) | onNumber) & Permissions.PerNumber;
        return (reach & Permission.ManageNumberPeople) == Permission.ManageNumberPeople ? reach : null;
    }

    /// <summary>Whether a role's per-number permissions are all within this authority.</summary>
    public static bool Within(Permission role, Permission authority) =>
        authority == Permission.All || (role & Permissions.PerNumber & ~authority) == Permission.None;

    /// <summary>The numbers this person may manage: every number for a site admin, else those their roles allow.</summary>
    public static async Task<List<string>> ManagedAsync(TalkWatchDbContext db, CurrentSite site, ClaimsPrincipal user)
    {
        var numbers = await db.Lines.IgnoreQueryFilters().Where(l => l.SiteId == site.Id && l.Kind == LineKind.Did && l.Present)
            .OrderBy(l => l.Key).Select(l => l.Key).ToListAsync();
        if (user.Can(Permission.ManagePeople) || user.Can(Permission.ManageNumberPeople))
        {
            return numbers;
        }

        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var me))
        {
            return [];
        }

        var mine = await NumberAccess.NumbersWith(db, me, Permission.ManageNumberPeople).ToListAsync();
        return [.. numbers.Where(mine.Contains)];
    }

    public static void MapNumbers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/numbers").RequireAuthorization();

        group.MapPost("/people", async ([FromForm] SetForm form, HttpContext http, TalkWatchDbContext db, RoleManager<IdentityRole<Guid>> roles,
            UserManager<AppUser> users, CurrentSite site, Audit audit, TimeProvider clock) =>
        {
            var did = form.Did?.Trim() ?? "";
            if (!await db.Lines.IgnoreQueryFilters().AnyAsync(l => l.SiteId == site.Id && l.Kind == LineKind.Did && l.Key == did))
            {
                return Back("Choose a number.");
            }

            if (await AuthorityAsync(db, roles, http.User, did) is not { } authority)
            {
                return Results.Forbid();
            }

            if (await roles.FindByIdAsync(form.RoleId.ToString()) is not { } role || !Within(await RolePermissions.GetAsync(roles, role), authority))
            {
                return Back("Choose a role within what you hold on this number.");
            }

            if (await users.Users.SingleOrDefaultAsync(u => u.Id == form.PersonId && u.SiteId == site.Id) is not { } person)
            {
                return Back("Choose a person.");
            }

            var held = await db.NumberRoles.SingleOrDefaultAsync(n => n.UserId == person.Id && n.Did == did);
            if (held is not null)
            {
                // Changing someone's role is taking the old one away: only within reach too.
                if (await roles.FindByIdAsync(held.RoleId.ToString()) is { } old && !Within(await RolePermissions.GetAsync(roles, old), authority))
                {
                    return Back("Their role on this number is beyond what you hold there.");
                }

                held.RoleId = role.Id;
            }
            else
            {
                db.NumberRoles.Add(new NumberRole
                {
                    Id = Guid.NewGuid(), SiteId = site.Id, UserId = person.Id, Did = did, RoleId = role.Id,
                    CreatedAt = clock.GetUtcNow(), CreatedBy = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var by) ? by : null,
                });
            }

            await db.SaveChangesAsync();
            await audit.WriteAsync("number.role.set", "user", person.Id, $"{person.UserName} {role.Name} on {did}");
            return Back($"{person.UserName} is {role.Name} on {did}.");
        });

        group.MapPost("/people/remove", async ([FromForm] RemoveForm form, HttpContext http, TalkWatchDbContext db, RoleManager<IdentityRole<Guid>> roles,
            UserManager<AppUser> users, Audit audit) =>
        {
            if (await db.NumberRoles.SingleOrDefaultAsync(n => n.Id == form.Id) is not { } held)
            {
                return Results.NotFound();
            }

            if (await AuthorityAsync(db, roles, http.User, held.Did) is not { } authority)
            {
                return Results.Forbid();
            }

            if (await roles.FindByIdAsync(held.RoleId.ToString()) is { } role && !Within(await RolePermissions.GetAsync(roles, role), authority))
            {
                return Back("Their role on this number is beyond what you hold there.");
            }

            db.NumberRoles.Remove(held);
            await db.SaveChangesAsync();
            var name = (await users.FindByIdAsync(held.UserId.ToString()))?.UserName ?? "someone";
            await audit.WriteAsync("number.role.remove", "user", held.UserId, $"{name} on {held.Did}");
            return Back($"{name} no longer has a role on {held.Did}.");
        });
    }

    private static IResult Back(string message) => Results.Redirect($"/numbers?msg={Uri.EscapeDataString(message)}");
}
