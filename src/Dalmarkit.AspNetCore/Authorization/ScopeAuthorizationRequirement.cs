// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Dalmarkit.Common.Validation;
using Microsoft.AspNetCore.Authorization;

namespace Dalmarkit.AspNetCore.Authorization;

/// <summary>
/// Implements an <see cref="IAuthorizationRequirement"/>
/// which requires at least one instance of the specified claim type, and, if allowed values are specified,
/// the claim value must be any of the allowed values.
/// </summary>
public class ScopeAuthorizationRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Creates a new instance of <see cref="ScopeAuthorizationRequirement"/>.
    /// </summary>
    /// <param name="claimType">type of claim</param>
    /// <param name="allowedValues">The optional list of scope values.</param>
    public ScopeAuthorizationRequirement(string claimType, IEnumerable<string>? allowedValues = null)
    {
        _ = Guard.NotNullOrWhiteSpace(claimType, nameof(claimType));

        ClaimType = claimType;
        AllowedValues = allowedValues;
    }

    /// <summary>
    /// Gets the optional list of scope values.
    /// </summary>
    public IEnumerable<string>? AllowedValues { get; }

    /// <summary>
    /// Gets the claim type that must be present.
    /// </summary>
    public string ClaimType { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        string value = (AllowedValues?.Any() != true)
            ? string.Empty
            : $" and Claim.Value is one of the following values: ({string.Join("|", AllowedValues)})";

        return $"{nameof(ScopeAuthorizationRequirement)}:Claim.Type={ClaimType}{value}";
    }
}
