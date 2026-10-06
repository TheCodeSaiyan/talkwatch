using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Accounts and grants, for admins only. Every change is written to the audit log, and the rails that matter hold:
/// nobody can demote or lock themselves, and the last admin cannot be demoted or locked.
/// </summary>
public static class AdminEndpoints
{
    public static readonly string Policy = Permissions.Policy(Permission.ManagePeople);

    // Plain classes with settable properties: form binding leaves a missing field at its default, and an unticked
    // checkbox sends nothing at all, so missing must mean no. (A positional record makes every field required.)
    public sealed class NewUserForm
    {
        public string? Username { get; set; }
        public string? Role { get; set; }
        public string? Password { get; set; }
    }

    public sealed class TalkUserForm
    {
        public string? TalkUser { get; set; }
    }

    /// <summary>The outside contacts someone carries the phone for, by Talk's uuid; none ticked sends none.</summary>
    public sealed class OutsideContactsForm
    {
        public List<string>? Contacts { get; set; }
    }

    /// <summary>The people who carry one outside contact's phone; none ticked sends none.</summary>
    public sealed class ContactPeopleForm
    {
        public string? Contact { get; set; }

        public List<Guid>? People { get; set; }
    }

    public sealed class EmailForm
    {
        public string? Email { get; set; }
    }

    public sealed class ReportEmailForm
    {
        public string? ReportEmail { get; set; }
    }

    public sealed class RoleForm
    {
        public string? Role { get; set; }
    }

    /// <summary>A group's mapping: its name (new mappings only), the role it gives (empty for lines only) and its order.</summary>
    public sealed class GroupMappingForm
    {
        public string? Group { get; set; }
        public string? Role { get; set; }
        public int Order { get; set; }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<string?> RoleProblemAsync(string? role, RoleManager<IdentityRole<Guid>> roles) =>
        Blank(role) is { } name && !await roles.RoleExistsAsync(name) ? "Choose one of the roles listed, or lines only." : null;

    public sealed class NewRoleForm
    {
        public string? Name { get; set; }
    }

    /// <summary>The permissions ticked, by name; none ticked sends none.</summary>
    public sealed class RolePermissionsForm
    {
        public List<string>? Permissions { get; set; }
    }

    public sealed class PasswordForm
    {
        public string? Password { get; set; }
    }

    public class GrantFlagsForm
    {
        public bool AllowRecordings { get; set; }
        public bool AllowVoicemail { get; set; }
        public bool AllowTranscripts { get; set; }
    }

    public sealed class NewGrantForm : GrantFlagsForm
    {
        public string? Line { get; set; }
    }


    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        // A 404 from a form post stays a 404, rather than being re-run through the not-found page, whose antiforgery
        // check turns it into a 400.
        var admin = app.MapGroup("/admin").RequireAuthorization(Policy).WithMetadata(new SkipStatusCodePagesAttribute());

        admin.MapPost("/users", async ([FromForm] NewUserForm form, UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, CurrentSite site, Audit audit, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(form.Username) || string.IsNullOrEmpty(form.Role) || !await roles.RoleExistsAsync(form.Role) || string.IsNullOrEmpty(form.Password))
            {
                return Back("/admin/users", "Give a username, a role and a first password.");
            }

            if (await (await PeopleReach.ForAsync(http)).RoleAsync(form.Role) is { } beyond)
            {
                return Back("/admin/users", beyond);
            }

            var user = new AppUser { Id = Guid.NewGuid(), UserName = form.Username.Trim(), SiteId = site.Id };
            var created = await users.CreateAsync(user, form.Password);
            if (!created.Succeeded)
            {
                return Back("/admin/users", string.Join(" ", created.Errors.Select(e => e.Description)));
            }

            await users.AddToRoleAsync(user, form.Role!);
            await audit.WriteAsync("user.create", "user", user.Id, form.Role);
            return Results.Redirect($"/admin/users/{user.Id}");
        });

        admin.MapPost("/roles", async ([FromForm] NewRoleForm form, RoleManager<IdentityRole<Guid>> roles, Audit audit) =>
        {
            var name = form.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 50)
            {
                return Back("/admin/roles", "Give the role a name of up to 50 characters.");
            }

            if (await roles.RoleExistsAsync(name))
            {
                return Back("/admin/roles", $"There is already a role called {name}.");
            }

            var role = new IdentityRole<Guid>(name);
            await roles.CreateAsync(role);
            await audit.WriteAsync("role.create", "role", role.Id, name);
            return Back("/admin/roles", $"{name} added, with no permissions yet: tick what it allows.");
        });

        admin.MapPost("/roles/{id:guid}/permissions", async (Guid id, [FromForm] RolePermissionsForm? form, RoleManager<IdentityRole<Guid>> roles, UserManager<AppUser> users, Audit audit, HttpContext http) =>
        {
            if (await roles.FindByIdAsync(id.ToString()) is not { } role)
            {
                return Results.NotFound();
            }

            if (role.Name == Roles.Admin)
            {
                return Back("/admin/roles", "Admin holds every permission and cannot be changed.");
            }

            // Nothing ticked posts no field at all, and binds as no form.
            var permissions = (form?.Permissions ?? []).Aggregate(Permission.None, (all, name) =>
                Enum.TryParse<Permission>(name, out var p) && Permissions.Each.Contains(p) ? all | p : all);
            // Neither what the role holds now nor what it is to hold may go beyond what the person changing it holds.
            if ((await PeopleReach.ForAsync(http)).Give(permissions | await RolePermissions.GetAsync(roles, role)) is { } beyond)
            {
                return Back("/admin/roles", beyond);
            }

            await RolePermissions.ChangeAsync(roles, role, permissions);
            await audit.WriteAsync("role.permissions", "role", role.Id, $"{role.Name}: {permissions}");
            return Back("/admin/roles", $"{role.Name} saved. Its members have it within a minute.");
        });

        admin.MapPost("/roles/{id:guid}/delete", async (Guid id, RoleManager<IdentityRole<Guid>> roles, UserManager<AppUser> users, Audit audit) =>
        {
            if (await roles.FindByIdAsync(id.ToString()) is not { } role)
            {
                return Results.NotFound();
            }

            if (Roles.BuiltIn.Contains(role.Name))
            {
                return Back("/admin/roles", $"{role.Name} is built in and stays.");
            }

            if ((await users.GetUsersInRoleAsync(role.Name!)).Count > 0)
            {
                return Back("/admin/roles", $"Give {role.Name}'s members another role first.");
            }

            await roles.DeleteAsync(role);
            await audit.WriteAsync("role.delete", "role", role.Id, role.Name);
            return Back("/admin/roles", $"{role.Name} removed.");
        });

        // Group mappings: what identity-provider groups give at sign-in. They take effect at each member's next sign-in.
        admin.MapPost("/groups", async ([FromForm] GroupMappingForm form, TalkWatchDbContext db, RoleManager<IdentityRole<Guid>> roles, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var group = form.Group?.Trim();
            if (string.IsNullOrEmpty(group) || group.Length > 200)
            {
                return Back("/admin/groups", "Give the group's name as the identity provider sends it.");
            }

            if (await db.GroupMappings.AnyAsync(m => m.Group == group))
            {
                return Back("/admin/groups", $"{group} is mapped already.");
            }

            if (await RoleProblemAsync(form.Role, roles) is { } problem)
            {
                return Back("/admin/groups", problem);
            }

            if (await (await PeopleReach.ForAsync(http)).RoleAsync(Blank(form.Role)) is { } beyond)
            {
                return Back("/admin/groups", beyond);
            }

            var mapping = new GroupMapping { Id = Guid.NewGuid(), SiteId = site.Id, Group = group, Role = Blank(form.Role), Order = form.Order };
            db.GroupMappings.Add(mapping);
            await db.SaveChangesAsync();
            await audit.WriteAsync("group.add", "group_mapping", mapping.Id, $"{group} → {mapping.Role ?? "lines only"}");
            return Back("/admin/groups", $"{group} mapped. Add the lines it grants, if any; members get it at their next sign-in.");
        });

        admin.MapPost("/groups/{id:guid}", async (Guid id, [FromForm] GroupMappingForm form, TalkWatchDbContext db, RoleManager<IdentityRole<Guid>> roles, Audit audit, HttpContext http) =>
        {
            if (await db.GroupMappings.SingleOrDefaultAsync(m => m.Id == id) is not { } mapping)
            {
                return Results.NotFound();
            }

            if (await RoleProblemAsync(form.Role, roles) is { } problem)
            {
                return Back("/admin/groups", problem);
            }

            var reach = await PeopleReach.ForAsync(http);
            if ((await reach.RoleAsync(mapping.Role) ?? await reach.RoleAsync(Blank(form.Role))) is { } beyond)
            {
                return Back("/admin/groups", beyond);
            }

            (mapping.Role, mapping.Order) = (Blank(form.Role), form.Order);
            await db.SaveChangesAsync();
            await audit.WriteAsync("group.change", "group_mapping", mapping.Id, $"{mapping.Group} → {mapping.Role ?? "lines only"}, order {mapping.Order}");
            return Back("/admin/groups", $"{mapping.Group} saved.");
        });

        admin.MapPost("/groups/{id:guid}/delete", async (Guid id, TalkWatchDbContext db, Audit audit, HttpContext http) =>
        {
            if (await db.GroupMappings.SingleOrDefaultAsync(m => m.Id == id) is not { } mapping)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).RoleAsync(mapping.Role) is { } beyond)
            {
                return Back("/admin/groups", beyond);
            }

            db.GroupMappings.Remove(mapping);
            await db.SaveChangesAsync();
            await audit.WriteAsync("group.remove", "group_mapping", mapping.Id, mapping.Group);
            return Back("/admin/groups", $"{mapping.Group} unmapped. Its members lose what it gave at their next sign-in.");
        });

        admin.MapPost("/groups/{id:guid}/lines", async (Guid id, [FromForm] NewGrantForm form, TalkWatchDbContext db, Audit audit, HttpContext http) =>
        {
            if (await db.GroupMappings.Include(m => m.Lines).SingleOrDefaultAsync(m => m.Id == id) is not { } mapping)
            {
                return Results.NotFound();
            }

            var parts = (form.Line ?? "").Split(':', 2);
            if (parts.Length != 2 || !Enum.TryParse<LineKind>(parts[0], out var kind) || string.IsNullOrWhiteSpace(parts[1]))
            {
                return Back("/admin/groups", "Choose a line.");
            }

            if (mapping.Lines.Any(l => l.Kind == kind && l.Key == parts[1]))
            {
                return Back("/admin/groups", $"{mapping.Group} grants that line already.");
            }

            var reach = await PeopleReach.ForAsync(http);
            if ((await reach.RoleAsync(mapping.Role) ?? reach.Line(kind, parts[1], form.AllowRecordings, form.AllowVoicemail, form.AllowTranscripts)) is { } beyond)
            {
                return Back("/admin/groups", beyond);
            }

            var line = new GroupMappingLine
            {
                Id = Guid.NewGuid(), MappingId = mapping.Id, Kind = kind, Key = parts[1],
                AllowRecordings = form.AllowRecordings, AllowVoicemail = form.AllowVoicemail, AllowTranscripts = form.AllowTranscripts,
            };
            db.Add(line);
            await db.SaveChangesAsync();
            await audit.WriteAsync("group.line.add", "group_mapping", mapping.Id, $"{mapping.Group} {kind}:{parts[1]}");
            return Back("/admin/groups", $"{mapping.Group} now grants that line.");
        });

        admin.MapPost("/groups/{id:guid}/lines/{lineId:guid}/delete", async (Guid id, Guid lineId, TalkWatchDbContext db, Audit audit, HttpContext http) =>
        {
            if (await db.GroupMappings.Include(m => m.Lines).SingleOrDefaultAsync(m => m.Id == id) is not { } mapping
                || mapping.Lines.FirstOrDefault(l => l.Id == lineId) is not { } line)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).RoleAsync(mapping.Role) is { } beyond)
            {
                return Back("/admin/groups", beyond);
            }

            mapping.Lines.Remove(line);
            await db.SaveChangesAsync();
            await audit.WriteAsync("group.line.remove", "group_mapping", mapping.Id, $"{mapping.Group} {line.Kind}:{line.Key}");
            return Back("/admin/groups", "Line removed from the mapping.");
        });

        admin.MapPost("/users/{id:guid}/role", async (Guid id, [FromForm] RoleForm form, UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            var reach = await PeopleReach.ForAsync(http);
            if ((await reach.AccountAsync(user) ?? await reach.RoleAsync(form.Role)) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            if (string.IsNullOrEmpty(form.Role) || !await roles.RoleExistsAsync(form.Role))
            {
                return Back($"/admin/users/{id}", "Choose one of the roles listed.");
            }

            if (form.Role != Roles.Admin && await IsAdminAsync(users, user) && await RefuseAsync(users, site, user, http) is { } refusal)
            {
                return Back($"/admin/users/{id}", refusal);
            }

            // Not their security stamp: that would sign them out. Their next page is built with the new role anyway.
            await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user));
            await users.AddToRoleAsync(user, form.Role!);
            await audit.WriteAsync("user.role", "user", user.Id, form.Role);
            return Back($"/admin/users/{id}", $"Role set to {form.Role}.");
        });

        admin.MapPost("/users/{id:guid}/talk-user", async (Guid id, [FromForm] TalkUserForm form, UserManager<AppUser> users, CurrentSite site, TalkWatchDbContext db, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            var talkUser = string.IsNullOrWhiteSpace(form.TalkUser) ? null : form.TalkUser.Trim();
            if (talkUser is not null && !await db.Lines.AnyAsync(l => l.Kind == LineKind.User && l.Key == talkUser))
            {
                return Back($"/admin/users/{id}", "Choose one of the Talk users listed.");
            }

            // One person for each Talk user, so a ring group's member is never told twice or told as the wrong person.
            if (talkUser is not null && await users.Users.Where(u => u.SiteId == site.Id && u.Id != id && u.TalkUserUuid == talkUser).Select(u => u.UserName).FirstOrDefaultAsync() is { } other)
            {
                return Back($"/admin/users/{id}", $"That Talk user is already linked to {other}.");
            }

            user.TalkUserUuid = talkUser;
            await users.UpdateAsync(user);
            await audit.WriteAsync("user.talk_user", "user", user.Id, talkUser ?? "");
            return Back($"/admin/users/{id}", talkUser is null ? "No longer linked to a Talk user." : "Linked to their Talk user.");
        });

        // The outside phones this person carries: a call Talk puts through to one can alert them while it rings.
        admin.MapPost("/users/{id:guid}/outside-contacts", async (Guid id, [FromForm] OutsideContactsForm? form, UserManager<AppUser> users, CurrentSite site, TalkWatchDbContext db,
            LineDirectorySync directory, TimeProvider clock, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            var known = directory.Current.Contacts.Select(c => c.Uuid).OfType<string>().ToHashSet(StringComparer.Ordinal);
            // Nothing ticked posts no field at all, and binds as no form.
            var wanted = (form?.Contacts ?? []).Where(known.Contains).ToHashSet(StringComparer.Ordinal);
            var have = await db.ContactLinks.Where(l => l.UserId == id).ToListAsync();
            db.ContactLinks.RemoveRange(have.Where(l => !wanted.Contains(l.ContactUuid)));
            db.ContactLinks.AddRange(wanted.Where(c => have.All(l => l.ContactUuid != c))
                .Select(c => new ContactLink { SiteId = site.Id, ContactUuid = c, UserId = id, CreatedAt = clock.GetUtcNow(), CreatedBy = CurrentUserId(http) }));
            await db.SaveChangesAsync();
            await audit.WriteAsync("user.outside_contacts", "user", user.Id, string.Join(',', wanted.Order(StringComparer.Ordinal)));
            return Back($"/admin/users/{id}", wanted.Count == 0 ? "Carries no outside phone." : $"Carries {wanted.Count} outside phone{(wanted.Count == 1 ? "" : "s")}.");
        });

        // The same links from the contact's side: everyone who carries this phone, as an on-call mobile is passed round.
        admin.MapPost("/outside-phones", async ([FromForm] ContactPeopleForm form, TalkWatchDbContext db, CurrentSite site, LineDirectorySync directory, TimeProvider clock,
            Audit audit, HttpContext http) =>
        {
            if (directory.Current.Contacts.FirstOrDefault(c => c.Uuid is { Length: > 0 } && c.Uuid == form.Contact) is not { Uuid: { } uuid } contact)
            {
                return Back("/admin/outside-phones", "That contact is not in Talk any more.");
            }

            var people = form.People ?? [];
            var wanted = (await db.Users.Where(u => u.SiteId == site.Id && people.Contains(u.Id)).Select(u => u.Id).ToListAsync()).ToHashSet();
            var have = await db.ContactLinks.Where(l => l.ContactUuid == uuid).ToListAsync();
            db.ContactLinks.RemoveRange(have.Where(l => !wanted.Contains(l.UserId)));
            db.ContactLinks.AddRange(wanted.Where(p => have.All(l => l.UserId != p))
                .Select(p => new ContactLink { SiteId = site.Id, ContactUuid = uuid, UserId = p, CreatedAt = clock.GetUtcNow(), CreatedBy = CurrentUserId(http) }));
            await db.SaveChangesAsync();
            await audit.WriteAsync("contact.people", "contact", site.Id, $"{contact.DisplayName}: {wanted.Count} people");
            return Results.Redirect($"/admin/outside-phones?msg={Uri.EscapeDataString(wanted.Count == 0 ? $"Nobody carries {contact.DisplayName}'s phone now." : $"{wanted.Count} {(wanted.Count == 1 ? "person carries" : "people carry")} {contact.DisplayName}'s phone.")}#contact-{Uri.EscapeDataString(uuid)}");
        });

        // Where reports and email alerts for them go. Someone who signs in with single sign-on has it from the provider at
        // each sign-in, so a change here lasts until then; the page says so.
        admin.MapPost("/users/{id:guid}/email", async (Guid id, [FromForm] EmailForm form, UserManager<AppUser> users, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            var email = string.IsNullOrWhiteSpace(form.Email) ? null : form.Email.Trim();
            if (email is not null && !System.Net.Mail.MailAddress.TryCreate(email, out _))
            {
                return Back($"/admin/users/{id}", "That is not an email address.");
            }

            (user.Email, user.NormalizedEmail, user.EmailConfirmed) = (email, email is null ? null : users.NormalizeEmail(email), email is not null);
            await users.UpdateAsync(user);
            await audit.WriteAsync("user.email", "user", user.Id, email ?? "");
            return Back($"/admin/users/{id}", email is null ? "Email address removed." : $"Email address set to {email}.");
        });

        // Where their reports go instead of their email; empty to send them to their email again.
        admin.MapPost("/users/{id:guid}/report-email", async (Guid id, [FromForm] ReportEmailForm form, UserManager<AppUser> users, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            var address = string.IsNullOrWhiteSpace(form.ReportEmail) ? null : form.ReportEmail.Trim();
            if (address is not null && !System.Net.Mail.MailAddress.TryCreate(address, out _))
            {
                return Back($"/admin/users/{id}", "That reporting address is not an email address.");
            }

            user.ReportEmail = address;
            await users.UpdateAsync(user);
            await audit.WriteAsync("user.report_email", "user", user.Id, address ?? "");
            return Back($"/admin/users/{id}", address is null
                ? $"Reports go to their email{(user.Email is { Length: > 0 } email ? $", {email}" : "")}."
                : $"Reports go to {address}.");
        });

        admin.MapPost("/users/{id:guid}/password", async (Guid id, [FromForm] PasswordForm form, UserManager<AppUser> users, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            var token = await users.GeneratePasswordResetTokenAsync(user);
            var reset = await users.ResetPasswordAsync(user, token, form.Password ?? "");
            if (!reset.Succeeded)
            {
                return Back($"/admin/users/{id}", string.Join(" ", reset.Errors.Select(e => e.Description)));
            }

            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await audit.WriteAsync("user.password", "user", user.Id, null);
            return Back($"/admin/users/{id}", "Password set. Any lockout has been cleared.");
        });

        admin.MapPost("/users/{id:guid}/lock", async (Guid id, UserManager<AppUser> users, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            if (await RefuseAsync(users, site, user, http) is { } refusal)
            {
                return Back($"/admin/users/{id}", refusal);
            }

            await users.SetLockoutEnabledAsync(user, true);
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await users.UpdateSecurityStampAsync(user);
            await audit.WriteAsync("user.lock", "user", user.Id, null);
            return Back($"/admin/users/{id}", "Locked. They are signed out and cannot sign in.");
        });

        admin.MapPost("/users/{id:guid}/unlock", async (Guid id, UserManager<AppUser> users, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await audit.WriteAsync("user.unlock", "user", user.Id, null);
            return Back($"/admin/users/{id}", "Unlocked.");
        });

        admin.MapPost("/users/{id:guid}/two-factor/reset", async (Guid id, UserManager<AppUser> users, CurrentSite site, Audit audit, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            // For a lost phone: they sign in with their password alone, and can turn it on again from their account.
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
            await users.UpdateSecurityStampAsync(user);
            await audit.WriteAsync("2fa.reset", "user", user.Id, null);
            return Back($"/admin/users/{id}", "Two-factor sign-in turned off. They can turn it on again from their account page.");
        });

        admin.MapPost("/users/{id:guid}/tokens/{tokenId:guid}/revoke", async (Guid id, Guid tokenId, TalkWatchDbContext db, Audit audit, UserManager<AppUser> users, CurrentSite site, HttpContext http) =>
        {
            if (await BeyondAsync(users, site, id, http) is { } beyond)
            {
                return beyond;
            }

            var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == tokenId && t.UserId == id);
            if (token is null)
            {
                return Results.NotFound();
            }

            db.ApiTokens.Remove(token);
            await db.SaveChangesAsync();
            await audit.WriteAsync("token.revoke", "api_token", token.Id, token.Name);
            return Back($"/admin/users/{id}", $"Token '{token.Name}' revoked.");
        });

        admin.MapPost("/users/{id:guid}/grants", async (Guid id, [FromForm] NewGrantForm form, UserManager<AppUser> users, CurrentSite site, TalkWatchDbContext db, Audit audit, TimeProvider clock, HttpContext http) =>
        {
            var user = await FindAsync(users, site, id);
            if (user is null)
            {
                return Results.NotFound();
            }

            var reach = await PeopleReach.ForAsync(http);
            if (await reach.AccountAsync(user) is { } beyond)
            {
                return Back($"/admin/users/{id}", beyond);
            }

            // Lines are offered as "Kind:Key" from the directory; a key may itself contain ':' (E.164 does not, uuids do not).
            var parts = (form.Line ?? "").Split(':', 2);
            if (parts.Length != 2 || !Enum.TryParse<LineKind>(parts[0], out var kind) || string.IsNullOrWhiteSpace(parts[1]))
            {
                return Back($"/admin/users/{id}", "Choose a line.");
            }

            if (reach.Line(kind, parts[1], form.AllowRecordings, form.AllowVoicemail, form.AllowTranscripts) is { } unseen)
            {
                return Back($"/admin/users/{id}", unseen);
            }

            if (await db.Grants.AnyAsync(g => g.UserId == id && g.Kind == kind && g.Key == parts[1] && !g.ByGroups))
            {
                return Back($"/admin/users/{id}", "They already have that line.");
            }

            var grant = new Grant
            {
                Id = Guid.NewGuid(), SiteId = site.Id, UserId = id, Kind = kind, Key = parts[1],
                AllowRecordings = form.AllowRecordings, AllowVoicemail = form.AllowVoicemail,
                AllowTranscripts = form.AllowTranscripts,
                CreatedAt = clock.GetUtcNow(), CreatedBy = CurrentUserId(http),
            };
            db.Grants.Add(grant);
            await db.SaveChangesAsync();
            await audit.WriteAsync("grant.add", "grant", grant.Id, $"{user.UserName} {kind}:{parts[1]} {Flags(grant)}");
            return Back($"/admin/users/{id}", "Line granted.");
        });

        admin.MapPost("/users/{id:guid}/grants/{grantId:guid}", async (Guid id, Guid grantId, [FromForm] GrantFlagsForm form, TalkWatchDbContext db, Audit audit, UserManager<AppUser> users, CurrentSite site, HttpContext http) =>
        {
            if (await BeyondAsync(users, site, id, http) is { } beyond)
            {
                return beyond;
            }

            // Grants by groups follow the groups, at sign-in; only those given by hand are changed here.
            var grant = await db.Grants.SingleOrDefaultAsync(g => g.Id == grantId && g.UserId == id && !g.ByGroups);
            if (grant is null)
            {
                return Results.NotFound();
            }

            if ((await PeopleReach.ForAsync(http)).Line(grant.Kind, grant.Key, form.AllowRecordings, form.AllowVoicemail, form.AllowTranscripts) is { } unseen)
            {
                return Back($"/admin/users/{id}", unseen);
            }

            grant.AllowRecordings = form.AllowRecordings;
            grant.AllowVoicemail = form.AllowVoicemail;
            grant.AllowTranscripts = form.AllowTranscripts;
            await db.SaveChangesAsync();
            await audit.WriteAsync("grant.update", "grant", grant.Id, $"{grant.Kind}:{grant.Key} {Flags(grant)}");
            return Back($"/admin/users/{id}", "Access updated.");
        });

        admin.MapPost("/users/{id:guid}/grants/{grantId:guid}/delete", async (Guid id, Guid grantId, TalkWatchDbContext db, Audit audit, UserManager<AppUser> users, CurrentSite site, HttpContext http) =>
        {
            if (await BeyondAsync(users, site, id, http) is { } beyond)
            {
                return beyond;
            }

            // Grants by groups follow the groups, at sign-in; only those given by hand are changed here.
            var grant = await db.Grants.SingleOrDefaultAsync(g => g.Id == grantId && g.UserId == id && !g.ByGroups);
            if (grant is null)
            {
                return Results.NotFound();
            }

            db.Grants.Remove(grant);
            await db.SaveChangesAsync();
            await audit.WriteAsync("grant.remove", "grant", grant.Id, $"{grant.Kind}:{grant.Key}");
            return Back($"/admin/users/{id}", "Line removed.");
        });
    }

    private static string Flags(Grant g) =>
        string.Join(",", new[] { g.AllowRecordings ? "recordings" : null, g.AllowVoicemail ? "voicemail" : null, g.AllowTranscripts ? "transcripts" : null }
            .Where(f => f is not null)) is { Length: > 0 } flags ? flags : "calls only";

    private static IResult Back(string path, string message) => Results.Redirect($"{path}?msg={Uri.EscapeDataString(message)}");

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    // Accounts belong to a site; an admin manages only their own site's.
    private static async Task<AppUser?> FindAsync(UserManager<AppUser> users, CurrentSite site, Guid id) =>
        await users.Users.SingleOrDefaultAsync(u => u.Id == id && u.SiteId == site.Id);

    /// <summary>Not found, or sent back with why, when this account is not the signed-in person's to change; null when it is.</summary>
    private static async Task<IResult?> BeyondAsync(UserManager<AppUser> users, CurrentSite site, Guid id, HttpContext http) =>
        await FindAsync(users, site, id) is not { } user ? Results.NotFound()
        : await (await PeopleReach.ForAsync(http)).AccountAsync(user) is { } beyond ? Back($"/admin/users/{id}", beyond)
        : null;

    private static async Task<bool> IsAdminAsync(UserManager<AppUser> users, AppUser user) => await users.IsInRoleAsync(user, Roles.Admin);

    /// <summary>Why demoting or locking this account is refused, or null when it may go ahead.</summary>
    private static async Task<string?> RefuseAsync(UserManager<AppUser> users, CurrentSite site, AppUser user, HttpContext http)
    {
        if (CurrentUserId(http) == user.Id)
        {
            return "You cannot demote or lock your own account. Ask another admin.";
        }

        if (await IsAdminAsync(users, user))
        {
            var admins = await users.GetUsersInRoleAsync(Roles.Admin);
            var now = DateTimeOffset.UtcNow;
            if (!admins.Any(a => a.SiteId == site.Id && a.Id != user.Id && (a.LockoutEnd is null || a.LockoutEnd <= now)))
            {
                return "This is the last admin who can sign in. Make someone else an admin first.";
            }
        }

        return null;
    }
}

/// <summary>Writes to the audit log as the signed-in person.</summary>
public sealed class Audit(TalkWatchDbContext db, CurrentSite site, TimeProvider clock, IHttpContextAccessor http)
{
    /// <param name="by">Who did it, when there is no request to say: an interactive page's signed-in person.</param>
    public async Task WriteAsync(string action, string targetType, Guid targetId, string? detail, ClaimsPrincipal? by = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            At = clock.GetUtcNow(),
            UserId = Guid.TryParse((by ?? http.HttpContext?.User)?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null,
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString(),
            Detail = detail,
        });
        await db.SaveChangesAsync();
    }
}
