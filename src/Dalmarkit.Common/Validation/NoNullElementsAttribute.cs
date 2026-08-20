using Dalmarkit.Common.Errors;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Dalmarkit.Common.Validation;

/// <summary>
/// Validates that a bound collection contains no <see langword="null"/> elements, at any nesting depth,
/// including the values of a dictionary.
/// </summary>
/// <remarks>
/// <para>
/// Collection-level attributes (<see cref="RequiredAttribute"/>, <see cref="MinLengthAttribute"/>,
/// <see cref="MaxLengthAttribute"/>) constrain the collection, not its contents, and a non-nullable
/// element type does not stop a JSON <c>null</c> from being materialized into the collection. Without
/// this attribute the first code to dereference an element — an <see cref="IValidatableObject"/>
/// aggregate, a mapper — throws, and because model validation runs before the action that throw becomes
/// a 500 rather than a validation failure.
/// </para>
/// <para>
/// Every level is inspected, so <c>{"matrix":[[null]]}</c> and <c>{"map":{"a":null}}</c> are rejected as
/// well as <c>{"tags":[null]}</c>. A dictionary is enumerated as <see cref="DictionaryEntry"/> or
/// <see cref="KeyValuePair{TKey,TValue}"/> — both are non-nullable structs, so its VALUES are what this
/// inspects; keys are not, because a JSON object cannot carry a null key. Recursion follows nested
/// sequences only, never the properties of a complex element: those carry their own attributes and the
/// MVC validator already recurses into them.
/// </para>
/// <para>
/// Cyclic graphs terminate — a sequence already inspected at the same or a shallower depth in the current
/// call is not re-entered — and <see cref="MaxDepth"/> bounds the work regardless. Set
/// <see cref="MaxDepth"/> to 1 to restore the shallow, top-level-only behaviour of 0.9.12 through 0.9.16.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public class NoNullElementsAttribute : ValidationAttribute
{
    /// <summary>
    /// The default nesting depth inspected when <see cref="MaxDepth"/> is not set.
    /// </summary>
    public const int DefaultMaxDepth = 32;

    private static readonly ConcurrentDictionary<Type, bool> CanContainNullCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NoNullElementsAttribute"/> class.
    /// </summary>
    public NoNullElementsAttribute() : base(ErrorMessages.ModelStateErrors.ElementNull)
    {
    }

    /// <summary>
    /// Gets or sets the maximum nesting depth inspected. The bound is a backstop against a pathological
    /// payload, not a validation rule: anything deeper is left unchecked rather than reported invalid,
    /// so raising it can only ever reject more. Must be greater than zero; 1 inspects the top level only.
    /// </summary>
    public int MaxDepth
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = DefaultMaxDepth;

    /// <inheritdoc/>
    public override bool IsValid(object? value)
    {
        // An absent collection is RequiredAttribute's business, not this one's — the same division of
        // labour NotDefaultAttribute observes by passing null through.
        if (value is null)
        {
            return true;
        }

        // A string is IEnumerable<char>, whose elements are value types and can never be null, so it is
        // trivially valid. Short-circuited rather than enumerated: otherwise a long string is walked
        // character by character to prove something that is true by construction.
        if (value is not IEnumerable enumerable || value is string)
        {
            return true;
        }

        Dictionary<object, int>? visited = null;
        return IsValidSequence(enumerable, 0, MaxDepth, ref visited);
    }

    private static bool IsValidSequence(IEnumerable sequence, int depth, int maxDepth, ref Dictionary<object, int>? visited)
    {
        // Fail OPEN past the bound: a payload nested deeper than any real DTO is a caller problem for the
        // JSON reader's own depth limit to refuse, and reporting "cannot contain null elements" for a
        // collection this code declined to look at would be a lie.
        if (depth >= maxDepth)
        {
            return true;
        }

        // Skips List<int>, byte[], Guid[] and friends outright: a non-nullable value type element can
        // never be null, so walking a large one proves something the type system already guarantees.
        if (!CanContainNull(sequence.GetType()))
        {
            return true;
        }

        // Only nested sequences can close a cycle, so the top level needs no bookkeeping and the common
        // flat-collection case allocates nothing. A sequence already inspected was necessarily valid — an
        // invalid one short-circuits the whole call — so skipping a repeat is safe as well as cheap.
        //
        // The DEPTH has to be part of that memo, not just the identity. MaxDepth truncates, so how much of
        // a sequence was actually inspected depends on the depth it was reached at, and a shared sequence
        // can be reached at two different depths: the deeper visit may have had its children cut off at
        // the bound while the shallower one still has budget to descend into them. Remembering only "seen"
        // would let the deeper, more truncated visit suppress the shallower, more thorough one and miss a
        // null that is inside the bound. So re-enter whenever this visit is strictly shallower than the
        // best one so far, and record the new best.
        //
        // Still terminates: depth rises by one per level, so a cycle re-enters at a GREATER depth and is
        // skipped, and a sequence can improve its recorded depth at most MaxDepth times.
        if (depth > 0)
        {
            visited ??= new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            if (visited.TryGetValue(sequence, out int inspectedAtDepth) && inspectedAtDepth <= depth)
            {
                return true;
            }

            visited[sequence] = depth;
        }

        // Enumerating a Dictionary<TKey, TValue> yields KeyValuePair<,> STRUCTS, which are never null, so
        // the loop below would pass a dictionary holding a null value. Its values are its elements for
        // this purpose; DictionaryEntry.Key is non-nullable by contract, and JSON has no null key.
        if (sequence is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!IsValidElement(entry.Value, depth, maxDepth, ref visited))
                {
                    return false;
                }
            }

            return true;
        }

        // A sequence of pairs that is NOT a dictionary — a List<KeyValuePair<,>> property, a LINQ
        // projection — never reaches the IDictionary branch above, so the pair is unwrapped below and the
        // same half inspected. The accessor is resolved once per run of like-typed elements rather than
        // once per element, because the reflective read dominates this path and a bound sequence is
        // uniformly typed in every shape MVC can produce. A LOCAL, deliberately: a static
        // Dictionary<Type, PropertyInfo> would root every element type it ever saw for the life of the
        // process — blocking AssemblyLoadContext unload — to save less than this hoist does. Accessor
        // resolution plus read, measured in isolation over 1,000,000 pairs: 35.8 ms resolving per element,
        // 30.6 ms with a static Type-keyed cache, 17.8 ms resolving once as below.
        Type? pairType = null;
        PropertyInfo? pairValueProperty = null;

        foreach (object? element in sequence)
        {
            object? inspected = element;

            if (element is not null)
            {
                Type elementType = element.GetType();
                if (IsKeyValuePair(elementType))
                {
                    if (!ReferenceEquals(elementType, pairType))
                    {
                        pairType = elementType;
                        pairValueProperty = elementType.GetProperty(nameof(KeyValuePair<,>.Value))!;
                    }

                    inspected = pairValueProperty!.GetValue(element);
                }
            }

            if (!IsValidElement(inspected, depth, maxDepth, ref visited))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidElement(object? element, int depth, int maxDepth, ref Dictionary<object, int>? visited)
    {
        if (element is null)
        {
            return false;
        }

        if (element is string)
        {
            return true;
        }

        // A complex element is NOT walked property by property: those properties carry their own
        // attributes and the MVC validator recurses into them already. Only nested sequences continue.
        return element is not IEnumerable nested || IsValidSequence(nested, depth + 1, maxDepth, ref visited);
    }

    private static bool CanContainNull(Type sequenceType)
    {
        return CanContainNullCache.GetOrAdd(sequenceType, static type =>
        {
            Type? elementType = GetElementType(type);
            if (elementType is null)
            {
                // A non-generic IEnumerable (ArrayList, a hand-rolled iterator) says nothing about its
                // elements, so it has to be walked.
                return true;
            }

            if (!elementType.IsValueType || Nullable.GetUnderlyingType(elementType) is not null)
            {
                return true;
            }

            // A struct element is not null, but it can still CARRY one: KeyValuePair<,> holds a value and
            // ImmutableArray<T> is a struct that is itself a sequence.
            return IsKeyValuePair(elementType) || typeof(IEnumerable).IsAssignableFrom(elementType);
        });
    }

    private static Type? GetElementType(Type sequenceType)
    {
        if (sequenceType.IsArray)
        {
            return sequenceType.GetElementType();
        }

        Type? elementType = null;
        foreach (Type interfaceType in sequenceType.GetInterfaces())
        {
            if (!interfaceType.IsGenericType || interfaceType.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                continue;
            }

            if (elementType is not null)
            {
                // Implements IEnumerable<T> more than once: no single element type to reason about, so
                // fall back to walking it.
                return null;
            }

            elementType = interfaceType.GetGenericArguments()[0];
        }

        return elementType;
    }

    private static bool IsKeyValuePair(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
    }
}
