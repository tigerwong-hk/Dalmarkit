// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Dalmarkit.Common.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;

namespace Dalmarkit.AspNetCore.Authorization;

/// <summary>
///  Scope authorization handler that needs to be called for a specific requirement type.
///  In this case, <see cref="ScopeAuthorizationRequirement"/>.
/// </summary>
/// <remarks>
/// Constructor for the scope authorization handler, which takes a configuration.
/// </remarks>
/// <param name="configuration">Configuration.</param>
public class ScopeAuthorizationHandler(IConfiguration configuration) : AuthorizationHandler<ScopeAuthorizationRequirement>
{
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable RCS1213 // Remove unused field declaration
    private readonly IConfiguration _configuration = configuration;
#pragma warning restore RCS1213 // Remove unused field declaration
#pragma warning restore IDE0052 // Remove unread private members

    /// <summary>
    ///  Makes a decision if authorization is allowed based on a specific requirement.
    /// </summary>
    /// <param name="context">AuthorizationHandlerContext.</param>
    /// <param name="requirement">Scope authorization requirement.</param>
    /// <returns>Task.</returns>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeAuthorizationRequirement requirement)
    {
        _ = Guard.NotNull(context, nameof(context));
        _ = Guard.NotNull(requirement, nameof(requirement));

        if (context.User == null)
        {
            return Task.CompletedTask;
        }

        List<Claim> scopeClaims = context.User.FindAll(requirement.ClaimType).ToList();
        if (scopeClaims.Count == 0)
        {
            return Task.CompletedTask;
        }

        IEnumerable<string>? scopes = requirement.AllowedValues;
        if (scopes is null)
        {
            return Task.CompletedTask;
        }

        bool hasScope = scopeClaims.SelectMany(s => s.Value.Split(' ')).Intersect(scopes).Any();
        if (hasScope)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
