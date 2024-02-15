// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Dalmarkit.Common.Validation;
using Microsoft.AspNetCore.Authorization;

namespace Dalmarkit.AspNetCore.Authorization;

/// <summary>
/// Extensions for building the RequiredScope policy during application startup.
/// </summary>
/// <example>
/// <code>
/// services.AddAuthorization(o =>
/// { o.AddPolicy("Custom",
///     policyBuilder =>policyBuilder.RequireScope("access_as_user"));
/// });
/// </code>
/// </example>
public static class AuthorizationPolicyBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="ScopeAuthorizationRequirement"/> to the current instance which requires
    /// that the current user has the specified claim and that the claim value must be one of the allowed values.
    /// </summary>
    /// <param name="authorizationPolicyBuilder">Used for building policies during application startup.</param>
    /// <param name="claimType">type of claim</param>
    /// <param name="allowedValues">Values the claim must process one or more of for evaluation to succeed.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static AuthorizationPolicyBuilder RequireScope(
        this AuthorizationPolicyBuilder authorizationPolicyBuilder,
        string claimType,
        params string[]? allowedValues)
    {
        return RequireScope(authorizationPolicyBuilder, claimType, allowedValues as IEnumerable<string>);
    }

    /// <summary>
    /// Adds a <see cref="ScopeAuthorizationRequirement"/> to the current instance which requires
    /// that the current user has the specified claim and that the claim value must be one of the allowed values.
    /// </summary>
    /// <param name="authorizationPolicyBuilder">Used for building policies during application startup.</param>
    /// <param name="claimType">type of claim</param>
    /// <param name="allowedValues">Values the claim must process one or more of for evaluation to succeed.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static AuthorizationPolicyBuilder RequireScope(
        this AuthorizationPolicyBuilder authorizationPolicyBuilder,
        string claimType,
        IEnumerable<string>? allowedValues)
    {
        _ = Guard.NotNull(authorizationPolicyBuilder, nameof(authorizationPolicyBuilder));
        _ = Guard.NotNullOrWhiteSpace(claimType, nameof(claimType));

        authorizationPolicyBuilder.Requirements.Add(new ScopeAuthorizationRequirement(claimType, allowedValues));
        return authorizationPolicyBuilder;
    }
}
